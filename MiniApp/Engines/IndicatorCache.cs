using System.Collections.Concurrent;
using ValutaBot.MiniApp.Indicators;

namespace ValutaBot.MiniApp;

/// <summary>
/// Manages per-(asset, timeframe) stateful indicator instances and their
/// incremental update logic. Only processes unseen candles on each call,
/// resetting the state machine if candles arrive out of order or in bulk.
/// </summary>
internal sealed class IndicatorCache
{
    private sealed class CacheState
    {
        public StatefulRsi?        Rsi;
        public long                RsiLastTick;
        public double              RsiLast;

        public StatefulConnorsRsi? ConnorsRsi;
        public long                ConnorsRsiLastTick;
        public double              ConnorsRsiLast;

        public StatefulHma?        Hma;
        public long                HmaLastTick;
        public double              HmaLast;

        public StatefulEma?        Ema;
        public long                EmaLastTick;
        public double              EmaLast;

        public StatefulTrueAdx?    Adx;
        public long                AdxLastTick;

        public StatefulAtr?        Atr;
        public long                AtrLastTick;

        public StatefulSmc?        Smc;
        public long                SmcLastTick;
    }

    private readonly ConcurrentDictionary<(string, string), CacheState> _states = new();

    private static readonly ConcurrentDictionary<string, Indicators.StatefulOrderFlow> _orderFlowCache = new();

    // Evict oldest 25% of entries instead of clearing all — prevents losing active asset state
    private static void PruneOrderFlowCache()
    {
        var keys = _orderFlowCache.Keys.ToArray();
        int toRemove = Math.Max(1, keys.Length / 4);
        foreach (var k in keys.Take(toRemove))
            _orderFlowCache.TryRemove(k, out _);
    }

    // Maintain last tick for OrderFlow cache validation
    private static readonly ConcurrentDictionary<string, long> _orderFlowLastTicks = new();

    // FIX C-03: three non-atomic ConcurrentDictionary operations had no single lock →
    // a concurrent request could see the reset state before GetOrAdd reinserts the new object.
    private static readonly object _orderFlowLock = new();

    public static Indicators.StatefulOrderFlow GetOrderFlow(string asset, string timeframe, ReadOnlySpan<MiniAppController.OhlcCandle> candles)
    {
        if (_orderFlowCache.Count > 1000) PruneOrderFlowCache();
        string key = $"{asset}_{timeframe}";

        lock (_orderFlowLock)
        {
            long lastTick = _orderFlowLastTicks.GetValueOrDefault(key, 0);
            int unseen    = CountUnseen(candles, lastTick);

            if (unseen > 50 || IsTimestampRewind(candles, lastTick))
                _orderFlowCache[key] = new Indicators.StatefulOrderFlow();

            if (candles.Length > 0)
                _orderFlowLastTicks[key] = candles[^1].Timestamp.Ticks;

            return _orderFlowCache.GetOrAdd(key, _ => new Indicators.StatefulOrderFlow());
        }
    }

    // ── RSI ──────────────────────────────────────────────────────────────────

    // Evict oldest 25% of entries instead of clearing all — prevents losing active asset state
    private void PruneStates()
    {
        var keys = _states.Keys.ToArray();
        int toRemove = Math.Max(1, keys.Length / 4);
        foreach (var k in keys.Take(toRemove))
            _states.TryRemove(k, out _);
    }

