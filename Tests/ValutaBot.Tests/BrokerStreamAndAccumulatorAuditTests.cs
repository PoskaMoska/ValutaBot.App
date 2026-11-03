using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests
{
    public class BrokerStreamAndAccumulatorAuditTests
    {
        [Fact]
        public void CandleRingBuffer_DuplicateTimestamp_UpdatesInPlaceWithoutExpandingCount()
        {
            var ring = new RealtimeTickCollector.CandleRingBuffer(10);
            var t1 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 11, 3, 12, 0, 5, DateTimeKind.Utc);

            var c1 = new MiniAppController.OhlcCandle(1.0500, 1.0510, 1.0490, 1.0505, 10, t1);
            var c2 = new MiniAppController.OhlcCandle(1.0505, 1.0520, 1.0500, 1.0515, 15, t2);

            ring.Add(c1);
            ring.Add(c2);
            Assert.Equal(2, ring.Count);

            // Re-add c2 with updated close/volume
            var c2Updated = new MiniAppController.OhlcCandle(1.0505, 1.0530, 1.0500, 1.0525, 20, t2);
            ring.Add(c2Updated);

            Assert.Equal(2, ring.Count);
            var recent = ring.GetRecent(2);
            Assert.Equal(2, recent.Length);
            Assert.Equal(1.0525, recent[1].Close);
            Assert.Equal(1.0530, recent[1].High);
            Assert.Equal(20, recent[1].Volume);
        }

        [Fact]
        public void CandleRingBuffer_StaleTimestamp_IsRejected()
        {
            var ring = new RealtimeTickCollector.CandleRingBuffer(10);
            var t1 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 11, 3, 12, 0, 5, DateTimeKind.Utc);
            var tStale = new DateTime(2026, 11, 3, 11, 59, 55, DateTimeKind.Utc);

            ring.Add(new MiniAppController.OhlcCandle(1.0500, 1.0510, 1.0490, 1.0505, 10, t1));
            ring.Add(new MiniAppController.OhlcCandle(1.0505, 1.0520, 1.0500, 1.0515, 15, t2));

            // Attempt to add older candle
            ring.Add(new MiniAppController.OhlcCandle(1.0490, 1.0500, 1.0480, 1.0495, 8, tStale));

            Assert.Equal(2, ring.Count);
            var recent = ring.GetRecent(2);
            Assert.Equal(t1, recent[0].Timestamp);
            Assert.Equal(t2, recent[1].Timestamp);
        }

        [Fact]
        public async Task RealtimeTickCollector_LateOrOutdatedTick_NeverRewindsLiveAccumulator()
        {
            RealtimeTickCollector.ResetAllAccumulatorsAndRingBuffers();

            string asset = "EURUSD_otc";
            string candleKey = PriceFeed.CandleKey(asset); // EURUSD_PO

            // Send tick at 12:10:00 UTC
            var tBase = new DateTime(2026, 11, 3, 12, 10, 0, DateTimeKind.Utc);
            await RealtimeTickCollector.OnPriceUpdateAsync(candleKey, 1.0850, tBase.Ticks);

            // Send tick at 12:10:02 UTC (advancing High and Close)
            await RealtimeTickCollector.OnPriceUpdateAsync(candleKey, 1.0860, tBase.AddSeconds(2).Ticks);

            // Now send a late tick from 12:02:00 UTC (>5 minutes in the past)
            var tOld = tBase.AddMinutes(-8);
            await RealtimeTickCollector.OnPriceUpdateAsync(candleKey, 1.0700, tOld.Ticks);

            // Verify live candles are NOT rewound
            var candles = await RealtimeTickCollector.GetRecentCandles(asset, "s5", 10);
            Assert.NotEmpty(candles);
            var liveCandle = candles[^1];
            Assert.Equal(tBase, liveCandle.Timestamp);
            Assert.Equal(1.0850, liveCandle.Open);
            Assert.Equal(1.0860, liveCandle.High);
            Assert.Equal(1.0860, liveCandle.Close);
        }

        [Fact]
        public void PriceFeed_CanonicalKeyNormalization_IsStrictAndConsistent()
        {
            Assert.Equal("EURUSD_PO", PriceFeed.CandleKey("EURUSD"));
            Assert.Equal("EURUSD_PO", PriceFeed.CandleKey("EURUSD_otc"));
            Assert.Equal("EURUSD_PO", PriceFeed.CandleKey("EUR/USD OTC"));
            Assert.Equal("EURUSD_PO", PriceFeed.CandleKey("EUR/USD ОТС")); // Cyrillic
            Assert.Equal("EURUSD_PO", PriceFeed.CandleKey("EURUSD_PO"));
            Assert.Equal("EURUSD", PriceFeed.BaseAsset("EURUSD_PO"));
        }
    }
}
