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
    private readonly TimeSpan _poSubminuteRetention = TimeSpan.FromDays(21); // 21 день для накопления датасета нейросетей (po_ticks + subminute_candles *_PO)
    private readonly TimeSpan _legacySubminuteRetention = TimeSpan.FromDays(3); // 3 дня для устаревших не-PO записей
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1); // Чистим каждый час, а не раз в сутки

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[DataRetention] Service started. Will clean PO subminute/ticks (21d), legacy subminute (3d), and historical data (30d) every hour.");
        
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
        var poSubCutoff = DateTime.UtcNow.Subtract(_poSubminuteRetention).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var legacySubCutoff = DateTime.UtcNow.Subtract(_legacySubminuteRetention).ToString("yyyy-MM-ddTHH:mm:ssZ");
        double tickCutoffEpoch = DateTimeOffset.UtcNow.Subtract(_poSubminuteRetention).ToUnixTimeSeconds();
        
        using var conn = DbConnectionFactory.GetConnection();
        
        int histDeleted = await conn.ExecuteAsync("DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff = histCutoff });
        int subPoDeleted = await conn.ExecuteAsync("DELETE FROM subminute_candles WHERE asset LIKE '%_PO' AND open_time < @cutoff", new { cutoff = poSubCutoff });
        int subLegacyDeleted = await conn.ExecuteAsync("DELETE FROM subminute_candles WHERE asset NOT LIKE '%_PO' AND open_time < @cutoff", new { cutoff = legacySubCutoff });
        int subDeleted = subPoDeleted + subLegacyDeleted;
        int tickDeleted = 0;
        try
        {
            tickDeleted = await conn.ExecuteAsync("DELETE FROM po_ticks WHERE ts < @cutoff", new { cutoff = tickCutoffEpoch });
        }
        catch { }
        
        if (histDeleted > 0 || subDeleted > 0 || tickDeleted > 0)
        {
            BotLogger.Info($"[DataRetention] Pruned old data: {histDeleted} historical (>30d), {subPoDeleted} PO subminute (>21d), {subLegacyDeleted} legacy subminute (>3d), {tickDeleted} po_ticks (>21d).");
        }
    }
}
