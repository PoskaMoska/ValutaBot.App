using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace ValutaBot.MiniApp;

public static class TiingoService
{
    private static readonly IMemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions());
    private static string? _apiKey;

    public static string GetApiKey()
    {
        if (_apiKey != null) return _apiKey;
        _apiKey = Environment.GetEnvironmentVariable("TIINGO_API_KEY");
        return _apiKey ?? "";
    }

    public static async Task<(double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles)?> FetchCandlesAsync(string rawAsset, string interval, int limit = 100, int cacheTtlSeconds = 45)
    {
        string key = $"TIINGO_DATA_{AssetSanitizer.Sanitize(rawAsset)}_{interval.ToLower()}";

        if (cacheTtlSeconds > 0 && _memoryCache.TryGetValue(key, out (double[] prices, double[] volumes, MiniAppController.OhlcCandle[] candles) cachedData))
        {
            BotLogger.Info($"[Tiingo] Using IMemoryCache data for {rawAsset} ({interval})");
            int take = Math.Min(limit, cachedData.prices.Length);
            return (
                cachedData.prices.TakeLast(take).ToArray(),
                cachedData.volumes.TakeLast(take).ToArray(),
                cachedData.candles.TakeLast(take).ToArray()
            );
        }

        string apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey)) 
        {
            BotLogger.Error("[Tiingo] TIINGO_API_KEY is not set.");
            return null;
        }

        string cleanAsset = AssetSanitizer.Sanitize(rawAsset).ToLower();
        // Tiingo Forex expects ticker like 'eurusd'. 
        // Our limit logic needs to fetch enough to cover the requested amount.
        // Tiingo's /prices endpoint for 1min gives up to latest available intraday data.
        
        string freq = interval.ToLower() switch {
            "m1" => "1min",
            "1m" => "1min",
            "m5" => "5min",
            "5m" => "5min",
            "m15" => "15min",
            "h1" => "1hour",
            "h4" => "4hour",
            "d1" => "1day",
            "1day" => "1day",
            _ => "1min" // fallback
        };

        // If subminute is requested accidentally via REST, fallback to 1min
        if (freq.StartsWith("s")) freq = "1min";

        string url = $"https://api.tiingo.com/tiingo/fx/prices?tickers={cleanAsset}&resampleFreq={freq}&token={apiKey}";
        
        BotLogger.Info($"[Tiingo] Fetching REST API for {cleanAsset} {freq} (req limit: {limit})...");

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var priceData = root[0].GetProperty("priceData");
                if (priceData.ValueKind == JsonValueKind.Array)
                {
                    var cList = new List<MiniAppController.OhlcCandle>();
                    var pList = new List<double>();
                    var vList = new List<double>();

                    foreach (var element in priceData.EnumerateArray())
                    {
                        double open = element.GetProperty("open").GetDouble();
                        double high = element.GetProperty("high").GetDouble();
                        double low = element.GetProperty("low").GetDouble();
                        double close = element.GetProperty("close").GetDouble();
                        DateTime date = element.GetProperty("date").GetDateTime().ToUniversalTime();

                        var candle = new MiniAppController.OhlcCandle(open, high, low, close, 0, date);
                        cList.Add(candle);
                        pList.Add(close);
                        vList.Add(0);
                    }

                    if (cList.Count > 0)
                    {
                        var tuple = (pList.ToArray(), vList.ToArray(), cList.ToArray());
                        
                        if (cacheTtlSeconds > 0)
                        {
                            _memoryCache.Set(key, tuple, TimeSpan.FromSeconds(cacheTtlSeconds));
                        }

                        int take = Math.Min(limit, cList.Count);
                        return (
                            pList.TakeLast(take).ToArray(),
                            vList.TakeLast(take).ToArray(),
                            cList.TakeLast(take).ToArray()
                        );
                    }
                }
            }
            
            BotLogger.Error($"[Tiingo] Invalid or empty format returned for {cleanAsset}.");
            return null;
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[Tiingo] HTTP Error fetching {cleanAsset}: {ex.Message}");
            return null;
        }
    }
}
