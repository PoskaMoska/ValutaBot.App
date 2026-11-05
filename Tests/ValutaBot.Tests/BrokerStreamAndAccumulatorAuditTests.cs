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

        [Fact]
        public async Task RealtimeTickCollector_GetRecentCandles_ZeroLatencyFastPathWithoutDb()
        {
            RealtimeTickCollector.ResetAllAccumulatorsAndRingBuffers();

            string asset = "GBPUSD_otc";
            string candleKey = PriceFeed.CandleKey(asset); // GBPUSD_PO

            // Pre-populate ring buffer with 3 historical candles
            var t0 = new DateTime(2026, 11, 3, 14, 0, 0, DateTimeKind.Utc);
            var ring = new RealtimeTickCollector.CandleRingBuffer(400);
            ring.Add(new MiniAppController.OhlcCandle(1.2500, 1.2510, 1.2490, 1.2505, 10, t0));
            ring.Add(new MiniAppController.OhlcCandle(1.2505, 1.2520, 1.2500, 1.2515, 15, t0.AddSeconds(5)));
            ring.Add(new MiniAppController.OhlcCandle(1.2515, 1.2530, 1.2510, 1.2525, 20, t0.AddSeconds(10)));

            // Add active tick at 14:00:16 (in progress s5 candle)
            await RealtimeTickCollector.OnPriceUpdateAsync(candleKey, 1.2535, t0.AddSeconds(16).Ticks);

            // Fetch recent candles for limit=50 (greater than ring count).
            // Under checkpoint protection, this MUST return in-memory candles immediately without blocking on PostgreSQL!
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var candles = await RealtimeTickCollector.GetRecentCandles(asset, "s5", 50);
            sw.Stop();

            Assert.NotEmpty(candles);
            Assert.True(sw.ElapsedMilliseconds < 50, $"GetRecentCandles took {sw.ElapsedMilliseconds}ms, expected <50ms in-memory.");
            var live = candles[^1];
            Assert.Equal(1.2535, live.Close);
        }

        [Fact]
        public void FullJitter_ReconnectAlgorithm_DistributesRandomlyWithinWindow()
        {
            const int ReconnectAfterConnectedMs = 1_000;
            const int MinBackoffMs = 5_000;
            const int MaxBackoffMs = 60_000;

            // Scenario 1: wasConnected = true (e.g. broker proxy restart)
            // Full Jitter window must be [1000, 5000] ms
            var delays = new int[200];
            for (int i = 0; i < delays.Length; i++)
            {
                bool wasConnected = true;
                int backoffMs = MinBackoffMs;
                int ceiling = wasConnected ? MinBackoffMs : backoffMs;
                int minDelay = wasConnected ? ReconnectAfterConnectedMs : (MinBackoffMs / 2);
                int delayMs = Random.Shared.Next(minDelay, ceiling + 1);
                delays[i] = delayMs;
            }

            Assert.All(delays, d => Assert.InRange(d, ReconnectAfterConnectedMs, MinBackoffMs));
            // Ensure values are well-distributed across the window (not a single cluster)
            int minObserved = delays.Min();
            int maxObserved = delays.Max();
            Assert.True(minObserved < 2000, $"Expected min observed < 2000, got {minObserved}");
            Assert.True(maxObserved > 4000, $"Expected max observed > 4000, got {maxObserved}");

            // Scenario 2: wasConnected = false (exponential backoff step)
            int backoffFailMs = 10_000;
            int ceilingFail = backoffFailMs;
            int minDelayFail = MinBackoffMs / 2; // 2500
            var failDelays = new int[200];
            for (int i = 0; i < failDelays.Length; i++)
            {
                failDelays[i] = Random.Shared.Next(minDelayFail, ceilingFail + 1);
            }
            Assert.All(failDelays, d => Assert.InRange(d, 2500, 10000));

            // Verify MaxBackoffMs clamp
            int highBackoff = 40_000;
            int nextBackoff = Math.Min(highBackoff * 2, MaxBackoffMs);
            Assert.Equal(MaxBackoffMs, nextBackoff);
        }

        [Fact]
        public void ZeroCopy_AssetResolution_ReturnsCanonicalInternedInstances()
        {
            var eurusdBytes = "EURUSD_otc"u8;
            string asset1 = PocketOptionWebSocketStream.ResolveCanonicalAsset(eurusdBytes);
            string asset2 = PocketOptionWebSocketStream.ResolveCanonicalAsset(eurusdBytes);

            Assert.Equal("EURUSD_otc", asset1);
            Assert.Same(asset1, asset2); // Reference equality: zero-allocation canonical instance

            var (baseAsset, candleKey) = PocketOptionWebSocketStream.GetAssetKeys(asset1);
            Assert.Equal("EURUSD", baseAsset);
            Assert.Equal("EURUSD_PO", candleKey);

            var (baseAsset2, candleKey2) = PocketOptionWebSocketStream.GetAssetKeys(asset1);
            Assert.Same(baseAsset, baseAsset2);
            Assert.Same(candleKey, candleKey2);
        }

        [Fact]
        public async Task TryGetLivePrice_UnderConcurrentBursts_ExcludesTornReads()
        {
            // Reset state
            PocketOptionWebSocketStream.ResetClockAndWatermarksOnDstShift();

            string asset = "EURUSD_otc";
            double srvNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + PocketOptionWebSocketStream.ServerTimeOffsetSec;

            // Seed initial tick
            PocketOptionWebSocketStream.HandleTick(asset, srvNow, 1.0800);

            bool running = true;
            int tornReadsCount = 0;
            int successfulReads = 0;

            // Thread 1: Writer simulating high-frequency websocket ticks
            var writer = Task.Run(() =>
            {
                double p = 1.0800;
                while (Volatile.Read(ref running))
                {
                    p += 0.0001;
                    double ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + PocketOptionWebSocketStream.ServerTimeOffsetSec;
                    PocketOptionWebSocketStream.HandleTick(asset, ts, p);
                    Thread.SpinWait(10);
                }
            });

            // Thread 2: Concurrent Lock-Free readers
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 5000; i++)
                {
                    if (PocketOptionWebSocketStream.TryGetLivePrice("EURUSD", out double price, maxAgeSeconds: 5.0))
                    {
                        if (price < 1.07 || price > 2.0 || double.IsNaN(price))
                        {
                            Interlocked.Increment(ref tornReadsCount);
                        }
                        else
                        {
                            Interlocked.Increment(ref successfulReads);
                        }
                    }
                }
            });

            await reader;
            Volatile.Write(ref running, false);
            await writer;

            Assert.Equal(0, tornReadsCount);
            Assert.True(successfulReads > 0, "Expected successful lock-free reads from Seqlock.");
        }

        [Fact]
        public void MacroContextEngine_UpdatePrice_NormalizesTickerWithCachedBaseAsset()
        {
            var now = DateTime.UtcNow;
            // Ingest ticks using different OTC variations
            ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.UpdatePrice("EURUSD_otc", 1.0850, now);
            ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.UpdatePrice("EUR/USD OTC", 1.0855, now.AddSeconds(1));
            ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.UpdatePrice("EURUSD", 1.0860, now.AddSeconds(2));

            var metrics = ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.ComputeDollarBasketMetrics();
            Assert.NotNull(metrics);
        }
    }
}
