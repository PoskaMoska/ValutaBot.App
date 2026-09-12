using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.App.MiniApp.Data; // ADD: for DbConnectionFactory

namespace ValutaBot.MiniApp
{
    public static class RealtimeTickCollector
    {
        private class CandleAccumulator
        {
            public double? Open { get; set; }
            public double High { get; set; } = double.MinValue;
            public double Low { get; set; } = double.MaxValue;
            public double Close { get; set; }
            public int TickCount { get; set; }
            public DateTime OpenTime { get; set; }
            
            public void AddTick(double price)
            {
                if (!Open.HasValue) Open = price;
                if (price > High) High = price;
                if (price < Low) Low = price;
                Close = price;
                TickCount++;
            }
            
            public void Reset(DateTime openTime)
            {
                Open = null;
                High = double.MinValue;
                Low = double.MaxValue;
                Close = 0;
                TickCount = 0;
                OpenTime = openTime;
            }
        }
        
        // Real-time accumulators for current in-progress candles (used by GetRecentCandles for zero-lag)
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s5  = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s10 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s15 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s30 = new();

        public static async Task InitializeAsync()
        {
            BotLogger.Info("[TickCollector] Initialized continuous real-time subminute candle accumulation.");
        }

        // Kept for backward compatibility with DbConnectionFactory.Initialize()
        public static void Initialize()
        {
            InitializeAsync().GetAwaiter().GetResult();
        }

