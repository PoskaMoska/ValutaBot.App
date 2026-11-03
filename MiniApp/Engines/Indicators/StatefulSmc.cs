using System;
using System.Collections.Generic;

namespace ValutaBot.MiniApp.Indicators;

/// <summary>
/// Thread-safe stateful Smart Money Concepts indicator.
/// Readers access an immutable volatile snapshot without acquiring locks (lock-free read path),
/// preventing monitor contention during high-frequency parallel evaluations.
/// </summary>
public class StatefulSmc
{
    public readonly record struct FvgZone(double Top, double Bottom, bool IsBullish, DateTime CreationTime);
    public readonly record struct OrderBlockZone(double Top, double Bottom, bool IsBullish, DateTime CreationTime);
    public readonly record struct Fractal(double Price, bool IsHigh, DateTime Time);

    public sealed class SmcSnapshot
    {
        public bool HasLiquiditySweep { get; }
        public string SweepDirection { get; }
        public bool HasBos { get; }
        public string BosDirection { get; }
        public string CurrentTrend { get; }
        public FvgZone[] ActiveFvgs { get; }
        public OrderBlockZone[] ActiveObs { get; }
        public DateTime LastProcessedTime { get; }
        public TimeSpan TfEstimate { get; }

        public SmcSnapshot(
            bool hasLiquiditySweep, string sweepDirection,
            bool hasBos, string bosDirection, string currentTrend,
            FvgZone[] activeFvgs, OrderBlockZone[] activeObs,
            DateTime lastProcessedTime, TimeSpan tfEstimate)
        {
            HasLiquiditySweep = hasLiquiditySweep;
            SweepDirection = sweepDirection;
            HasBos = hasBos;
            BosDirection = bosDirection;
            CurrentTrend = currentTrend;
            ActiveFvgs = activeFvgs;
            ActiveObs = activeObs;
            LastProcessedTime = lastProcessedTime;
            TfEstimate = tfEstimate;
        }

        public (bool hasBullishFvg, bool hasBearishFvg, FvgZone? nearestFvg) GetNearestFvg(double currentPrice)
        {
            bool bull = false;
            bool bear = false;
            FvgZone? nearest = null;
            double minDistance = double.MaxValue;

            var fvgs = ActiveFvgs;
            for (int i = 0; i < fvgs.Length; i++)
            {
                var fvg = fvgs[i];
                if (fvg.IsBullish) bull = true;
                else               bear = true;

                double dist = Math.Min(
                    Math.Abs(currentPrice - fvg.Top),
                    Math.Abs(currentPrice - fvg.Bottom));
                if (dist < minDistance)
                {
                    minDistance = dist;
                    nearest     = fvg;
                }
            }
            return (bull, bear, nearest);
        }

        public (bool hasBullishOb, bool hasBearishOb, OrderBlockZone? nearestOb) GetNearestOb(double currentPrice, int maxAgeBars = 30)
        {
            bool bull = false;
            bool bear = false;
            OrderBlockZone? nearest = null;
            double minDistance = double.MaxValue;

            DateTime cutoff = LastProcessedTime != default
                ? LastProcessedTime - (TfEstimate * maxAgeBars)
                : DateTime.MinValue;

            var obs = ActiveObs;
            for (int i = 0; i < obs.Length; i++)
            {
                var ob = obs[i];
                if (ob.CreationTime < cutoff) continue;

                if (ob.IsBullish) bull = true;
                else              bear = true;

                double dist = Math.Min(
                    Math.Abs(currentPrice - ob.Top),
                    Math.Abs(currentPrice - ob.Bottom));
                if (dist < minDistance)
                {
                    minDistance = dist;
                    nearest     = ob;
                }
            }
            return (bull, bear, nearest);
        }
    }

    private readonly object _lockObj = new();

    private List<FvgZone>        _activeFvgs = new();
    private List<OrderBlockZone> _activeObs  = new();

    private Fractal _lastSwingHigh;
    private Fractal _lastSwingLow;
    private string  _currentTrend = "NEUTRAL";

    private DateTime _lastProcessedTime;
    private double   _recentAtr;
    private TimeSpan _tfEstimate = TimeSpan.FromMinutes(1);

    private bool   _hasLiquiditySweep;
    private string _sweepDirection = "NONE";
    private bool   _hasBos;
    private string _bosDirection   = "NONE";

