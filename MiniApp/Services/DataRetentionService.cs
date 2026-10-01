using Dapper;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.Core;

namespace ValutaBot.MiniApp.Services;

public class DataRetentionService : BackgroundService
{
    private readonly TimeSpan _historicalRetention = TimeSpan.FromDays(30);
    private readonly TimeSpan _subminuteRetention = TimeSpan.FromDays(3); // Оставляем только 3 дня (чтобы покрыть выходные), так как это гигантский объем тиков
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1); // Чистим каждый час, а не раз в сутки

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[DataRetention] Service started. Will clean subminute data (3d) and historical data (30d) every hour.");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanOldDataAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                BotLogger.Error("[DataRetention] Error during cleanup cycle", ex);
            }
            
            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private async Task CleanOldDataAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        
        var histCutoff = DateTime.UtcNow.Subtract(_historicalRetention).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var subCutoff = DateTime.UtcNow.Subtract(_subminuteRetention).ToString("yyyy-MM-ddTHH:mm:ssZ");
        
        using var conn = DbConnectionFactory.GetConnection();
        
        int histDeleted = await conn.ExecuteAsync("DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff = histCutoff });
        int subDeleted = await conn.ExecuteAsync("DELETE FROM subminute_candles WHERE open_time < @cutoff", new { cutoff = subCutoff });
        
        if (histDeleted > 0 || subDeleted > 0)
        {
            BotLogger.Info($"[DataRetention] Pruned old data: {histDeleted} historical (>30d), {subDeleted} subminute (>3d).");
        }
    }
}
