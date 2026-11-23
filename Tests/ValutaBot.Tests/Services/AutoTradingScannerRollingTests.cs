using System;
using System.Collections.Generic;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Services;

public class AutoTradingScannerRollingTests
{
    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(5, 10, true)]
    [InlineData(10, 10, false)]
    [InlineData(15, 10, true)]
    [InlineData(20, 10, false)]
    [InlineData(25, 10, true)]
    [InlineData(0, 15, false)]
    [InlineData(5, 15, true)]
    [InlineData(10, 15, true)]
    [InlineData(15, 15, false)]
    [InlineData(20, 15, true)]
    [InlineData(25, 15, true)]
    [InlineData(30, 15, false)]
    public void ShouldEnqueueRollingStepFromS5_EnqueuesOnlyOffGrid5sPhases(int openSecondMod60, int targetTfSecs, bool expected)
    {
        // closedS5OpenUtc is openTime of the just-closed s5 bar; close time is +5s
        var baseUtc = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var closedOpenUtc = baseUtc.AddSeconds((openSecondMod60 - 5 + 60) % 60);
        if (openSecondMod60 == 0) closedOpenUtc = baseUtc.AddSeconds(-5);

        bool actual = AutoTradingScannerService.ShouldEnqueueRollingStepFromS5(closedOpenUtc, targetTfSecs);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildRollingCandlesFromS5_ShiftedS10AndS15_MatchExactPhaseOhlcvAndSkipGaps()
    {
        var anchorCloseUtc = new DateTime(2026, 11, 23, 12, 1, 5, DateTimeKind.Utc); // :05 phase for s10, :05 phase for s15
        var s5 = new List<MiniAppController.OhlcCandle>();
        // Construct contiguous closed s5 bars from 12:00:00..12:01:00 (13 bars, last closes at 12:01:05)
        // Deliberately omit 12:00:15 to verify partial gap bucket handling.
        for (int s = 0; s <= 60; s += 5)
        {
            if (s == 15) continue;
            var ts = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc).AddSeconds(s);
            double px = 1.1000 + s * 0.0001;
            s5.Add(new MiniAppController.OhlcCandle(px, px + 0.00005, px - 0.00003, px + 0.00002, 10.0, ts, 55.0));
        }
        // Append an unclosed future live s5 bar opening at 12:01:05 (must be stripped)
        s5.Add(new MiniAppController.OhlcCandle(1.9999, 2.0000, 1.9900, 1.9999, 99.0, anchorCloseUtc, 50.0));

        var s10Rolling = AutoTradingScannerService.BuildRollingCandlesFromS5(s5.ToArray(), 10, anchorCloseUtc, 160);
        // Expected complete s10 buckets on phase=5 inside [12:00:00..12:01:05]:
        // [12:00:05..12:00:15] (incomplete due to missing 12:00:15? No, [12:00:05, 12:00:10] are both present!)
        // Wait: 12:00:15 belongs to [12:00:15..12:00:25] which is incomplete -> skipped.
        // Last bar must be open=12:00:55 (bars 12:00:55 and 12:01:00, close=12:01:05).
        Assert.NotEmpty(s10Rolling);
        var last10 = s10Rolling[^1];
        Assert.Equal(new DateTime(2026, 11, 23, 12, 0, 55, DateTimeKind.Utc), last10.Timestamp);
        Assert.Equal(20.0, last10.Volume, 6);
        Assert.Equal(1.1055, last10.Open, 6);
        Assert.Equal(1.10602, last10.Close, 6);
        Assert.DoesNotContain(s10Rolling, c => c.Timestamp == new DateTime(2026, 11, 23, 12, 0, 15, DateTimeKind.Utc));

        var s15AnchorUtc = new DateTime(2026, 11, 23, 12, 1, 10, DateTimeKind.Utc); // :10 phase for s15 -> last bucket open=12:00:55 (bars 55, 00, 05)
        var s5For15 = new List<MiniAppController.OhlcCandle>(s5);
        s5For15[^1] = new MiniAppController.OhlcCandle(1.1065, 1.1067, 1.1064, 1.1066, 12.0, anchorCloseUtc, 62.0);
        var s15Rolling = AutoTradingScannerService.BuildRollingCandlesFromS5(s5For15.ToArray(), 15, s15AnchorUtc, 160);
        Assert.NotEmpty(s15Rolling);
        var last15 = s15Rolling[^1];
        Assert.Equal(new DateTime(2026, 11, 23, 12, 0, 55, DateTimeKind.Utc), last15.Timestamp);
        Assert.Equal(32.0, last15.Volume, 6);
        Assert.Equal(1.1055, last15.Open, 6);
        Assert.Equal(1.1066, last15.Close, 6);
        Assert.Equal(1.1067, last15.High, 6);
    }

    [Fact]
    public void ResolveAnchorBarOpenUtc_PreservesRollingPhaseInsteadOfClockModulo()
    {
        var rollingLastClosedOpen = new DateTime(2026, 11, 23, 12, 0, 55, DateTimeKind.Utc);
        var candles = new[]
        {
            new MiniAppController.OhlcCandle(1.10, 1.11, 1.09, 1.105, 10, rollingLastClosedOpen)
        };
        // Inference finishes at 12:01:05.450 -> bar open must stay 12:01:05.000 (not 12:01:00.000 modulo)
        var execRef = new DateTime(2026, 11, 23, 12, 1, 5, 450, DateTimeKind.Utc);
        var resolved = MarketAnalysisOrchestrator.ResolveAnchorBarOpenUtc(candles, 10, execRef);

        Assert.Equal(new DateTime(2026, 11, 23, 12, 1, 5, DateTimeKind.Utc), resolved);
        Assert.True((execRef - resolved).TotalSeconds < 0.6);
    }

    [Theory]
    [InlineData(true, "LONDON", 0.92, 32.0, true)]
    [InlineData(true, "OVERLAP", 0.86, 30.0, true)]
    [InlineData(true, "NY", 1.10, 41.5, true)]
    [InlineData(true, "TOKYO", 1.10, 41.5, false)]
    [InlineData(true, "QUIET", 1.10, 41.5, false)]
    [InlineData(true, "LONDON", 0.85, 35.0, false)]
    [InlineData(true, "LONDON", 1.20, 29.9, false)]
    [InlineData(false, "LONDON", 1.20, 40.0, false)]
    public void IsSubminuteDispersionDriftChop_MatchesEmpiricalSessionRegime(
        bool isSubminute, string session, double entropy, double adx, bool expectedBlocked)
    {
        bool actual = MarketAnalysisOrchestrator.IsSubminuteDispersionDriftChop(isSubminute, session, entropy, adx);
        Assert.Equal(expectedBlocked, actual);
    }
}