    private volatile SmcSnapshot _snapshot = new(false, "NONE", false, "NONE", "NEUTRAL", Array.Empty<FvgZone>(), Array.Empty<OrderBlockZone>(), default, TimeSpan.FromMinutes(1));

    public StatefulSmc Clone()
    {
        lock (_lockObj)
        {
            var clone = (StatefulSmc)this.MemberwiseClone();
            clone._activeFvgs = new List<FvgZone>(this._activeFvgs);
            clone._activeObs = new List<OrderBlockZone>(this._activeObs);
            clone._snapshot = this._snapshot;
            return clone;
        }
    }

    public bool   HasLiquiditySweep => _snapshot.HasLiquiditySweep;
    public string SweepDirection    => _snapshot.SweepDirection;
    public bool   HasBos            => _snapshot.HasBos;
    public string BosDirection      => _snapshot.BosDirection;
    public string CurrentTrend      => _snapshot.CurrentTrend;
    public SmcSnapshot CurrentSnapshot => _snapshot;

    public void Update(ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice)
    {
        if (candles.Length < 5) return;

        lock (_lockObj)
        {
            for (int i = 2; i < candles.Length; i++)
            {
                var c = candles[i];
                if (c.Timestamp <= _lastProcessedTime && _lastProcessedTime != default)
                    continue;

                // Reset transient single-tick events for the NEW closed candle
                _hasLiquiditySweep = false;
                _sweepDirection    = "NONE";
                _hasBos            = false;
                _bosDirection      = "NONE";

                _recentAtr = GetAtrAt(candles, i, 14);

                if (i >= 4) DetectFractalAt(candles, i - 2);
                DetectFvgAt(candles, i);
                DetectOrderBlockAt(candles, i - 1, i);
                DetectStructureBreak(c);
                DetectLiquiditySweep(c);
                MitigateZones(c);

                _lastProcessedTime = c.Timestamp;
            }

            // Live mitigation on current open candle
            MitigateZones(candles[^1]);

            if (_lastProcessedTime != default)
            {
                TimeSpan tf = candles.Length > 1 
                    ? candles[^1].Timestamp - candles[^2].Timestamp 
                    : TimeSpan.FromMinutes(1);
                
                if (tf.TotalSeconds < 1 || tf.TotalDays > 1) tf = TimeSpan.FromMinutes(1);
                _tfEstimate = tf;
                
                PruneStaleZones(_lastProcessedTime.Subtract(tf * 50));
            }

            // Publish immutable snapshot for lock-free concurrent readers
            _snapshot = new SmcSnapshot(
                _hasLiquiditySweep,
                _sweepDirection,
                _hasBos,
                _bosDirection,
                _currentTrend,
                _activeFvgs.ToArray(),
                _activeObs.ToArray(),
                _lastProcessedTime,
                _tfEstimate);
        }
    }

    private void PruneStaleZones(DateTime cutoffTime)
    {
        _activeFvgs.RemoveAll(z => z.CreationTime < cutoffTime);
        _activeObs.RemoveAll(z => z.CreationTime < cutoffTime);
    }

    private double GetAtrAt(ReadOnlySpan<MiniAppController.OhlcCandle> candles, int currentIdx, int period)
    {
        int count = 0;
        double sum = 0;
        for (int i = currentIdx - period; i < currentIdx; i++)
        {
            if (i >= 0)
            {
                sum += (candles[i].High - candles[i].Low);
                count++;
            }
        }
        return count > 0 ? sum / count : 0;
    }

    private void DetectFractalAt(ReadOnlySpan<MiniAppController.OhlcCandle> candles, int idx)
    {
        var cM2 = candles[idx - 2];
        var cM1 = candles[idx - 1];
        var c   = candles[idx];
        var cP1 = candles[idx + 1];
        var cP2 = candles[idx + 2];

        bool isSwingHigh = c.High > cM2.High && c.High > cM1.High && c.High > cP1.High && c.High > cP2.High;
        bool isSwingLow  = c.Low  < cM2.Low  && c.Low  < cM1.Low  && c.Low  < cP1.Low  && c.Low  < cP2.Low;

        if (isSwingHigh)
            _lastSwingHigh = new Fractal(c.High, true, c.Timestamp);

        if (isSwingLow)
            _lastSwingLow = new Fractal(c.Low, false, c.Timestamp);
    }

