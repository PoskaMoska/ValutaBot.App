using Dapper;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.Core;

namespace ValutaBot.MiniApp.Services;

public enum StorageZone
{
    Green,  // < 3.0 GB (< 60%): Full 30-day historical depth, no eviction
    Yellow, // 3.0 - 3.9 GB (60% - 78%): Importance sampling active, striding chop, 21d subminute
    Red     // > 3.9 GB (> 78%): High capacity defense, 10d subminute, soft vacuum, Telegram notice
}

public record StorageTelemetry(
    double DatabaseSizeMb,
    double MaxVolumeSizeMb,
    double UsagePercent,
    StorageZone ActiveZone,
    Dictionary<string, double> TableSizesMb,
    DateTime MeasuredAtUtc
);

/// <summary>
/// Intelligent Adaptive Database Storage Governor.
/// Replaces blunt hardcoded retention with watermark-based tiering, importance sampling,
/// and zero-downtime table-level autovacuum tuning.
/// </summary>
public class DataRetentionService : BackgroundService
{
    private const double MaxVolumeCapacityMb = 5000.0; // Railway 5 GB starter volume
    private const double GreenThresholdMb = 3000.0;     // 60%
    private const double YellowThresholdMb = 3900.0;    // 78%

    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private static StorageTelemetry? _latestTelemetry;
    private static bool _tableTuningApplied = false;

    public static StorageTelemetry? GetLatestTelemetry() => _latestTelemetry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[SmartStorageGovernor] Service started. Real-time watermark monitoring, importance sampling, adaptive 30d retention.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteSmartGovernorCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                BotLogger.Error("[SmartStorageGovernor] Error during adaptive maintenance cycle", ex);
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    public static async Task<StorageTelemetry> RunEmergencyCleanupAsync(bool allowVacuumFull = false)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
            throw new InvalidOperationException("DATABASE_URL is not configured.");

        using var conn = DbConnectionFactory.GetConnection();
        await conn.OpenAsync();

