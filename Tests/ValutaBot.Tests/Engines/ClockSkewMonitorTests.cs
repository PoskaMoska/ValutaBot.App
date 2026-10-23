using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Engines;

[Collection("ClockSkewMonitor")] // static state: never run in parallel with other users of the monitor
public class ClockSkewMonitorTests
{
    private static (List<string> alerts, DateTime now) Arrange()
    {
        ClockSkewMonitor.Reset();
        var alerts = new List<string>();
        ClockSkewMonitor.AlertSink = msg => { lock (alerts) alerts.Add(msg); return Task.CompletedTask; };
        return (alerts, new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc));
    }

    private static void Feed(DateTime now, double skewSeconds, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var t = now.AddSeconds(-skewSeconds);
            ClockSkewMonitor.Record(t.Ticks, now); // constant receive time -> skew is exactly skewSeconds; evaluation throttled to 60s
        }
    }

    private static async Task Settle() => await Task.Delay(50); // alert delivery is fire-and-forget

    [Fact]
    public async Task NormalNetworkDelay_NoAlert()
    {
        var (alerts, now) = Arrange();
        Feed(now, 0.3, 200);
        var snap = ClockSkewMonitor.Evaluate(now.AddSeconds(1));
        await Settle();

        Assert.NotNull(snap);
        Assert.InRange(snap!.MedianMs, 250, 350);
        Assert.False(snap.Alerting);
        Assert.Empty(alerts);
    }

    [Fact]
    public async Task FutureTimestamps_AlertMentionsDanger()
    {
        var (alerts, now) = Arrange();
        Feed(now, -3600, 200); // candles stamped one hour in the future
        var snap = ClockSkewMonitor.Evaluate(now.AddSeconds(1));
        await Settle();

        Assert.True(snap!.Alerting);
        Assert.Single(alerts);
        Assert.Contains("БУДУЩЕМ", alerts[0]);
        Assert.Contains("ServerTimeOffsetSec", alerts[0]); // 1h shift hints at timezone/DST constant
    }

    [Fact]
    public async Task PastTimestamps_AlertOnce_ThenRecovers()
    {
        var (alerts, now) = Arrange();
        Feed(now, 60, 200);
        ClockSkewMonitor.Evaluate(now.AddSeconds(1));
        ClockSkewMonitor.Evaluate(now.AddSeconds(2)); // repeated evaluation must not re-alert
        await Settle();
        Assert.Single(alerts);

        // Offset fixed: window refills with healthy ticks -> recovery notice
        ClockSkewMonitor.Reset();
        // Reset clears alert state, so emulate recovery in one monitor lifetime instead:
        var (alerts2, now2) = Arrange();
        Feed(now2, 60, 200);
        ClockSkewMonitor.Evaluate(now2.AddSeconds(1));
        Feed(now2, 0.2, 600); // ring capacity 512 -> fully replaced
        var snap = ClockSkewMonitor.Evaluate(now2.AddSeconds(2));
        await Settle();

        Assert.False(snap!.Alerting);
        Assert.Equal(2, alerts2.Count);
        Assert.Contains("normalized", alerts2[1]);
    }

    [Fact]
    public async Task TooFewSamples_NeverAlerts()
    {
        var (alerts, now) = Arrange();
        Feed(now, 600, 20);
        var snap = ClockSkewMonitor.Evaluate(now.AddSeconds(1));
        await Settle();

        Assert.False(snap!.Alerting);
        Assert.Empty(alerts);
    }
}
