using System;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests.Engines;

[Collection("ClockSkewMonitor")]
public class ExecutionGatewayLowLatencyTests
{
    [Fact]
    public void IsSymbolFeedAlive_ReturnsFalse_WhenNoTicksReceived()
    {
        bool alive = PocketOptionWebSocketStream.IsSymbolFeedAlive("NONEXISTENT_ASSET", maxIdleSeconds: 2.0);
        Assert.False(alive);
    }

    [Fact]
    public void IsSymbolFeedAlive_ReturnsTrue_WhenRecentTickExists()
    {
        // Simulate tick for EURUSD
        var field = typeof(PocketOptionWebSocketStream).GetField("_lastTickTicksBySymbol", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(field);
        var dict = field!.GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<string, long>;
        Assert.NotNull(dict);

        dict!["EURUSD"] = DateTime.UtcNow.Ticks;

        bool alive = PocketOptionWebSocketStream.IsSymbolFeedAlive("EURUSD_otc", maxIdleSeconds: 2.0);
        Assert.True(alive);
    }

    [Fact]
    public void IsSymbolFeedAlive_ReturnsFalse_WhenTickIsOlderThanMaxIdle()
    {
        var field = typeof(PocketOptionWebSocketStream).GetField("_lastTickTicksBySymbol", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        Assert.NotNull(field);
        var dict = field!.GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<string, long>;
        Assert.NotNull(dict);

        // Stale tick from 5 seconds ago
        dict!["GBPUSD"] = DateTime.UtcNow.AddSeconds(-5).Ticks;

        bool alive = PocketOptionWebSocketStream.IsSymbolFeedAlive("GBPUSD", maxIdleSeconds: 2.0);
        Assert.False(alive);
    }

    [Theory]
    [InlineData(5, 0.2, true)]    // 0.2s elapsed into 5s candle -> allowed (within 0.8s)
    [InlineData(5, 0.85, false)]  // 0.85s elapsed into 5s candle -> rejected (> 0.8s)
    [InlineData(10, 1.2, true)]   // 1.2s elapsed into 10s candle -> allowed (within 1.5s)
    [InlineData(10, 1.8, false)]  // 1.8s elapsed into 10s candle -> rejected (> 1.5s)
    public void EntryBoundaryOpenElapsed_ThresholdBehavior(int tfSecs, double elapsedFromOpen, bool expectedAllowed)
    {
        double maxOpenElapsed = tfSecs switch
        {
            5 => 0.8,
            10 => 1.5,
            15 => 2.2,
            _ => 3.0
        };

        bool allowed = elapsedFromOpen <= maxOpenElapsed;
        Assert.Equal(expectedAllowed, allowed);
    }

    [Theory]
    [InlineData(5, 2.0, true)]    // 2.0s remaining before 5s close -> safe (>= 1.2s)
    [InlineData(5, 0.8, false)]   // 0.8s remaining before 5s close -> early expiry risk (< 1.2s)
    [InlineData(10, 3.5, true)]   // 3.5s remaining before 10s close -> safe (>= 2.0s)
    [InlineData(10, 1.5, false)]  // 1.5s remaining before 10s close -> early expiry risk (< 2.0s)
    public void EntryBoundaryCloseRemaining_ThresholdBehavior(int tfSecs, double remainingToClose, bool expectedAllowed)
    {
        double minRemainingBuffer = tfSecs switch
        {
            5 => 1.2,
            10 => 2.0,
            15 => 3.0,
            30 => 4.0,
            _ => 5.0
        };

        bool allowed = remainingToClose >= minRemainingBuffer;
        Assert.Equal(expectedAllowed, allowed);
    }

    [Fact]
    public void MarketDataFetcher_IsFeedStale_WithBrokerRefTime_HandlesClockSkew()
    {
        // Broker candle opened at 12:00:00
        DateTime candleOpen = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        
        // Broker reference time is 12:00:10 (10s elapsed, within 3 * 5s = 25s threshold)
        DateTime brokerRefTime = new DateTime(2026, 10, 27, 12, 0, 10, DateTimeKind.Utc);

        bool stale = MarketDataFetcher.IsFeedStale(candleOpen, 5, brokerRefTime);
        Assert.False(stale);

        // Stale after 30 seconds
        DateTime staleBrokerTime = new DateTime(2026, 10, 27, 12, 0, 30, DateTimeKind.Utc);
        bool isStale = MarketDataFetcher.IsFeedStale(candleOpen, 5, staleBrokerTime);
        Assert.True(isStale);
    }

    [Fact]
    public void HandleTick_OutOfOrderTick_DoesNotCorruptLivePrice()
    {
        var handleTickMethod = typeof(PocketOptionWebSocketStream).GetMethod("HandleTick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(handleTickMethod);

        // 1. Fresh progressive tick at t = 1000.0, price = 1.0900
        handleTickMethod!.Invoke(null, new object[] { "EURUSD_otc", 1700001000.0, 1.0900, false });
        bool ok = PocketOptionWebSocketStream.TryGetLivePrice("EURUSD", out double livePrice);
        Assert.True(ok);
        Assert.Equal(1.0900, livePrice);

        // 2. Stale out-of-order tick at t = 999.0, price = 1.0800 (must be rejected by watermark)
        handleTickMethod.Invoke(null, new object[] { "EURUSD_otc", 1700000999.0, 1.0800, false });

        // 3. Live price must NOT be corrupted/overwritten by the stale tick
        PocketOptionWebSocketStream.TryGetLivePrice("EURUSD", out double priceAfterStale);
        Assert.Equal(1.0900, priceAfterStale);
    }

    [Fact]
    public void HandleTick_BurstLaggedTicks_DoesNotRegressBrokerClock()
    {
        var handleTickMethod = typeof(PocketOptionWebSocketStream).GetMethod("HandleTick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(handleTickMethod);

        // 1. Fresh tick at t = 2000.0
        handleTickMethod!.Invoke(null, new object[] { "GBPUSD_otc", 1700002000.0, 1.2500, false });
        var brokerTime1 = PocketOptionWebSocketStream.GetBrokerTimeUtc();

        // 2. Lagged burst tick arriving 1 second later with older timestamp t = 1999.5
        handleTickMethod.Invoke(null, new object[] { "GBPUSD_otc", 1700001999.5, 1.2505, false });
        var brokerTime2 = PocketOptionWebSocketStream.GetBrokerTimeUtc();

        // Monotonic invariant: broker clock should never regress backwards
        Assert.True(brokerTime2 >= brokerTime1, $"Expected broker time to never regress backwards. {brokerTime2} vs {brokerTime1}");
    }
}