/// <summary>
/// Stateful incremental indicator implementations.
/// Each class is a self-contained mathematical state machine — no cache,
/// no logging, no HTTP dependencies. Feed prices one tick at a time.
/// </summary>
namespace ValutaBot.MiniApp.Indicators;

// ── RSI (Wilder's Smoothed Moving Average variant) ─────────────────────────

public sealed class StatefulRsi
{
    private readonly int _period;
    private int _count;
    private double _avgGain;
    private double _avgLoss;
    private double _prevPrice;

    public StatefulRsi(int period = 14) { _period = period; }

    /// <summary>Returns RSI 0-100. Returns 50.0 during warm-up.</summary>
    public double Update(double price)
    {
        if (_count == 0)
        {
            _prevPrice = price;
            _count++;
            return 50.0;
        }
        double diff = price - _prevPrice;
        _prevPrice = price;

        if (_count <= _period)
        {
            if (diff > 0) _avgGain += diff;
            else          _avgLoss -= diff;
            if (_count == _period)
            {
                _avgGain /= _period;
                _avgLoss /= _period;
            }
            _count++;
            if (_count <= _period) return 50.0;
        }
        else
        {
            double gain = diff > 0 ? diff : 0;
            double loss = diff < 0 ? -diff : 0;
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
            _count++;
        }
        // If both gain and loss are near zero — true flat market. Return neutral 50.0.
        // If only avgLoss is near zero but avgGain > 0 — genuine uptrend, return 100.0.
        if (_avgLoss < 1e-10) return _avgGain < 1e-10 ? 50.0 : 100.0;
        return 100.0 - (100.0 / (1.0 + (_avgGain / _avgLoss)));
    }

    public bool IsWarm => _count > _period;
}

// ── ConnorsRSI ─────────────────────────────────────────────────────────────

public sealed class StatefulConnorsRsi
{
    private readonly StatefulRsi _rsi       = new(3);
    private readonly StatefulRsi _streakRsi = new(2);
    private double _currentStreak;
    private double _prevPrice;
    private int _count;

    // Percentile rank: 50 periods → 2% resolution (industry standard uses 100)
    private const int RankPeriod = 50;
    private readonly double[] _returnsHistory = new double[RankPeriod];
    private int _returnsCount;

    /// <summary>Returns ConnorsRSI 0-100. Returns 50.0 during warm-up.</summary>
    public double Update(double price)
    {
        double rsiVal = _rsi.Update(price);
        if (_count > 0)
        {
            if      (price > _prevPrice) _currentStreak = _currentStreak > 0 ? _currentStreak + 1 :  1;
            else if (price < _prevPrice) _currentStreak = _currentStreak < 0 ? _currentStreak - 1 : -1;
            else                         _currentStreak = 0;
        }
        double streakRsiVal = _streakRsi.Update(_currentStreak);

        double currentReturn = (_count > 0 && Math.Abs(_prevPrice) > 1e-10)
            ? (price - _prevPrice) / _prevPrice
            : 0.0;

        // Percentile rank against rolling history (before adding current return)
        int winCount = 0;
        for (int i = 0; i < _returnsCount; i++)
            if (currentReturn > _returnsHistory[i]) winCount++;
        double pctRank = _returnsCount > 0 ? (winCount / (double)_returnsCount) * 100.0 : 50.0;

        // Slide window
        if (_returnsCount < RankPeriod)
            _returnsHistory[_returnsCount++] = currentReturn;
        else
        {
            Array.Copy(_returnsHistory, 1, _returnsHistory, 0, RankPeriod - 1);
            _returnsHistory[RankPeriod - 1] = currentReturn;
        }

        _prevPrice = price;
        _count++;
        return (rsiVal + streakRsiVal + pctRank) / 3.0;
    }
}

// ── Hull Moving Average (HMA) ──────────────────────────────────────────────

public sealed class StatefulHma
{
    private readonly int _period;
    private readonly int _halfPeriod;
    private readonly int _sqrtPeriod;
    private readonly double[] _priceHistory;
    private int _priceCount;
    private readonly double[] _diffHistory;
    private int _diffCount;

    public StatefulHma(int period = 9)
    {
        // FIX W-03: HMA math requires at least period >= 4 to produce meaningful half-period/sqrt-period.
        _period      = Math.Max(4, period);
        _halfPeriod  = _period / 2;
        _sqrtPeriod  = (int)Math.Sqrt(_period);
        _priceHistory = new double[_period];
        _diffHistory  = new double[_sqrtPeriod];
    }

    /// <summary>Returns HMA. Returns raw price during warm-up.</summary>
    public double Update(double price)
    {
        if (_priceCount < _period)
            _priceHistory[_priceCount++] = price;
        else
        {
            Array.Copy(_priceHistory, 1, _priceHistory, 0, _period - 1);
            _priceHistory[_period - 1] = price;
        }

        if (_priceCount == _period)
        {
            double diff = 2.0 * Wma(_priceHistory, _halfPeriod) - Wma(_priceHistory, _period);

            if (_diffCount < _sqrtPeriod)
                _diffHistory[_diffCount++] = diff;
            else
            {
                Array.Copy(_diffHistory, 1, _diffHistory, 0, _sqrtPeriod - 1);
                _diffHistory[_sqrtPeriod - 1] = diff;
            }

            if (_diffCount == _sqrtPeriod)
                return Wma(_diffHistory, _sqrtPeriod);
        }
        return price;
    }

