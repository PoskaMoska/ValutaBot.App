using System;
using System.Collections.Concurrent;
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
}
