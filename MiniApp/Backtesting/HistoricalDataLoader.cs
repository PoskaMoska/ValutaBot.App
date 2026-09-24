using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.MiniApp;
using Npgsql;

namespace ValutaBot.App.MiniApp.Backtesting
{
    public static class HistoricalDataLoader
    {
        private const string DefaultSymbol = "EUR/USD";

        public static async Task<MiniAppController.OhlcCandle[]> LoadAsync(
            int totalCandles,
            string interval     = "1min",
            string symbol       = DefaultSymbol,
            bool   forceRefresh = false) 
        {
            string safeSymbol = symbol.Replace("/", "");
            string dbInterval = interval == "1min" ? "1m" : interval;

            Console.WriteLine($"[Loader] Loading {totalCandles} candles for {safeSymbol} ({dbInterval}) directly from PostgreSQL...");

            var all = new List<CachedOhlc>();
            
            using var conn = DbConnectionFactory.GetConnection();
            if (conn == null)
            {
                Console.WriteLine("[Loader] ERROR: No Database connection available.");
                return Array.Empty<MiniAppController.OhlcCandle>();
            }
            
            await conn.OpenAsync();

            string sql = @"
                SELECT open_time, open, high, low, close, volume 
                FROM historical_candles 
                WHERE asset = @asset AND interval = @interval 
                ORDER BY open_time DESC 
                LIMIT @limit";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("asset", safeSymbol);
            cmd.Parameters.AddWithValue("interval", dbInterval);
            cmd.Parameters.AddWithValue("limit", totalCandles);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                all.Add(new CachedOhlc(
                    reader.GetDouble(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    DateTime.Parse(reader.GetString(0))
                ));
            }

            Console.WriteLine($"[Loader] Fetched {all.Count} candles from DB. Sorting chronologically...");
            all.Sort((a, b) => a.Dt.CompareTo(b.Dt));
            return ToCandleArray(all);
        }

        private static MiniAppController.OhlcCandle[] ToCandleArray(List<CachedOhlc> src)
        {
            var r = new MiniAppController.OhlcCandle[src.Count];
            for (int i = 0; i < src.Count; i++)
                r[i] = new MiniAppController.OhlcCandle(src[i].O, src[i].H, src[i].L, src[i].C, src[i].V, src[i].Dt);
            return r;
        }

        private record CachedOhlc(double O, double H, double L, double C, double V, DateTime Dt);
    }
}
