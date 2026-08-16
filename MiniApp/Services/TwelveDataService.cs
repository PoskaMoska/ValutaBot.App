using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using System.Threading.RateLimiting;

namespace ValutaBot.MiniApp;

public static partial class TwelveDataService
{
    
    private static readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private static string? _apiKey;

    private static readonly SlidingWindowRateLimiter _rateLimiter = new(
        new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 7,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            AutoReplenishment = true
        });

    public static string GetApiKey()
    {
        _apiKey ??= Environment.GetEnvironmentVariable("TwelveDataApiKey") ?? "";
        return _apiKey;
    }

    public static async Task<(double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles)?> FetchCandlesAsync(string rawAsset, string interval, int limit = 100, int cacheTtlSeconds = 10)
    {
        string key = $"TWELVE_DATA_{AssetSanitizer.Sanitize(rawAsset)}_{interval.ToLower()}";

        if (cacheTtlSeconds > 0 && _memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) cachedData))
        {
            BotLogger.Info($"[TwelveData] Using IMemoryCache data for {rawAsset} ({interval})");
            return cachedData;
        }

        string apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey)) return null;

        using var lease = _rateLimiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            if (_memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) lastData))
            {
                BotLogger.Info($"[TwelveData] Rate limit safety triggered. Serving IMemoryCache for {rawAsset} ({interval}).");
                return lastData;
            }
            BotLogger.Warn($"[TwelveData] Rate limit safety triggered, but no cache exists for {rawAsset} ({interval})!");
            return null;
        }

        try
        {
            string symbol = ConvertToTwelveSymbol(rawAsset) ?? "";
            string tdInterval = ConvertInterval(interval) ?? "";
            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(tdInterval)) return null;

            string url = $"https://api.twelvedata.com/time_series?symbol={Uri.EscapeDataString(symbol)}&interval={tdInterval}&outputsize={limit}&timezone=UTC&apikey={apiKey}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ValutaBot/1.0");

            using var response = await MiniAppController.HttpFactory!.CreateClient("TwelveData").SendAsync(request);
            
            var doc = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync(response.Content, ValutaBotJsonContext.Default.TwelveDataResponse);

            if (doc?.Status == "error")
            {
                BotLogger.Warn($"[TwelveData] API error for {rawAsset}: {doc.Message}");
                throw new Exception($"TwelveData API error: {doc.Message}");
            }

            if (doc?.Values == null)
            {
                if (_memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) lastData))
                {
                    BotLogger.Warn($"[TwelveData] No values in response, serving IMemoryCache for {rawAsset}");
                    return lastData;
                }
                return null;
            }

            var arr = doc.Values.Where(x => x != null).ToList();
            int count = arr.Count;
            if (count < Math.Min(limit, 10) && count == 0)
            {
                if (_memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) lastData))
                {
                    BotLogger.Warn($"[TwelveData] Too few candles ({count}), serving IMemoryCache for {rawAsset}");
                    return lastData;
                }
                return null;
            }

            var prices = new double[count];
            var volumes = new double[count];
            var ohlc = new MiniAppController.OhlcCandle[count];

            for (int i = 0; i < count; i++)
            {
                int revIdx = count - 1 - i;
                var v = arr[i];
                
                prices[revIdx] = v.Close;
                volumes[revIdx] = v.Volume;
                ohlc[revIdx] = new MiniAppController.OhlcCandle(v.Open, v.High, v.Low, v.Close, v.Volume, string.IsNullOrEmpty(v.Datetime) ? DateTime.UtcNow.AddMinutes(revIdx - count) : DateTime.SpecifyKind(DateTime.Parse(v.Datetime), DateTimeKind.Utc));
            }

            if (cacheTtlSeconds > 0)
            {
                _memoryCache.Set(key, (prices, volumes, ohlc), TimeSpan.FromSeconds(cacheTtlSeconds));
            }
            BotLogger.Info($"[TwelveData] Successfully fetched {prices.Length} candles for {symbol} ({interval})");
            return (prices, volumes, ohlc);
        }
        catch (JsonException jsonEx)
        {
            BotLogger.Warn($"[TwelveData] JSON parse error for {rawAsset}", jsonEx);

            if (_memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) lastData))
            {
                BotLogger.Info($"[TwelveData] Serving IMemoryCache fallback data for {rawAsset}");
                return lastData;
            }
            return null;
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[TwelveData] Fetch failed for {rawAsset}: {ex.Message}");

            if (_memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) lastData))
            {
                BotLogger.Info($"[TwelveData] Serving IMemoryCache fallback data for {rawAsset}");
                return lastData;
            }
            return null;
        }
    }

    public static string? ConvertToTwelveSymbol(string raw)
    {
        string original = AssetSanitizer.Sanitize(raw);
        if (string.IsNullOrEmpty(original)) return null;

        if (original.Contains("GOLD") || original.Contains("XAUUSD")) return "XAU/USD";
        if (original.Contains("SILVER") || original.Contains("XAGUSD")) return "XAG/USD";

        string cleanTicker = original;
        string[] knownStocks = { "AAPL", "TSLA", "AMZN", "GOOGL", "MSFT", "NVDA", "META" };

        if (cleanTicker.EndsWith("USDT") && !knownStocks.Contains(cleanTicker))
        {
            // Convert XXXUSDT → XXXUSD for TwelveData (remove trailing 'T')
            cleanTicker = cleanTicker[..^1];
        }

        if (knownStocks.Contains(cleanTicker))
        {
            return cleanTicker;
        }

        if (original.Contains("/"))
        {
            var parts = original.Split('/');
            if (parts.Length == 2)
            {
                string left = parts[0].Trim();
                string right = parts[1].Trim();
                return $"{left}/{right}";
            }
        }

        if (cleanTicker.Length == 6 || cleanTicker.Length == 7)
        {
            int split = cleanTicker.Length / 2;
            string left = cleanTicker[..split];
            string right = cleanTicker[split..];
            return $"{left}/{right}";
        }

        return null;
    }

    private static string? ConvertInterval(string interval) => interval.ToLower() switch
    {
        "1m" or "m1" => "1min",
        "2m" or "m2" => "2min",
        "3m" or "m3" => "3min",
        "5m" or "m5" => "5min",
        "15m" or "m15" => "15min",
        "30m" or "m30" => "30min",
        "45m" => "45min",
        "1h" or "h1" => "1h",
        "2h" or "h2" => "2h",
        "4h" or "h4" => "4h",
        "1d" or "d1" => "1day",
        _ => "1min"
    };

    public class TwelveDataResponse
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public List<TwelveDataCandle>? Values { get; set; }
    }

    public class TwelveDataCandle
    {
        public string? Datetime { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }
    }
    public class TwelveDataPriceResponse
    {
        public string? Price { get; set; }
    }

    public static async Task<double?> FetchCurrentPriceAsync(string rawAsset)
    {
        string apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey)) return null;
        string symbol = ConvertToTwelveSymbol(rawAsset) ?? "";
        if (string.IsNullOrEmpty(symbol)) return null;

        string url = $"https://api.twelvedata.com/price?symbol={Uri.EscapeDataString(symbol)}&apikey={apiKey}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await MiniAppController.HttpFactory!.CreateClient("TwelveData").SendAsync(request);
        
        var doc = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync(response.Content, ValutaBotJsonContext.Default.TwelveDataPriceResponse);
        
        if (doc != null && double.TryParse(doc.Price, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double price))
        {
            return price;
        }
        return null;
    }
}
