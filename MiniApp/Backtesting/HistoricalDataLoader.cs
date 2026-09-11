using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Backtesting
{
    /// <summary>
    /// Загружает исторические OHLC-свечи EUR/USD из TwelveData API.
    /// Кэширует результат на диск — повторные запуски не тратят API-кредиты.
    /// </summary>
    public static class HistoricalDataLoader
    {
        private const string CacheDir      = "Logs/backtest_cache";
        private const int    PageSize      = 5000;
        private const string DefaultSymbol = "EUR/USD";

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<MiniAppController.OhlcCandle[]> LoadAsync(
            int totalCandles,
            string interval     = "1min",
            string symbol       = DefaultSymbol,
            bool   forceRefresh = false)
        {
            string safeSymbol = symbol.Replace("/", "");
            string cacheFile  = Path.Combine(CacheDir, $"{safeSymbol}_{interval}_{totalCandles}.json");
            Directory.CreateDirectory(CacheDir);

            if (!forceRefresh && File.Exists(cacheFile))
            {
                Console.WriteLine($"[Loader] Кэш найден: {cacheFile}");
                var cached = JsonSerializer.Deserialize<List<CachedOhlc>>(
                    await File.ReadAllTextAsync(cacheFile), _jsonOpts);
                return cached == null ? Array.Empty<MiniAppController.OhlcCandle>() : ToCandleArray(cached);
            }

            string apiKey = Environment.GetEnvironmentVariable("TwelveDataApiKey") ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("TwelveDataApiKey env var is not set — backtest data load requires it.");
            Console.WriteLine($"[Loader] Загружаю {totalCandles} свечей {symbol} ({interval}) из TwelveData...");

            var all     = new List<CachedOhlc>(totalCandles);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ValutaBot/1.0");
            DateTime endDate = DateTime.UtcNow;

            while (all.Count < totalCandles)
            {
                int needed = Math.Min(PageSize, totalCandles - all.Count);
                string endStr = endDate.ToString("yyyy-MM-dd HH:mm:ss");
                string url = $"https://api.twelvedata.com/time_series"
                           + $"?symbol={Uri.EscapeDataString(symbol)}"
                           + $"&interval={interval}"
                           + $"&outputsize={needed}"
                           + $"&end_date={Uri.EscapeDataString(endStr)}"
                           + $"&format=JSON"
                           + $"&apikey={apiKey}";

                Console.WriteLine($"[Loader] Запрос {all.Count}/{totalCandles} (до {endStr})...");
                string json;
                try   { var r = await http.GetAsync(url); json = await r.Content.ReadAsStringAsync(); }
                catch (Exception ex) { Console.WriteLine($"[Loader] HTTP: {ex.Message}"); break; }

                TwelvePageResponse? page;
                try   { page = JsonSerializer.Deserialize<TwelvePageResponse>(json, _jsonOpts); }
                catch { Console.WriteLine("[Loader] JSON parse error."); break; }

                if (page?.Status == "error") { Console.WriteLine($"[Loader] API: {page.Message}"); break; }
                if (page?.Values == null || page.Values.Count == 0) { Console.WriteLine("[Loader] Нет данных."); break; }

                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Any;
                foreach (var v in page.Values)
                {
                    if (double.TryParse(v.Open, ns, ci, out double o) &&
                        double.TryParse(v.High, ns, ci, out double h) &&
                        double.TryParse(v.Low,  ns, ci, out double l) &&
                        double.TryParse(v.Close,ns, ci, out double c) &&
                        DateTime.TryParse(v.Datetime, out DateTime dt))
                        all.Add(new CachedOhlc(o, h, l, c, dt));
                }

                if (DateTime.TryParse(page.Values[^1].Datetime, out DateTime oldest))
                    endDate = oldest.AddSeconds(-1);
                else break;

                Console.WriteLine($"[Loader] Получено {all.Count} свечей. Ожидаю 8с...");
                await Task.Delay(8000); // 8 req/min TwelveData
            }

            all.Sort((a, b) => a.Dt.CompareTo(b.Dt));
            Console.WriteLine($"[Loader] Загружено {all.Count} свечей. Сохраняю кэш...");
            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(all, _jsonOpts));
            return ToCandleArray(all);
        }

        private static MiniAppController.OhlcCandle[] ToCandleArray(List<CachedOhlc> src)
        {
            var r = new MiniAppController.OhlcCandle[src.Count];
            for (int i = 0; i < src.Count; i++)
                r[i] = new MiniAppController.OhlcCandle(src[i].O, src[i].H, src[i].L, src[i].C, 0, src[i].Dt);
            return r;
        }

        private record CachedOhlc(double O, double H, double L, double C, DateTime Dt);
        private class TwelvePageResponse
        {
            public string? Status  { get; set; }
            public string? Message { get; set; }
            public List<TwelveCandle>? Values { get; set; }
        }
        private class TwelveCandle
        {
            public string? Datetime { get; set; }
            public string? Open  { get; set; }
            public string? High  { get; set; }
            public string? Low   { get; set; }
            public string? Close { get; set; }
        }
    }
}
