using System;
using System.Diagnostics;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class TelemetryAlertingTests
{
    [Fact]
    public void ParseAdminChatIds_HandlesCommaSpaceAndInvalidTokens()
    {
        long[] parsed = BotLogger.ParseAdminChatIds(
            "123456789",
            "123456789, 987654321; not_a_number -5 0 555666777",
            null,
            "   ");

        Assert.Equal(3, parsed.Length);
        Assert.Contains(123456789L, parsed);
        Assert.Contains(987654321L, parsed);
        Assert.Contains(555666777L, parsed);
    }

    [Fact]
    public void SanitizeMarkdownCodeBlock_ReplacesTripleBackticksAndCapsLength()
    {
        string raw = "Error ```nested``` stack\n" + new string('x', 4200);
        string sanitized = BotLogger.SanitizeMarkdownCodeBlock(raw);

        Assert.DoesNotContain("```", sanitized, StringComparison.Ordinal);
        Assert.Contains("'''nested'''", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length <= 4003);
        Assert.EndsWith("...", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateFeedStallTransition_HonorsGraceAlertThrottlingAndRecovery()
    {
        long freq = Stopwatch.Frequency;
        long t0 = 1_000_000L;
        bool alertActive = false;
        DateTime lastAlertUtc = DateTime.MinValue;
        DateTime now = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

        // Startup within grace period (< 120s) must not trigger false alarm
        var aStartupOk = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: false, lastRealTickSw: 0, streamStartedSw: t0, currentSw: t0 + 60 * freq,
            utcNow: now, ref alertActive, ref lastAlertUtc, out _);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.None, aStartupOk);
        Assert.False(alertActive);

        // Live ticks arriving normally keep system silent
        long tickSw = t0 + 70 * freq;
        var aHealthy = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: false, lastRealTickSw: tickSw, streamStartedSw: t0, currentSw: tickSw + 15 * freq,
            utcNow: now.AddSeconds(85), ref alertActive, ref lastAlertUtc, out double idleHealthy);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.None, aHealthy);
        Assert.Equal(15.0, idleHealthy, precision: 2);

        // Feed silent >= 90s triggers RaiseAlert once
        var aStall1 = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: false, lastRealTickSw: tickSw, streamStartedSw: t0, currentSw: tickSw + 95 * freq,
            utcNow: now.AddSeconds(165), ref alertActive, ref lastAlertUtc, out double idleStall);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.RaiseAlert, aStall1);
        Assert.True(alertActive);
        Assert.Equal(95.0, idleStall, precision: 2);

        // Next check 15s later stays throttled (no spam)
        var aStallRepeatSuppressed = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: false, lastRealTickSw: tickSw, streamStartedSw: t0, currentSw: tickSw + 110 * freq,
            utcNow: now.AddSeconds(180), ref alertActive, ref lastAlertUtc, out _);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.None, aStallRepeatSuppressed);

        // Repeat reminder fires only after 30 minutes (1800s)
        var aStallReminder = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: false, lastRealTickSw: tickSw, streamStartedSw: t0, currentSw: tickSw + 1900 * freq,
            utcNow: now.AddSeconds(1966), ref alertActive, ref lastAlertUtc, out _);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.RaiseAlert, aStallReminder);

        // Fresh live tick restores stream -> triggers SendRecovery once and clears active alert
        long recoveredTickSw = tickSw + 1905 * freq;
        var aRecovered = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: false, lastRealTickSw: recoveredTickSw, streamStartedSw: t0, currentSw: recoveredTickSw + 1 * freq,
            utcNow: now.AddSeconds(1972), ref alertActive, ref lastAlertUtc, out _);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.SendRecovery, aRecovered);
        Assert.False(alertActive);

        // Explicit STOPPED stream never raises stall alerts
        var aStopped = PocketOptionWebSocketStream.EvaluateFeedStallTransition(
            isStopped: true, lastRealTickSw: 0, streamStartedSw: t0, currentSw: t0 + 500 * freq,
            utcNow: now.AddSeconds(2000), ref alertActive, ref lastAlertUtc, out _);
        Assert.Equal(PocketOptionWebSocketStream.FeedStallAction.None, aStopped);
    }
}
