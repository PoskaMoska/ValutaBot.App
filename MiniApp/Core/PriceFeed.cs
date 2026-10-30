using System;

namespace ValutaBot.MiniApp;

/// <summary>
/// Single source of truth for the price feed used by the trading pipeline.
/// All quotes come exclusively from the Pocket Option OTC stream ("{ASSET}_PO").
/// </summary>
public static class PriceFeed
{
    public const string PoSuffix = "_PO";

    public static bool UsePocketOption => true;

    /// <summary>Plain asset name without separators / OTC marks, e.g. "EUR/USD OTC" -> "EURUSD".</summary>
    public static string BaseAsset(string asset)
    {
        string a = asset.ToUpperInvariant();
        if (a.EndsWith(PoSuffix)) a = a[..^PoSuffix.Length];
        return AssetSanitizer.Sanitize(a);
    }

    /// <summary>Key under which candles are stored in subminute_candles ("{ASSET}_PO").</summary>
    public static string CandleKey(string asset)
        => BaseAsset(asset) + PoSuffix;

    public static bool IsPoKey(string key) => key.EndsWith(PoSuffix, StringComparison.OrdinalIgnoreCase);
}
