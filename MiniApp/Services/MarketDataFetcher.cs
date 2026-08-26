using System;
using System.Collections.Concurrent;
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

    public MarketClosedException(string message, string userFriendlyMessage)
        : base(message)
    {
        UserFriendlyMessage = userFriendlyMessage;
    }
}

/// <summary>
/// Service for fetching historical candle data via TwelveData.
/// Sub-minute (s5, s15, s30) uses RealtimeTickCollector for live data.
/// </summary>
public class MarketDataFetcher
{
    public static MarketDataFetcher Instance { get; set; } = new MarketDataFetcher();

    public string IntervalMap(string tf) => tf.ToLower() switch
    {
        "m1" => "1m", "m2" => "1m", "m3" => "5m",
        "m5" => "5m", "m15" => "15m", "m30" => "30m",
        "h1" => "1h", "h4" => "4h",
        "d1" => "1d", _ => "1m" // Sub-minute tf will fallback to 1m for historical bounds if needed
    };

    public int TimeframeSeconds(string tf) => tf.ToLower() switch
    {
        "s3" => 3, "s5" => 5, "s10" => 10, "s15" => 15, "s30" => 30,
        "m1" => 60, "m2" => 120, "m3" => 180, "m5" => 300,
        "m15" => 900, "m30" => 1800,
        "h1" => 3600, "h4" => 14400,
        "d1" => 86400, _ => 60
    };

    public string? HigherTf(string tf) => tf.ToLower() switch
    {
        "s3" or "s5" or "s10" or "s15" or "s30" => "m5",
        "m1" => "m5", "m2" => "m5", "m3" => "m5",
        "m5" => "m15", "m15" => "h1", "m30" => "h1",
        "h1" => "h4", "h4" => "d1", _ => null
    };

    public string? LowerTf(string tf) => tf.ToLower() switch
    {
        "s10" or "s15" or "s30" => "s5",
        "s5" => "s3",
        "m1" => "s30",
        "m2" => "m1", "m3" => "m1",
        "m5" => "m1", "m15" => "m5", "m30" => "m15",
        "h1" => "m30", "h4" => "h1",
        "d1" => "h4", _ => null
    };

    private void CheckWeekendClosure()
    {
        var utcNow = DateTime.UtcNow;
        var dayOfWeek = utcNow.DayOfWeek;
        
        bool isWeekend = (dayOfWeek == DayOfWeek.Friday && utcNow.Hour >= 22) ||
                         (dayOfWeek == DayOfWeek.Saturday) ||
                         (dayOfWeek == DayOfWeek.Sunday && utcNow.Hour < 22);

        if (isWeekend)
        {
            throw new MarketClosedException(
                "Weekend Market Closed",
                "\u26a0\ufe0f Р С‹РЅРѕРє Р¤РѕСЂРµРєСЃ Р·Р°РєСЂС‹С‚ РЅР° РІС‹С…РѕРґРЅС‹Рµ (РџС‚ 22:00 - Р’СЃ 22:00 UTC)."
            );
        }
    }

    public virtual async Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        CheckWeekendClosure();

        string assetToFetch = originalAsset ?? symbol ?? "EUR/USD";
        string cleanAsset = AssetSanitizer.Sanitize(assetToFetch);
        if (cleanAsset.Length == 6) cleanAsset = $"{cleanAsset.Substring(0, 3)}/{cleanAsset.Substring(3, 3)}";

        // For sub-minute timeframes, first try live ticks from the DB
        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            // Use the same key format as RealtimeTickCollector writes: "EURUSD" not "EUR/USD"
            string cleanKey = AssetSanitizer.Sanitize(assetToFetch).Replace("/", "").ToUpper();
            var liveCandles = await RealtimeTickCollector.GetRecentCandles(cleanKey, rawInterval, limit);

            if (liveCandles.Length >= limit / 2)
            {
                BotLogger.Info($"[MarketDataFetcher] Using {liveCandles.Length} live {rawInterval} candles for {cleanKey}.");
                return liveCandles;
            }

            // Cold-start: not enough live ticks yet — synthesize from 1m as warm-up.
            // S5CandleSynthesizer is intentionally used ONLY here as a temporary placeholder
            // until the WebSocket accumulates enough real ticks (typically < 5 min after startup).
            BotLogger.Warn($"[MarketDataFetcher] Cold start for {rawInterval} ({liveCandles.Length} ticks in DB). Synthesizing from 1m candles as warm-up.");

            int m1Needed = (limit / 12) + 2; // 12 s5-candles per 1m candle
            var m1Result = await TwelveDataService.FetchCandlesAsync(cleanAsset, "1m", m1Needed);

            if (m1Result != null && m1Result.Value.candles.Length > 0)
            {
                // Synthesize to s5 first (finest granularity)
                var s5Candles = ValutaBot.App.MiniApp.Backtesting.S5CandleSynthesizer.SynthesizeFromM1(m1Result.Value.candles);

                // Aggregate s5 up to the requested sub-minute interval
                int groupSize = rawInterval.ToLower() switch
                {
                    "s5"  => 1,
                    "s10" => 2,
                    "s15" => 3,
                    "s30" => 6,
                    _     => 1
                };

                var aggregated = groupSize == 1
                    ? s5Candles
                    : AggregateCandles(s5Candles, groupSize);

                return aggregated.TakeLast(limit).ToArray();
            }

            // If even 1m data is unavailable, fall through to the normal TwelveData fetch below
            BotLogger.Warn($"[MarketDataFetcher] 1m cold-start data unavailable for {cleanAsset}.");
        }

        string interval = IntervalMap(rawInterval);
        var tdResult = await TwelveDataService.FetchCandlesAsync(cleanAsset, interval, limit);
        
        if (tdResult != null)
            return tdResult.Value.candles;

        throw new ExchangeUnavailableException("TwelveData API Unavailable", "\u26a0\ufe0f РќРµ СѓРґР°Р»РѕСЃСЊ РїРѕР»СѓС‡РёС‚СЊ РґР°РЅРЅС‹Рµ РѕС‚ Р±СЂРѕРєРµСЂР° (TwelveData).");
    }

    public virtual async Task<(double[] prices, double[] volumes)> FetchBinanceWithFallback(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        var candles = await FetchOhlcWithFallbackAsync(symbol, rawInterval, originalAsset, limit);
        
        var prices = candles.Select(c => c.Close).ToArray();
        var volumes = candles.Select(c => c.Volume).ToArray();
        
        return (prices, volumes);
    }

    /// <summary>
    /// Aggregates an array of fine-grained candles (e.g. s5) into coarser candles (e.g. s10, s15, s30).
    /// Used only during cold-start to upsample synthesized s5 data to the requested sub-minute timeframe.
    /// </summary>
    private static MiniAppController.OhlcCandle[] AggregateCandles(MiniAppController.OhlcCandle[] candles, int groupSize)
    {
        if (groupSize <= 1) return candles;

        var result = new List<MiniAppController.OhlcCandle>(candles.Length / groupSize);

        for (int i = 0; i + groupSize <= candles.Length; i += groupSize)
        {
            double open   = candles[i].Open;
            double high   = candles[i].High;
            double low    = candles[i].Low;
            double close  = candles[i + groupSize - 1].Close;
            double volume = 0;

            for (int j = i; j < i + groupSize; j++)
            {
                if (candles[j].High > high)  high   = candles[j].High;
                if (candles[j].Low  < low)   low    = candles[j].Low;
                volume += candles[j].Volume;
            }

            result.Add(new MiniAppController.OhlcCandle(open, high, low, close, volume, candles[i].Timestamp));
        }

        return result.ToArray();
    }
}