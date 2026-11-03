using System.Collections.Concurrent;
using ValutaBot.MiniApp.Indicators;

namespace ValutaBot.MiniApp;

/// <summary>
/// Manages per-(asset, timeframe) stateful indicator instances and their
/// incremental update logic. Only processes unseen candles on each call,
/// resetting the state machine if candles arrive out of order or in bulk.
/// Immunized against heap allocation via struct CacheKey and non-allocating live computations.
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
        public double              HmaPrev;   // предыдущий HMA — для slope-based сравнения (FIX-HMA-SLOPE)

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

    internal enum IndicatorType : byte
    {
        Rsi,
        ConnorsRsi,
        Hma,
        Ema,
        Adx,
        Atr,
        Smc
    }

    internal readonly record struct CacheKey(string Asset, string Timeframe, IndicatorType Type, int Period);

    private readonly ConcurrentDictionary<CacheKey, CacheState> _states = new();
    private static readonly Func<CacheKey, CacheState> StateFactory = static _ => new CacheState();
    private long _accessCounter;

    // ── RSI ──────────────────────────────────────────────────────────────────

    private int _isPruning = 0;

    // FIX C-3: Thread-safe, non-blocking LRU eviction with TOCTOU lock protection.
    // Evicts only states idle for >= 30 minutes, preventing active state evictions.
    private void SchedulePruneStates()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isPruning, 1, 0) != 0) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-30);
                // C-4 FIX: Replace 4-iterator LINQ chain (Where+OrderBy+Take+Select+ToList)
                // with a single foreach that stops after filling the cap — zero LINQ alloc.
                int _pruneMax = Math.Max(1, _states.Count / 4);
                long _cutoffTicks = cutoff.Ticks;
                var toDelete = new List<CacheKey>(_pruneMax);
                foreach (var kv in _states)
                {
                    if (kv.Value.LastAccess.Ticks < _cutoffTicks)
                    {
                        toDelete.Add(kv.Key);
                        if (toDelete.Count >= _pruneMax) break;
                    }
                }

                foreach (var k in toDelete)
                {
                    if (_states.TryGetValue(k, out var s))
                    {
                        lock (s)
                        {
                            if (s.LastAccess < cutoff)
                            {
                                _states.TryRemove(new KeyValuePair<CacheKey, CacheState>(k, s));
                            }
                        }
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _isPruning, 0);
            }
        });
    }

    private void CheckPeriodicPrune()
    {
        if ((System.Threading.Interlocked.Increment(ref _accessCounter) & 511) == 0 && _states.Count > 1000)
        {
            SchedulePruneStates();
        }
    }

    /// <summary>
    /// Returns true if the indicator state is too old and must be fully recalculated.
    /// Called inside lock(s) — no thread-safety concerns.
    /// </summary>
    private static bool IsStale(CacheState s)
    {
        var now = DateTime.UtcNow;
        // FIX: Removed UTC midnight hard-reset to fix the "First bet of the day loses" (Cold Start) bug.
        // Trigger: 4-hour threshold — within-session contamination (cold-start synthetic → real ticks)
        if ((now - s.LastFullReset).TotalHours > MaxCacheAgeHours) return true;
        return false;
    }

    public double GetRsi(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return 50.0;
        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.Rsi, period), StateFactory);
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

            s.RsiLast = s.RsiBase.ComputeLive(candles[^1].Close);
            return s.RsiLast;
        }
    }

    // ── ConnorsRSI ────────────────────────────────────────────────────────────

    public double GetConnorsRsi(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles)
    {
        if (candles.Length < 20) return GetRsi(asset, tf, candles, 14);
        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.ConnorsRsi, 0), StateFactory);
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
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

            s.ConnorsRsiLast = s.ConnorsRsiBase.ComputeLive(candles[^1].Close);
            return s.ConnorsRsiLast;
        }
    }

    // ── HMA ───────────────────────────────────────────────────────────────────

    public double GetHma(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
    {
        if (candles.Length < period) return candles.Length > 0 ? candles[^1].Close : 0.0;
        return GetHmaWithSlope(asset, tf, candles, period).current;
    }

    // ── HMA Slope — (current, previous) для slope-based сравнения ────────────
    // FIX-HMA-SLOPE: вместо сравнения price vs HMA (leading indicator → даёт инверсию)
    // используем slope: HMA↑ → bullish, HMA↓ → bearish. Нейтрально к leading/lagging природе.
    public (double current, double previous) GetHmaWithSlope(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
    {
        if (candles.Length < period + 1)
            return (candles.Length > 0 ? candles[^1].Close : 0.0, candles.Length > 1 ? candles[^2].Close : 0.0);

        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.Hma, period), StateFactory);
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
            int unseen = CountUnseen(candles, s.HmaLastClosedTick);
            if (s.HmaBase is null || unseen > 50 || IsTimestampRewind(candles, s.HmaLastClosedTick) || IsStale(s))
            {
                s.HmaBase = new StatefulHma(period);
                s.HmaPrev = 0.0;
                // Прогрев на candles[0..^2] → после этого получаем HmaPrev
                for (int i = 0; i < candles.Length - 2; i++)
                    s.HmaBase.Update(candles[i].Close);

                // Одно обновление candles[^2] → текущий base = previous HMA
                s.HmaPrev = s.HmaBase.Update(candles[^2].Close);
                s.HmaLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
                s.LastFullReset = DateTime.UtcNow;
            }
            else if (unseen > 0)
            {
                for (int i = candles.Length - unseen; i < candles.Length - 1; i++)
                {
                    if (candles[i].Timestamp.Ticks > s.HmaLastClosedTick)
                    {
                        s.HmaPrev = s.HmaBase.Update(candles[i].Close);
                        s.HmaLastClosedTick = candles[i].Timestamp.Ticks;
                    }
                }
            }

            // Zero-allocation live HMA calculation without heap cloning
            double currentHma = s.HmaBase.ComputeLive(candles[^1].Close);
            s.HmaLast = currentHma;
            return (currentHma, (double.IsFinite(s.HmaPrev) && s.HmaPrev != 0.0) ? s.HmaPrev : currentHma); // C-2 FIX: > 0 sentinel fails for valid negative HMA on JPY/inverse pairs
        }
    }

    // ── EMA ───────────────────────────────────────────────────────────────────

    public double GetEma(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 9)
    {
        if (candles.Length == 0) return 0.0;
        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.Ema, period), StateFactory);
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
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

            s.EmaLast = s.EmaBase.ComputeLive(candles[^1].Close);
            return s.EmaLast;
        }
    }

    // ── ADX ───────────────────────────────────────────────────────────────────

    public (double adx, double pdi, double mdi) GetAdx(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return (20.0, 0.0, 0.0);
        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.Adx, period), StateFactory);
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
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

            return s.AdxBase.ComputeLive(candles[^1].High, candles[^1].Low, candles[^1].Close);
        }
    }

    // ── ATR ───────────────────────────────────────────────────────────────────

    public double GetAtr(string asset, string tf,
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return 0.0;
        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.Atr, period), StateFactory);
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
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

            return s.AtrBase.ComputeLive(candles[^1].High, candles[^1].Low, candles[^1].Close);
        }
    }

    // ── SMC ───────────────────────────────────────────────────────────────────

    public StatefulSmc GetSmcState(string asset, string tf, ReadOnlySpan<MiniAppController.OhlcCandle> candles, double currentPrice)
    {
        CheckPeriodicPrune();
        var s = _states.GetOrAdd(new CacheKey(asset, tf, IndicatorType.Smc, 0), StateFactory);
        lock (s)
        {
            s.LastAccess = DateTime.UtcNow;
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
                if (unseen > 1) s.SmcBase.Update(candles.Slice(startIdx, candles.Length - 1 - startIdx), currentPrice);
                s.SmcLastClosedTick = candles.Length > 1 ? candles[^2].Timestamp.Ticks : 0;
            }

            var liveSmc = s.SmcBase.Clone();
            liveSmc.Update(candles.Slice(Math.Max(0, candles.Length - 5)), currentPrice);
            return liveSmc;
        }
    }

    // ── Raw (cache-bypass) methods for Gatekeeper ─────────────────────────────
    // IMPORTANT: These methods do NOT read or write the IndicatorCache.
    // They exist solely so ValidateMarketGatekeeper can compute ATR/ADX
    // without poisoning the cache that ScoreTimeframe relies on.
    // Root cause of the "Shared Cache Poisoning" bug was Gatekeeper calling
    // GetAtr/GetAdx with 'allCandles' (including the live unclosed candle),
    // advancing the permanent cache state and causing ScoreTimeframe to
    // double-count the last closed candle on the very next call.

    public static double ComputeAtrRaw(
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return 0.0;
        var atr = new StatefulAtr(period);
        for (int i = 0; i < candles.Length; i++)
            atr.Update(candles[i].High, candles[i].Low, candles[i].Close);
        return atr.LastAtr;
    }

    public static (double adx, double pdi, double mdi) ComputeAdxRaw(
        ReadOnlySpan<MiniAppController.OhlcCandle> candles, int period = 14)
    {
        if (candles.Length <= period) return (20.0, 0.0, 0.0);
        var adx = new StatefulTrueAdx(period);
        for (int i = 0; i < candles.Length; i++)
            adx.Update(candles[i].High, candles[i].Low, candles[i].Close);
        return (adx.LastAdx, adx.LastPdi, adx.LastMdi);
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
