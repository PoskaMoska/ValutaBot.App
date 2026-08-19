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
        
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s5 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s15 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s30 = new();

        private static Timer? _s5Timer;
        private static Timer? _s15Timer;
        private static Timer? _s30Timer;
        private static Timer? _pruneTimer;

        public static void Initialize()
        {
            DateTime now = DateTime.UtcNow;
            
            int msToNext5s = 5000 - (now.Millisecond + (now.Second % 5) * 1000);
            _s5Timer = new Timer(async _ => await FlushAsync(_s5, "s5"), null, msToNext5s, 5000);
            
            int msToNext15s = 15000 - (now.Millisecond + (now.Second % 15) * 1000);
            _s15Timer = new Timer(async _ => await FlushAsync(_s15, "s15"), null, msToNext15s, 15000);
            
            int msToNext30s = 30000 - (now.Millisecond + (now.Second % 30) * 1000);
            _s30Timer = new Timer(async _ => await FlushAsync(_s30, "s30"), null, msToNext30s, 30000);
            
            _pruneTimer = new Timer(async _ => await TickRepository.PruneOldCandlesAsync(14), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));
            BotLogger.Info("[TickCollector] Initialized real-time subminute candle accumulation.");
        }

        public static void OnPriceUpdate(string asset, double price)
        {
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "");
            if (cleanAsset.Contains("OTC")) return;
            
            UpdateAccumulator(_s5, cleanAsset, price);
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
                int count;
                DateTime openTime;

                lock (acc)
                {
                    if (acc.TickCount == 0 || !acc.Open.HasValue) continue;
                    open = acc.Open.Value;
                    high = acc.High;
                    low = acc.Low;
                    close = acc.Close;
                    count = acc.TickCount;
                    openTime = acc.OpenTime;
                    
                    acc.Reset(DateTime.UtcNow);
                }

                _ = TickRepository.SaveCandleAsync(asset, intervalName, openTime, open, high, low, close, count);
            }
        }
    }
}