    public double GetRsi(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return 50.0;
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.RsiLastTick);
            if (s.Rsi is null || unseen > 50 || IsTimestampRewind(candles, s.RsiLastTick))
            {
                s.Rsi     = new StatefulRsi(period);
                s.RsiLast = 50.0;
                for (int i = 0; i < candles.Length; i++)
                    s.RsiLast = s.Rsi.Update(candles[i].Close);
                s.RsiLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length; i++)
                    s.RsiLast = s.Rsi.Update(candles[i].Close);
                s.RsiLastTick = candles[^1].Timestamp.Ticks;
            }
            return s.RsiLast;
        }
    }

    // ── ConnorsRSI ────────────────────────────────────────────────────────────

    public double GetConnorsRsi(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles)
    {
        if (candles.Length < 20) return GetRsi(asset, tf, candles, 14);
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.ConnorsRsiLastTick);
            if (s.ConnorsRsi is null || unseen > 50 || IsTimestampRewind(candles, s.ConnorsRsiLastTick))
            {
                s.ConnorsRsi     = new StatefulConnorsRsi();
                s.ConnorsRsiLast = 50.0;
                for (int i = 0; i < candles.Length; i++)
                    s.ConnorsRsiLast = s.ConnorsRsi.Update(candles[i].Close);
                s.ConnorsRsiLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length; i++)
                    s.ConnorsRsiLast = s.ConnorsRsi.Update(candles[i].Close);
                s.ConnorsRsiLastTick = candles[^1].Timestamp.Ticks;
            }
            return s.ConnorsRsiLast;
        }
    }

    // ── HMA ───────────────────────────────────────────────────────────────────

    public double GetHma(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
    {
        if (candles.Length < period) return candles.Length > 0 ? candles[^1].Close : 0.0;
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.HmaLastTick);
            if (s.Hma is null || unseen > 50 || IsTimestampRewind(candles, s.HmaLastTick))
            {
                s.Hma     = new StatefulHma(period);
                s.HmaLast = 0.0;
                for (int i = 0; i < candles.Length; i++)
                    s.HmaLast = s.Hma.Update(candles[i].Close);
                s.HmaLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length; i++)
                    s.HmaLast = s.Hma.Update(candles[i].Close);
                s.HmaLastTick = candles[^1].Timestamp.Ticks;
            }
            return s.HmaLast;
        }
    }

    // ── EMA ───────────────────────────────────────────────────────────────────

    public double GetEma(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
    {
        if (candles.Length == 0) return 0.0;
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.EmaLastTick);
            if (s.Ema is null || unseen > 50 || IsTimestampRewind(candles, s.EmaLastTick))
            {
                s.Ema     = new StatefulEma(period);
                s.EmaLast = 0.0;
                for (int i = 0; i < candles.Length; i++)
                    s.EmaLast = s.Ema.Update(candles[i].Close);
                s.EmaLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length; i++)
                    s.EmaLast = s.Ema.Update(candles[i].Close);
                s.EmaLastTick = candles[^1].Timestamp.Ticks;
            }
            return s.EmaLast;
        }
    }

    // ── ADX ───────────────────────────────────────────────────────────────────

    public (double adx, double pdi, double mdi) GetAdx(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return (20.0, 0.0, 0.0);
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.AdxLastTick);
            if (s.Adx is null || unseen > 50 || IsTimestampRewind(candles, s.AdxLastTick))
            {
                s.Adx = new StatefulTrueAdx(period);
                for (int i = 0; i < candles.Length; i++)
                    s.Adx.Update(candles[i].High, candles[i].Low, candles[i].Close);
                s.AdxLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length; i++)
                    s.Adx.Update(candles[i].High, candles[i].Low, candles[i].Close);
                s.AdxLastTick = candles[^1].Timestamp.Ticks;
            }
            return (s.Adx.LastAdx, s.Adx.LastPdi, s.Adx.LastMdi);
        }
    }

    // ── ATR ───────────────────────────────────────────────────────────────────

    public double GetAtr(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return 0.0;
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.AtrLastTick);
            if (s.Atr is null || unseen > 50 || IsTimestampRewind(candles, s.AtrLastTick))
            {
                s.Atr = new StatefulAtr(period);
                for (int i = 0; i < candles.Length; i++)
                    s.Atr.Update(candles[i].High, candles[i].Low, candles[i].Close);
                s.AtrLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length; i++)
                    s.Atr.Update(candles[i].High, candles[i].Low, candles[i].Close);
                s.AtrLastTick = candles[^1].Timestamp.Ticks;
            }
            return s.Atr?.LastAtr ?? 0.0;
        }
    }

    // ── SMC ───────────────────────────────────────────────────────────────────

    public StatefulSmc GetSmcState(string asset, string tf, ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice)
    {
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.SmcLastTick);
            // W-04 FIX: Reset threshold was 500, allowing stale FVG/OB zones to linger for hours
            // if bot was paused. Changed to 50 to match technical indicators.
            if (s.Smc is null || unseen > 50 || IsTimestampRewind(candles, s.SmcLastTick))
            {
                s.Smc = new StatefulSmc();
                s.Smc.Update(candles, currentPrice);
                if (candles.Length > 0) s.SmcLastTick = candles[^1].Timestamp.Ticks;
            }
            else if (unseen > 0)
            {
                // We only need the unseen candles + the previous 5 for context (fractals need 5 candles)
                int startIdx = Math.Max(0, candles.Length - unseen - 5);
                s.Smc.Update(candles.Slice(startIdx), currentPrice);
                s.SmcLastTick = candles[^1].Timestamp.Ticks;
            }
            else
            {
                // Just update with latest currentPrice for live mitigation
                s.Smc.Update(candles.Slice(Math.Max(0, candles.Length - 5)), currentPrice);
            }
            return s.Smc;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Count candles whose timestamp is strictly newer than lastTick.</summary>
    private static int CountUnseen(
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, long lastTick)
    {
        int count = 0;
        for (int i = candles.Length - 1; i >= 0; i--)
        {
            if (candles[i].Timestamp.Ticks <= lastTick) break;
            count++;
        }
        return count;
    }

    /// <summary>Returns true if the last candle is older than what we've already processed
    /// — indicates a time rewind (reconnect, data replay) requiring full reset.</summary>
    private static bool IsTimestampRewind(
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, long lastTick)
        => candles.Length > 0 && candles[^1].Timestamp.Ticks < lastTick;
}
