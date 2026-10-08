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
        double BasketSyncScore
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
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace(" OTC", "").Replace("_OTC", "");
            var now = DateTime.UtcNow;

            if (_dayAnchorsCache.TryGetValue(cleanAsset, out var cached) && now < cached.expiry)
            {
                // Update live distances using current live price
                double posPct = (cached.anchors.DayHigh > cached.anchors.DayLow)
                    ? Math.Clamp((currentPrice - cached.anchors.DayLow) / (cached.anchors.DayHigh - cached.anchors.DayLow), 0.0, 1.0)
                    : 0.5;
                double distHigh = Math.Round((cached.anchors.DayHigh - currentPrice) / currentPrice * 10000, 2);
                double distLow = Math.Round((currentPrice - cached.anchors.DayLow) / currentPrice * 10000, 2);
                double distAsiaH = cached.anchors.AsianHigh > 0 ? Math.Round((cached.anchors.AsianHigh - currentPrice) / currentPrice * 10000, 2) : 0.0;
                double distAsiaL = cached.anchors.AsianLow > 0 ? Math.Round((currentPrice - cached.anchors.AsianLow) / currentPrice * 10000, 2) : 0.0;

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
                      AND open_time >= @DayStart;
                ", new { Asset = cleanAsset, DayStart = dayStart, AsianEnd = asianEnd });

                double dayH = row?.day_high ?? currentPrice;
                double dayL = row?.day_low ?? currentPrice;
                double asiaH = row?.asian_high ?? 0.0;
                double asiaL = row?.asian_low ?? 0.0;

                if (dayH < currentPrice) dayH = currentPrice;
                if (dayL > currentPrice) dayL = currentPrice;

                double posPct = (dayH > dayL) ? Math.Clamp((currentPrice - dayL) / (dayH - dayL), 0.0, 1.0) : 0.5;
                double distHigh = Math.Round((dayH - currentPrice) / currentPrice * 10000, 2);
                double distLow = Math.Round((currentPrice - dayL) / currentPrice * 10000, 2);
                double distAsiaH = asiaH > 0 ? Math.Round((asiaH - currentPrice) / currentPrice * 10000, 2) : 0.0;
                double distAsiaL = asiaL > 0 ? Math.Round((currentPrice - asiaL) / currentPrice * 10000, 2) : 0.0;

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

                _dayAnchorsCache[cleanAsset] = (anchors, now.AddSeconds(60));
                return anchors;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[MacroContext] Day anchors fetch failed for {cleanAsset}: {ex.Message}");
                return new DayLevelAnchors(currentPrice, currentPrice, 0.5, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            }
        }

        // ── 2. Synthetic Dollar Basket Tracker ─────────────────────────────────────
        private static readonly ConcurrentDictionary<string, Queue<(DateTime time, double price)>> _priceHistory = new();
        private static readonly object _basketLock = new();

        public static void UpdatePrice(string asset, double price, DateTime timeUtc)
        {
            if (price <= 0) return;
            string clean = asset.ToUpper().Replace("/", "").Replace("-", "").Replace(" OTC", "").Replace("_OTC", "");
            
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
            var now = DateTime.UtcNow;
            var cutoff1m = now.AddSeconds(-60);
            var cutoff5m = now.AddSeconds(-300);

            var usdReturns1m = new List<double>();
            var usdReturns5m = new List<double>();

            // Major Dollar pairs: EURUSD, GBPUSD, AUDUSD (inverse USD), USDJPY, USDCAD, USDCHF (direct USD)
            string[] trackedPairs = { "EURUSD", "GBPUSD", "AUDUSD", "USDJPY", "USDCAD", "USDCHF" };

            lock (_basketLock)
            {
                foreach (var pair in trackedPairs)
                {
                    if (!_priceHistory.TryGetValue(pair, out var queue) || queue.Count < 2) continue;

                    var arr = queue.ToArray();
                    double currentPrice = arr[^1].price;

                    // 1m return
                    var item1m = arr.FirstOrDefault(x => x.time >= cutoff1m);
                    if (item1m.price > 0 && currentPrice > 0)
                    {
                        double ret = (currentPrice - item1m.price) / item1m.price;
                        double usdRet = IsUsdBase(pair) ? ret : -ret;
                        usdReturns1m.Add(usdRet);
                    }

                    // 5m return
                    var item5m = arr.FirstOrDefault(x => x.time >= cutoff5m);
                    if (item5m.price > 0 && currentPrice > 0)
                    {
                        double ret = (currentPrice - item5m.price) / item5m.price;
                        double usdRet = IsUsdBase(pair) ? ret : -ret;
                        usdReturns5m.Add(usdRet);
                    }
                }
            }

            if (usdReturns1m.Count == 0)
            {
                return new DollarBasketMetrics(0.0, 0.0, 0.5);
            }

            double dxy1m = Math.Round(usdReturns1m.Average() * 10000, 2);
            double dxy5m = usdReturns5m.Count > 0 ? Math.Round(usdReturns5m.Average() * 10000, 2) : 0.0;

            // Measure cross-pair synchronization: what proportion of pairs agree with the aggregate USD direction
            int dominantSign = Math.Sign(dxy1m);
            double syncScore = 0.5;
            if (dominantSign != 0 && usdReturns1m.Count > 1)
            {
                int agreeing = usdReturns1m.Count(r => Math.Sign(r) == dominantSign);
                syncScore = Math.Round((double)agreeing / usdReturns1m.Count, 2);
            }

            return new DollarBasketMetrics(dxy1m, dxy5m, syncScore);
        }

        private static bool IsUsdBase(string pair)
        {
            return pair.StartsWith("USD", StringComparison.OrdinalIgnoreCase);
        }
    }
}
