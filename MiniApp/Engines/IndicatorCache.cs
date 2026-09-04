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
        public DateTime            LastAccess    = DateTime.UtcNow;

        // ROOT-CAUSE FIX: Time-based cache invalidation.
        // Previously indicators accumulated state indefinitely — reset only on unseen > 50.
        // At active trading pace (1 req/30s), unseen is always 0-3 → reset NEVER happened.
        // Result: yesterday's RSI/HMA/EMA "memory" poisoned today's signals.
        //
        // Two reset triggers:
        //   1. Every MaxCacheAgeHours (4h) — clears within-session contamination
        //      (e.g. cold-start synthetic 1m candles followed by real s5 ticks)
        //   2. New trading day (UTC midnight) — ensures day-to-day clean slate
        public DateTime            LastFullReset = DateTime.MinValue;

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

    // Indicators are fully recalculated if their last reset is older than this.
    // 4 hours covers: London→NY session transition, cold-start synthetic contamination.
    private const double MaxCacheAgeHours = 4.0;

    private readonly ConcurrentDictionary<(string, string), CacheState> _states = new();

    private static readonly ConcurrentDictionary<string, Indicators.StatefulOrderFlow> _orderFlowCache = new();

    // FIX C-3: LRU eviction — evict the least-recently-used 25% of entries.
    // Previously used Take(toRemove) on unordered ConcurrentDictionary keys,
    // which was effectively random and could delete actively-trading pairs.
    private static void PruneOrderFlowCache()
    {
        var ordered = _orderFlowCache
            .OrderBy(kv => _orderFlowLastTicks.GetValueOrDefault($"{kv.Key}", 0))
            .Take(Math.Max(1, _orderFlowCache.Count / 4))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in ordered)
        {
            _orderFlowCache.TryRemove(k, out _);
            // FIX M-1: Also remove from _orderFlowLastTicks to prevent stale tick lookup
            _orderFlowLastTicks.TryRemove(k, out _);
        }
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

    // FIX C-3: LRU eviction — sort by LastAccess so the most recently used pairs survive.
    // Previously used Take(toRemove) on unordered ConcurrentDictionary keys — random eviction.
    private void PruneStates()
    {
        var toDelete = _states
            .OrderBy(kv => kv.Value.LastAccess)
            .Take(Math.Max(1, _states.Count / 4))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in toDelete)
            _states.TryRemove(k, out _);
    }

    /// <summary>
    /// Returns true if the indicator state is too old and must be fully recalculated.
    /// Called inside lock(s) — no thread-safety concerns.
    /// </summary>
    private static bool IsStale(CacheState s)
    {
        var now = DateTime.UtcNow;
        // Trigger 1: New trading day (UTC midnight) — day-to-day market regime change
        if (s.LastFullReset.Date < now.Date) return true;
        // Trigger 2: 4-hour threshold — within-session contamination (cold-start synthetic → real ticks)
        if ((now - s.LastFullReset).TotalHours > MaxCacheAgeHours) return true;
        return false;
    }

    public double GetRsi(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return 50.0;
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
            int unseen = CountUnseen(candles, s.RsiLastTick);
            if (s.Rsi is null || unseen > 50 || IsTimestampRewind(candles, s.RsiLastTick) || IsStale(s))
            {
                s.Rsi     = new StatefulRsi(period);
                s.RsiLast = 50.0;
                for (int i = 0; i < candles.Length; i++)
                    s.RsiLast = s.Rsi.Update(candles[i].Close);
                s.RsiLastTick  = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
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
            if (s.ConnorsRsi is null || unseen > 50 || IsTimestampRewind(candles, s.ConnorsRsiLastTick) || IsStale(s))
            {
                s.ConnorsRsi     = new StatefulConnorsRsi();
                s.ConnorsRsiLast = 50.0;
                for (int i = 0; i < candles.Length; i++)
                    s.ConnorsRsiLast = s.ConnorsRsi.Update(candles[i].Close);
                s.ConnorsRsiLastTick = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
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
            if (s.Hma is null || unseen > 50 || IsTimestampRewind(candles, s.HmaLastTick) || IsStale(s))
            {
                s.Hma     = new StatefulHma(period);
                s.HmaLast = 0.0;
                for (int i = 0; i < candles.Length; i++)
                    s.HmaLast = s.Hma.Update(candles[i].Close);
                s.HmaLastTick  = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
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
            if (s.Ema is null || unseen > 50 || IsTimestampRewind(candles, s.EmaLastTick) || IsStale(s))
            {
                s.Ema     = new StatefulEma(period);
                s.EmaLast = 0.0;
                for (int i = 0; i < candles.Length; i++)
                    s.EmaLast = s.Ema.Update(candles[i].Close);
                s.EmaLastTick  = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
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
            if (s.Adx is null || unseen > 50 || IsTimestampRewind(candles, s.AdxLastTick) || IsStale(s))
            {
                s.Adx = new StatefulTrueAdx(period);
                for (int i = 0; i < candles.Length; i++)
                    s.Adx.Update(candles[i].High, candles[i].Low, candles[i].Close);
                s.AdxLastTick  = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
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
            if (s.Atr is null || unseen > 50 || IsTimestampRewind(candles, s.AtrLastTick) || IsStale(s))
            {
                s.Atr = new StatefulAtr(period);
                for (int i = 0; i < candles.Length; i++)
                    s.Atr.Update(candles[i].High, candles[i].Low, candles[i].Close);
                s.AtrLastTick  = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
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
            if (s.Smc is null || unseen > 50 || IsTimestampRewind(candles, s.SmcLastTick) || IsStale(s))
            {
                s.Smc = new StatefulSmc();
                s.Smc.Update(candles, currentPrice);
                if (candles.Length > 0) s.SmcLastTick = candles[^1].Timestamp.Ticks;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                // FIX C-4: ATR-14 inside StatefulSmc needs at least 14 candles of context.
                // Previously only passed unseen+5, which caused ATR to be computed over 4-5 bars
                // instead of 14, generating false FVG/OrderBlock signals on every tick.
                int startIdx = Math.Max(0, candles.Length - unseen - 20);
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