    private void DetectLiquiditySweep(MiniAppController.OhlcCandle c)
    {
        if (_lastSwingHigh.Time != default
            && c.High  > _lastSwingHigh.Price
            && c.Close < _lastSwingHigh.Price)
        {
            _hasLiquiditySweep = true;
            _sweepDirection    = "BEARISH_SWEEP";
        }

        if (_lastSwingLow.Time != default
            && c.Low   < _lastSwingLow.Price
            && c.Close > _lastSwingLow.Price)
        {
            _hasLiquiditySweep = true;
            _sweepDirection    = "BULLISH_SWEEP";
        }
    }

    private void DetectFvgAt(ReadOnlySpan<MiniAppController.OhlcCandle> candles, int idx)
    {
        var    c1     = candles[idx - 2];
        var    c3     = candles[idx];
        double minGap = _recentAtr * 0.20;

        if (c3.Low > c1.High && (c3.Low - c1.High) >= minGap)
        {
            _activeFvgs.Add(new FvgZone(c3.Low, c1.High, true, c3.Timestamp));
            if (_activeFvgs.Count > 10) _activeFvgs.RemoveAt(0);
        }
        else if (c3.High < c1.Low && (c1.Low - c3.High) >= minGap)
        {
            _activeFvgs.Add(new FvgZone(c1.Low, c3.High, false, c3.Timestamp));
            if (_activeFvgs.Count > 10) _activeFvgs.RemoveAt(0);
        }
    }

    private void DetectOrderBlockAt(ReadOnlySpan<MiniAppController.OhlcCandle> candles, int obIdx, int dispIdx)
    {
        var ob   = candles[obIdx];
        var disp = candles[dispIdx];

        double body  = Math.Abs(ob.Close - ob.Open);
        double range = ob.High - ob.Low;
        if (range <= 1e-8 || (body / range) < 0.60) return;

        bool isBearishCandle = ob.Close < ob.Open;
        bool isBullishCandle = ob.Close > ob.Open;

        if (isBearishCandle && disp.Close > disp.Open && (disp.Close - disp.Open) > (_recentAtr * 0.4))
        {
            _activeObs.Add(new OrderBlockZone(ob.High, ob.Low, true, disp.Timestamp));
            if (_activeObs.Count > 5) _activeObs.RemoveAt(0);
        }
        else if (isBullishCandle && disp.Close < disp.Open && (disp.Open - disp.Close) > (_recentAtr * 0.4))
        {
            _activeObs.Add(new OrderBlockZone(ob.High, ob.Low, false, disp.Timestamp));
            if (_activeObs.Count > 5) _activeObs.RemoveAt(0);
        }
    }

    private void DetectStructureBreak(MiniAppController.OhlcCandle c)
    {
        if (_lastSwingHigh.Time != default && c.Close > _lastSwingHigh.Price)
        {
            if (_currentTrend != "BULLISH")
            {
                _hasBos       = true;
                _bosDirection = "BULLISH_BOS";
                _currentTrend = "BULLISH";
            }
        }
        else if (_lastSwingLow.Time != default && c.Close < _lastSwingLow.Price)
        {
            if (_currentTrend != "BEARISH")
            {
                _hasBos       = true;
                _bosDirection = "BEARISH_BOS";
                _currentTrend = "BEARISH";
            }
        }
    }

    private void MitigateZones(MiniAppController.OhlcCandle c)
    {
        _activeFvgs.RemoveAll(f =>
            (f.IsBullish  && c.Low  <= f.Bottom) ||
            (!f.IsBullish && c.High >= f.Top));

        _activeObs.RemoveAll(o =>
            (o.IsBullish  && c.Close < o.Bottom) ||
            (!o.IsBullish && c.Close > o.Top));
    }

    public (bool hasBullishFvg, bool hasBearishFvg, FvgZone? nearestFvg) GetNearestFvg(double currentPrice)
        => _snapshot.GetNearestFvg(currentPrice);

    public (bool hasBullishOb, bool hasBearishOb, OrderBlockZone? nearestOb) GetNearestOb(double currentPrice, int maxAgeBars = 30)
        => _snapshot.GetNearestOb(currentPrice, maxAgeBars);
}