    private static double Wma(double[] arr, int period)
    {
        double sum = 0, weightSum = 0;
        int startIndex = arr.Length - period;
        for (int i = 0; i < period; i++)
        {
            double w = i + 1;
            sum       += arr[startIndex + i] * w;
            weightSum += w;
        }
        return weightSum > 0 ? sum / weightSum : 0;
    }
}

// ── Exponential Moving Average (EMA) ──────────────────────────────────────

public sealed class StatefulEma
{
    private readonly int _period;
    private readonly double _k;
    private int _count;
    private double _ema;
    private double _sum;

    public StatefulEma(int period = 9)
    {
        _period = period;
        _k      = 2.0 / (period + 1.0);
    }

    /// <summary>
    /// Returns EMA. Returns 0.0 during warm-up (not raw price) so callers
    /// can detect uninitialized state. IsWarm indicates when values are valid.
    /// </summary>
    public double Update(double price)
    {
        if (_count < _period)
        {
            _sum += price;
            _count++;
            if (_count == _period) _ema = _sum / _period;
            // Return 0.0 during warmup — NOT raw price (misleading).
            return _count < _period ? 0.0 : _ema;
        }
        _ema = (price - _ema) * _k + _ema;
        _count++;
        return _ema;
    }

    public bool IsWarm => _count >= _period;
}

// ── Average True Range (ATR) ───────────────────────────────────────────────

public sealed class StatefulAtr
{
    private readonly int _period;
    private int _count;
    private double _atr;
    private double _prevClose;
    private double _sumTr;

    public double LastAtr { get; private set; }

    public StatefulAtr(int period = 14) { _period = period; }

    /// <summary>Returns ATR (Wilder). Returns 0.0 during warm-up.</summary>
    public double Update(double high, double low, double close)
    {
        if (_count == 0)
        {
            _prevClose = close;
            _count++;
            return 0.0;
        }
        double tr = Math.Max(high - low,
                    Math.Max(Math.Abs(high - _prevClose),
                             Math.Abs(low  - _prevClose)));
        _prevClose = close;

        if (_count <= _period)
        {
            _sumTr += tr;
            if (_count == _period) _atr = _sumTr / _period;
            _count++;
            LastAtr = _count <= _period ? 0.0 : _atr;
            return LastAtr;
        }
        _atr   = (_atr * (_period - 1) + tr) / _period;
        _count++;
        LastAtr = _atr;
        return _atr;
    }

    public bool IsWarm => _count > _period;
}

// ── True ADX (Wilder) ──────────────────────────────────────────────────────

public sealed class StatefulTrueAdx
{
    private readonly int _period;
    private int _count;
    private double _prevClose, _prevHigh, _prevLow;
    private double _smoothTr, _smoothPdm, _smoothMdm;
    private double _adx;
    private readonly double[] _dxHistory;
    private double _sumDx;

    public double LastPdi { get; private set; }
    public double LastMdi { get; private set; }
    public double LastAdx { get; private set; }

    public StatefulTrueAdx(int period = 14)
    {
        _period    = period;
        _dxHistory = new double[period];
    }

    /// <summary>Returns ADX 0-100. Returns 20.0 during warm-up.</summary>
    public double Update(double high, double low, double close)
    {
        if (_count == 0)
        {
            _prevClose = close; _prevHigh = high; _prevLow = low;
            _count++;
            return 20.0;
        }

        double tr      = Math.Max(high - low,
                         Math.Max(Math.Abs(high - _prevClose),
                                  Math.Abs(low  - _prevClose)));
        double upMove   = high - _prevHigh;
        double downMove = _prevLow - low;

        double pdm = (upMove   > downMove && upMove   > 0) ? upMove   : 0;
        double mdm = (downMove > upMove   && downMove > 0) ? downMove : 0;

        _prevClose = close; _prevHigh = high; _prevLow = low;

        if (_count <= _period)
        {
            _smoothTr += tr; _smoothPdm += pdm; _smoothMdm += mdm;
            _count++;
            return 20.0;
        }

        // Fix: was `_count > _period + 1` — skipped smoothing on the first live tick.
        // Now smoothing is applied from _count == _period + 1 onwards (correct).
        if (_count > _period)
        {
            _smoothTr  = _smoothTr  - (_smoothTr  / _period) + tr;
            _smoothPdm = _smoothPdm - (_smoothPdm / _period) + pdm;
            _smoothMdm = _smoothMdm - (_smoothMdm / _period) + mdm;
        }

        LastPdi = _smoothTr == 0 ? 0 : 100.0 * _smoothPdm / _smoothTr;
        LastMdi = _smoothTr == 0 ? 0 : 100.0 * _smoothMdm / _smoothTr;
        double dx = (LastPdi + LastMdi) == 0
            ? 0
            : 100.0 * Math.Abs(LastPdi - LastMdi) / (LastPdi + LastMdi);

        if (_count <= _period * 2)
        {
            _dxHistory[_count - _period - 1] = dx;
            if (_count == _period * 2)
            {
                for (int i = 0; i < _period; i++) _sumDx += _dxHistory[i];
                _adx = _sumDx / _period;
            }
        }
        else
        {
            _adx = (_adx * (_period - 1) + dx) / _period;
        }

        // FIX C-04: previously LastAdx was assigned AFTER _count++.
        // At the exact tick where _count just became period*2+1, the check
        // `_count <= _period*2` evaluated false → first real ADX discarded (returned 20.0).
        // Fix: lock in isWarm BEFORE the increment.
        bool isWarm = _count >= _period * 2;
        _count++;
        LastAdx = isWarm ? _adx : 20.0;
        return LastAdx;
    }

    public bool IsWarm => _count > _period * 2;
}
