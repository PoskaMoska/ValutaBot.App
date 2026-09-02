using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data;
using Dapper;
using System.Collections.Generic;

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
    // Caches mappings for standard intervals
    public string IntervalMap(string tf) => tf.ToLower() switch
    {
        "s5" => "1min", "s15" => "1min", "s30" => "1min",
        "m1" => "1min", "m2" => "1min", "m3" => "3min", 
        "m5" => "5min", "m15" => "15min", "m30" => "30min",
        "h1" => "1h", "h4" => "4h", "d1" => "1day", _ => "1min"
    };

    public string? HigherTf(string tf) => tf.ToLower() switch
    {
        "s5" => "m1", "s10" => "m1", "s15" => "m1", "s30" => "m1",
        "m1" => "m5", "m2" => "m15", "m3" => "m15",
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

    private static bool IsWeekendNow()
    {
        var utcNow = DateTime.UtcNow;
        var dayOfWeek = utcNow.DayOfWeek;
        return (dayOfWeek == DayOfWeek.Friday && utcNow.Hour >= 22) ||
               (dayOfWeek == DayOfWeek.Saturday) ||
               (dayOfWeek == DayOfWeek.Sunday && utcNow.Hour < 22);
    }

    private void CheckWeekendClosure(string asset, bool isOtc)
    {
        if (isOtc) return;

        if (IsWeekendNow())
        {
            throw new MarketClosedException(
                "Weekend Market Closed",
                "⚠️ Рынок Форекс закрыт на выходные (Пт 22:00 - Вс 22:00 UTC)."
            );
        }
    }

    public int TimeframeSeconds(string rawInterval)
    {
        string t = rawInterval.ToLower();
        if (t.StartsWith("s") && int.TryParse(t.Substring(1), out int s)) return s;
        if (t.StartsWith("m") && int.TryParse(t.Substring(1), out int m)) return m * 60;
        if (t.StartsWith("h") && int.TryParse(t.Substring(1), out int h)) return h * 3600;
        if (t == "d1") return 86400;
        return 60;
    }

    public virtual async Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        string assetToFetch = originalAsset ?? symbol ?? "EUR/USD";
        bool isOtc = assetToFetch.Contains("OTC", StringComparison.OrdinalIgnoreCase);
        
        CheckWeekendClosure(assetToFetch, isOtc);

        string cleanAsset = AssetSanitizer.Sanitize(assetToFetch);
        if (cleanAsset.Length == 6) cleanAsset = $"{cleanAsset.Substring(0, 3)}/{cleanAsset.Substring(3, 3)}";

        // If weekend + OTC -> Use offline database
        if (IsWeekendNow() && isOtc)
        {
            return await FetchOtcHistoricalAsync(assetToFetch, rawInterval, limit);
        }

        // For sub-minute timeframes, first try live ticks from the DB
        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            string cleanKey = AssetSanitizer.Sanitize(assetToFetch).Replace("/", "").ToUpper();
            var liveCandles = await RealtimeTickCollector.GetRecentCandles(cleanKey, rawInterval, limit);

            if (liveCandles.Length >= limit / 2)
            {
                BotLogger.Info($"[MarketDataFetcher] Using {liveCandles.Length} live {rawInterval} candles for {cleanKey}.");
                return liveCandles;
            }

            BotLogger.Warn($"[MarketDataFetcher] Cold start for {rawInterval} ({liveCandles.Length} ticks in DB). Synthesizing from 1m candles as warm-up.");

            int m1Needed = (limit / 12) + 2;
            var m1Result = await TwelveDataService.FetchCandlesAsync(cleanAsset, "1m", m1Needed);

            if (m1Result != null && m1Result.Value.candles.Length > 0)
            {
                var s5Candles = ValutaBot.App.MiniApp.Backtesting.S5CandleSynthesizer.SynthesizeFromM1(m1Result.Value.candles);
                int groupSize = rawInterval.ToLower() switch { "s5" => 1, "s10" => 2, "s15" => 3, "s30" => 6, _ => 1 };
                var aggregated = groupSize == 1 ? s5Candles : AggregateCandles(s5Candles, groupSize);
                return aggregated.TakeLast(limit).ToArray();
            }

            BotLogger.Warn($"[MarketDataFetcher] 1m cold-start data unavailable for {cleanAsset}.");
        }

        string interval = IntervalMap(rawInterval);
        var tdResult = await TwelveDataService.FetchCandlesAsync(cleanAsset, interval, limit);
        
        if (tdResult != null)
            return tdResult.Value.candles;

        throw new ExchangeUnavailableException("TwelveData API Unavailable", "⚠️ Не удалось получить данные от брокера (TwelveData).");
    }

    private async Task<MiniAppController.OhlcCandle[]> FetchOtcHistoricalAsync(string asset, string rawInterval, int limit)
    {
        string dbSymbol = asset.Replace("/", "").Replace("OTC", "").Trim().ToUpper();
        if (dbSymbol == "GBPJPY") dbSymbol = "USDCHF"; // Fallback proxy

        int m1Needed = limit;
        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase)) m1Needed = (limit / 12) + 2;
        else if (rawInterval.StartsWith("m") && int.TryParse(rawInterval.Substring(1), out int m)) m1Needed = limit * m;
        else if (rawInterval.StartsWith("h") && int.TryParse(rawInterval.Substring(1), out int h)) m1Needed = limit * h * 60;

        int maxIndex = 98000; 
        int offset = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute) % maxIndex);

        using var conn = DbConnectionFactory.GetConnection();
        var rows = await conn.QueryAsync<MiniAppController.OhlcCandle>(@"
            SELECT open AS Open, high AS High, low AS Low, close AS Close, volume AS Volume
            FROM historical_candles
            WHERE asset = @Asset
            ORDER BY open_time ASC
            LIMIT @Limit OFFSET @Offset
        ", new { Asset = dbSymbol, Limit = m1Needed, Offset = offset });

        var m1Candles = rows.ToArray();
        if (m1Candles.Length < m1Needed)
        {
            m1Candles = (await conn.QueryAsync<MiniAppController.OhlcCandle>(@"
                SELECT open AS Open, high AS High, low AS Low, close AS Close, volume AS Volume
                FROM historical_candles WHERE asset = 'EURUSD' ORDER BY open_time ASC LIMIT @Limit OFFSET @Offset
            ", new { Limit = m1Needed, Offset = offset })).ToArray();
        }

        MiniAppController.OhlcCandle[] finalCandles;
        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            var s5 = ValutaBot.App.MiniApp.Backtesting.S5CandleSynthesizer.SynthesizeFromM1(m1Candles);
            int groupSize = rawInterval.ToLower() switch { "s5" => 1, "s10" => 2, "s15" => 3, "s30" => 6, _ => 1 };
            finalCandles = (groupSize == 1 ? s5 : AggregateCandles(s5, groupSize)).TakeLast(limit).ToArray();
        }
        else
        {
            int mGroup = 1;
            if (rawInterval.StartsWith("m") && int.TryParse(rawInterval.Substring(1), out int m)) mGroup = m;
            if (rawInterval.StartsWith("h") && int.TryParse(rawInterval.Substring(1), out int h)) mGroup = h * 60;
            finalCandles = (mGroup == 1 ? m1Candles : AggregateCandles(m1Candles, mGroup)).TakeLast(limit).ToArray();
        }

        var now = DateTime.UtcNow;
        int intervalSeconds = TimeframeSeconds(rawInterval);
        for (int i = 0; i < finalCandles.Length; i++)
        {
            finalCandles[i] = finalCandles[i] with { Timestamp = now.AddSeconds(- (finalCandles.Length - 1 - i) * intervalSeconds) };
        }

        BotLogger.Info($"[OTC Weekend] Served {finalCandles.Length} virtual candles for {asset} from history.");
        return finalCandles;
    }

    public virtual async Task<(double[] prices, double[] volumes)> FetchBinanceWithFallback(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        var candles = await FetchOhlcWithFallbackAsync(symbol, rawInterval, originalAsset, limit);
        var prices = candles.Select(c => c.Close).ToArray();
        var volumes = candles.Select(c => c.Volume).ToArray();
        return (prices, volumes);
    }

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
