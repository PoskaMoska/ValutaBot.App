using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class CircuitBreakerServiceTests
{
    [Fact]
    public void EnableSymbolQuarantine_DefaultsToFalse()
    {
        var settings = new TradingBotSettings();
        Assert.False(settings.EnableSymbolQuarantine);
    }

    [Fact]
    public void IsSymbolQuarantined_ReturnsFalse_WhenDisabledByDefault()
    {
        var settings = new TradingBotSettings();
        var cb = new CircuitBreakerService(Options.Create(settings), () => null!);

        // Populate internal quarantine dictionary to prove disabled flag overrides any active quarantine entry
        var field = typeof(CircuitBreakerService).GetField("_symbolQuarantine", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = field!.GetValue(cb) as ConcurrentDictionary<string, (DateTime QuarantinedUntil, string Reason)>;
        Assert.NotNull(dict);
        dict!["EURUSD_s5"] = (DateTime.UtcNow.AddMinutes(30), "3 consecutive losses on EURUSD_s5");
        dict["EURUSD"] = (DateTime.UtcNow.AddMinutes(30), "Pair quarantine");

        bool quarantined = cb.IsSymbolQuarantined("EURUSD_otc", "s5", out var reason);

        Assert.False(quarantined);
        Assert.Null(reason);
    }

    [Fact]
    public void IsSymbolQuarantined_ReturnsTrue_WhenExplicitlyEnabled()
    {
        var settings = new TradingBotSettings { EnableSymbolQuarantine = true };
        var cb = new CircuitBreakerService(Options.Create(settings), () => null!);

        var field = typeof(CircuitBreakerService).GetField("_symbolQuarantine", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var dict = field!.GetValue(cb) as ConcurrentDictionary<string, (DateTime QuarantinedUntil, string Reason)>;
        Assert.NotNull(dict);
        dict!["EURUSD_s5"] = (DateTime.UtcNow.AddMinutes(30), "3 consecutive losses on EURUSD_s5");

        bool quarantined = cb.IsSymbolQuarantined("EURUSD", "s5", out var reason);

        Assert.True(quarantined);
        Assert.NotNull(reason);
        Assert.Contains("SYMBOL_QUARANTINE", reason);
    }

    [Fact]
    public async Task CheckSymbolStateAsync_DoesNotPopulateQuarantine_WhenDisabled()
    {
        var settings = new TradingBotSettings();
        var cb = new CircuitBreakerService(Options.Create(settings), () => null!);

        await cb.CheckSymbolStateAsync("EURUSD", "s5", latestWasWin: false, isShadow: false);

        var field = typeof(CircuitBreakerService).GetField("_symbolQuarantine", BindingFlags.NonPublic | BindingFlags.Instance);
        var dict = field!.GetValue(cb) as ConcurrentDictionary<string, (DateTime QuarantinedUntil, string Reason)>;
        Assert.NotNull(dict);
        Assert.Empty(dict!);
    }

    [Fact]
    public async Task ActivateHaltAsync_NotifiesAdminOncePerActiveHalt()
    {
        var settings = new TradingBotSettings { CircuitBreakerCooldownMinutes = 15 };
        var cb = new CircuitBreakerService(Options.Create(settings), () => null!);
        var captured = new ConcurrentQueue<string>();
        string marker = $"streak-{Guid.NewGuid():N}";
        Action<string> observer = msg =>
        {
            if (msg.Contains(marker, StringComparison.Ordinal))
                captured.Enqueue(msg);
        };

        BotLogger.AlertObserverForTests += observer;
        try
        {
            await cb.ActivateHaltAsync(marker);
            await cb.ActivateHaltAsync(marker + "-duplicate");

            Assert.True(cb.IsHalted());
            Assert.Single(captured);
            Assert.Contains("[CircuitBreaker]", captured.ToArray()[0]);
        }
        finally
        {
            BotLogger.AlertObserverForTests -= observer;
        }
    }

    [Fact]
    public void IsHalted_WhenCooldownExpires_NotifiesRecoveryOnce()
    {
        var settings = new TradingBotSettings { CircuitBreakerCooldownMinutes = 15 };
        var cb = new CircuitBreakerService(Options.Create(settings), () => null!);

        var haltedUntilField = typeof(CircuitBreakerService).GetField("_haltedUntil", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(haltedUntilField);
        DateTime expiredTime = DateTime.UtcNow.AddSeconds(-2);
        haltedUntilField!.SetValue(cb, (DateTime?)expiredTime);

        string expectedStamp = expiredTime.ToString("HH:mm:ss");
        var captured = new ConcurrentQueue<string>();
        Action<string> observer = msg =>
        {
            if (msg.Contains("[CircuitBreaker]", StringComparison.Ordinal) && msg.Contains(expectedStamp, StringComparison.Ordinal))
                captured.Enqueue(msg);
        };

        BotLogger.AlertObserverForTests += observer;
        try
        {
            Assert.False(cb.IsHalted());
            Assert.False(cb.IsHalted());
            Assert.Single(captured);
        }
        finally
        {
            BotLogger.AlertObserverForTests -= observer;
        }
    }

    [Fact]
    public void Defaults_PreventVarianceHaltsOnThreeLossesOrTinySamples()
    {
        var settings = new TradingBotSettings();
        Assert.Equal(25, settings.CircuitBreakerWindowSize);
        Assert.Equal(9, settings.CircuitBreakerMaxConsecutiveLosses);
        Assert.Equal(0.32, settings.CircuitBreakerMinWinRate);
        Assert.Equal(5, settings.CircuitBreakerCooldownMinutes);
    }
}
