using System;
using ValutaBot.MiniApp; // BotLogger
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.App.MiniApp.Services;

/// <summary>
/// Fetches the Crypto Fear and Greed Index from alternative.me.
/// Only meaningful for crypto pairs - returns Neutral for Forex.
/// Cached 15 minutes to avoid hitting the free API too often.
/// </summary>
public static class FearGreedService
{
    public enum FearGreedZone { ExtremeFear, Fear, Neutral, Greed, ExtremeGreed }

    public record FearGreedResult(
        int Value,
        string Classification,
        FearGreedZone Zone,
        double ScoreContribution,
        string Description
    );

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static FearGreedResult? _cached;
    private static DateTime _cacheExpiry = DateTime.MinValue;

    public static readonly FearGreedResult Neutral =
        new(50, "Neutral", FearGreedZone.Neutral, 0.0, "Net dannyh o nastroenii rynka.");

    /// <summary>
    /// Returns Fear and Greed data. For Forex pairs always returns Neutral.
    /// Never throws - failures return Neutral with 0 score contribution.
    /// </summary>
    public static async Task<FearGreedResult> GetAsync(bool isForex)
    {
        // Fear and Greed is crypto-only; Forex has separate drivers (central banks, macro)
        if (isForex)
            return new FearGreedResult(50, "N/A (Forex)", FearGreedZone.Neutral, 0.0, "Fear and Greed ne primenim k Forex param.");

        if (_cached != null && DateTime.UtcNow < _cacheExpiry)
            return _cached;

        await _lock.WaitAsync();
        try
        {
            if (_cached != null && DateTime.UtcNow < _cacheExpiry)
                return _cached;

            var result = await FetchAsync();
            _cached = result;
            _cacheExpiry = DateTime.UtcNow.AddMinutes(15);
            return result;
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[FearGreed] API unavailable: {ex.Message}. Using Neutral.");
            return Neutral;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<FearGreedResult> FetchAsync()
    {
        var response = await _http.GetStringAsync("https://api.alternative.me/fng/?limit=1&format=json");
        using var doc = JsonDocument.Parse(response);

        var data = doc.RootElement.GetProperty("data")[0];
        int value = int.Parse(data.GetProperty("value").GetString() ?? "50");
        string classification = data.GetProperty("value_classification").GetString() ?? "Neutral";

        var zone = value switch
        {
            <= 24 => FearGreedZone.ExtremeFear,
            <= 49 => FearGreedZone.Fear,
            <= 74 => FearGreedZone.Greed,
            _     => FearGreedZone.ExtremeGreed
        };

        // Counter-signal: extreme fear = market oversold = slight BUY bias
        double contribution = zone switch
        {
            FearGreedZone.ExtremeFear  => +0.12,
            FearGreedZone.Fear         => +0.05,
            FearGreedZone.Greed        => -0.05,
            FearGreedZone.ExtremeGreed => -0.12,
            _                          => 0.0
        };

        string emoji = zone switch
        {
            FearGreedZone.ExtremeFear  => "Extreme Fear",
            FearGreedZone.Fear         => "Fear",
            FearGreedZone.Greed        => "Greed",
            FearGreedZone.ExtremeGreed => "Extreme Greed",
            _                          => "Neutral"
        };

        string desc = $"Nastroenie kriptorynka: {classification} ({value}/100) [{emoji}]";

        BotLogger.Info($"[FearGreed] value={value}, zone={zone}, contribution={contribution:+0.00;-0.00}");
        return new FearGreedResult(value, classification, zone, contribution, desc);
    }
}