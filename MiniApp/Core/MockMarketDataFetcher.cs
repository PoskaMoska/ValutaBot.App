using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.MiniApp;

namespace ValutaBot.MiniApp.Tests
{
    public class MockMarketDataFetcher : MarketDataFetcher
    {
        private readonly MiniAppController.OhlcCandle[] _candles;
        public MockMarketDataFetcher()
        {
            var r = new Random(42);
            double price = 1.1000;
            _candles = new MiniAppController.OhlcCandle[200];
            for(int i=0; i<200; i++) {
                double change = (r.NextDouble() - 0.5) * 0.0010;
                double o = price;
                double c = price + change;
                double h = Math.Max(o, c) + r.NextDouble() * 0.0005;
                double l = Math.Min(o, c) - r.NextDouble() * 0.0005;
                _candles[i] = new MiniAppController.OhlcCandle(o, h, l, c, r.Next(100, 1000), DateTime.UtcNow.AddMinutes(i - 200));
                price = c;
            }
        }
        public override Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
        {
            return Task.FromResult(_candles.TakeLast(limit).ToArray());
        }
    }
}
