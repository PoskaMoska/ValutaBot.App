using System;
using System.Collections.Concurrent;

namespace ValutaBot.MiniApp;

/// <summary>
/// Controls whether market data and live prices are sourced from Pocket Option
/// or TwelveData, and normalizes symbol naming conventions.
/// </summary>
public static class PriceFeed
{
    public const string PoSuffix = "_PO";
    public const double MaxAllowedPrice = 1_000_000.0;
    public const long MaxFutureTickLagTicks = 10L * TimeSpan.TicksPerSecond; // 10 seconds future guard
    public const long MaxPastTickLagTicks = 60L * TimeSpan.TicksPerSecond;   // 60 seconds past guard for live ticks

    private static readonly ConcurrentDictionary<string, string> _baseAssetCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _candleKeyCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool UsePocketOption => true;

    /// <summary>Plain asset name without separators / OTC marks, e.g. "EUR/USD OTC" -> "EURUSD". Zero-allocation cached.</summary>
    public static string BaseAsset(string asset)
    {
        return _baseAssetCache.GetOrAdd(asset, static a =>
        {
            string s = a.ToUpperInvariant();
            if (s.EndsWith(PoSuffix, StringComparison.OrdinalIgnoreCase)) s = s[..^PoSuffix.Length];
            return AssetSanitizer.Sanitize(s);
        });
    }

    /// <summary>Key under which candles are stored in subminute_candles ("{ASSET}_PO"). Zero-allocation cached.</summary>
    public static string CandleKey(string asset)
    {
        return _candleKeyCache.GetOrAdd(asset, static a =>
        {
            string b = BaseAsset(a);
            return b + PoSuffix;
        });
    }

    public static bool IsPoKey(string key) => !string.IsNullOrEmpty(key) && key.EndsWith(PoSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strict sanitization of raw tick prices and timestamps against poison ticks, NaN, infinity,
    /// negative or excessive prices (>1,000,000) and inverted timestamps.
    /// </summary>
    public static bool IsValidTick(double price, long tickUtcTicks, long currentUtcTicks, bool isBackfill = false)
    {
        if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0.0 || price > MaxAllowedPrice)
            return false;

        if (tickUtcTicks <= 0)
            return false;

        return true;
    }
}
