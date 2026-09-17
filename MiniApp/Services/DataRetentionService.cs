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
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(30);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[DataRetention] Service started. Will clean old data every 24h.");
        
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
        
        var cutoff = DateTime.UtcNow.Subtract(_retentionPeriod).ToString("yyyy-MM-ddTHH:mm:ssZ");
        
        using var conn = DbConnectionFactory.GetConnection();
        
        int histDeleted = await conn.ExecuteAsync("DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff });
        int subDeleted = await conn.ExecuteAsync("DELETE FROM subminute_candles WHERE open_time < @cutoff", new { cutoff });
        
        if (histDeleted > 0 || subDeleted > 0)
        {
            BotLogger.Info($"[DataRetention] Pruned old data (older than 30d): {histDeleted} historical candles, {subDeleted} subminute candles.");
        }
    }
}
