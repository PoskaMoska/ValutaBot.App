using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Single source of truth for which price feed the trading pipeline uses.
///
/// PRICE_SOURCE=po (default)  -> Pocket Option OTC quotes only. Candles are stored under "{ASSET}_PO"
///                               (e.g. EURUSD_PO) so they never mix with Tiingo candles stored under "EURUSD".
///                               No Tiingo fallback: if PO data is missing/stale the bot does not trade.
/// PRICE_SOURCE=tiingo         -> legacy behaviour (real Forex quotes from Tiingo).
/// </summary>
public static class PriceFeed
{
    public const string PoSuffix = "_PO";

    public static bool UsePocketOption { get; } =
        !string.Equals(Environment.GetEnvironmentVariable("PRICE_SOURCE"), "tiingo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain asset name without separators / OTC marks, e.g. "EUR/USD OTC" -> "EURUSD".</summary>
    public static string BaseAsset(string asset)
    {
        string a = asset.ToUpperInvariant();
        if (a.EndsWith(PoSuffix)) a = a[..^PoSuffix.Length];
        return AssetSanitizer.Sanitize(a);
    }

    /// <summary>Key under which candles of the active feed are stored in subminute_candles.</summary>
    public static string CandleKey(string asset)
        => UsePocketOption ? BaseAsset(asset) + PoSuffix : BaseAsset(asset);

    public static bool IsPoKey(string key) => key.EndsWith(PoSuffix, StringComparison.OrdinalIgnoreCase);
}
