using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// Fetches high-impact economic calendar events from TwelveData API.
/// Blocks forex analysis signals within 15 min before / 10 min after events.
/// Cache TTL: 1 hour. Only applies to live weekday forex pairs.
/// </summary>
public static class EconomicCalendarService
{
    public record EconomicEvent(
        string Name,
        string Currency,
        string Country,
        DateTime EventTimeUtc,
        string Importance
    );

    private const int BlockBeforeMinutes = 15;
    private const int BlockAfterMinutes  = 10;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static List<EconomicEvent> _cache = new();
    private static DateTime _cacheExpiry = DateTime.MinValue;

    /// <summary>
    /// Returns the nearest high-impact event blocking the given asset, or null if clear.
    /// Never throws — fail-open: don't block trading if calendar unavailable.
    /// </summary>
    public static async Task<EconomicEvent?> GetBlockingEventAsync(string asset)
    {
        if (asset.Contains("OTC", StringComparison.OrdinalIgnoreCase))
            return null;

        if (asset.Contains("BTC") || asset.Contains("ETH") || asset.Contains("SOL")
            || asset.Contains("XRP") || asset.Contains("BNB") || asset.Contains("ADA"))
            return null;

        var dayOfWeek = DateTime.UtcNow.DayOfWeek;
        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
            return null;

        try
        {
            var events = await GetEventsAsync();
            var currencies = ExtractCurrencies(asset);
            var now = DateTime.UtcNow;

            return events
                .Where(e => currencies.Contains(e.Currency, StringComparer.OrdinalIgnoreCase))
                .Where(e => now >= e.EventTimeUtc.AddMinutes(-BlockBeforeMinutes)
                         && now <= e.EventTimeUtc.AddMinutes(BlockAfterMinutes))
                .OrderBy(e => Math.Abs((e.EventTimeUtc - now).TotalMinutes))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[EconomicCalendar] Check failed: {ex.Message}. Fail-open.");
            return null;
        }
    }

    /// <summary>
    /// Human-readable block message: "NFP (USD) через 8 мин — анализ EUR/USD приостановлен."
    /// </summary>
    public static string FormatBlockMessage(EconomicEvent ev, string asset)
    {
        var now = DateTime.UtcNow;
        double minutesToEvent = (ev.EventTimeUtc - now).TotalMinutes;

        string timing = minutesToEvent switch
        {
            > 1  => $"через {(int)Math.Ceiling(minutesToEvent)} мин",
            > -1 => "прямо сейчас",
            _    => $"{(int)Math.Abs(Math.Floor(minutesToEvent))} мин назад"
        };

        return $"⚠️ Важные новости: {ev.Name} ({ev.Currency}) {timing}. " +
               $"Анализ {asset} приостановлен на время волатильности.";
    }

    private static async Task<List<EconomicEvent>> GetEventsAsync()
    {
        if (DateTime.UtcNow < _cacheExpiry)
            return _cache;

        await _lock.WaitAsync();
        try
        {
            if (DateTime.UtcNow < _cacheExpiry)
                return _cache;

            var fetched = await FetchFromTwelveDataAsync();
            _cache = fetched;
            _cacheExpiry = DateTime.UtcNow.AddHours(1);
            BotLogger.Info($"[EconomicCalendar] Refreshed: {fetched.Count} high-impact events today/tomorrow.");
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<List<EconomicEvent>> FetchFromTwelveDataAsync()
    {
        string apiKey = System.Environment.GetEnvironmentVariable("TwelveDataApiKey") ?? "";
        if (string.IsNullOrEmpty(apiKey))
        {
            BotLogger.Warn("[EconomicCalendar] TwelveDataApiKey not set — calendar disabled.");
            return new List<EconomicEvent>();
        }

        string startDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string endDate   = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        string url = $"https://api.twelvedata.com/economic_calendar" +
                     $"?start_date={startDate}&end_date={endDate}" +
                     $"&importance=3" +
                     $"&apikey={apiKey}";

        using var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();

        return ParseEvents(json);
    }

    private static List<EconomicEvent> ParseEvents(string json)
    {
        var result = new List<EconomicEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement list;
            if (root.TryGetProperty("result", out var resultEl) && resultEl.TryGetProperty("list", out list)) { }
            else if (root.TryGetProperty("list", out list)) { }
            else
            {
                BotLogger.Warn("[EconomicCalendar] Unexpected JSON structure.");
                return result;
            }

            foreach (var item in list.EnumerateArray())
            {
                try
                {
                    string? eventName  = item.TryGetProperty("event",      out var e)  ? e.GetString()  : null;
                    string? currency   = item.TryGetProperty("currency",   out var c)  ? c.GetString()  : null;
                    string? country    = item.TryGetProperty("country",    out var co) ? co.GetString() : null;
                    string? dateStr    = item.TryGetProperty("date",       out var d)  ? d.GetString()  : null;
                    string? timeStr    = item.TryGetProperty("time",       out var t)  ? t.GetString()  : null;
                    string? importance = item.TryGetProperty("importance", out var i)  ? i.GetString()  : null;

                    if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(currency) || string.IsNullOrEmpty(dateStr))
                        continue;

                    // Skip all-day events (no specific time)
                    if (string.IsNullOrEmpty(timeStr) || timeStr == "00:00:00")
                        continue;

                    string dtString = $"{dateStr} {timeStr}";
                    if (!DateTime.TryParse(dtString, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out DateTime eventTime))
                        continue;

                    result.Add(new EconomicEvent(
                        Name:         eventName,
                        Currency:     currency.ToUpperInvariant(),
                        Country:      country ?? "",
                        EventTimeUtc: eventTime,
                        Importance:   importance ?? "High"
                    ));
                }
                catch (Exception itemEx)
                {
                    BotLogger.Warn($"[EconomicCalendar] Failed to parse event: {itemEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[EconomicCalendar] JSON parse error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Extracts currency codes from asset name.
    /// "EUR/USD" -> ["EUR","USD"]  |  "EURUSD OTC" -> ["EUR","USD"]
    /// </summary>
    private static HashSet<string> ExtractCurrencies(string asset)
    {
        string clean = asset
            .Replace("OTC", "", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "")
            .Replace(" ", "")
            .Trim()
            .ToUpperInvariant();

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (clean.Length >= 6)
        {
            result.Add(clean[..3]);
            result.Add(clean[3..6]);
        }
        return result;
    }
}
