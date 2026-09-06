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

        public StatefulRsi?        RsiBase;
        public long                RsiLastClosedTick;
        public double              RsiLast;

        public StatefulConnorsRsi? ConnorsRsiBase;
        public long                ConnorsRsiLastClosedTick;
        public double              ConnorsRsiLast;

        public StatefulHma?        HmaBase;
        public long                HmaLastClosedTick;
        public double              HmaLast;

        public StatefulEma?        EmaBase;
        public long                EmaLastClosedTick;
        public double              EmaLast;

        public StatefulTrueAdx?    AdxBase;
        public long                AdxLastClosedTick;

        public StatefulAtr?        AtrBase;
        public long                AtrLastClosedTick;

        public StatefulSmc?        SmcBase;
        public long                SmcLastClosedTick;
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
            int unseen = CountUnseen(candles, s.RsiLastClosedTick);
            if (s.RsiBase is null || unseen > 50 || IsTimestampRewind(candles, s.RsiLastClosedTick) || IsStale(s))
            {
                s.RsiBase = new StatefulRsi(period);
                for (int i = 0; i < candles.Length - 1; i++)
                    s.RsiBase.Update(candles[i].Close);
                
                s.RsiLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.RsiLastClosedTick)
                    {
                        s.RsiBase.Update(candles[i].Close);
                        s.RsiLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            var liveRsi = s.RsiBase.Clone();
            s.RsiLast = liveRsi.Update(candles[^1].Close);
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
            int unseen = CountUnseen(candles, s.ConnorsRsiLastClosedTick);
            if (s.ConnorsRsiBase is null || unseen > 50 || IsTimestampRewind(candles, s.ConnorsRsiLastClosedTick) || IsStale(s))
            {
                s.ConnorsRsiBase = new StatefulConnorsRsi();
                for (int i = 0; i < candles.Length - 1; i++)
                    s.ConnorsRsiBase.Update(candles[i].Close);
                
                s.ConnorsRsiLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.ConnorsRsiLastClosedTick)
                    {
                        s.ConnorsRsiBase.Update(candles[i].Close);
                        s.ConnorsRsiLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            var liveRsi = s.ConnorsRsiBase.Clone();
            s.ConnorsRsiLast = liveRsi.Update(candles[^1].Close);
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
            int unseen = CountUnseen(candles, s.HmaLastClosedTick);
            if (s.HmaBase is null || unseen > 50 || IsTimestampRewind(candles, s.HmaLastClosedTick) || IsStale(s))
            {
                s.HmaBase = new StatefulHma(period);
                for (int i = 0; i < candles.Length - 1; i++)
                    s.HmaBase.Update(candles[i].Close);
                
                s.HmaLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.HmaLastClosedTick)
                    {
                        s.HmaBase.Update(candles[i].Close);
                        s.HmaLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            var liveHma = s.HmaBase.Clone();
            s.HmaLast = liveHma.Update(candles[^1].Close);
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
            int unseen = CountUnseen(candles, s.EmaLastClosedTick);
            if (s.EmaBase is null || unseen > 50 || IsTimestampRewind(candles, s.EmaLastClosedTick) || IsStale(s))
            {
                s.EmaBase = new StatefulEma(period);
                for (int i = 0; i < candles.Length - 1; i++)
                    s.EmaBase.Update(candles[i].Close);
                
                s.EmaLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.EmaLastClosedTick)
                    {
                        s.EmaBase.Update(candles[i].Close);
                        s.EmaLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            var liveEma = s.EmaBase.Clone();
            s.EmaLast = liveEma.Update(candles[^1].Close);
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
            int unseen = CountUnseen(candles, s.AdxLastClosedTick);
            if (s.AdxBase is null || unseen > 50 || IsTimestampRewind(candles, s.AdxLastClosedTick) || IsStale(s))
            {
                s.AdxBase = new StatefulTrueAdx(period);
                for (int i = 0; i < candles.Length - 1; i++)
                    s.AdxBase.Update(candles[i].High, candles[i].Low, candles[i].Close);
                
                s.AdxLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.AdxLastClosedTick)
                    {
                        s.AdxBase.Update(candles[i].High, candles[i].Low, candles[i].Close);
                        s.AdxLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            var liveAdx = s.AdxBase.Clone();
            liveAdx.Update(candles[^1].High, candles[^1].Low, candles[^1].Close);
            return (liveAdx.LastAdx, liveAdx.LastPdi, liveAdx.LastMdi);
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
            int unseen = CountUnseen(candles, s.AtrLastClosedTick);
            if (s.AtrBase is null || unseen > 50 || IsTimestampRewind(candles, s.AtrLastClosedTick) || IsStale(s))
            {
                s.AtrBase = new StatefulAtr(period);
                for (int i = 0; i < candles.Length - 1; i++)
                    s.AtrBase.Update(candles[i].High, candles[i].Low, candles[i].Close);
                
                s.AtrLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.AtrLastClosedTick)
                    {
                        s.AtrBase.Update(candles[i].High, candles[i].Low, candles[i].Close);
                        s.AtrLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            var liveAtr = s.AtrBase.Clone();
            liveAtr.Update(candles[^1].High, candles[^1].Low, candles[^1].Close);
            return liveAtr.LastAtr;
        }
    }

    // ── SMC ───────────────────────────────────────────────────────────────────

    public StatefulSmc GetSmcState(string asset, string tf, ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice)
    {
        if (_states.Count > 1000) PruneStates();
        var s = _states.GetOrAdd((asset, tf), _ => new CacheState());
        lock (s)
        {
            int unseen = CountUnseen(candles, s.SmcLastClosedTick);
            if (s.SmcBase is null || unseen > 50 || IsTimestampRewind(candles, s.SmcLastClosedTick) || IsStale(s))
            {
                s.SmcBase = new StatefulSmc();
                s.SmcBase.Update(candles.Slice(0, Math.Max(0, candles.Length - 1)), currentPrice);
                s.SmcLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                int startIdx = Math.Max(0, candles.Length - unseen - 20);
                s.SmcBase.Update(candles.Slice(startIdx, candles.Length - 1 - startIdx), currentPrice);
                s.SmcLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
            }

            var liveSmc = s.SmcBase.Clone();
            liveSmc.Update(candles.Slice(Math.Max(0, candles.Length - 5)), currentPrice);
            return liveSmc;
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
