using System;
using System.Collections.Generic;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class SubminuteNoiseGatekeeperTests
{
    private static List<MiniAppController.OhlcCandle> CreateCandles(double basePrice, double pipRange, int count = 20, double trendSlope = 0.0)
    {
        var list = new List<MiniAppController.OhlcCandle>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < count; i++)
        {
            double jitter = (pipRange > 0) ? ((i % 2 == 0) ? 0.000005 : -0.000005) : 0.0;
            double center = basePrice + (i * trendSlope) + jitter;
            list.Add(new MiniAppController.OhlcCandle(
                center - (pipRange / 2),
                center + pipRange,
                center - pipRange,
                center + (pipRange / 2),
                10.0,
                now.AddSeconds(i * 10)
            ));
        }
        return list;
    }

    [Fact]
    public void InsufficientCandles_ReturnsFalse()
    {
        var candles = CreateCandles(1.1000, 0.00010, count: 5);
        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.False(result.IsTradeableImpulse);
        Assert.Contains("Недостаточно", result.RegimeDescription);
    }

    [Fact]
    public void DeadFlatZeroSpread_ReturnsUntradeable()
    {
        // 0-range candles (absolute dead market)
        var candles = CreateCandles(1.1000, 0.0, count: 20);
        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.False(result.IsTradeableImpulse);
        Assert.True(result.LocalAtrBps <= 0.0001);
    }

    [Fact]
    public void LowAmplitudeBelowNoiseFloor_ReturnsUntradeable()
    {
        // 0.2 bps candles (below 1.0 bps broker floor for s10)
        var candles = CreateCandles(1.1000, 0.00002, count: 20);
        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.False(result.IsTradeableImpulse);
        Assert.True(result.LocalAtrBps < result.NoiseFloorBps);
        Assert.Contains("Шумовой флэт", result.RegimeDescription);
    }

    [Fact]
    public void HighAmplitudeImpulse_ReturnsTradeableImpulse()
    {
        // 2.5 bps candles with strong directional trend (well above 1.0 bps floor)
        var candles = CreateCandles(1.1000, 0.00025, count: 20, trendSlope: 0.00008);
        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.True(result.LocalAtrBps >= result.NoiseFloorBps);
        Assert.True(result.IsTradeableImpulse);
        Assert.Contains("Импульс подтвержден", result.RegimeDescription);
    }

    [Fact]
    public void BullishWickRejection_DetectsLongLowerShadow()
    {
        var candles = CreateCandles(1.1000, 0.00025, count: 20, trendSlope: 0.00005);
        // Replace last candle with a clear pinbar / bullish rejection wick:
        // Open: 1.1010, Close: 1.1012, High: 1.1013, Low: 1.1000 -> Lower wick: 1.1010 - 1.1000 = 0.0010 (77% of range)
        candles[^1] = new MiniAppController.OhlcCandle(1.1010, 1.1013, 1.1000, 1.1012, 15.0, DateTime.UtcNow);

        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.True(result.HasBullishWick, "Expected Bullish Wick rejection on hammer/pinbar candle");
        Assert.False(result.HasBearishWick);
    }

    [Fact]
    public void BearishWickRejection_DetectsLongUpperShadow()
    {
        var candles = CreateCandles(1.1000, 0.00025, count: 20, trendSlope: -0.00005);
        // Replace last candle with shooting star / bearish rejection wick:
        // Open: 1.1002, Close: 1.1000, High: 1.1012, Low: 1.0999 -> Upper wick: 1.1012 - 1.1002 = 0.0010 (77% of range)
        candles[^1] = new MiniAppController.OhlcCandle(1.1002, 1.1012, 1.0999, 1.1000, 15.0, DateTime.UtcNow);

        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.True(result.HasBearishWick, "Expected Bearish Wick rejection on shooting star candle");
        Assert.False(result.HasBullishWick);
    }

    [Fact]
    public void ExtremeSpikeAnomaly_IsMarkedUntradeableAndBlocksWick()
    {
        var candles = CreateCandles(1.1000, 0.00020, count: 20);
        // Replace last candle with an extreme 500 bps flash spike
        candles[^1] = new MiniAppController.OhlcCandle(1.1000, 1.1500, 1.0990, 1.1450, 50.0, DateTime.UtcNow);

        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.False(result.IsTradeableImpulse, "Extreme spike should NOT be classified as a tradeable impulse");
        Assert.False(result.HasBullishWick);
        Assert.False(result.HasBearishWick);
        Assert.Contains("Аномальный спайк", result.RegimeDescription);
    }

    [Fact]
    public void SymmetricDoji_DoesNotTriggerWickRejection()
    {
        var candles = CreateCandles(1.1000, 0.00020, count: 20);
        // Equal upper and lower wicks (spinning top / doji)
        candles[^1] = new MiniAppController.OhlcCandle(1.1005, 1.1015, 1.0995, 1.1005, 15.0, DateTime.UtcNow);

        var result = SubminuteNoiseGatekeeper.AuditCandleStream(candles, "s10");

        Assert.False(result.HasBullishWick, "Symmetric doji should not trigger Bullish Wick");
        Assert.False(result.HasBearishWick, "Symmetric doji should not trigger Bearish Wick");
    }
}
