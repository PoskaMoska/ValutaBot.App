using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp.Features.MarketAnalysis.Engines
{
    public record DayLevelAnchors(
        double DayHigh,
        double DayLow,
        double DayRangePositionPct,
        double DistToDayHighBps,
        double DistToDayLowBps,
        double AsianHigh,
        double AsianLow,
        double DistToAsianHighBps,
        double DistToAsianLowBps
    );

    public record DollarBasketMetrics(
        double DxyMomentum1mBps,
        double DxyMomentum5mBps,
        double BasketSyncScore,
        double DxyMomentum5sBps = 0.0,
        double DxyMomentum15sBps = 0.0,
        double DxyMomentum30sBps = 0.0,
        double BasketSync5sScore = 0.5
    );

    /// <summary>
    /// Computes macro-market context:
    /// 1. Day-level liquidity anchors (PDH, PDL, Asian Range, Day Position) with in-memory TTL caching.
    /// 2. Synthetic Dollar Index (DXY) momentum and cross-pair synchronization across major currency pairs.
    /// </summary>
    public static class MacroContextEngine
    {
        private record DbAnchorsDto(double? day_high, double? day_low, double? asian_high, double? asian_low);
        // ── 1. Day-Level Anchors Cache ─────────────────────────────────────────────
        private static readonly ConcurrentDictionary<string, (DayLevelAnchors anchors, DateTime expiry)> _dayAnchorsCache = new();

        public static async Task<DayLevelAnchors> GetDayLevelAnchorsAsync(string asset, double currentPrice)
        {
            string cleanAsset = ValutaBot.MiniApp.PriceFeed.BaseAsset(asset);
            string candleKey = ValutaBot.MiniApp.PriceFeed.CandleKey(cleanAsset);
            var now = DateTime.UtcNow;

            if (_dayAnchorsCache.TryGetValue(candleKey, out var cached) && now < cached.expiry)
            {
                // Update live distances using current live price
                double posPct = (cached.anchors.DayHigh > cached.anchors.DayLow)
                    ? Math.Clamp((currentPrice - cached.anchors.DayLow) / (cached.anchors.DayHigh - cached.anchors.DayLow), 0.0, 1.0)
                    : 0.5;
                double safeCurrentPrice = Math.Max(1e-6, currentPrice);
                double distHigh = Math.Round((cached.anchors.DayHigh - currentPrice) / safeCurrentPrice * 10000, 2);
                double distLow = Math.Round((currentPrice - cached.anchors.DayLow) / safeCurrentPrice * 10000, 2);
                double distAsiaH = cached.anchors.AsianHigh > 0 ? Math.Round((cached.anchors.AsianHigh - currentPrice) / safeCurrentPrice * 10000, 2) : 0.0;
                double distAsiaL = cached.anchors.AsianLow > 0 ? Math.Round((currentPrice - cached.anchors.AsianLow) / safeCurrentPrice * 10000, 2) : 0.0;

                return cached.anchors with {
                    DayRangePositionPct = Math.Round(posPct, 3),
                    DistToDayHighBps = distHigh,
                    DistToDayLowBps = distLow,
                    DistToAsianHighBps = distAsiaH,
                    DistToAsianLowBps = distAsiaL
                };
            }

            try
            {
                string dayStart = now.ToString("yyyy-MM-dd");
                string asianEnd = dayStart + "T08:00:00";

                using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync();

                var row = await conn.QueryFirstOrDefaultAsync<DbAnchorsDto>(@"
                    SELECT 
                        MAX(high_price) as day_high, 
                        MIN(low_price) as day_low,
                        MAX(CASE WHEN open_time < @AsianEnd THEN high_price ELSE NULL END) as asian_high,
                        MIN(CASE WHEN open_time < @AsianEnd THEN low_price ELSE NULL END) as asian_low
                    FROM subminute_candles 
                    WHERE asset = @Asset 
                      AND interval = '1min'
                      AND open_time >= @DayStart;
                ", new { Asset = candleKey, DayStart = dayStart, AsianEnd = asianEnd });

                double dayH = row?.day_high ?? currentPrice;
                double dayL = row?.day_low ?? currentPrice;
                double asiaH = row?.asian_high ?? 0.0;
                double asiaL = row?.asian_low ?? 0.0;

                if (dayH < currentPrice) dayH = currentPrice;
                if (dayL > currentPrice) dayL = currentPrice;

                double posPct = (dayH > dayL) ? Math.Clamp((currentPrice - dayL) / (dayH - dayL), 0.0, 1.0) : 0.5;
                double safePrice = Math.Max(1e-6, currentPrice);
                double distHigh = Math.Round((dayH - currentPrice) / safePrice * 10000, 2);
                double distLow = Math.Round((currentPrice - dayL) / safePrice * 10000, 2);
                double distAsiaH = asiaH > 0 ? Math.Round((asiaH - currentPrice) / safePrice * 10000, 2) : 0.0;
                double distAsiaL = asiaL > 0 ? Math.Round((currentPrice - asiaL) / safePrice * 10000, 2) : 0.0;

                var anchors = new DayLevelAnchors(
                    DayHigh: dayH,
                    DayLow: dayL,
                    DayRangePositionPct: Math.Round(posPct, 3),
                    DistToDayHighBps: distHigh,
                    DistToDayLowBps: distLow,
                    AsianHigh: asiaH,
                    AsianLow: asiaL,
                    DistToAsianHighBps: distAsiaH,
                    DistToAsianLowBps: distAsiaL
                );

                _dayAnchorsCache[candleKey] = (anchors, now.AddSeconds(60));
                return anchors;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[MacroContext] Day anchors fetch failed for {candleKey}: {ex.Message}");
                return new DayLevelAnchors(currentPrice, currentPrice, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            }
        }

        // ── 2. Synthetic Dollar Basket Tracker ─────────────────────────────────────
        private static readonly ConcurrentDictionary<string, Queue<(DateTime time, double price)>> _priceHistory = new();
        private static readonly object _basketLock = new();

        public static void UpdatePrice(string asset, double price, DateTime timeUtc)
        {
            if (price <= 0) return;
            string clean = ValutaBot.MiniApp.PriceFeed.BaseAsset(asset);
            
            var queue = _priceHistory.GetOrAdd(clean, _ => new Queue<(DateTime, double)>());
            lock (_basketLock)
            {
                queue.Enqueue((timeUtc, price));
                // Prune records older than 10 minutes
                var cutoff = timeUtc.AddMinutes(-10);
                while (queue.Count > 0 && queue.Peek().time < cutoff)
                {
                    queue.Dequeue();
                }
            }
        }

        public static DollarBasketMetrics ComputeDollarBasketMetrics()
        {
            var snapshots = new Dictionary<string, (DateTime time, double price)[]>(StringComparer.OrdinalIgnoreCase);
            string[] trackedPairs = { "EURUSD", "GBPUSD", "AUDUSD", "USDJPY", "USDCAD", "USDCHF" };
            lock (_basketLock)
            {
                foreach (var pair in trackedPairs)
                {
                    if (_priceHistory.TryGetValue(pair, out var queue) && queue.Count >= 2)
                        snapshots[pair] = queue.ToArray();
                }
            }
            return ComputeMetricsFromSnapshots(DateTime.UtcNow, snapshots);
        }

        internal static DollarBasketMetrics ComputeMetricsFromSnapshots(
            DateTime now,
            IReadOnlyDictionary<string, (DateTime time, double price)[]> snapshots)
        {
            var cutoff5s = now.AddSeconds(-5);
            var cutoff15s = now.AddSeconds(-15);
            var cutoff30s = now.AddSeconds(-30);
            var cutoff1m = now.AddSeconds(-60);
            var cutoff5m = now.AddSeconds(-300);

            var usdReturns5s = new List<double>();
            var usdReturns15s = new List<double>();
            var usdReturns30s = new List<double>();
            var usdReturns1m = new List<double>();
            var usdReturns5m = new List<double>();

            // Major Dollar pairs: EURUSD, GBPUSD, AUDUSD (inverse USD), USDJPY, USDCAD, USDCHF (direct USD)
            string[] trackedPairs = { "EURUSD", "GBPUSD", "AUDUSD", "USDJPY", "USDCAD", "USDCHF" };

            static void AccumulateWindow((DateTime time, double price)[] arr, double currentPrice, DateTime cutoff, bool isUsdBase, List<double> target)
            {
                var item = arr.FirstOrDefault(x => x.time >= cutoff);
                if (item.price > 0 && currentPrice > 0)
                {
                    double ret = (currentPrice - item.price) / item.price;
                    target.Add(isUsdBase ? ret : -ret);
                }
            }

            static (double AvgBps, double Sync) SummarizeBasket(List<double> returns)
            {
                if (returns.Count == 0) return (0.0, 0.5);
                double avgBps = Math.Round(returns.Average() * 10000, 2);
                int dominantSign = Math.Sign(avgBps);
                double sync = 0.5;
                if (dominantSign != 0 && returns.Count > 1)
                {
                    int agreeing = returns.Count(r => Math.Sign(r) == dominantSign);
                    sync = Math.Round((double)agreeing / returns.Count, 2);
                }
                return (avgBps, sync);
            }

            foreach (var pair in trackedPairs)
            {
                if (!snapshots.TryGetValue(pair, out var arr) || arr.Length < 2) continue;

                double currentPrice = arr[^1].price;
                bool isUsdBase = IsUsdBase(pair);

                AccumulateWindow(arr, currentPrice, cutoff5s, isUsdBase, usdReturns5s);
                AccumulateWindow(arr, currentPrice, cutoff15s, isUsdBase, usdReturns15s);
                AccumulateWindow(arr, currentPrice, cutoff30s, isUsdBase, usdReturns30s);
                AccumulateWindow(arr, currentPrice, cutoff1m, isUsdBase, usdReturns1m);
                AccumulateWindow(arr, currentPrice, cutoff5m, isUsdBase, usdReturns5m);
            }

            if (usdReturns1m.Count == 0 && usdReturns5s.Count == 0)
            {
                return new DollarBasketMetrics(0.0, 0.0, 0.5);
            }

            var (dxy5s, sync5s) = SummarizeBasket(usdReturns5s);
            var (dxy15s, _) = SummarizeBasket(usdReturns15s);
            var (dxy30s, _) = SummarizeBasket(usdReturns30s);
            var (dxy1m, syncScore) = SummarizeBasket(usdReturns1m);
            var (dxy5m, _) = SummarizeBasket(usdReturns5m);

            return new DollarBasketMetrics(dxy1m, dxy5m, syncScore, dxy5s, dxy15s, dxy30s, sync5s);
        }

        private static bool IsUsdBase(string pair)
        {
            return pair.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
        }
    }
}
