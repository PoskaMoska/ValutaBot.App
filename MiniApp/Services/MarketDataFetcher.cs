using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Polly;
using Polly.Retry;

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
/// Service for fetching historical candle data with fallback, and sub-minute interpolation.
/// Fully stateless. Relies on WebSocket for fast path caching.
/// </summary>
public class MarketDataFetcher
{
    public static MarketDataFetcher Instance { get; set; } = new MarketDataFetcher();
    
    // Rate limit protection cache for HTTP fallback
    private static readonly ConcurrentDictionary<string, (DateTime Expiration, MiniAppController.OhlcCandle[] Data)> _klinesCache = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new();

    /* removed pipeline */ 

    public string IntervalMap(string tf) => tf.ToLower() switch
    {
        "s3" or "s5" or "s10" or "s15" or "s30" => "1m",
        "m1" => "1m", "m2" => "1m", "m3" => "3m",
        "m5" => "5m", "m15" => "15m", "m30" => "30m",
        "h1" => "1h", "h4" => "4h",
        "d1" => "1d", _ => "1m"
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

    public async Task<MiniAppController.OhlcCandle[]> FetchBinanceOhlcCandlesAsync(string symbol, string interval, int limit = 50)
    {
        // Try Websocket fast path first
        if (BinanceWebSocketStream.TryGetLiveCandles(symbol, interval, out var wsOpens, out var wsHighs, out var wsLows, out var wsPrices, out var wsVolumes, out int count))
        {
            try
            {
                if (count >= limit)
                {
                    var resultFast = new MiniAppController.OhlcCandle[limit];
                    int startIdx = count - limit;
                    
                    // FIX W-01: Parse interval properly so timestamps match the timeframe
                    TimeSpan step = TimeSpan.FromMinutes(1);
                    if (interval == "5m") step = TimeSpan.FromMinutes(5);
                    else if (interval == "15m") step = TimeSpan.FromMinutes(15);
                    else if (interval == "1h") step = TimeSpan.FromHours(1);
                    else if (interval == "4h") step = TimeSpan.FromHours(4);
                    else if (interval == "1d") step = TimeSpan.FromDays(1);
                    else if (interval.StartsWith("s")) step = TimeSpan.FromSeconds(int.Parse(interval[1..]));
                    
                    for (int i = 0; i < limit; i++)
                    {
                        var ts = DateTime.UtcNow.Subtract(step * (limit - i - 1));
                        resultFast[i] = new MiniAppController.OhlcCandle(wsOpens[startIdx + i], wsHighs[startIdx + i], wsLows[startIdx + i], wsPrices[startIdx + i], wsVolumes[startIdx + i], ts);
                    }
                    return resultFast;
                }
            }
            finally
            {
                // FIX: always return rented arrays вЂ” even if count < limit (previously leaked)
                System.Buffers.ArrayPool<double>.Shared.Return(wsOpens);
                System.Buffers.ArrayPool<double>.Shared.Return(wsHighs);
                System.Buffers.ArrayPool<double>.Shared.Return(wsLows);
                System.Buffers.ArrayPool<double>.Shared.Return(wsPrices);
                System.Buffers.ArrayPool<double>.Shared.Return(wsVolumes);
            }
        }

        return await FetchBinanceKlinsAsync(symbol, interval, limit) ?? Array.Empty<MiniAppController.OhlcCandle>();
    }

    public virtual async Task<MiniAppController.OhlcCandle[]> FetchOhlcWithFallbackAsync(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        string interval = IntervalMap(rawInterval);
        
        var utcNow = DateTime.UtcNow;
        var dayOfWeek = utcNow.DayOfWeek;
        
        // FIX W-02: Forex market is closed from Friday 22:00 UTC to Sunday 22:00 UTC
        bool isWeekend = (dayOfWeek == DayOfWeek.Friday && utcNow.Hour >= 22) ||
                         (dayOfWeek == DayOfWeek.Saturday) ||
                         (dayOfWeek == DayOfWeek.Sunday && utcNow.Hour < 22);

        // WEEKDAY POLICY: Use TwelveData (real Forex) first
        if (!isWeekend && originalAsset != null)
        {
            var tdResult = await TwelveDataService.FetchCandlesAsync(originalAsset, interval, limit);
            if (tdResult != null)
                return tdResult.Value.candles;
                
            BotLogger.Warn($"[MarketDataFetcher] TwelveData unavailable on a weekday for {originalAsset}, falling back to Binance OTC.");
        }

        // WEEKEND POLICY (or fallback): Use Binance OTC
        string cleanAsset = AssetSanitizer.Sanitize(originalAsset ?? symbol ?? "EURUSDT");
        string binanceSymbol = symbol ?? (cleanAsset switch
        {
            "EURUSD" or "EURUSDT" => "EURUSDT",
            "GBPUSD" or "GBPUSDT" => "GBPUSDT",
            "AUDUSD" or "AUDUSDT" => "AUDUSDT",
            _ => cleanAsset.EndsWith("USDT") ? cleanAsset : cleanAsset + "USDT"
        });
        
        try
        {
            return await FetchBinanceOhlcCandlesAsync(binanceSymbol, interval, limit);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MarketDataFetcher] Fallback OHLC fetch failed for {binanceSymbol}: {ex.Message}");
            return Array.Empty<MiniAppController.OhlcCandle>();
        }
    }

