using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.MiniApp;

namespace ValutaBot.MiniApp.Tests
{
    public class MockMarketDataFetcher : MarketDataFetcher
    {
        public override Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
        {
            var candles = new MiniAppController.OhlcCandle[200];
            string sym = originalAsset ?? symbol ?? "";
            
            if (sym.Contains("TEST_DEAD"))
            {
                for(int i = 0; i < 200; i++)
                    candles[i] = new MiniAppController.OhlcCandle(1.0, 1.0, 1.0, 1.0, 0, DateTime.UtcNow.AddMinutes(i - 200));
                return Task.FromResult(candles.TakeLast(limit).ToArray());
            }

            var r = new Random(42);
            double price = 1.1000;
            for(int i = 0; i < 200; i++) 
            {
                double change = (r.NextDouble() - 0.5) * 0.0010;
                
                if (sym.Contains("TEST_TREND")) 
                    change = 0.0005; // Constant upward trend
                else if (sym.Contains("TEST_REVERSAL")) 
                    change = i > 180 ? -0.0020 : 0.0005; // Upward then sharp crash

                double o = price;
                double c = price + change;
                double h = Math.Max(o, c) + (sym.Contains("TEST_DEAD") ? 0 : r.NextDouble() * 0.0005);
                double l = Math.Min(o, c) - (sym.Contains("TEST_DEAD") ? 0 : r.NextDouble() * 0.0005);
                double vol = sym.Contains("TEST_REVERSAL") && i > 180 ? 5000 : r.Next(100, 1000);
                
                candles[i] = new MiniAppController.OhlcCandle(o, h, l, c, vol, DateTime.UtcNow.AddMinutes(i - 200));
                price = c;
            }
            return Task.FromResult(candles.TakeLast(limit).ToArray());
        }
    }
}
