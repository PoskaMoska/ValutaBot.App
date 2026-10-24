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
}