    private static async Task<MiniAppController.OhlcCandle[]?> FetchBinanceKlinsAsync(string symbol, string interval, int limit)
    {
        string cacheKey = $"{symbol}_{interval}_{limit}";
        if (_klinesCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiration)
        {
            return cached.Data;
        }

        var semaphore = _fetchLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            // Double check lock
            if (_klinesCache.TryGetValue(cacheKey, out cached) && DateTime.UtcNow < cached.Expiration)
            {
                return cached.Data;
            }

            string url = $"https://api.binance.com/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
            
            var httpFactory = MiniAppController.HttpFactory
                ?? throw new InvalidOperationException("[MarketDataFetcher] HttpFactory РЅРµ РёРЅРёС†РёР°Р»РёР·РёСЂРѕРІР°РЅ. РЎРµСЂРІРµСЂ РµС‰С‘ Р·Р°РїСѓСЃРєР°РµС‚СЃСЏ, РїРѕРІС‚РѕСЂРёС‚Рµ Р·Р°РїСЂРѕСЃ.");
            var arr = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<double[][]>(httpFactory.CreateClient("Binance"), new Uri(url), ValutaBotJsonContext.Default.DoubleArrayArray);
            
            if (arr == null || arr.Length == 0) return Array.Empty<MiniAppController.OhlcCandle>();

            var result = new MiniAppController.OhlcCandle[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                var k = arr[i];
                result[i] = new MiniAppController.OhlcCandle(k[1], k[2], k[3], k[4], k[5], DateTimeOffset.FromUnixTimeMilliseconds((long)k[0]).UtcDateTime);
            }
            
            _klinesCache[cacheKey] = (DateTime.UtcNow.AddSeconds(2), result);
            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<(double[] prices, double[] volumes)> FetchBinanceCandles(string symbol, string interval, int limit = 50)
    {
        var ohlc = await FetchBinanceOhlcCandlesAsync(symbol, interval, limit);
        if (ohlc.Length == 0) return (Array.Empty<double>(), Array.Empty<double>());

        var prices = new double[ohlc.Length];
        var volumes = new double[ohlc.Length];

        for (int i = 0; i < ohlc.Length; i++)
        {
            prices[i] = ohlc[i].Close;
            volumes[i] = ohlc[i].Volume;
        }

        return (prices, volumes);
    }

    public virtual async Task<(double[] prices, double[] volumes)> FetchBinanceWithFallback(string? symbol, string rawInterval, string? originalAsset = null, int limit = 50)
    {
        string interval = IntervalMap(rawInterval);
        
        // --- BINANCE DISABLED GLOBALLY ---
        // As per user request, Binance OTC logic is completely disabled.
        // We solely rely on TwelveData (Real Forex).
        if (originalAsset != null)
        {
            var tdResult = await TwelveDataService.FetchCandlesAsync(originalAsset, interval, limit);
            if (tdResult != null)
            {
                BotLogger.Info($"[MarketDataFetcher] Binance globally disabled. Served TwelveData for {originalAsset}");
                return (tdResult.Value.prices, tdResult.Value.volumes);
            }
        }
        
        throw new ExchangeUnavailableException("Binance is disabled", "Биржа отключена или недоступна на выходных.");
    }

    
    public async Task<double?> FetchHistoricalPriceAsync(string symbol, long endTimeMs)
    {
        string url = $"https://api.binance.com/api/v3/klines?symbol={symbol}&interval=1m&endTime={endTimeMs}&limit=1";
        var httpFactory = MiniAppController.HttpFactory
            ?? throw new InvalidOperationException("[MarketDataFetcher] HttpFactory РЅРµ РёРЅРёС†РёР°Р»РёР·РёСЂРѕРІР°РЅ.");
        var arr = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync(httpFactory.CreateClient("Binance"), new Uri(url), ValutaBotJsonContext.Default.DoubleArrayArray);
        if (arr != null && arr.Length > 0 && arr[0].Length >= 5)
        {
            return arr[0][4];
        }
        return null;
    }
}




