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
    private static int _consecutiveFailures = 0;
    private static DateTime _lastAlertTime = DateTime.MinValue;

    private static void RecordSuccess()
    {
        _consecutiveFailures = 0;
    }

    private static void RecordFailureAndAlert(string reason)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3 && (DateTime.UtcNow - _lastAlertTime).TotalMinutes > 5)
        {
            _lastAlertTime = DateTime.UtcNow;
            _ = Task.Run(() => ValutaBot.MiniApp.TelegramBotService.SendMessageToAdmins($"🚨 <b>Отвал Котировок!</b>\nБот не может получить свечи ({reason}) уже 3 раза подряд. Торговля заблокирована."));
        }
    }

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
        // Forex markets close at 17:00 EST on Friday, which is 21:00 UTC (Summer) or 22:00 UTC (Winter).
        // Using 21:00 UTC safely switches to OTC mode without hitting the 1-hour dead zone.
        return (dayOfWeek == DayOfWeek.Friday && utcNow.Hour >= 21) ||
               (dayOfWeek == DayOfWeek.Saturday) ||
               (dayOfWeek == DayOfWeek.Sunday && utcNow.Hour < 21);
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
        
        // ARCHITECTURAL REFACTORING: Completely strip OTC from business logic
        assetToFetch = assetToFetch.Replace(" OTC", "").Replace("OTC", "").Trim();
        
        string cleanAsset = AssetSanitizer.Sanitize(assetToFetch);
        if (cleanAsset.Length == 6) cleanAsset = $"{cleanAsset.Substring(0, 3)}/{cleanAsset.Substring(3, 3)}";

        bool isWeekend = IsWeekendNow();

        // Убрана привязка к OTC по просьбе пользователя.
        // Теперь в будние дни для OTC пар будут загружаться реальные котировки TwelveData.
        if (isWeekend)
        {
            BotLogger.Info($"[SmartRouting] Weekend detected. Routing {assetToFetch} to local historical DB as {cleanAsset}.");
            return await FetchOtcHistoricalAsync(cleanAsset, rawInterval, limit);
        }

        // For sub-minute timeframes, first try live ticks from the DB
        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            string cleanKey = cleanAsset.Replace("/", "").ToUpper();
            var liveCandles = await RealtimeTickCollector.GetRecentCandles(cleanKey, rawInterval, limit);

            if (liveCandles.Length >= limit)
            {
                BotLogger.Info($"[MarketDataFetcher] Using {liveCandles.Length} live {rawInterval} candles for {cleanKey}.");
                RecordSuccess();
                return liveCandles;
            }

            // CRITICAL UX FIX: Cold Start Backfill
            // Instead of aborting and forcing the user to wait 15+ minutes, we backfill the missing
            // older candles by synthesizing them from 1min data, and append whatever true live ticks we have.
            int missing = limit - liveCandles.Length;
            BotLogger.Warn($"[MarketDataFetcher] Cold start for {rawInterval} ({liveCandles.Length}/{limit} live ticks). Backfilling {missing} from 1m...");

            int groupSize = rawInterval.ToLower() switch { "s5" => 1, "s10" => 2, "s15" => 3, "s30" => 6, _ => 1 };
            int subCandlesPerM1 = 12 / groupSize; 
            int m1Needed = Math.Max(10, (int)Math.Ceiling((double)(missing + 10) / subCandlesPerM1));

            var tdResult1m = await TwelveDataService.FetchCandlesAsync(cleanAsset, "1min", m1Needed, cacheTtlSeconds: 15);
            if (tdResult1m == null)
            {
                throw new ExchangeUnavailableException("TwelveData API Unavailable", "Не удалось загрузить минутные котировки для генерации микро-тиков.");
            }

            var m1Candles = tdResult1m.Value.candles.ToArray();
            var s5 = ValutaBot.App.MiniApp.Backtesting.S5CandleSynthesizer.SynthesizeFromM1(m1Candles);
            var synthesized = groupSize == 1 ? s5 : AggregateCandles(s5, groupSize);

            // Stitch together
            var finalCandles = new MiniAppController.OhlcCandle[limit];
            int synthCount = Math.Min(missing, synthesized.Length);
            
            // If API didn't return enough 1m history, we just serve what we could build
            if (synthCount < missing)
            {
                finalCandles = new MiniAppController.OhlcCandle[synthCount + liveCandles.Length];
            }

            // Take from the end of the synthesized array (the most recent synthesized candles)
            for (int i = 0; i < synthCount; i++)
            {
                finalCandles[i] = synthesized[synthesized.Length - synthCount + i];
            }

            for (int i = 0; i < liveCandles.Length; i++)
            {
                finalCandles[synthCount + i] = liveCandles[i];
            }

            // Fix timestamps to be perfectly continuous
            DateTime lastTime = liveCandles.Length > 0 ? liveCandles[^1].Timestamp : DateTime.UtcNow;
            int intervalSeconds = TimeframeSeconds(rawInterval);
            for (int i = finalCandles.Length - 1; i >= 0; i--)
            {
                finalCandles[i] = finalCandles[i] with { Timestamp = lastTime.AddSeconds(-(finalCandles.Length - 1 - i) * intervalSeconds) };
            }

            RecordSuccess();
            return finalCandles;
        }

        string interval = IntervalMap(rawInterval);
        // Cache TTL scales with timeframe — shorter TFs need fresher data
        int cacheTtl = rawInterval.ToLower() switch
        {
            "s5" or "s10"       => 5,
            "s15" or "s30"      => 10,
            "m1"                => 15,
            "m2" or "m3"        => 30,
            "m5"                => 60,
            "m15" or "m30"      => 120,
            "h1" or "h4"        => 300,
            _                   => 300
        };
        var tdResult = await TwelveDataService.FetchCandlesAsync(cleanAsset, interval, limit, cacheTtlSeconds: cacheTtl);
        
        if (tdResult != null)
        {
            var candles = tdResult.Value.candles.ToArray();
            // ROOT CAUSE FIX: Ghost Pricing. Overwrite the final (forming) candle's Close/High/Low with real WS tick.
            // This prevents a 15-second stale cache from causing ML entries & targets to be completely disjointed from reality.
            if (candles.Length > 0 && TwelveDataWebSocketStream.TryGetLivePrice(cleanAsset, out double realPrice))
            {
                var last = candles[^1];
                int intervalSecs = TimeframeSeconds(rawInterval);
                bool isClosed = last.Timestamp.AddSeconds(intervalSecs) <= DateTime.UtcNow;

                if (isClosed)
                {
                    var synthetic = new MiniAppController.OhlcCandle(realPrice, realPrice, realPrice, realPrice, 0, last.Timestamp.AddSeconds(intervalSecs));
                    candles = candles.Append(synthetic).ToArray();
                }
                else
                {
                    candles[^1] = last with 
                    { 
                        Close = realPrice, 
                        High = Math.Max(last.High, realPrice), 
                        Low = Math.Min(last.Low, realPrice) 
                    };
                }
            }
            RecordSuccess();
            return candles;
        }

        RecordFailureAndAlert("TwelveData API Unavailable");
        throw new ExchangeUnavailableException("TwelveData API Unavailable", "Не удалось загрузить живые котировки (TwelveData). API недоступен.");
    }

    private async Task<MiniAppController.OhlcCandle[]> FetchOtcHistoricalAsync(string asset, string rawInterval, int limit)
    {
        string dbSymbol = asset.Replace("/", "").Replace("OTC", "").Trim().ToUpper();

        int m1Needed = limit;

        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            int groupSize = rawInterval.ToLower() switch { "s5" => 1, "s10" => 2, "s15" => 3, "s30" => 6, _ => 1 };
            int subCandlesPerM1 = 12 / groupSize; 
            m1Needed = Math.Max(10, (int)Math.Ceiling((double)(limit + 10) / subCandlesPerM1));
        }
        else if (rawInterval.StartsWith("m") && int.TryParse(rawInterval.Substring(1), out int m)) m1Needed = limit * m;
        else if (rawInterval.StartsWith("h") && int.TryParse(rawInterval.Substring(1), out int h)) m1Needed = limit * h * 60;

        int maxIndex = 98000; 
        int offset = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute) % maxIndex);

        using var conn = DbConnectionFactory.GetConnection();
        var rows = await conn.QueryAsync<dynamic>(@"
            SELECT open, high, low, close, volume
            FROM historical_candles
            WHERE asset = @Asset
            ORDER BY open_time ASC
            LIMIT @Limit OFFSET @Offset
          ", new { Asset = dbSymbol, Limit = m1Needed, Offset = offset });

        var m1Candles = rows.Select(r => new MiniAppController.OhlcCandle(Convert.ToDouble(r.open), Convert.ToDouble(r.high), Convert.ToDouble(r.low), Convert.ToDouble(r.close), Convert.ToDouble(r.volume), default(DateTime))).ToArray();
        if (m1Candles.Length < m1Needed)
        {
            throw new ExchangeUnavailableException("Insufficient Data", $"⚠️ Недостаточно исторических данных для OTC актива {dbSymbol}. Ожидалось {m1Needed}, найдено {m1Candles.Length}.");
        }

        MiniAppController.OhlcCandle[] finalCandles;
        if (rawInterval.StartsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            var s5 = ValutaBot.App.MiniApp.Backtesting.S5CandleSynthesizer.SynthesizeFromM1(m1Candles);
            int groupSize = rawInterval.ToLower() switch { "s5" => 1, "s10" => 2, "s15" => 3, "s30" => 6, _ => 1 };
            var allSubCandles = groupSize == 1 ? s5 : AggregateCandles(s5, groupSize);
            
            int subInterval = groupSize * 5; 
            int maxShift = 60 / subInterval;
            int shift = DateTime.UtcNow.Second / subInterval;
            
            int endIndex = allSubCandles.Length - maxShift + shift - 1;
            int startIndex = endIndex - limit + 1;
            if (startIndex < 0) startIndex = 0;
            
            if (endIndex >= allSubCandles.Length) endIndex = allSubCandles.Length - 1;
            
            finalCandles = allSubCandles.Skip(startIndex).Take(limit).ToArray();
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
        
        long ticksPerInterval = TimeSpan.TicksPerSecond * intervalSeconds;
        if (ticksPerInterval > 0)
        {
            now = new DateTime(now.Ticks - (now.Ticks % ticksPerInterval), DateTimeKind.Utc);
        }

        for (int i = 0; i < finalCandles.Length; i++)
        {
            finalCandles[i] = finalCandles[i] with { Timestamp = now.AddSeconds(- (finalCandles.Length - 1 - i) * intervalSeconds) };
        }

        BotLogger.Info($"[OTC Weekend] Served {finalCandles.Length} virtual candles for {asset} from history.");
        return finalCandles;
    }

    public virtual async Task<(double[] prices, double[] volumes)> FetchPricesAndVolumesAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
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
                if (candles[j].High > high)   high   = candles[j].High;
                if (candles[j].Low  < low)    low    = candles[j].Low;
                volume += candles[j].Volume;
            }
            result.Add(new MiniAppController.OhlcCandle(open, high, low, close, volume, candles[i].Timestamp));
        }
        return result.ToArray();
    }
}