        // Continuous save: called on every price tick - saves directly to DB, eliminates timer/flush dependency
        public static async Task SaveCandleAsync(string asset, string interval, double price)
        {
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                // Determine open_time grid-snapped to interval
                long ticks = DateTime.UtcNow.Ticks;
                long intervalTicks = interval switch
                {
                    "s5" => TimeSpan.FromSeconds(5).Ticks,
                    "s10" => TimeSpan.FromSeconds(10).Ticks,
                    "s15" => TimeSpan.FromSeconds(15).Ticks,
                    "s30" => TimeSpan.FromSeconds(30).Ticks,
                    _ => TimeSpan.FromSeconds(5).Ticks
                };
                var gridTime = new DateTime(ticks - (ticks % intervalTicks), DateTimeKind.Utc);
                string openTimeStr = gridTime.ToString("o");

                // FIX D-2: DO UPDATE so every tick updates close/high/low.
                // Previously DO NOTHING meant close_price = open_price (first tick only),
                // causing the verifier to use open price as exit price → biased WIN/LOSS labels.
                await conn.ExecuteAsync(@"
                    INSERT INTO subminute_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume)
                    VALUES (@Asset, @Interval, @OpenTime, @Open, @High, @Low, @Close, @Volume)
                    ON CONFLICT (asset, interval, open_time) DO UPDATE SET
                        high_price  = GREATEST(subminute_candles.high_price,  EXCLUDED.close_price),
                        low_price   = LEAST(subminute_candles.low_price,      EXCLUDED.close_price),
                        close_price = EXCLUDED.close_price,
                        volume      = subminute_candles.volume + 1;
                ", new { Asset = asset, Interval = interval, OpenTime = openTimeStr, Open = price, High = price, Low = price, Close = price, Volume = 1 });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TickSync] Failed to save tick for {asset}/{interval}: {ex.Message}");
            }
        }

        // Get recent candles from DB + live accumulator (live candle gives TA engine immediate reflexes)
        public static async Task<MiniAppController.OhlcCandle[]> GetRecentCandles(string asset, string interval, int limit)
        {
            try
            {
                // CRITICAL FIX: OnPriceUpdate writes as "EURUSD" (stripped), so query must match.
                string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");

                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                var records = (await Dapper.SqlMapper.QueryAsync(conn, @"
                    SELECT open_time as OpenTime, open_price as Open, high_price as High, low_price as Low, close_price as Close, volume as Volume
                    FROM subminute_candles
                    WHERE asset = @Asset AND interval = @Interval
                    ORDER BY open_time DESC
                    LIMIT @Limit;
                ", new { Asset = cleanAsset, Interval = interval, Limit = limit })).ToList();

                // FIX: Retrieve the live, unclosed candle from memory to eliminate the 2-5 second DB flush lag.
                ConcurrentDictionary<string, CandleAccumulator>? targetDict = interval switch
                {
                    "s5" => _s5, "s10" => _s10, "s15" => _s15, "s30" => _s30, _ => null
                };

                CandleAccumulator? liveAcc = null;
                if (targetDict != null && targetDict.TryGetValue(cleanAsset, out var acc) && acc.Open.HasValue)
                {
                    liveAcc = acc;
                }

                int totalCount = records.Count + (liveAcc != null ? 1 : 0);
                int resultSize = Math.Min(limit, totalCount);
                var result = new MiniAppController.OhlcCandle[resultSize];
                
                int resultIdx = 0;
                // If total exceeds limit, we must skip the oldest DB record to make room for the live one
                int dbRecordsToTake = liveAcc != null ? Math.Min(records.Count, limit - 1) : Math.Min(records.Count, limit);
                
                // Add DB records in chronological order (records is DESC, so start from the oldest we want to take)
                for (int i = dbRecordsToTake - 1; i >= 0; i--)
                {
                    var r = records[i]; 
                    result[resultIdx++] = new MiniAppController.OhlcCandle((double)r.Open, (double)r.High, (double)r.Low, (double)r.Close, (double)r.Volume,
                        DateTime.Parse((string)r.OpenTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
                }

                // Append the live, unclosed candle to give the TA engine zero-lag reflexes
                if (liveAcc != null && resultIdx < resultSize)
                {
                    lock (liveAcc)
                    {
                        result[resultIdx] = new MiniAppController.OhlcCandle(
                            liveAcc.Open.Value, liveAcc.High, liveAcc.Low, liveAcc.Close, liveAcc.TickCount, liveAcc.OpenTime);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[RealtimeTickCollector] Failed to fetch recent {interval} candles for {asset}: {ex.Message}");
                return Array.Empty<MiniAppController.OhlcCandle>();
            }
        }

        // Continuous per-tick async save - eliminates dependency on timer-based flush and Friday DB falls
        public static async Task OnPriceUpdateAsync(string asset, double price)
        {
            // FIX W-25: OTC ticks were silently dropped here, so no subminute candles were built
            // for EURUSD_OTC, GBPUSD_OTC etc. → SGD /feedback for OTC pairs always had empty candles.
            // Fix: strip _OTC suffix so ticks are accumulated under the base symbol key (EURUSD etc.)
            // which aligns with the model key used in training and feedback.
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");

            // Continuous async save per-tick (eliminates dependency on timer flush & Friday DB falls)
            await SaveCandleAsync(cleanAsset, "s5", price);
            await SaveCandleAsync(cleanAsset, "s10", price);
            await SaveCandleAsync(cleanAsset, "s15", price);
            await SaveCandleAsync(cleanAsset, "s30", price);

            // FIX D-3: Also update the in-memory live accumulator so GetRecentCandles
            // can append the current open candle without waiting for DB flush.
            // Previously OnPriceUpdateAsync only wrote to DB, leaving _s5/_s10/_s15/_s30 always empty.
            long nowTicks = DateTime.UtcNow.Ticks;
            UpdateAccumulator(_s5,  cleanAsset, price, nowTicks, TimeSpan.FromSeconds(5).Ticks);
            UpdateAccumulator(_s10, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(10).Ticks);
            UpdateAccumulator(_s15, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(15).Ticks);
            UpdateAccumulator(_s30, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(30).Ticks);
        }

        private static void UpdateAccumulator(
            ConcurrentDictionary<string, CandleAccumulator> dict,
            string asset, double price, long nowTicks, long intervalTicks)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            var acc = dict.GetOrAdd(asset, _ => new CandleAccumulator());
            lock (acc)
            {
                // New candle interval started — reset the accumulator
                if (acc.OpenTime != openTime && acc.OpenTime != default)
                    acc.Reset(openTime);
                else if (acc.OpenTime == default)
                    acc.Reset(openTime);
                acc.AddTick(price);
            }
        }
    }
}