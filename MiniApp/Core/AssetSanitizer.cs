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
        return asset.ToUpperInvariant()
            .Replace("ОТК", "") // Replace Cyrillic OTC
            .Replace("ОТС", "")
            .Replace("OTC", "")
            .Replace(" ", "")
            .Replace("/", "")
            .Replace("-", "")
            .Replace("_", "")
            .Trim();
    }

    /// <summary>
    /// Determines if a sanitized asset is a forex/commodity pair (not crypto).
    /// Centralizes this check so it works consistently regardless of day of week.
    /// </summary>
    public static bool IsForexAsset(string cleanAsset)
    {
        // Crypto assets — NOT forex
        if (cleanAsset is "BTCUSDT" or "BTC" or "BTCUSD"
                       or "ETHUSDT" or "ETH" or "ETHUSD"
                       or "SOLUSDT" or "SOL" or "SOLUSD"
                       or "BNBUSDT" or "BNB"
                       or "XRPUSDT" or "XRP"
                       or "ADAUSDT" or "ADA"
                       or "DOGEUSDT" or "DOGE")
            return false;

        // Forex pairs, commodities — YES
        if (cleanAsset.StartsWith("EUR") || cleanAsset.StartsWith("GBP") ||
            cleanAsset.StartsWith("AUD") || cleanAsset.StartsWith("NZD") ||
            cleanAsset.StartsWith("USD") || cleanAsset.StartsWith("JPY") ||
            cleanAsset.StartsWith("CHF") || cleanAsset.StartsWith("CAD") ||
            cleanAsset.StartsWith("XAU") || cleanAsset.StartsWith("XAG") ||
            cleanAsset.StartsWith("GOLD") || cleanAsset.StartsWith("SILVER"))
            return true;

        // Default: if ends in USDT and not in crypto list above → treat as crypto
        return !cleanAsset.EndsWith("USDT");
    }

    /// <summary>
    /// Map normalized asset to Binance symbol on weekends or return null for TwelveData fetching.
    /// </summary>
    public static string? MapSymbolByDayOfWeek(string cleanAsset, DayOfWeek day)
    {
        bool isWeekend = day == DayOfWeek.Saturday || day == DayOfWeek.Sunday;
        if (!isWeekend) return null; // 100% TwelveData on weekdays

        // On weekends, map Forex assets to their Binance equivalents (e.g., EURUSD -> EURUSDT)
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
