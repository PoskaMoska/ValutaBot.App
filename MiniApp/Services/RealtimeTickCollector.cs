using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.App.MiniApp.Data;

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

        private record struct TickEvent(string Asset, string Interval, double Price, DateTime OpenTime);

        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s5  = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s10 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s15 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s30 = new();

        private static readonly Channel<TickEvent> _tickChannel = Channel.CreateUnbounded<TickEvent>();
        private static int _isInitialized = 0;

        public static async Task InitializeAsync()
        {
            if (Interlocked.Exchange(ref _isInitialized, 1) == 0)
            {
                _ = Task.Run(ProcessTickQueueAsync);
                BotLogger.Info("[TickCollector] Initialized continuous real-time subminute candle accumulation (Batched).");
            }
        }

        public static void Initialize()
        {
            InitializeAsync().GetAwaiter().GetResult();
        }

        private static async Task ProcessTickQueueAsync()
        {
            var buffer = new List<TickEvent>(1000);
            while (await _tickChannel.Reader.WaitToReadAsync())
            {
                buffer.Clear();
                while (buffer.Count < 1000 && _tickChannel.Reader.TryRead(out var tick))
                {
                    buffer.Add(tick);
                }

                if (buffer.Count > 0)
                {
                    try
                    {
                        using var conn = DbConnectionFactory.GetConnection();
                        await conn.OpenAsync();
                        using var tx = conn.BeginTransaction();
                        
                        // Aggregate in-memory before saving to reduce DB commands
                        var grouped = buffer.GroupBy(t => new { t.Asset, t.Interval, t.OpenTime })
                            .Select(g => new {
                                Asset = g.Key.Asset,
                                Interval = g.Key.Interval,
                                OpenTime = g.Key.OpenTime.ToString("o"),
                                Open = g.First().Price,
                                High = g.Max(t => t.Price),
                                Low = g.Min(t => t.Price),
                                Close = g.Last().Price,
                                Volume = g.Count()
                            });

                        foreach (var batchItem in grouped)
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO subminute_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume)
                                VALUES (@Asset, @Interval, @OpenTime, @Open, @High, @Low, @Close, @Volume)
                                ON CONFLICT (asset, interval, open_time) DO UPDATE SET
                                    high_price  = GREATEST(subminute_candles.high_price,  EXCLUDED.high_price),
                                    low_price   = LEAST(subminute_candles.low_price,      EXCLUDED.low_price),
                                    close_price = EXCLUDED.close_price,
                                    volume      = subminute_candles.volume + EXCLUDED.volume;
                            ", batchItem, tx);
                        }
                        
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Warn($"[TickSync] Failed to batch save ticks: {ex.Message}");
                    }
                }
                
                // Throttle batch commits to every 500ms
                await Task.Delay(500);
            }
        }

        public static async Task<MiniAppController.OhlcCandle[]> GetRecentCandles(string asset, string interval, int limit)
        {
            try
            {
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
                int dbRecordsToTake = liveAcc != null ? Math.Min(records.Count, limit - 1) : Math.Min(records.Count, limit);
                
                for (int i = dbRecordsToTake - 1; i >= 0; i--)
                {
                    var r = records[i]; 
                    result[resultIdx++] = new MiniAppController.OhlcCandle((double)r.Open, (double)r.High, (double)r.Low, (double)r.Close, (double)r.Volume,
                        DateTime.Parse((string)r.OpenTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
                }

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

        public static Task OnPriceUpdateAsync(string asset, double price)
        {
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
            long nowTicks = DateTime.UtcNow.Ticks;

            QueueTick(cleanAsset, "s5", price, nowTicks, TimeSpan.FromSeconds(5).Ticks);
            QueueTick(cleanAsset, "s10", price, nowTicks, TimeSpan.FromSeconds(10).Ticks);
            QueueTick(cleanAsset, "s15", price, nowTicks, TimeSpan.FromSeconds(15).Ticks);
            QueueTick(cleanAsset, "s30", price, nowTicks, TimeSpan.FromSeconds(30).Ticks);

            UpdateAccumulator(_s5,  cleanAsset, price, nowTicks, TimeSpan.FromSeconds(5).Ticks);
            UpdateAccumulator(_s10, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(10).Ticks);
            UpdateAccumulator(_s15, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(15).Ticks);
            UpdateAccumulator(_s30, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(30).Ticks);
            
            return Task.CompletedTask;
        }

        private static void QueueTick(string asset, string interval, double price, long nowTicks, long intervalTicks)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            _tickChannel.Writer.TryWrite(new TickEvent(asset, interval, price, openTime));
        }

        private static void UpdateAccumulator(
            ConcurrentDictionary<string, CandleAccumulator> dict,
            string asset, double price, long nowTicks, long intervalTicks)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            var acc = dict.GetOrAdd(asset, _ => new CandleAccumulator());
            lock (acc)
            {
                if (acc.OpenTime != openTime && acc.OpenTime != default)
                    acc.Reset(openTime);
                else if (acc.OpenTime == default)
                    acc.OpenTime = openTime;

                acc.AddTick(price);
            }
        }
    }
}
