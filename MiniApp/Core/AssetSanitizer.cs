using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValutaBot.MiniApp;

/// <summary>
/// Handles asset ticker normalization, Cyrillic OTC conversion, and exchange symbol mapping.
/// </summary>
public static partial class AssetSanitizer
{
    [GeneratedRegex(@"[^A-Z0-9]")]
    private static partial Regex NonAlphanumericRegex();

    private static readonly FrozenSet<string> _forexPrefixes3 = new[]
    {
        "EUR", "GBP", "AUD", "NZD", "USD", "JPY", "CHF", "CAD", "XAU", "XAG", "WTI"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clean and normalize user-provided asset names (e.g. "EUR/USD OTC" or "EUR/USD ОТС" -> "EURUSD").
    /// </summary>
    public static string Sanitize(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return "EURUSD";
        string stripped = asset.ToUpperInvariant()
            .Replace("OTC", "")
            .Replace("\u041E\u0422\u0421", "")
            .Replace("\u041E\u0422\u041A", "");
        return NonAlphanumericRegex().Replace(stripped, "");
    }

    /// <summary>
    /// Determines if a sanitized asset is a forex/commodity pair (not crypto).
    /// Centralizes this check so it works consistently regardless of day of week.
    /// </summary>
    public static bool IsForexAsset(string cleanAsset)
    {
        if (string.IsNullOrEmpty(cleanAsset)) return false;

        if (cleanAsset.Length >= 3 && _forexPrefixes3.Contains(cleanAsset[..3]))
            return true;

        if (cleanAsset.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase) ||
            cleanAsset.StartsWith("SILVER", StringComparison.OrdinalIgnoreCase) ||
            cleanAsset.StartsWith("BRENT", StringComparison.OrdinalIgnoreCase))
            return true;

        // Default: if it does not start with a known fiat/commodity base, treat as Crypto
        return false;
    }

    /// <summary>
    /// Map normalized asset to Broker symbol on weekends, or return null for Tiingo fetching.
    /// </summary>
    public static string? MapSymbolByDayOfWeek(string cleanAsset, DayOfWeek day)
    {
        bool isWeekend = day == DayOfWeek.Saturday || day == DayOfWeek.Sunday;
        if (!isWeekend) return null; // On weekdays: fetch via Tiingo

        // On weekends, map Forex assets to their crypto equivalents (e.g., EURUSD -> EURUSDT)
        if (IsForexAsset(cleanAsset))
        {
            return cleanAsset switch
            {
                "EURUSD" or "EURUSDT" => "EURUSDT",
                "GBPUSD" or "GBPUSDT" => "GBPUSDT",
                "AUDUSD" or "AUDUSDT" => "AUDUSDT",
                _ => cleanAsset.EndsWith("USDT") ? cleanAsset : cleanAsset + "USDT"
            };
        }

        return null;
    }
}
