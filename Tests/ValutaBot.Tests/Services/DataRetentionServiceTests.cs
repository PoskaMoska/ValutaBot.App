using System;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Services;

public class DataRetentionServiceTests
{
    [Theory]
    [InlineData(2497.0, StorageZone.Green)]
    [InlineData(3599.9, StorageZone.Green)]
    [InlineData(3600.0, StorageZone.Yellow)]
    [InlineData(3999.9, StorageZone.Yellow)]
    [InlineData(4000.0, StorageZone.Red)]
    [InlineData(4349.9, StorageZone.Red)]
    [InlineData(4350.0, StorageZone.Emergency)]
    public void ClassifyStorageZone_MatchesContinuousWatermarkThresholds(double dbSizeMb, StorageZone expected)
    {
        Assert.Equal(expected, DataRetentionService.ClassifyStorageZone(dbSizeMb));
    }

    [Fact]
    public void ComputeWatermarkSubminuteCutoffUtc_GreenZonePreservesThirtyDays()
    {
        var now = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);
        DateTime? cutoff = DataRetentionService.ComputeWatermarkSubminuteCutoffUtc(StorageZone.Green, now, now.AddDays(-10));
        Assert.Equal(now.AddDays(-30), cutoff);
    }

    [Fact]
    public void ComputeWatermarkSubminuteCutoffUtc_YellowSteps12hAndNeverCutsBelowFourDays()
    {
        var now = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);

        // 15 days of history in Yellow: step forward only 12 hours from the oldest edge
        DateTime oldest15d = now.AddDays(-15);
        DateTime? stepped = DataRetentionService.ComputeWatermarkSubminuteCutoffUtc(StorageZone.Yellow, now, oldest15d);
        Assert.Equal(oldest15d.AddHours(12), stepped);

        // Almost at the 4-day floor (4d + 3h): clamp cleanly to the 4-day floor
        DateTime nearFloor = now.AddDays(-4).AddHours(-3);
        DateTime? clamped = DataRetentionService.ComputeWatermarkSubminuteCutoffUtc(StorageZone.Yellow, now, nearFloor);
        Assert.Equal(now.AddDays(-4), clamped);

        // Inside protected 4-day window (3.5d): do not trim anything
        DateTime? insideProtected = DataRetentionService.ComputeWatermarkSubminuteCutoffUtc(StorageZone.Yellow, now, now.AddHours(-84));
        Assert.Null(insideProtected);
    }

    [Fact]
    public void ComputeWatermarkSubminuteCutoffUtc_RedSteps24hAndReturnsNullWhenOldestMissing()
    {
        var now = new DateTime(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);

        Assert.Null(DataRetentionService.ComputeWatermarkSubminuteCutoffUtc(StorageZone.Red, now, null));

        DateTime oldest10d = now.AddDays(-10);
        DateTime? stepped = DataRetentionService.ComputeWatermarkSubminuteCutoffUtc(StorageZone.Red, now, oldest10d);
        Assert.Equal(oldest10d.AddHours(24), stepped);
    }
}
