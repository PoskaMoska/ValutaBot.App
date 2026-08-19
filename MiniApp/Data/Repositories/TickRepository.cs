using System;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp
{
    public static class TickRepository
    {
        public static async Task SaveCandleAsync(string asset, string interval, DateTime openTime, double open, double high, double low, double close, double volume)
        {
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                string openTimeStr = openTime.ToString("o"); // ISO 8601
                
                // Insert or ignore
                await conn.ExecuteAsync(@"
                    INSERT INTO subminute_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume)
                    VALUES (@Asset, @Interval, @OpenTime, @Open, @High, @Low, @Close, @Volume)
                    ON CONFLICT (asset, interval, open_time) DO NOTHING;
                ", new { Asset = asset, Interval = interval, OpenTime = openTimeStr, Open = open, High = high, Low = low, Close = close, Volume = volume });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TickRepository] Failed to save {interval} candle for {asset}: {ex.Message}");
            }
        }

        public static async Task PruneOldCandlesAsync(int daysToKeep = 14)
        {
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                string cutoff = DateTime.UtcNow.AddDays(-daysToKeep).ToString("o");
                int deleted = await conn.ExecuteAsync(@"
                    DELETE FROM subminute_candles 
                    WHERE open_time < @Cutoff;
                ", new { Cutoff = cutoff });

                if (deleted > 0)
                {
                    BotLogger.Info($"[TickRepository] Pruned {deleted} old subminute candles (>{daysToKeep} days).");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TickRepository] Failed to prune old candles: {ex.Message}");
            }
        }
    }
}
