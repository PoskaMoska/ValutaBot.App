namespace ValutaBot.MiniApp;

/// <summary>
/// Handles asset ticker normalization, Cyrillic OTC conversion, and exchange symbol mapping.
/// </summary>
public static class AssetSanitizer
{
    /// <summary>
    /// Clean and normalize user-provided asset names (e.g. "EUR/USD OTC" or "EUR/USD ОТС" -> "EURUSD").
    /// </summary>
    public static string Sanitize(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return "EURUSD";
        return System.Text.RegularExpressions.Regex.Replace(asset.ToUpperInvariant().Replace("OTC", "").Replace("\u041E\u0422\u0421", "").Replace("\u041E\u0422\u041A", ""), @"[^A-Z0-9]", "");
    }









    /// <summary>
    /// Determines if a sanitized asset is a forex/commodity pair (not crypto).
    /// Centralizes this check so it works consistently regardless of day of week.
    /// </summary>
    public static bool IsForexAsset(string cleanAsset)
    {
        // Forex pairs, commodities — YES
        if (cleanAsset.StartsWith("EUR") || cleanAsset.StartsWith("GBP") ||
            cleanAsset.StartsWith("AUD") || cleanAsset.StartsWith("NZD") ||
            cleanAsset.StartsWith("USD") || cleanAsset.StartsWith("JPY") ||
            cleanAsset.StartsWith("CHF") || cleanAsset.StartsWith("CAD") ||
            cleanAsset.StartsWith("XAU") || cleanAsset.StartsWith("XAG") ||
            cleanAsset.StartsWith("GOLD") || cleanAsset.StartsWith("SILVER") ||
            cleanAsset.StartsWith("BRENT") || cleanAsset.StartsWith("WTI"))
            return true;

        // Default: if it does not start with a known fiat/commodity base, treat as Crypto
        return false;
    }

    /// <summary>
    /// Map normalized asset to Broker symbol on weekends or return null for TwelveData fetching.
    /// </summary>
    public static string? MapSymbolByDayOfWeek(string cleanAsset, DayOfWeek day)
    {
        bool isWeekend = day == DayOfWeek.Saturday || day == DayOfWeek.Sunday;
        if (!isWeekend) return null; // 100% TwelveData on weekdays

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
