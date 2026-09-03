using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;

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
        
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s5  = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s10 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s15 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s30 = new();

        private static Timer? _s5Timer;
        private static Timer? _s10Timer;
        private static Timer? _s15Timer;
        private static Timer? _s30Timer;
        private static Timer? _pruneTimer;

        public static void Initialize()
        {
            DateTime now = DateTime.UtcNow;
            
            // W-24 FIX: async void lambdas in Timer crash the process on exception. Wrapped in safe task runner.
            Action<ConcurrentDictionary<string, CandleAccumulator>, string> safeFlush = (dict, interval) =>
            {
                _ = Task.Run(async () => {
                    try { await FlushAsync(dict, interval); }
                    catch (Exception ex) { Console.WriteLine($"[TickCollector] Flush error {interval}: {ex.Message}"); }
                });
            };

            int msToNext5s = Math.Max(1, 5000 - (now.Millisecond + (now.Second % 5) * 1000));
            _s5Timer = new Timer(_ => safeFlush(_s5, "s5"), null, msToNext5s, 5000);

            int msToNext10s = Math.Max(1, 10000 - (now.Millisecond + (now.Second % 10) * 1000));
            _s10Timer = new Timer(_ => safeFlush(_s10, "s10"), null, msToNext10s, 10000);
            
            int msToNext15s = Math.Max(1, 15000 - (now.Millisecond + (now.Second % 15) * 1000));
            _s15Timer = new Timer(_ => safeFlush(_s15, "s15"), null, msToNext15s, 15000);
            
            int msToNext30s = Math.Max(1, 30000 - (now.Millisecond + (now.Second % 30) * 1000));
            _s30Timer = new Timer(_ => safeFlush(_s30, "s30"), null, msToNext30s, 30000);
            
            _pruneTimer = new Timer(async _ => await TickRepository.PruneOldCandlesAsync(14), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));
            BotLogger.Info("[TickCollector] Initialized real-time subminute candle accumulation.");
        }

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

                var result = new MiniAppController.OhlcCandle[records.Count];
                // Convert reverse order (DESC) back to chronological
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[records.Count - 1 - i]; // Reverse back
                    result[i] = new MiniAppController.OhlcCandle((double)r.Open, (double)r.High, (double)r.Low, (double)r.Close, (double)r.Volume,
                        // FIX H-1: Parse with AdjustToUniversal to prevent timezone shift.
                        // DateTime.Parse on ISO 8601 "Z" strings converts to local time by default.
                        DateTime.Parse((string)r.OpenTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
                }
                return result;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[RealtimeTickCollector] Failed to fetch recent {interval} candles for {asset}: {ex.Message}");
                return Array.Empty<MiniAppController.OhlcCandle>();
            }
        }

        public static void OnPriceUpdate(string asset, double price)
        {
            // FIX W-25: OTC ticks were silently dropped here, so no subminute candles were built
            // for EURUSD_OTC, GBPUSD_OTC etc. → SGD /feedback for OTC pairs always had empty candles.
            // Fix: strip _OTC suffix so ticks are accumulated under the base symbol key (EURUSD etc.)
            // which aligns with the model key used in training and feedback.
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");

            UpdateAccumulator(_s5,  cleanAsset, price);
            UpdateAccumulator(_s10, cleanAsset, price);
            UpdateAccumulator(_s15, cleanAsset, price);
            UpdateAccumulator(_s30, cleanAsset, price);
        }

        private static void UpdateAccumulator(ConcurrentDictionary<string, CandleAccumulator> dict, string asset, double price)
        {
            var acc = dict.GetOrAdd(asset, _ => new CandleAccumulator { OpenTime = DateTime.UtcNow });
            lock (acc)
            {
                acc.AddTick(price);
            }
        }

        private static async Task FlushAsync(ConcurrentDictionary<string, CandleAccumulator> dict, string intervalName)
        {
            foreach (var kvp in dict)
            {
                var asset = kvp.Key;
                var acc = kvp.Value;
                
                double open, high, low, close;
                DateTime openTime;
                double tickVolume;

                lock (acc)
                {
                    if (acc.TickCount == 0 || !acc.Open.HasValue) 
                    {
                        // Emit a flat candle using the last known price to prevent timeframe gaps!
                        if (ValutaBot.MiniApp.SignalTracker._livePrices.TryGetValue(asset, out double lastPrice))
                        {
                            open = lastPrice; high = lastPrice; low = lastPrice; close = lastPrice;
                            openTime = acc.OpenTime;
                            tickVolume = 0;
                            acc.Reset(DateTime.UtcNow);
                            _ = TickRepository.SaveCandleAsync(asset, intervalName, openTime, open, high, low, close, tickVolume);
                        }
                        continue;
                    }
                    
                    open = acc.Open.Value;
                    high = acc.High;
                    low = acc.Low;
                    close = acc.Close;
                    openTime = acc.OpenTime;
                    tickVolume = acc.TickCount;
                    
                    acc.Reset(DateTime.UtcNow);
                }
                
                _ = TickRepository.SaveCandleAsync(asset, intervalName, openTime, open, high, low, close, tickVolume);
            }
        }
    }
}
