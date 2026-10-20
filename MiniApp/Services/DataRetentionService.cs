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
    private readonly TimeSpan _poSubminuteRetention = TimeSpan.FromDays(10); // 10 дней для subminute_candles *_PO (~500 МБ)
    private readonly TimeSpan _poTicksRetention = TimeSpan.FromDays(2);      // 48 часов для сырых po_ticks (~280 МБ)
    private readonly TimeSpan _shadowOutcomesRetention = TimeSpan.FromDays(8); // 8 дней для фоновых SHADOW/HOLD в trade_outcomes (REAL хранятся бессрочно)
    private readonly TimeSpan _legacySubminuteRetention = TimeSpan.FromDays(1);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[DataRetention] Service started. Will clean po_ticks (48h), SHADOW/HOLD outcomes (8d), PO subminute (10d), and historical data (30d) every hour.");
        
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
        var shadowCutoff = DateTime.UtcNow.Subtract(_shadowOutcomesRetention);
        var stalePendingCutoff = DateTime.UtcNow.AddHours(-24).ToString("o");
        double tickCutoffEpoch = DateTimeOffset.UtcNow.Subtract(_poTicksRetention).ToUnixTimeSeconds();
        
        using var conn = DbConnectionFactory.GetConnection();
        await conn.OpenAsync(ct);
        
        int histDeleted = await ExecuteSafeDeleteAsync(conn, "historical_candles",
            "DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff = histCutoff });
        int subPoDeleted = await ExecuteSafeDeleteAsync(conn, "subminute_candles (PO)",
            "DELETE FROM subminute_candles WHERE asset LIKE '%_PO' AND open_time < @cutoff", new { cutoff = poSubCutoff });
        int subLegacyDeleted = await ExecuteSafeDeleteAsync(conn, "subminute_candles (legacy)",
            "DELETE FROM subminute_candles WHERE asset NOT LIKE '%_PO' AND open_time < @cutoff", new { cutoff = legacySubCutoff });
        int subDeleted = subPoDeleted + subLegacyDeleted;
        int shadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (SHADOW/HOLD)",
            "DELETE FROM trade_outcomes WHERE direction NOT IN ('BUY', 'PUT') AND created_at::timestamptz < @cutoff",
            new { cutoff = shadowCutoff });
        int stalePendingDeleted = await ExecuteSafeDeleteAsync(conn, "pending_trades",
            "DELETE FROM pending_trades WHERE verify_at < @cutoff",
            new { cutoff = stalePendingCutoff });
        int tickDeleted = await ExecuteSafeDeleteAsync(conn, "po_ticks",
            "DELETE FROM po_ticks WHERE ts < @cutoff",
            new { cutoff = tickCutoffEpoch });
        
        if (histDeleted > 0 || subDeleted > 0 || tickDeleted > 0 || shadowDeleted > 0 || stalePendingDeleted > 0)
        {
            BotLogger.Info($"[DataRetention] Pruned old data: {histDeleted} historical (>30d), {subPoDeleted} PO subminute (>10d), {shadowDeleted} SHADOW/HOLD outcomes (>8d), {tickDeleted} po_ticks (>48h), {stalePendingDeleted} stale pending.");
        }
    }

    private static async Task<int> ExecuteSafeDeleteAsync(System.Data.Common.DbConnection conn, string tableLabel, string sql, object param)
    {
        try
        {
            return await conn.ExecuteAsync(sql, param, commandTimeout: 60);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[DataRetention] Cleanup skipped for {tableLabel}: {ex.Message}");
            return 0;
        }
    }
}
