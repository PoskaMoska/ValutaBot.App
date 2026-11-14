using System;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class WeekendShadowExperimentTests
{
    private static MiniAppController.OhlcCandle Candle(double open, double close) =>
        new(open, Math.Max(open, close) + 0.0001, Math.Min(open, close) - 0.0001, close, 10.0, DateTime.UtcNow, 50.0);

    [Theory]
    [InlineData(2026, 11, 13, 20, 43, 59, false)] // Friday before 20:44 UTC
    [InlineData(2026, 11, 13, 20, 44, 0, true)]   // Friday exact start
    [InlineData(2026, 11, 14, 14, 0, 0, true)]    // Saturday mid-day
    [InlineData(2026, 11, 15, 20, 59, 59, true)]  // Sunday before 21:00 UTC
    [InlineData(2026, 11, 15, 21, 0, 0, false)]   // Sunday exact FX reopen
    [InlineData(2026, 11, 18, 12, 0, 0, false)]   // Wednesday weekday
    public void IsWeekendOtcWindowUtc_MatchesExactUtcBoundaries(
        int year, int month, int day, int hour, int minute, int second, bool expected)
    {
        var utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        Assert.Equal(expected, WeekendShadowExperimentService.IsWeekendOtcWindowUtc(utc));
    }

    [Fact]
    public void EvaluateMeanReversion_ReversesConsecutiveSameSignStreak()
    {
        var bullStreak = new[]
        {
            Candle(1.1000, 1.0998), // down breaks earlier history
            Candle(1.0998, 1.1001),
            Candle(1.1001, 1.1004),
            Candle(1.1004, 1.1007),
        };
        var (dirBull, streakBull) = WeekendShadowExperimentService.EvaluateMeanReversion(bullStreak);
        Assert.Equal("PUT", dirBull);
        Assert.Equal(3, streakBull);

        var bearStreak = new[]
        {
            Candle(1.1000, 1.1003),
            Candle(1.1003, 1.1000),
            Candle(1.1000, 1.0996),
        };
        var (dirBear, streakBear) = WeekendShadowExperimentService.EvaluateMeanReversion(bearStreak);
        Assert.Equal("BUY", dirBear);
        Assert.Equal(2, streakBear);

        var singleAfterFlat = new[]
        {
            Candle(1.1000, 1.1002),
            Candle(1.1002, 1.1002), // flat doji stops streak lookback
            Candle(1.1002, 1.1005),
        };
        var (dirFlat, streakFlat) = WeekendShadowExperimentService.EvaluateMeanReversion(singleAfterFlat);
        Assert.Equal("NEUTRAL", dirFlat);
        Assert.Equal(1, streakFlat);
    }

    [Theory]
    [InlineData(58.0, "PUT")]
    [InlineData(55.0, "PUT")]
    [InlineData(42.0, "BUY")]
    [InlineData(45.0, "BUY")]
    [InlineData(50.0, "NEUTRAL")]
    [InlineData(double.NaN, "NEUTRAL")]
    public void EvaluateCrowdContrarian_AppliesThresholdsSafely(double sentiment, string expected)
    {
        Assert.Equal(expected, WeekendShadowExperimentService.EvaluateCrowdContrarian(sentiment));
    }

    [Fact]
    public void EvaluateBasketDesync_HandlesDirectAndInverseUsdPairs()
    {
        // Strong USD rally across the basket (+3.0 bps/min, sync=0.83)
        var dxyUp = new DollarBasketMetrics(DxyMomentum1mBps: 3.0, DxyMomentum5mBps: 5.0, BasketSyncScore: 0.83);

        // Inverse USD pair (EURUSD) lagged/rose slightly instead of dropping -> expect catch-up PUT
        var eurusdCandles = new[]
        {
            Candle(1.1000, 1.1000),
            Candle(1.1000, 1.10005),
            Candle(1.10005, 1.10010),
        };
        Assert.Equal("PUT", WeekendShadowExperimentService.EvaluateBasketDesync("EURUSD", eurusdCandles, 30, dxyUp));

        // Direct USD pair (USDCAD) fell against surging USD basket -> expect catch-up BUY
        var usdcadCandles = new[]
        {
            Candle(1.4000, 1.4000),
            Candle(1.4000, 1.3999),
            Candle(1.3999, 1.3998),
        };
        Assert.Equal("BUY", WeekendShadowExperimentService.EvaluateBasketDesync("USDCAD_PO", usdcadCandles, 30, dxyUp));

        // Already moving strongly in sync with USD rally (EURUSD dropped -4 bps) -> NEUTRAL
        var eurusdAlreadyMoved = new[]
        {
            Candle(1.1000, 1.1000),
            Candle(1.1000, 1.09978),
            Candle(1.09978, 1.09956), // -4 bps over 2x30s=60s
        };
        Assert.Equal("NEUTRAL", WeekendShadowExperimentService.EvaluateBasketDesync("EURUSD", eurusdAlreadyMoved, 30, dxyUp));

        // Low basket sync -> NEUTRAL
        var weakSync = new DollarBasketMetrics(DxyMomentum1mBps: 3.0, DxyMomentum5mBps: 3.0, BasketSyncScore: 0.50);
        Assert.Equal("NEUTRAL", WeekendShadowExperimentService.EvaluateBasketDesync("USDCAD", usdcadCandles, 30, weakSync));
    }

    [Fact]
    public void EvaluateHypotheses_PreservesRawUncappedProbOverWeekendClamp()
    {
        var candles = new[]
        {
            Candle(1.1000, 1.1001),
            Candle(1.1001, 1.1002),
            Candle(1.1002, 1.1003),
            Candle(1.1003, 1.1004),
        };
        // Confidence is weekend-clamped to 0.52, while RawUncappedProb carries uncapped weekday P(BUY) = 0.64
        var pred = new MLPythonService.MLPythonPrediction(
            Direction: "BUY",
            Confidence: 0.52,
            ModelVersion: "neural_quant_v6",
            Accuracy: 0.55,
            Auc: 0.56,
            NTrain: 5000,
            VarianceEstimate: null,
            RawConfidence: 0.52,
            HorizonCandles: 1,
            TopFeatures: null,
            RawUncappedProb: 0.64);
        var dxy = new DollarBasketMetrics(DxyMomentum1mBps: 0.1, DxyMomentum5mBps: 0.2, BasketSyncScore: 0.3);

        var snap = WeekendShadowExperimentService.EvaluateHypotheses("EURUSD", 15, candles, pred, 62.5, dxy);

        Assert.Equal("BUY", snap.WeekdayModelDir);
        Assert.Equal(0.64, snap.WeekdayModelProb);
        Assert.Equal("PUT", snap.MeanRevDir);
        Assert.Equal(4, snap.MeanRevStreak);
        Assert.Equal("PUT", snap.CrowdContraDir);
        Assert.Equal(62.5, snap.CrowdSentiment);
        Assert.Equal("NEUTRAL", snap.BasketDesyncDir);
    }

    [Fact]
    public void ResolveOutcome_ClassifiesWinLossTieNeutralAndInvalidPrices()
    {
        var buyWin = WeekendShadowExperimentService.ResolveOutcome(1.1000, 1.1002, "BUY");
        Assert.Equal(("BUY", (bool?)true), buyWin);

        var buyLoss = WeekendShadowExperimentService.ResolveOutcome(1.1000, 1.0998, "BUY");
        Assert.Equal(("PUT", (bool?)false), buyLoss);

        var tie = WeekendShadowExperimentService.ResolveOutcome(1.1000, 1.1000, "BUY");
        Assert.Equal(("TIE", (bool?)null), tie);

        var neutral = WeekendShadowExperimentService.ResolveOutcome(1.1000, 1.1002, "NEUTRAL");
        Assert.Equal(("BUY", (bool?)null), neutral);

        var invalid = WeekendShadowExperimentService.ResolveOutcome(0.0, 1.1002, "BUY");
        Assert.Equal(("TIE", (bool?)null), invalid);
    }
}