        return await ExecuteCycleInternalAsync(conn, forceEmergencyVacuum: allowVacuumFull);
    }

    private async Task ExecuteSmartGovernorCycleAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;

        using var conn = DbConnectionFactory.GetConnection();
        await conn.OpenAsync(ct);

        await ExecuteCycleInternalAsync(conn, forceEmergencyVacuum: false);
    }

    private static async Task<StorageTelemetry> ExecuteCycleInternalAsync(System.Data.Common.DbConnection conn, bool forceEmergencyVacuum)
    {
        // 1. One-time zero-downtime PostgreSQL autovacuum tuning per table
        if (!_tableTuningApplied)
        {
            await ApplyAutovacuumTuningAsync(conn);
            _tableTuningApplied = true;
        }

        // 2. Telemetry: Query actual database size and table breakdown
        var telemetry = await MeasureStorageTelemetryAsync(conn);
        _latestTelemetry = telemetry;

        BotLogger.Info($"[SmartStorageGovernor] DB Size: {telemetry.DatabaseSizeMb:F1} MB / {telemetry.MaxVolumeSizeMb:F0} MB ({telemetry.UsagePercent:F1}%). Zone: {telemetry.ActiveZone}.");

        // 3. Always truncate unused raw tick queue (po_ticks is purely transient)
        try
        {
            await conn.ExecuteAsync("TRUNCATE TABLE po_ticks;", commandTimeout: 60);
        }
        catch { }

        // 4. Adaptive retention parameters based on Storage Zone
        TimeSpan subminuteRetention;
        TimeSpan shadowRetention;
        bool doImportanceSampling;
        bool doRedZonePruning;

        switch (telemetry.ActiveZone)
        {
            case StorageZone.Green:
                // Full historical depth: 30 days of subminute candles and shadow records
                subminuteRetention = TimeSpan.FromDays(30);
                shadowRetention = TimeSpan.FromDays(30);
                doImportanceSampling = false;
                doRedZonePruning = false;
                break;

            case StorageZone.Yellow:
                // Balanced depth: 21 days subminute, 14 days shadow with importance sampling
                subminuteRetention = TimeSpan.FromDays(21);
                shadowRetention = TimeSpan.FromDays(14);
                doImportanceSampling = true;
                doRedZonePruning = false;
                break;

            case StorageZone.Red:
            default:
                // Defensive tier: 10 days subminute, 5 days shadow
                subminuteRetention = TimeSpan.FromDays(10);
                shadowRetention = TimeSpan.FromDays(5);
                doImportanceSampling = true;
                doRedZonePruning = true;
                break;
        }

        var subCutoff = DateTime.UtcNow.Subtract(subminuteRetention).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var legacySubCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(12)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var histCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(30)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var shadowCutoff = DateTime.UtcNow.Subtract(shadowRetention);
        var stalePendingCutoff = DateTime.UtcNow.AddHours(-2).ToString("o");

        // 5. Prune expired subminute and historical candles according to current zone
        int subPoDeleted = await ExecuteSafeDeleteAsync(conn, "subminute_candles (PO)",
            "DELETE FROM subminute_candles WHERE asset LIKE '%_PO' AND open_time < @cutoff", new { cutoff = subCutoff });
        int subLegacyDeleted = await ExecuteSafeDeleteAsync(conn, "subminute_candles (legacy)",
            "DELETE FROM subminute_candles WHERE asset NOT LIKE '%_PO' AND open_time < @cutoff", new { cutoff = legacySubCutoff });
        int histDeleted = await ExecuteSafeDeleteAsync(conn, "historical_candles",
            "DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff = histCutoff });
        int pendingDeleted = await ExecuteSafeDeleteAsync(conn, "pending_trades",
            "DELETE FROM pending_trades WHERE verify_at < @cutoff", new { cutoff = stalePendingCutoff });

        // 6. Importance Sampling for trade_outcomes
        // Golden Tier: All real trades (direction IN ('BUY', 'PUT')) and significant events are preserved indefinitely!
        int shadowDeleted = 0;
        int chopStrided = 0;
        int jsonCompacted = 0;

        if (doRedZonePruning)
        {
            // Red Zone: Prune non-essential flat chop older than shadowCutoff (5 days)
            shadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Red Zone Chop Pruning)",
                @"DELETE FROM trade_outcomes
                  WHERE direction NOT IN ('BUY', 'PUT')
                    AND created_at::timestamptz < @cutoff
                    AND smc_bos_dir = 'NONE'
                    AND smc_has_ob = false
                    AND COALESCE(ABS(pnl_bps), 0) <= 1e-4",
                new { cutoff = shadowCutoff });
        }

        if (doImportanceSampling)
        {
            // Yellow/Red Zone: Stride redundant flat chop older than 48 hours (keep 1 of 5)
            chopStrided = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Importance Sampling Stride)",
                @"DELETE FROM trade_outcomes
                  WHERE direction NOT IN ('BUY', 'PUT')
                    AND created_at::timestamptz < NOW() - INTERVAL '48 hours'
                    AND smc_bos_dir = 'NONE'
                    AND smc_has_ob = false
                    AND COALESCE(ABS(pnl_bps), 0) <= 1e-4
                    AND MOD(ABS(hashtext(id)), 5) != 0",
                new { });

            // Compact heavy candle arrays from features_json older than 24h into normalized metadata
            // (export_dataset.py reconstructs candles from subminute_candles via SubminuteCandleStore)
            jsonCompacted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Features JSON Compaction)",
                @"UPDATE trade_outcomes
                  SET features_json = (features_json::jsonb - 'Candles' - 'candles' - 'MtfCandles' - 'mtfCandles' - 'mtf_candles')::text
                  WHERE created_at::timestamptz < NOW() - INTERVAL '24 hours'
                    AND features_json IS NOT NULL
                    AND features_json LIKE '%""Candles""%'",
                new { });
        }

        // 7. Non-blocking VACUUM maintenance
        try
        {
            if (forceEmergencyVacuum)
            {
                BotLogger.Warn("[SmartStorageGovernor] Manual VACUUM FULL requested. Rewriting tables to release filesystem blocks...");
                await conn.ExecuteAsync("VACUUM FULL trade_outcomes;", commandTimeout: 300);
                await conn.ExecuteAsync("VACUUM FULL subminute_candles;", commandTimeout: 300);
                await conn.ExecuteAsync("VACUUM FULL pending_trades;", commandTimeout: 60);
                BotLogger.Info("[SmartStorageGovernor] VACUUM FULL complete.");
            }
            else
            {
                // Regular online pass: Zero-downtime concurrent VACUUM (ANALYZE)
                // Reclaims dead pages for reuse without locking any reads or writes
                await conn.ExecuteAsync("VACUUM (ANALYZE) trade_outcomes;", commandTimeout: 120);
                await conn.ExecuteAsync("VACUUM (ANALYZE) subminute_candles;", commandTimeout: 120);
                await conn.ExecuteAsync("VACUUM (ANALYZE) pending_trades;", commandTimeout: 60);
            }
        }
        catch (Exception exVac)
        {
            BotLogger.Warn($"[SmartStorageGovernor] Non-blocking vacuum notice: {exVac.Message}");
        }

        // Update telemetry after maintenance
        telemetry = await MeasureStorageTelemetryAsync(conn);
        _latestTelemetry = telemetry;

        BotLogger.Info($"[SmartStorageGovernor] Maintenance complete. Zone: {telemetry.ActiveZone}. Cleaned: {subPoDeleted} PO candles, {shadowDeleted} chop, {chopStrided} strided, {jsonCompacted} compacted, {pendingDeleted} pending. New DB Size: {telemetry.DatabaseSizeMb:F1} MB.");

        return telemetry;
    }

    private static async Task ApplyAutovacuumTuningAsync(System.Data.Common.DbConnection conn)
    {
        try
        {
            // Zero-downtime PostgreSQL table storage tuning:
            // autovacuum_vacuum_scale_factor = 0.05 instructs Postgres to automatically clean dead tuples
            // every 5% table modifications instead of the default 20%, keeping bloat near zero.
            await conn.ExecuteAsync(@"
                ALTER TABLE trade_outcomes SET (
                    autovacuum_vacuum_scale_factor = 0.05,
                    autovacuum_vacuum_cost_limit = 1000,
                    autovacuum_vacuum_cost_delay = 2
                );
                ALTER TABLE subminute_candles SET (
                    autovacuum_vacuum_scale_factor = 0.05,
                    autovacuum_vacuum_cost_limit = 1000,
                    autovacuum_vacuum_cost_delay = 2
                );
                ALTER TABLE pending_trades SET (
                    autovacuum_vacuum_scale_factor = 0.02
                );
            ", commandTimeout: 30);
            BotLogger.Info("[SmartStorageGovernor] Applied table-level autovacuum tuning (scale_factor=0.05).");
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[SmartStorageGovernor] Table autovacuum tuning notice (non-fatal): {ex.Message}");
        }
    }

    public static async Task<StorageTelemetry> MeasureStorageTelemetryAsync(System.Data.Common.DbConnection conn)
    {
        long dbSizeBytes = 0;
        try
        {
            dbSizeBytes = await conn.ExecuteScalarAsync<long>("SELECT pg_database_size(current_database());");
        }
        catch { }

        double dbSizeMb = dbSizeBytes / (1024.0 * 1024.0);
        double usagePct = Math.Clamp((dbSizeMb / MaxVolumeCapacityMb) * 100.0, 0.0, 100.0);

        StorageZone zone = dbSizeMb switch
        {
            < GreenThresholdMb => StorageZone.Green,
            < YellowThresholdMb => StorageZone.Yellow,
            _ => StorageZone.Red
        };

        var tableSizes = new Dictionary<string, double>();
        try
        {
            var rows = await conn.QueryAsync<(string tableName, long totalBytes)>(@"
                SELECT
                    relname AS tableName,
                    pg_total_relation_size(relid) AS totalBytes
                FROM pg_catalog.pg_statio_user_tables
                ORDER BY pg_total_relation_size(relid) DESC
                LIMIT 10;
            ");
            foreach (var r in rows)
            {
                tableSizes[r.tableName] = Math.Round(r.totalBytes / (1024.0 * 1024.0), 2);
            }
        }
        catch { }

        return new StorageTelemetry(
            DatabaseSizeMb: Math.Round(dbSizeMb, 2),
            MaxVolumeSizeMb: MaxVolumeCapacityMb,
            UsagePercent: Math.Round(usagePct, 1),
            ActiveZone: zone,
            TableSizesMb: tableSizes,
            MeasuredAtUtc: DateTime.UtcNow
        );
    }

    private static async Task<int> ExecuteSafeDeleteAsync(System.Data.Common.DbConnection conn, string tableLabel, string sql, object param)
    {
        try
        {
            return await conn.ExecuteAsync(sql, param, commandTimeout: 120);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[SmartStorageGovernor] Query skipped for {tableLabel}: {ex.Message}");
            return 0;
        }
    }
}
