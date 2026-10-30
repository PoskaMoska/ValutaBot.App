using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public class ExchangeUnavailableException : Exception
{
    public string UserFriendlyMessage { get; }

    public ExchangeUnavailableException(string message, string userFriendlyMessage, Exception? inner = null)
        : base(message, inner)
    {
        UserFriendlyMessage = userFriendlyMessage;
    }
}

public class MarketClosedException : Exception
{
    public string UserFriendlyMessage { get; }

    public MarketClosedException(string message, string userFriendlyMessage, Exception? inner = null)
        : base(message, inner)
    {
        UserFriendlyMessage = userFriendlyMessage;
    }
}

public class MarketDataFetcher
{
    public string IntervalMap(string tf) => TimeframeNormalizer.Normalize(tf) switch
    {
        "s5" or "s10" or "s15" or "s30" or "m1" => "1min",
        "m2" => "1min", "m3" => "3min",
        "m5" => "5min", "m15" => "15min", "m30" => "30min",
        "h1" => "1h", "h4" => "4h", "d1" => "1day", _ => "1min"
    };

    public string? HigherTf(string tf) => TimeframeNormalizer.Normalize(tf) switch
    {
        "s5" or "s10" or "s15" or "s30" => "m1",
        "m1" => "m5", "m2" => "m15", "m3" => "m15",
        "m5" => "m15", "m15" => "h1", "m30" => "h1",
        "h1" => "h4", "h4" => "d1", _ => null
    };

    public string? LowerTf(string tf) => TimeframeNormalizer.Normalize(tf) switch
    {
        "s10" or "s15" or "s30" => "s5",
        "s5" => "s3",
        "m1" => "s30",
        "m2" => "m1", "m3" => "m1",
        "m5" => "m1", "m15" => "m5", "m30" => "m15",
        "h1" => "m30", "h4" => "h1",
        "d1" => "h4", _ => null
    };

    public static bool IsWeekendNow()
    {
        var utcNow = DateTime.UtcNow;
        var dayOfWeek = utcNow.DayOfWeek;
        return (dayOfWeek == DayOfWeek.Friday && utcNow.Hour >= 21) ||
               (dayOfWeek == DayOfWeek.Saturday) ||
               (dayOfWeek == DayOfWeek.Sunday && utcNow.Hour < 21);
    }

    public int TimeframeSeconds(string rawInterval) => TimeframeNormalizer.ToSeconds(rawInterval);

    /// <summary>
    /// Single source of truth for "is the live feed stale": the last candle's open time is older than
    /// max(25s, 3 candle intervals). Pure function so it can be exercised by the chaos audit.
    /// </summary>
    public static bool IsFeedStale(DateTime lastCandleOpenUtc, int timeframeSeconds, DateTime nowUtc)
        => (nowUtc - lastCandleOpenUtc).TotalSeconds > Math.Max(25, timeframeSeconds * 3);

    public virtual async Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        string assetToFetch = originalAsset ?? symbol ?? "EUR/USD";
        assetToFetch = assetToFetch.Replace(" OTC", "").Replace("OTC", "").Trim();

        string cleanAsset = AssetSanitizer.Sanitize(assetToFetch);
        if (cleanAsset.Length == 6) cleanAsset = $"{cleanAsset.Substring(0, 3)}/{cleanAsset.Substring(3, 3)}";

        string poKey = PriceFeed.CandleKey(cleanAsset);
        string tfKey = TimeframeNormalizer.Normalize(rawInterval);
        int tfSecs = Math.Max(5, TimeframeSeconds(tfKey));

        if (!(tfKey.StartsWith("s") || tfKey == "m1"))
        {
            // Higher-TF context (m5, m15, ...) is aggregated from PO 1m candles aligned to true calendar boundaries.
            int group = Math.Max(1, tfSecs / 60);
            var m1 = await RealtimeTickCollector.GetRecentCandles(poKey, "m1", Math.Min((limit + 2) * group, 3000));
            return m1.Length >= group ? AggregateCalendarCandles(m1, tfSecs).TakeLast(limit).ToArray() : Array.Empty<MiniAppController.OhlcCandle>();
        }

        var poCandles = await RealtimeTickCollector.GetRecentCandles(poKey, tfKey, limit);
        if (poCandles.Length == 0)
            throw new ExchangeUnavailableException("PO feed: no candles", $"Нет котировок Pocket Option для {poKey}. Поток PO не подключен.");

        DateTime refNow = PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        double ageSecs = (refNow - poCandles[^1].Timestamp).TotalSeconds;
        if (IsFeedStale(poCandles[^1].Timestamp, tfSecs, refNow))
            throw new ExchangeUnavailableException("PO feed: stale", $"Котировки Pocket Option для {poKey} устарели ({ageSecs:F0}с).");

        return poCandles;
    }

    public virtual async Task<(double[] prices, double[] volumes)> FetchPricesAndVolumesAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        var candles = await FetchOhlcWithFallbackAsync(symbol, rawInterval, originalAsset, limit);
        var prices = candles.Select(c => c.Close).ToArray();
        var volumes = candles.Select(c => c.Volume).ToArray();
        return (prices, volumes);
    }

    private static MiniAppController.OhlcCandle[] AggregateCalendarCandles(MiniAppController.OhlcCandle[] candles, int tfSecs)
    {
        if (candles.Length == 0 || tfSecs <= 60) return candles;
        long bucketTicks = TimeSpan.FromSeconds(tfSecs).Ticks;
        var result = new List<MiniAppController.OhlcCandle>(candles.Length / Math.Max(1, tfSecs / 60) + 2);

        DateTime currentBucket = default;
        double open = 0, high = 0, low = 0, close = 0, volume = 0;
        bool hasBucket = false;

        for (int i = 0; i < candles.Length; i++)
        {
            var c = candles[i];
            DateTime tsUtc = c.Timestamp.Kind == DateTimeKind.Utc ? c.Timestamp : DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc);
            DateTime bucket = new DateTime(tsUtc.Ticks - (tsUtc.Ticks % bucketTicks), DateTimeKind.Utc);

            if (!hasBucket || bucket != currentBucket)
            {
                if (hasBucket)
                    result.Add(new MiniAppController.OhlcCandle(open, high, low, close, volume, currentBucket));
                currentBucket = bucket;
                open = c.Open;
                high = c.High;
                low = c.Low;
                close = c.Close;
                volume = c.Volume;
                hasBucket = true;
            }
            else
            {
                if (c.High > high) high = c.High;
                if (c.Low < low) low = c.Low;
                close = c.Close;
                volume += c.Volume;
            }
        }
        if (hasBucket)
            result.Add(new MiniAppController.OhlcCandle(open, high, low, close, volume, currentBucket));

        return result.ToArray();
    }
}
