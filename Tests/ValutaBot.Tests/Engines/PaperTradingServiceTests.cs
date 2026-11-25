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

        // Low-payout asset (74% -> BE=57.47% -> EV floor=62%): 59% rejected for negative/thin EV, 63% allowed (no hard payout ban)
        var rejectedNegativeEv = PaperTradingService.EvaluateWeekendSimCandidate(
            strongBuyUncapped with { RawUncappedProb = 0.59 }, "BUY", 52, requiredProbPct: 58, currentPayout: 74, isAssetOpen: true, isSubminuteImpulseOk: true);
        Assert.False(rejectedNegativeEv.ShouldEnter);

        var acceptedHighConvLowPayout = PaperTradingService.EvaluateWeekendSimCandidate(
            strongBuyUncapped with { RawUncappedProb = 0.63 }, "BUY", 52, requiredProbPct: 58, currentPayout: 74, isAssetOpen: true, isSubminuteImpulseOk: true);
        Assert.Equal((true, "BUY", 63), acceptedHighConvLowPayout);
    }

    [Theory]
    [InlineData(5, 0.05)]
    [InlineData(10, 0.08)]
    [InlineData(15, 0.15)]
    [InlineData(30, 0.10)]
    [InlineData(60, 0.40)]
    public void GetMaxAdverseSlippageBps_ScalesStrictlyWithCandleInterval(int intervalSecs, double expectedMaxBps)
    {
        Assert.Equal(expectedMaxBps, PaperTradingService.GetMaxAdverseSlippageBps(intervalSecs), precision: 4);

        // Verify 5s candle gate rejects +0.20 bps adverse drift while allowing <=0.05 bps
        if (intervalSecs == 5)
        {
            Assert.False(PaperTradingService.EvaluatePostDelayFill("BUY", 1.00000, 1.00002, expectedMaxBps).Accepted);
            Assert.True(PaperTradingService.EvaluatePostDelayFill("BUY", 1.00000, 1.00000, expectedMaxBps).Accepted);
        }
    }

    [Fact]
    public void TryAcquireCooldownSlot_PreventsSimultaneousBursts()
    {
        var store = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        Assert.True(PaperTradingService.TryAcquireCooldownSlot("WEEKEND_SIM:ASSET:EURUSD_otc", 15.0, store));
        Assert.False(PaperTradingService.TryAcquireCooldownSlot("WEEKEND_SIM:ASSET:EURUSD_otc", 15.0, store));
        Assert.True(PaperTradingService.TryAcquireCooldownSlot("WEEKEND_SIM:ASSET:GBPUSD_otc", 15.0, store));
        Assert.True(PaperTradingService.TryAcquireCooldownSlot("WEEKDAY:ASSET:EURUSD_otc", 15.0, store));
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

    [Fact]
    public void EvaluatePostDelayFill_EnforcesAdverseSlippageGateAndUsesPostDelayFillPrice()
    {
        // BUY: +2.0 bps upward slippage -> adverse > 1.5 bps -> requote rejected
        var rejectedBuy = PaperTradingService.EvaluatePostDelayFill("BUY", 1.00000, 1.00020);
        Assert.False(rejectedBuy.Accepted);
        Assert.True(rejectedBuy.AdverseBps > 1.5);

        // BUY: +1.0 bps upward slippage -> within 1.5 bps -> accepted at actual post-delay fill price
        var acceptedBuy = PaperTradingService.EvaluatePostDelayFill("BUY", 1.00000, 1.00010);
        Assert.True(acceptedBuy.Accepted);
        Assert.Equal(1.00010, acceptedBuy.FillPrice);

        // BUY: favorable downward dip (-3.0 bps) -> accepted at better fill price
        var favorableBuy = PaperTradingService.EvaluatePostDelayFill("BUY", 1.00000, 0.99970);
        Assert.True(favorableBuy.Accepted);
        Assert.Equal(0.99970, favorableBuy.FillPrice);
        Assert.True(favorableBuy.AdverseBps < 0);

        // PUT: -2.0 bps downward slippage -> adverse > 1.5 bps -> rejected
        var rejectedPut = PaperTradingService.EvaluatePostDelayFill("PUT", 1.00000, 0.99980);
        Assert.False(rejectedPut.Accepted);

        // PUT: favorable +2.5 bps upward pop -> accepted at post-delay fill price
        var favorablePut = PaperTradingService.EvaluatePostDelayFill("PUT", 1.00000, 1.00025);
        Assert.True(favorablePut.Accepted);
        Assert.Equal(1.00025, favorablePut.FillPrice);

        // Invalid quote rejected
        var invalid = PaperTradingService.EvaluatePostDelayFill("BUY", 1.00000, 0.0);
        Assert.False(invalid.Accepted);
    }

    [Fact]
    public void TryFindTickPriceAtOrBefore_HandlesExactTickFlatMarketColdStartAndStaleFeed()
    {
        long t0 = new DateTime(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc).Ticks;
        var samples = new RealtimeTickCollector.LiveTickSample[]
        {
            new(t0, 1.10000),
            new(t0 + TimeSpan.FromSeconds(2.2).Ticks, 1.10012),
            new(t0 + TimeSpan.FromSeconds(5.8).Ticks, 1.10025),
        };

        // Target at +5.250s (between +2.2s and +5.8s): newer tick at +5.8s confirms +2.2s held across expiry
        Assert.True(RealtimeTickCollector.TryFindTickPriceAtOrBefore(
            samples, t0 + TimeSpan.FromSeconds(5.25).Ticks, streamIsFlowingNow: false, out double heldPrice));
        Assert.Equal(1.10012, heldPrice);

        // Exact tick match
        Assert.True(RealtimeTickCollector.TryFindTickPriceAtOrBefore(
            samples, t0 + TimeSpan.FromSeconds(2.2).Ticks, streamIsFlowingNow: false, out double exactPrice));
        Assert.Equal(1.10012, exactPrice);

        // Flat market past last tick (+7.0s) when live stream is actively flowing -> uses +5.8s price
        Assert.True(RealtimeTickCollector.TryFindTickPriceAtOrBefore(
            samples, t0 + TimeSpan.FromSeconds(7.0).Ticks, streamIsFlowingNow: true, out double flatLivePrice));
        Assert.Equal(1.10025, flatLivePrice);

        // Same +7.0s when stream is NOT flowing -> refuses to settle blindly
        Assert.False(RealtimeTickCollector.TryFindTickPriceAtOrBefore(
            samples, t0 + TimeSpan.FromSeconds(7.0).Ticks, streamIsFlowingNow: false, out _));

        // Target before oldest buffered tick (cold restart) -> returns false so DB s5 fallback handles it
        Assert.False(RealtimeTickCollector.TryFindTickPriceAtOrBefore(
            samples, t0 - TimeSpan.FromSeconds(1.0).Ticks, streamIsFlowingNow: true, out _));

        // Stale >30s gap before target -> returns false
        Assert.False(RealtimeTickCollector.TryFindTickPriceAtOrBefore(
            samples, t0 + TimeSpan.FromSeconds(45.0).Ticks, streamIsFlowingNow: true, out _));
    }

    [Fact]
    public async System.Threading.Tasks.Task OnPriceUpdateAsync_RecordsLiveTicksAndExcludesBackfillForExactExpiryLookup()
    {
        const string testAsset = "EURUSD_otc";
        RealtimeTickCollector.ResetAssetAccumulator(testAsset);

        long baseTicks = DateTime.UtcNow.AddSeconds(-4).Ticks;
        await RealtimeTickCollector.OnPriceUpdateAsync(testAsset, 1.09500, baseTicks, isBackfill: true);
        await RealtimeTickCollector.OnPriceUpdateAsync(testAsset, 1.09510, baseTicks + TimeSpan.FromSeconds(1).Ticks, isBackfill: false);
        await RealtimeTickCollector.OnPriceUpdateAsync(testAsset, 1.09525, baseTicks + TimeSpan.FromSeconds(3).Ticks, isBackfill: false);

        // Backfill tick at baseTicks is excluded, so lookup at baseTicks + 0.5s finds nothing in live tick history
        Assert.False(RealtimeTickCollector.TryGetTickPriceAtOrBefore(
            testAsset, new DateTime(baseTicks + TimeSpan.FromSeconds(0.5).Ticks, DateTimeKind.Utc), streamIsFlowingNow: true, out _));

        // Lookup at baseTicks + 2.5s resolves exact prior live tick (1.09510)
        Assert.True(RealtimeTickCollector.TryGetTickPriceAtOrBefore(
            testAsset, new DateTime(baseTicks + TimeSpan.FromSeconds(2.5).Ticks, DateTimeKind.Utc), streamIsFlowingNow: false, out double liveClose));
        Assert.Equal(1.09510, liveClose);

        RealtimeTickCollector.ResetAssetAccumulator(testAsset);
    }

    [Fact]
    public void EvaluateAdaptiveEntryPolicy_AppliesDynamicEvMarginsWithoutHardBans()
    {
        var londonUtc = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc);
        var overlapUtc = new DateTime(2026, 11, 17, 14, 0, 0, DateTimeKind.Utc);

        // Low payout (<75%) requires BE + 6.5% (65.32% at 70%): block rounded 64.8%->65, allow true 65.6%->66
        var lowPayBorderline = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            70, "s5", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.648, consensusProbabilityPct: 65);
        Assert.False(lowPayBorderline.MeetsProbabilityGate);
        Assert.True(lowPayBorderline.RequiredExactPct > 65.3);

        var lowPayStrong = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            70, "s5", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.656, consensusProbabilityPct: 66);
        Assert.True(lowPayStrong.MeetsProbabilityGate);

        // Stage-2 dampeners (e.g. flat noise cap=52) must still block even if raw ML was high
        var dampened = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            85, "s5", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.66, consensusProbabilityPct: 52);
        Assert.False(dampened.MeetsProbabilityGate);

        // s10 in LONDON keeps high-payout (>=85%) 62.2% entries, while weak low-payout (76%) 63.0% is filtered naturally
        var s10HighPay = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            90, "s10", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.622, consensusProbabilityPct: 62);
        Assert.True(s10HighPay.MeetsProbabilityGate);

        var s10LowPay = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            76, "s10", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.630, consensusProbabilityPct: 63);
        Assert.False(s10LowPay.MeetsProbabilityGate);

        // OVERLAP session raises EV hurdle (+2%) on >=s10 (s10 at 85% needs ~63.55%), without throttling profitable OVERLAP s5 setups
        var s10Overlap = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            85, "s10", overlapUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.630, consensusProbabilityPct: 63);
        Assert.False(s10Overlap.MeetsProbabilityGate);
        Assert.Equal("OVERLAP", s10Overlap.Session);

        var s5Overlap = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            82, "s5", overlapUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.625, consensusProbabilityPct: 63);
        Assert.True(s5Overlap.MeetsProbabilityGate);

        // CHAOS_EXPANSION adds gentle +1.5% EV margin without hard-blocking strong 62.6%+ signals at high payout
        var chaosBorderlineLowPay = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            75, "s5", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.635, consensusProbabilityPct: 64, marketRegime: "CHAOS_EXPANSION");
        Assert.False(chaosBorderlineLowPay.MeetsProbabilityGate);
        Assert.True(chaosBorderlineLowPay.RequiredExactPct > 64.0);

        var chaosStrong = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            83, "s5", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.630, consensusProbabilityPct: 63, marketRegime: "CHAOS_EXPANSION");
        Assert.True(chaosStrong.MeetsProbabilityGate);

        // s30 blocks borderline 57.5% at mediocre 82% payout (BE 54.95% + 3.0% = 57.95%),
        // while high-payout 92% s30 (56.5%) and m1 (54.5% > BE 52.08% + 2.0% = 54.08%) are allowed cleanly.
        var s30WeakBlocked = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            82, "s30", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.575, consensusProbabilityPct: 58);
        Assert.False(s30WeakBlocked.MeetsProbabilityGate);
        Assert.True(s30WeakBlocked.RequiredExactPct >= 57.9);

        var s30HighPayAllowed = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            92, "s30", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.565, consensusProbabilityPct: 57);
        Assert.True(s30HighPayAllowed.MeetsProbabilityGate);

        var m1LowPayBlocked = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            82, "m1", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.545, consensusProbabilityPct: 55);
        Assert.False(m1LowPayBlocked.MeetsProbabilityGate);

        var m1HighPayAllowed = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            92, "m1", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.545, consensusProbabilityPct: 55);
        Assert.True(m1HighPayAllowed.MeetsProbabilityGate);

        var m1StrongAllowed = ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator.EvaluateAdaptiveEntryPolicy(
            90, "m1", londonUtc, isQuantSubminute: true, mlDirectionalConfidence: 0.610, consensusProbabilityPct: 61);
        Assert.True(m1StrongAllowed.MeetsProbabilityGate);
    }
}
