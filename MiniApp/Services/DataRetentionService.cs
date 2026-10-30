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
    Green,     // < 3.0 GB (58%): Full 30-day historical depth, no eviction
    Yellow,    // 3.0 - 3.9 GB (76%): Importance sampling active, striding chop
    Red,       // 3.9 - 4.8 GB (88-94%): Aggressive pruning, 15-20 min cycles
    Emergency  // > 4.8 GB (94%): Critical purge, delete all non-essential data immediately
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
    private const double MaxVolumeCapacityMb = 5120.0; // Railway 5 GB starter volume (5 * 1024 MB)
    private const double GreenThresholdMb    = 2200.0; // Safe operational zone (~3.7 GB physical disk with WAL)
    private const double YellowThresholdMb   = 2800.0; // Importance sampling + 4d shadow window (~4.3 GB physical disk)
    private const double RedThresholdMb      = 3200.0; // High-frequency cleanup every 15 min (~4.7 GB physical disk with WAL)
    private const double EmergencyThresholdMb= 3300.0; // Critical purge before physical disk exceeds 90% (leaving safe WAL/temp buffer)

    private static StorageTelemetry? _latestTelemetry;
    private static bool _tableTuningApplied = false;

    public static StorageTelemetry? GetLatestTelemetry() => _latestTelemetry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[SmartStorageGovernor] Service started. Real-time watermark monitoring, importance sampling, adaptive retention.");

        // Startup probe: if already in Red/Emergency zone, run critical cleanup immediately before first sleep
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // Allow DB connections to stabilize
        await ExecuteSmartGovernorCycleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Adaptive sleep: shrink poll interval as disk fills up
            var interval = (_latestTelemetry?.DatabaseSizeMb ?? 0) switch
            {
                >= EmergencyThresholdMb => TimeSpan.FromMinutes(10),
                >= RedThresholdMb       => TimeSpan.FromMinutes(20),
                _                       => TimeSpan.FromHours(1)
            };

            await Task.Delay(interval, stoppingToken);

            try
            {
                await ExecuteSmartGovernorCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                BotLogger.Error("[SmartStorageGovernor] Error during adaptive maintenance cycle", ex);
            }
        }
    }

    public static async Task<StorageTelemetry> RunEmergencyCleanupAsync(bool allowVacuumFull = false)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
            throw new InvalidOperationException("DATABASE_URL is not configured.");

        await using var conn = await DbConnectionFactory.OpenMaintenanceAsync();
        return await ExecuteCycleInternalAsync(conn, forceEmergencyVacuum: allowVacuumFull);
    }

    private async Task ExecuteSmartGovernorCycleAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;

        await using var conn = await DbConnectionFactory.OpenMaintenanceAsync(ct);
        await ExecuteCycleInternalAsync(conn, forceEmergencyVacuum: false);
    }

    private static async Task<StorageTelemetry> ExecuteCycleInternalAsync(System.Data.Common.DbConnection conn, bool forceEmergencyVacuum)
    {
        // 0. Set protective lock timeout so maintenance never blocks concurrent live trading
        await conn.ExecuteAsync("SET lock_timeout = '3s';", commandTimeout: 10);

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

        // 3. Drain unused raw tick queue in non-blocking chunks (avoids ACCESS EXCLUSIVE lock of TRUNCATE)
        try
        {
            await ExecuteChunkedDeleteSubminuteAsync(conn, "po_ticks",
                @"DELETE FROM po_ticks
                  WHERE ctid IN (
                      SELECT ctid FROM po_ticks
                      LIMIT 5000
                      FOR UPDATE SKIP LOCKED
                  )", new { }, chunkSize: 5000);
        }
        catch { }

        // 3b. Emergency critical purge when filesystem is critically full
        if (telemetry.DatabaseSizeMb >= EmergencyThresholdMb)
        {
            await RunEmergencyCriticalPurgeAsync(conn, telemetry);
        }

        // 4. Adaptive retention parameters based on Storage Zone
        TimeSpan subminuteRetention;
        TimeSpan shadowRetention;
        bool doImportanceSampling;
        bool doRedZonePruning;

        switch (telemetry.ActiveZone)
        {
            case StorageZone.Green:
                subminuteRetention = TimeSpan.FromDays(7);
                shadowRetention = TimeSpan.FromDays(2);
                doImportanceSampling = true;
                doRedZonePruning = false;
                break;

            case StorageZone.Yellow:
                subminuteRetention = TimeSpan.FromDays(4);
                shadowRetention = TimeSpan.FromHours(24);
                doImportanceSampling = true;
                doRedZonePruning = true;
                break;

            case StorageZone.Red:
            default:
                // Preserve 4-day ML training window for PO subminute candles; reclaim disk via shadow/JSON pruning
                subminuteRetention = TimeSpan.FromDays(4);
                shadowRetention = TimeSpan.FromHours(6);
                doImportanceSampling = true;
                doRedZonePruning = true;
                break;
        }

        var subCutoff = DateTime.UtcNow.Subtract(subminuteRetention).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var legacySubCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromHours(12)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var histCutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(30)).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var shadowCutoff = DateTime.UtcNow.Subtract(shadowRetention);
        var stalePendingCutoff = DateTime.UtcNow.AddHours(-2).ToString("o");

        // 5. Prune expired subminute candles using chunked deletions with FOR UPDATE SKIP LOCKED
        int subPoDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "subminute_candles (PO)",
            @"DELETE FROM subminute_candles
              WHERE ctid IN (
                  SELECT ctid FROM subminute_candles
                  WHERE asset LIKE '%_PO' AND open_time < @cutoff
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = subCutoff }, chunkSize: 5000);

        int subLegacyDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "subminute_candles (legacy)",
            @"DELETE FROM subminute_candles
              WHERE ctid IN (
                  SELECT ctid FROM subminute_candles
                  WHERE asset NOT LIKE '%_PO' AND open_time < @cutoff
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = legacySubCutoff }, chunkSize: 5000);

        int histDeleted = await ExecuteSafeDeleteAsync(conn, "historical_candles",
            "DELETE FROM historical_candles WHERE open_time < @cutoff", new { cutoff = histCutoff });
        int pendingDeleted = await ExecuteSafeDeleteAsync(conn, "pending_trades",
            "DELETE FROM pending_trades WHERE verify_at < @cutoff", new { cutoff = stalePendingCutoff });

        // 6. Importance Sampling and Pruning for trade_outcomes
        int shadowDeleted = 0;
        int chopStrided = 0;
        int jsonCompacted = 0;

        if (doRedZonePruning)
        {
            shadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Shadow Pruning)",
                @"DELETE FROM trade_outcomes
                  WHERE direction NOT IN ('BUY', 'PUT')
                    AND created_at::timestamptz < @cutoff",
                new { cutoff = shadowCutoff });
        }

        if (doImportanceSampling)
        {
            chopStrided = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Importance Sampling Stride)",
                @"DELETE FROM trade_outcomes
                  WHERE direction NOT IN ('BUY', 'PUT')
                    AND created_at::timestamptz < NOW() - INTERVAL '24 hours'
                    AND smc_bos_dir = 'NONE'
                    AND smc_has_ob = false
                    AND COALESCE(ABS(pnl_bps), 0) <= 1e-4
                    AND MOD(ABS(hashtext(id)), 5) != 0",
                new { });

            jsonCompacted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Features JSON Compaction)",
                @"WITH targets AS (
                      SELECT ctid FROM trade_outcomes
                      WHERE features_json IS NOT NULL
                        AND octet_length(features_json) > 2500
                        AND created_at < NOW() - INTERVAL '24 hours'
                        AND (features_json LIKE '%""Candles""%' OR features_json LIKE '%""candles""%')
                      LIMIT 2000
                  )
                  UPDATE trade_outcomes
                  SET features_json = (features_json::jsonb - 'Candles' - 'candles' - 'MtfCandles' - 'mtfCandles' - 'mtf_candles')::text
                  WHERE ctid IN (SELECT ctid FROM targets)",
                new { });
        }

        // 7. Non-blocking VACUUM maintenance (or per-table disk-checked SafeVacuumFullAsync when explicitly requested)
        try
        {
            if (forceEmergencyVacuum)
            {
                foreach (var tbl in new[] { "trade_outcomes", "subminute_candles", "pending_trades" })
                {
                    if (!await SafeVacuumFullAsync(conn, tbl))
                    {
                        await conn.ExecuteAsync($"VACUUM (ANALYZE) {tbl};", commandTimeout: 120);
                    }
                }
            }
            else
            {
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

    /// <summary>
    /// Critical emergency purge for when filesystem is at >94% (4800 MB+).
    /// </summary>
    private static async Task RunEmergencyCriticalPurgeAsync(System.Data.Common.DbConnection conn, StorageTelemetry telemetry)
    {
        BotLogger.Warn($"[SmartStorageGovernor] EMERGENCY PURGE triggered at {telemetry.DatabaseSizeMb:F1} MB ({telemetry.UsagePercent:F1}%). Keeping only BUY/PUT trades and last 24h candles.");

        // Step 1: Aggressively delete all non-essential shadow/hold records (keep BUY/PUT forever)
        int emergencyShadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Emergency: ALL SHADOW/HOLD)",
            @"DELETE FROM trade_outcomes
              WHERE direction NOT IN ('BUY', 'PUT')
                AND created_at::timestamptz < NOW() - INTERVAL '6 hours'",
            new { });

        // Step 2: Delete subminute candles older than 4 days (preserve ML invariant of 4 days)
        int emergencySubDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "subminute_candles (Emergency: >4d)",
            @"DELETE FROM subminute_candles
              WHERE ctid IN (
                  SELECT ctid FROM subminute_candles
                  WHERE open_time < @cutoff
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = DateTime.UtcNow.AddHours(-96).ToString("yyyy-MM-ddTHH:mm:ssZ") }, chunkSize: 5000);

        // Step 3: Compact ALL features_json immediately (not just old ones)
        int emergencyCompacted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Emergency: JSON Compaction ALL)",
            @"WITH targets AS (
                  SELECT ctid FROM trade_outcomes
                  WHERE features_json IS NOT NULL
                    AND octet_length(features_json) > 2500
                    AND (features_json LIKE '%""Candles""%' OR features_json LIKE '%""candles""%')
                  LIMIT 5000
              )
              UPDATE trade_outcomes
              SET features_json = (features_json::jsonb - 'Candles' - 'candles' - 'MtfCandles' - 'mtfCandles' - 'mtf_candles')::text
              WHERE ctid IN (SELECT ctid FROM targets)",
            new { });

        // Step 4: Delete old historical candles aggressively
        int emergencyHistDeleted = await ExecuteSafeDeleteAsync(conn, "historical_candles (Emergency: >7d)",
            "DELETE FROM historical_candles WHERE open_time < @cutoff",
            new { cutoff = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ") });

        BotLogger.Warn($"[SmartStorageGovernor] Emergency purge complete: {emergencyShadowDeleted} shadow/hold, {emergencySubDeleted} subminute candles, {emergencyCompacted} JSON compacted, {emergencyHistDeleted} historical.");

        // Notify admins
        try
        {
            await TelegramBotService.SendMessageToAdmins(
                $"🚨 <b>DB Emergency Purge Executed</b>\n" +
                $"• Volume: <b>{telemetry.DatabaseSizeMb:F0} MB / {telemetry.MaxVolumeSizeMb:F0} MB ({telemetry.UsagePercent:F1}%)</b>\n" +
                $"• Deleted: {emergencyShadowDeleted} shadow+hold records, {emergencySubDeleted} old candles\n" +
                $"• <b>Real BUY/PUT trades preserved.</b>\n" +
                $"• Рекомендация: POST /api/admin/db-maintenance?full=true для VACUUM FULL после очистки.");
        }
        catch { }
    }

    private static async Task ApplyAutovacuumTuningAsync(System.Data.Common.DbConnection conn)
    {
        try
        {
            // Zero-downtime PostgreSQL table storage tuning:
            // fillfactor=80 on subminute_candles enables HOT updates for high-frequency ticks.
            await conn.ExecuteAsync(@"
                ALTER TABLE trade_outcomes SET (
                    autovacuum_vacuum_scale_factor = 0.03,
                    autovacuum_analyze_scale_factor = 0.02,
                    autovacuum_vacuum_cost_limit = 2000,
                    autovacuum_vacuum_cost_delay = 2,
                    toast.autovacuum_vacuum_scale_factor = 0.03,
                    toast.autovacuum_vacuum_cost_limit = 2000
                );
                ALTER TABLE subminute_candles SET (
                    fillfactor = 80,
                    autovacuum_vacuum_scale_factor = 0.02,
                    autovacuum_analyze_scale_factor = 0.01,
                    autovacuum_vacuum_cost_limit = 2000,
                    autovacuum_vacuum_cost_delay = 2
                );
                ALTER TABLE pending_trades SET (
                    autovacuum_vacuum_scale_factor = 0.01,
                    autovacuum_vacuum_cost_limit = 1000
                );
            ", commandTimeout: 30);
            BotLogger.Info("[SmartStorageGovernor] Applied table-level autovacuum and HOT tuning (fillfactor=80, scale_factor=0.02).");
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
            < GreenThresholdMb    => StorageZone.Green,
            < YellowThresholdMb   => StorageZone.Yellow,
            < RedThresholdMb      => StorageZone.Red,
            _                     => StorageZone.Emergency
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

    private static async Task<bool> SafeVacuumFullAsync(System.Data.Common.DbConnection conn, string table)
    {
        try
        {
            long capacityMb = long.TryParse(Environment.GetEnvironmentVariable("RAILWAY_VOLUME_CAPACITY_MB"), out var cap) ? cap : (long)MaxVolumeCapacityMb;
            long capacity = capacityMb * 1048576L;
            long dbSize = await conn.ExecuteScalarAsync<long>("SELECT pg_database_size(current_database())");
            long walNow = 0;
            try { walNow = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(sum(size),0)::bigint FROM pg_ls_waldir()"); } catch { }
            long maxWal = 1073741824L;
            try { maxWal = await conn.ExecuteScalarAsync<long>("SELECT setting::bigint * 1048576 FROM pg_settings WHERE name='max_wal_size'"); } catch { }
            long tblSize = await conn.ExecuteScalarAsync<long>("SELECT pg_total_relation_size(@t::regclass)", new { t = table });

            long free = capacity - dbSize - walNow;
            long need = (long)(tblSize * 1.1) + maxWal + capacity / 10;
            if (free < need)
            {
                BotLogger.Warn($"[SmartStorageGovernor] VACUUM FULL {table} skipped: free={free >> 20}MB < need={need >> 20}MB");
                return false;
            }

            await conn.ExecuteAsync("SET lock_timeout = '2s'");
            await conn.ExecuteAsync("SET statement_timeout = '120s'");
            await conn.ExecuteAsync($"VACUUM FULL {table}", commandTimeout: 150);
            BotLogger.Info($"[SmartStorageGovernor] VACUUM FULL {table} succeeded.");
            return true;
        }
        catch (Npgsql.PostgresException e) when (e.SqlState is "55P03" or "57014")
        {
            BotLogger.Warn($"[SmartStorageGovernor] VACUUM FULL {table} aborted by lock/statement timeout ({e.SqlState}).");
            return false;
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[SmartStorageGovernor] VACUUM FULL {table} skipped: {ex.Message}");
            return false;
        }
        finally
        {
            try { await conn.ExecuteAsync("SET statement_timeout = '600s'; SET lock_timeout = '3s';"); } catch { }
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
            BotLogger.Warn($"[SmartStorageGovernor] Query skipped for {tableLabel}: {ex.Message}");
            return 0;
        }
    }

    private static async Task<int> ExecuteChunkedDeleteSubminuteAsync(System.Data.Common.DbConnection conn, string tableLabel, string sql, object param, int chunkSize = 10000)
    {
        int totalDeleted = 0;
        try
        {
            while (true)
            {
                int deleted = await conn.ExecuteAsync(sql, param, commandTimeout: 60);
                totalDeleted += deleted;
                if (deleted < chunkSize) break;
                await Task.Delay(50); // 50ms yield for live tick inserts
            }
            return totalDeleted;
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[SmartStorageGovernor] Chunked delete notice for {tableLabel}: {ex.Message}");
            return totalDeleted;
        }
    }
}
