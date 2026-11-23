using System;
using ValutaBot.MiniApp;
using Xunit;
using static ValutaBot.MiniApp.RealtimeTickCollector;

namespace ValutaBot.Tests.Services;

public class RealtimeTickCollectorSubsecondTests
{
    [Fact]
    public void ComputeSubsecondMetricsFromSamples_EmptyReturnsSentinel()
    {
        var refUtc = new DateTime(2026, 11, 23, 19, 0, 0, DateTimeKind.Utc);
        var metrics = ComputeSubsecondMetricsFromSamples(ReadOnlySpan<LiveTickSample>.Empty, refUtc.Ticks);

        Assert.Equal(0, metrics.Ticks1_5s);
        Assert.Equal(0, metrics.Ticks5s);
        Assert.Equal(0, metrics.Ticks15s);
        Assert.Equal(0.0, metrics.AccelRatio1_5s);
        Assert.Equal(0.0, metrics.Ret1_5sBps);
        Assert.Equal(0.0, metrics.Ret5sBps);
        Assert.Equal(0.0, metrics.DirPurity5s);
        Assert.Equal(-1.0, metrics.LastTickAgeMs);
    }

    [Fact]
    public void ComputeSubsecondMetricsFromSamples_AcceleratingBullishImpulse()
    {
        var refUtc = new DateTime(2026, 11, 23, 19, 0, 0, DateTimeKind.Utc);
        long t0 = refUtc.Ticks;

        LiveTickSample[] samples =
        [
            new(t0 - TimeSpan.FromSeconds(10.0).Ticks, 1.0998),
            new(t0 - TimeSpan.FromSeconds(5.2).Ticks, 1.1000),  // 5s baseline
            new(t0 - TimeSpan.FromSeconds(2.5).Ticks, 1.1002),  // 1.5s baseline (also inside 5s)
            new(t0 - TimeSpan.FromSeconds(1.2).Ticks, 1.1004),
            new(t0 - TimeSpan.FromSeconds(0.8).Ticks, 1.1006),
            new(t0 - TimeSpan.FromSeconds(0.4).Ticks, 1.1009),
            new(t0 - TimeSpan.FromSeconds(0.1).Ticks, 1.1011),  // +10.0 bps over 5s
        ];

        var metrics = ComputeSubsecondMetricsFromSamples(samples, t0);

        Assert.Equal(4, metrics.Ticks1_5s);
        Assert.Equal(5, metrics.Ticks5s);
        Assert.Equal(7, metrics.Ticks15s);
        // rate1_5 = 4/1.5 = 2.6667/s; rate5 = 5/5.0 = 1.0/s => ratio = 2.667
        Assert.Equal(2.667, metrics.AccelRatio1_5s);
        Assert.Equal(10.0, metrics.Ret5sBps);
        Assert.True(metrics.Ret1_5sBps > 8.0 && metrics.Ret1_5sBps < 8.5);
        Assert.Equal(1.0, metrics.DirPurity5s);
        Assert.Equal(100.0, metrics.LastTickAgeMs);
    }

    [Fact]
    public void ComputeSubsecondMetricsFromSamples_ChoppyAndStaleFeedsBeHonest()
    {
        var refUtc = new DateTime(2026, 11, 23, 19, 0, 0, DateTimeKind.Utc);
        long t0 = refUtc.Ticks;

        LiveTickSample[] choppy =
        [
            new(t0 - TimeSpan.FromSeconds(5.2).Ticks, 1.1000),
            new(t0 - TimeSpan.FromSeconds(3.0).Ticks, 1.1005),
            new(t0 - TimeSpan.FromSeconds(2.0).Ticks, 1.0995),
            new(t0 - TimeSpan.FromSeconds(0.5).Ticks, 1.1000),
        ];
        var choppyMetrics = ComputeSubsecondMetricsFromSamples(choppy, t0);
        Assert.Equal(0.0, choppyMetrics.Ret5sBps);
        Assert.Equal(0.0, choppyMetrics.DirPurity5s);

        LiveTickSample[] stale =
        [
            new(t0 - TimeSpan.FromSeconds(6.0).Ticks, 1.1000),
            new(t0 - TimeSpan.FromSeconds(4.2).Ticks, 1.1003),
        ];
        var staleMetrics = ComputeSubsecondMetricsFromSamples(stale, t0);
        Assert.Equal(0, staleMetrics.Ticks1_5s);
        Assert.Equal(1, staleMetrics.Ticks5s);
        Assert.Equal(0.0, staleMetrics.Ret1_5sBps);
        Assert.Equal(4200.0, staleMetrics.LastTickAgeMs);
    }
}
