using System;
using ValutaBot.App.MiniApp.Telegram.Commands;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class PaperTradingServiceTests
{
    [Fact]
    public void ResolvePaperOutcome_ComputesExactPayoutProfitLossAndAtmTie()
    {
        var winBuy = PaperTradingService.ResolvePaperOutcome("BUY", 1.0, 92.0, 1.1000, 1.1003);
        Assert.Equal(("WIN", 0.92), winBuy);

        var lossBuy = PaperTradingService.ResolvePaperOutcome("BUY", 1.0, 92.0, 1.1000, 1.0997);
        Assert.Equal(("LOSS", -1.0), lossBuy);

        var winPut = PaperTradingService.ResolvePaperOutcome("PUT", 2.0, 80.0, 1.1000, 1.0998);
        Assert.Equal(("WIN", 1.6), winPut);

        var tieExact = PaperTradingService.ResolvePaperOutcome("BUY", 1.0, 92.0, 1.1000, 1.1000);
        Assert.Equal(("TIE", 0.0), tieExact);

        var invalidPrice = PaperTradingService.ResolvePaperOutcome("BUY", 1.0, 92.0, 0.0, 1.1000);
        Assert.Equal(("TIE", 0.0), invalidPrice);
    }

    [Fact]
    public void ComputeUpdatedAccountMetrics_TracksBalancePeakDrawdownAndStreaks()
    {
        // Initial $100 -> Win +$0.88 -> $100.88 (peak 100.88, dd 0, streak +1)
        var s1 = PaperTradingService.ComputeUpdatedAccountMetrics(
            100.0, 100.0, 0.0, 0, 0, 0, 0, 0, 0, 0, 0, "WIN", 0.88);
        Assert.Equal(100.88, s1.BalanceUsd);
        Assert.Equal(100.88, s1.PeakBalanceUsd);
        Assert.Equal(0.0, s1.MaxDrawdownUsd);
        Assert.Equal(1, s1.CurrentStreak);
        Assert.Equal(1, s1.BestStreak);

        // Loss -$1.00 -> $99.88 (peak stays 100.88, dd 1.00, streak -1)
        var s2 = PaperTradingService.ComputeUpdatedAccountMetrics(
            s1.BalanceUsd, s1.PeakBalanceUsd, s1.MaxDrawdownUsd,
            s1.TotalTrades, s1.Wins, s1.Losses, s1.Ties, s1.Gaps,
            s1.CurrentStreak, s1.BestStreak, s1.WorstStreak, "LOSS", -1.0);
        Assert.Equal(99.88, s2.BalanceUsd);
        Assert.Equal(100.88, s2.PeakBalanceUsd);
        Assert.Equal(1.0, s2.MaxDrawdownUsd);
        Assert.Equal(-1, s2.CurrentStreak);
        Assert.Equal(-1, s2.WorstStreak);

        // Tie $0.00 keeps streak and balance unchanged while counting Tie
        var s3 = PaperTradingService.ComputeUpdatedAccountMetrics(
            s2.BalanceUsd, s2.PeakBalanceUsd, s2.MaxDrawdownUsd,
            s2.TotalTrades, s2.Wins, s2.Losses, s2.Ties, s2.Gaps,
            s2.CurrentStreak, s2.BestStreak, s2.WorstStreak, "TIE", 0.0);
        Assert.Equal(99.88, s3.BalanceUsd);
        Assert.Equal(1, s3.Ties);
        Assert.Equal(-1, s3.CurrentStreak);
    }

    [Fact]
    public void EvaluateWeekendSimCandidate_UsesRawUncappedProbAndRespectsRiskGuards()
    {
        var strongBuyUncapped = new MLPythonService.MLPythonPrediction(
            Direction: "BUY",
            Confidence: 0.52,
            ModelVersion: "neural_quant_v6",
            Accuracy: 0.56,
            Auc: 0.57,
            NTrain: 5000,
            VarianceEstimate: null,
            RawConfidence: 0.52,
            HorizonCandles: 1,
            TopFeatures: null,
            RawUncappedProb: 0.61);

        var ok = PaperTradingService.EvaluateWeekendSimCandidate(
            strongBuyUncapped, "BUY", 52, requiredProbPct: 59, currentPayout: 84, isAssetOpen: true, isSubminuteImpulseOk: true);
        Assert.Equal((true, "BUY", 61), ok);

        var strongPutUncapped = strongBuyUncapped with { Direction = "PUT", RawUncappedProb = 0.37 }; // P(PUT)=0.63
        var okPut = PaperTradingService.EvaluateWeekendSimCandidate(
            strongPutUncapped, "PUT", 52, requiredProbPct: 59, currentPayout: 84, isAssetOpen: true, isSubminuteImpulseOk: true);
        Assert.Equal((true, "PUT", 63), okPut);

        var coinFlipUncapped = strongBuyUncapped with { RawUncappedProb = 0.52 };
        var rejectedCoin = PaperTradingService.EvaluateWeekendSimCandidate(
            coinFlipUncapped, "BUY", 52, requiredProbPct: 59, currentPayout: 84, isAssetOpen: true, isSubminuteImpulseOk: true);
        Assert.False(rejectedCoin.ShouldEnter);

        var rejectedPayout = PaperTradingService.EvaluateWeekendSimCandidate(
            strongBuyUncapped, "BUY", 52, requiredProbPct: 59, currentPayout: 62, isAssetOpen: true, isSubminuteImpulseOk: true);
        Assert.False(rejectedPayout.ShouldEnter);

        var rejectedNoise = PaperTradingService.EvaluateWeekendSimCandidate(
            strongBuyUncapped, "BUY", 52, requiredProbPct: 59, currentPayout: 84, isAssetOpen: true, isSubminuteImpulseOk: false);
        Assert.False(rejectedNoise.ShouldEnter);
    }

    [Theory]
    [InlineData(2, "TOKYO")]
    [InlineData(10, "LONDON")]
    [InlineData(14, "OVERLAP")]
    [InlineData(19, "NY")]
    [InlineData(22, "QUIET")]
    public void ComputeUtcSession_MapsHourRangesCorrectly(int utcHour, string expected)
    {
        var ts = new DateTime(2026, 11, 15, utcHour, 30, 0, DateTimeKind.Utc);
        Assert.Equal(expected, PaperTradingService.ComputeUtcSession(ts));
    }

    [Fact]
    public void AdminPaperCommand_HandlesPaperAndSimCommands()
    {
        var cmd = new AdminPaperCommand();
        Assert.True(cmd.CanHandle(1, "/paper", "/paper"));
        Assert.True(cmd.CanHandle(1, "/paper", "/paper reset"));
        Assert.True(cmd.CanHandle(1, "/sim", "/sim"));
        Assert.False(cmd.CanHandle(1, "/stats", "/stats"));
    }
}
