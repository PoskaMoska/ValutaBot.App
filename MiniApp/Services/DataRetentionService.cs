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
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private bool _isFirstRun = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[DataRetention] Service started. 7-day PO subminute, 48h SHADOW outcomes, 12h features_json, TRUNCATE po_ticks, VACUUM maintenance.");
        
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

    public static async Task RunEmergencyCleanupAsync()
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        using var conn = DbConnectionFactory.GetConnection();
        await conn.OpenAsync();
        await ExecuteMaintenanceCycleAsync(conn, isEmergency: true);
    }

    private async Task CleanOldDataAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        
        using var conn = DbConnectionFactory.GetConnection();
        await conn.OpenAsync(ct);

        bool doVacuumFull = _isFirstRun;
        _isFirstRun = false;

        await ExecuteMaintenanceCycleAsync(conn, isEmergency: doVacuumFull);
    }

    public static async Task ExecuteMaintenanceCycleAsync(System.Data.Common.DbConnection conn, bool isEmergency = false)
    {
        // 1. Instantly free disk from unused raw po_ticks table
        try
        {
            await conn.ExecuteAsync("TRUNCATE TABLE po_ticks;", commandTimeout: 60);
            BotLogger.Info("[DataRetention] Truncated po_ticks table (reclaimed 100% tick disk space).");
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[DataRetention] Truncate po_ticks notice: {ex.Message}");
        }

        var histCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(30)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var poSubCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(7)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var legacySubCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(6)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var shadowCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(2));
        var featuresCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(12));
        var stalePendingCutoff = DateTime.UtcNow.AddHours(-2).ToString("o");

        int histDeleted = await ExecuteSafeDeleteAsync(conn, "historical_candles",
            "DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff = histCutoff });
        int subPoDeleted = await ExecuteSafeDeleteAsync(conn, "subminute_candles (PO)",
            "DELETE FROM subminute_candles WHERE asset LIKE '%_PO' AND open_time < @cutoff", new { cutoff = poSubCutoff });
        int subLegacyDeleted = await ExecuteSafeDeleteAsync(conn, "subminute_candles (legacy)",
            "DELETE FROM subminute_candles WHERE asset NOT LIKE '%_PO' AND open_time < @cutoff", new { cutoff = legacySubCutoff });
        int subDeleted = subPoDeleted + subLegacyDeleted;

        // 2. Удаляем старые SHADOW-строки старше 48 часов (REAL BUY/PUT хранятся бессрочно)
        int shadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (SHADOW >48h)",
            "DELETE FROM trade_outcomes WHERE direction NOT IN ('BUY', 'PUT') AND created_at::timestamptz < @cutoff",
            new { cutoff = shadowCutoff });

        // 3. Для SHADOW-строк старше 12 часов обнуляем features_json (свечи берутся из subminute_candles)
        int featuresCleared = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (clear features_json >12h)",
            "UPDATE trade_outcomes SET features_json = NULL WHERE direction NOT IN ('BUY', 'PUT') AND features_json IS NOT NULL AND created_at::timestamptz < @cutoff",
            new { cutoff = featuresCutoff });

        int stalePendingDeleted = await ExecuteSafeDeleteAsync(conn, "pending_trades",
            "DELETE FROM pending_trades WHERE verify_at < @cutoff",
            new { cutoff = stalePendingCutoff });

        BotLogger.Info($"[DataRetention] Pruned: {shadowDeleted} SHADOW (>48h), {featuresCleared} features_json cleared (>12h), {subPoDeleted} PO subminute (>7d), {histDeleted} historical, {stalePendingDeleted} stale pending.");

        // 4. Vacuum: VACUUM FULL physically shrinks tables on disk and returns bytes to Linux filesystem (Railway volume)
        try
        {
            if (isEmergency)
            {
                BotLogger.Info("[DataRetention] Running VACUUM FULL on trade_outcomes and subminute_candles to shrink disk files...");
                await conn.ExecuteAsync("VACUUM FULL trade_outcomes;", commandTimeout: 300);
                await conn.ExecuteAsync("VACUUM FULL subminute_candles;", commandTimeout: 300);
                await conn.ExecuteAsync("VACUUM FULL pending_trades;", commandTimeout: 60);
                BotLogger.Info("[DataRetention] VACUUM FULL completed. Disk space physically returned to volume.");
            }
            else
            {
                // Regular hourly pass: VACUUM ANALYZE reclaims dead pages for reuse without table rewrite lock
                await conn.ExecuteAsync("VACUUM (ANALYZE) trade_outcomes;", commandTimeout: 120);
                await conn.ExecuteAsync("VACUUM (ANALYZE) subminute_candles;", commandTimeout: 120);
                await conn.ExecuteAsync("VACUUM (ANALYZE) pending_trades;", commandTimeout: 60);
            }
        }
        catch (Exception exVac)
        {
            BotLogger.Warn($"[DataRetention] Vacuum notice: {exVac.Message}");
        }
    }

    private static async Task<int> ExecuteSafeDeleteAsync(System.Data.Common.DbConnection conn, string tableLabel, string sql, object param)
    {
        try
        {
            return await conn.ExecuteAsync(sql, param, commandTimeout: 120);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[DataRetention] Cleanup skipped for {tableLabel}: {ex.Message}");
            return 0;
        }
    }
}
