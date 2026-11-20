using Dapper;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.Core;

namespace ValutaBot.MiniApp.Services;

public enum StorageZone
{
    Green,     // < 3600 MB: Preserve up to 30 days of operational subminute history
    Yellow,    // 3600 - 4000 MB: Gentle 12h/cycle watermark trimming of oldest candles (min 4d floor)
    Red,       // 4000 - 4350 MB: 24h/cycle watermark trimming + 20-min cleanup + 6h shadow pruning
    Emergency  // >= 4350 MB (critical purge >= 4400 MB): Critical purge leaving >= 600 MB WAL/sort buffer on 5 GB volume
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
/// Uses a continuous high-watermark step-back on `subminute_candles` so disk space stabilizes
/// naturally via VACUUM page reuse without multi-day step cliffs or infinite delete loops.
/// </summary>
public class DataRetentionService : BackgroundService
{
    internal const double MaxVolumeCapacityMb = 5120.0; // Railway 5 GB starter volume (5 * 1024 MB)
    internal const double GreenThresholdMb    = 3600.0; // Full operational retention up to 30d (< 3.6 GB)
    internal const double YellowThresholdMb   = 4000.0; // Gentle continuous watermark trim (12h tail/cycle)
    internal const double RedThresholdMb      = 4350.0; // >= 4000 MB: Red (24h/cycle, 20m poll); >= 4350 MB: Emergency
    internal const double EmergencyThresholdMb= 4350.0; // Leaves ~770 MB buffer for 128 MB WAL + vacuum/sort temps on 5 GB volume
    internal const int MinSafeSubminuteDays   = 4;
    internal const int MaxSubminuteHistoryDays = 30;
    internal const int MaxHistoricalCandlesDays = 90;

    internal static StorageZone ClassifyStorageZone(double dbSizeMb) => dbSizeMb switch
    {
        < GreenThresholdMb  => StorageZone.Green,
        < YellowThresholdMb => StorageZone.Yellow,
        < RedThresholdMb    => StorageZone.Red,
        _                   => StorageZone.Emergency
    };

    /// <summary>
    /// Computes the upper cutoff timestamp for deleting the oldest tail of `subminute_candles`.
    /// Steps forward from `oldestPoOpenTimeUtc` by a bounded window (never looping on `pg_database_size`
    /// and never trimming inside the protected `MinSafeSubminuteDays` window during normal cycles).
    /// </summary>
    internal static DateTime? ComputeWatermarkSubminuteCutoffUtc(
        StorageZone zone,
        DateTime utcNow,
        DateTime? oldestPoOpenTimeUtc)
    {
        DateTime normNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        if (zone == StorageZone.Green)
        {
            return normNow.AddDays(-MaxSubminuteHistoryDays);
        }

        if (!oldestPoOpenTimeUtc.HasValue)
        {
            return null;
        }

        DateTime oldest = oldestPoOpenTimeUtc.Value.Kind == DateTimeKind.Utc
            ? oldestPoOpenTimeUtc.Value
            : oldestPoOpenTimeUtc.Value.ToUniversalTime();
        DateTime minKeep = normNow.AddDays(-MinSafeSubminuteDays);
        if (oldest >= minKeep)
        {
            return null;
        }

        int stepHours = zone switch
        {
            StorageZone.Yellow => 12,
            StorageZone.Red => 24,
            _ => 48
        };
        DateTime stepped = oldest.AddHours(stepHours);
        return stepped < minKeep ? stepped : minKeep;
    }

    private static StorageTelemetry? _latestTelemetry;
    private static bool _tableTuningApplied = false;

    public static StorageTelemetry? GetLatestTelemetry() => _latestTelemetry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[SmartStorageGovernor] Service started. Real-time watermark monitoring, importance sampling, adaptive retention.");

        // Startup probe: if already in Red/Emergency zone, run critical cleanup immediately before first sleep
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // Allow DB connections to stabilize
        try
        {
            await ExecuteSmartGovernorCycleAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exStartup)
        {
            BotLogger.Warn($"[SmartStorageGovernor] Startup storage probe warning (continuing to periodic cycle): {exStartup.Message}");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Adaptive sleep: shrink poll interval as disk fills up
            var interval = (_latestTelemetry?.DatabaseSizeMb ?? 0) switch
            {
                >= EmergencyThresholdMb => TimeSpan.FromMinutes(10),
                >= YellowThresholdMb    => TimeSpan.FromMinutes(20),
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
        TimeSpan shadowRetention;
        bool doImportanceSampling;
        bool doRedZonePruning;

        switch (telemetry.ActiveZone)
        {
            case StorageZone.Green:
                shadowRetention = TimeSpan.FromDays(2);
                doImportanceSampling = true;
                doRedZonePruning = false;
                break;

            case StorageZone.Yellow:
                shadowRetention = TimeSpan.FromHours(24);
                doImportanceSampling = true;
                doRedZonePruning = true;
                break;

            case StorageZone.Red:
            default:
                shadowRetention = TimeSpan.FromHours(6);
                doImportanceSampling = true;
                doRedZonePruning = true;
                break;
        }

        DateTime utcNow = DateTime.UtcNow;
        DateTime? oldestPoUtc = null;
        if (telemetry.ActiveZone != StorageZone.Green)
        {
            try
            {
                string? oldestStr = await conn.ExecuteScalarAsync<string?>(
                    "SELECT open_time FROM subminute_candles WHERE asset LIKE '%_PO' ORDER BY open_time ASC LIMIT 1;",
                    commandTimeout: 10);
                if (!string.IsNullOrWhiteSpace(oldestStr) &&
                    DateTime.TryParse(oldestStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedOldest))
                {
                    oldestPoUtc = parsedOldest;
                }
            }
            catch { }
        }

        DateTime? subCutoffUtc = ComputeWatermarkSubminuteCutoffUtc(telemetry.ActiveZone, utcNow, oldestPoUtc);
        var legacySubCutoff = utcNow.Subtract(TimeSpan.FromHours(12)).ToString("o");
        var histCutoff = utcNow.Subtract(TimeSpan.FromDays(MaxHistoricalCandlesDays)).ToString("o");
        var weekendArchiveCutoff = utcNow.Subtract(TimeSpan.FromDays(90)).ToString("o");
        var shadowCutoff = utcNow.Subtract(shadowRetention);
        var stalePendingCutoff = utcNow.AddHours(-2).ToString("o");

        // 5. Prune expired subminute candles using a single bounded watermark step per cycle
        int subPoDeleted = 0;
        if (subCutoffUtc.HasValue)
        {
            string subCutoff = subCutoffUtc.Value.ToString("o");
            subPoDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "subminute_candles (PO Watermark)",
                @"DELETE FROM subminute_candles
                  WHERE ctid IN (
                      SELECT ctid FROM subminute_candles
                      WHERE asset LIKE '%_PO' AND open_time < @cutoff
                      ORDER BY open_time ASC
                      LIMIT 5000
                      FOR UPDATE SKIP LOCKED
                  )", new { cutoff = subCutoff }, chunkSize: 5000);
        }

        int subLegacyDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "subminute_candles (legacy)",
            @"DELETE FROM subminute_candles
              WHERE ctid IN (
                  SELECT ctid FROM subminute_candles
                  WHERE asset NOT LIKE '%_PO' AND open_time < @cutoff
                  ORDER BY open_time ASC
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = legacySubCutoff }, chunkSize: 5000);

        int histDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "historical_candles",
            @"DELETE FROM historical_candles
              WHERE ctid IN (
                  SELECT ctid FROM historical_candles
                  WHERE open_time < @cutoff
                  LIMIT 3000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = histCutoff }, chunkSize: 3000);
        int pendingDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "pending_trades (Stale)",
            @"DELETE FROM pending_trades
              WHERE ctid IN (
                  SELECT ctid FROM pending_trades
                  WHERE verify_at < @cutoff
                  LIMIT 3000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = stalePendingCutoff }, chunkSize: 3000);

        await ExecuteChunkedDeleteSubminuteAsync(conn, "weekend_otc_candles (>90d)",
            @"DELETE FROM weekend_otc_candles
              WHERE ctid IN (
                  SELECT ctid FROM weekend_otc_candles
                  WHERE open_time < @cutoff
                  ORDER BY open_time ASC
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = weekendArchiveCutoff }, chunkSize: 5000);

        await ExecuteChunkedDeleteSubminuteAsync(conn, "weekend_otc_experiments (>90d)",
            @"DELETE FROM weekend_otc_experiments
              WHERE ctid IN (
                  SELECT ctid FROM weekend_otc_experiments
                  WHERE created_at < @cutoff
                  ORDER BY created_at ASC
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = weekendArchiveCutoff }, chunkSize: 5000);

        await ExecuteChunkedDeleteSubminuteAsync(conn, "paper_trades (>90d)",
            @"DELETE FROM paper_trades
              WHERE ctid IN (
                  SELECT ctid FROM paper_trades
                  WHERE created_at < @cutoff
                  ORDER BY created_at ASC
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = weekendArchiveCutoff }, chunkSize: 5000);

        // 6. Importance Sampling and Pruning for trade_outcomes
        int shadowDeleted = 0;
        int chopStrided = 0;
        int jsonCompacted = 0;

        if (doRedZonePruning)
        {
            shadowDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "trade_outcomes (Shadow Pruning)",
                @"DELETE FROM trade_outcomes
                  WHERE ctid IN (
                      SELECT ctid FROM trade_outcomes
                      WHERE direction NOT IN ('BUY', 'PUT')
                        AND created_at::timestamptz < @cutoff
                      LIMIT 3000
                      FOR UPDATE SKIP LOCKED
                  )", new { cutoff = shadowCutoff }, chunkSize: 3000);
        }

        if (doImportanceSampling)
        {
            chopStrided = await ExecuteChunkedDeleteSubminuteAsync(conn, "trade_outcomes (Importance Sampling Stride)",
                @"DELETE FROM trade_outcomes
                  WHERE ctid IN (
                      SELECT ctid FROM trade_outcomes
                      WHERE direction NOT IN ('BUY', 'PUT')
                        AND created_at::timestamptz < NOW() - INTERVAL '24 hours'
                        AND smc_bos_dir = 'NONE'
                        AND smc_has_ob = false
                        AND COALESCE(ABS(pnl_bps), 0) <= 1e-4
                        AND MOD(ABS(hashtext(id)), 5) != 0
                      LIMIT 3000
                      FOR UPDATE SKIP LOCKED
                  )", new { }, chunkSize: 3000);

            var jsonCutoff = DateTime.UtcNow.AddHours(-24).ToString("o");
            var jsonCutoffLegacy = DateTime.UtcNow.AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss+00");
            jsonCompacted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Features JSON Compaction)",
                @"WITH targets AS (
                      SELECT ctid FROM trade_outcomes
                      WHERE features_json IS NOT NULL
                        AND octet_length(features_json) > 2500
                        AND (created_at::timestamptz < @cutoff::timestamptz OR created_at::timestamptz < @cutoffLegacy::timestamptz)
                        AND (features_json LIKE '%""Candles""%' OR features_json LIKE '%""candles""%')
                      LIMIT 3000
                      FOR UPDATE SKIP LOCKED
                  )
                  UPDATE trade_outcomes
                  SET features_json = (features_json::jsonb - 'Candles' - 'candles' - 'MtfCandles' - 'mtfCandles' - 'mtf_candles')::text
                  WHERE ctid IN (SELECT ctid FROM targets)",
                new { cutoff = jsonCutoff, cutoffLegacy = jsonCutoffLegacy });
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
                        if (AllowedAnalyzeTables.Contains(tbl))
                        {
                            await conn.ExecuteAsync($"VACUUM (ANALYZE) \"{tbl}\";", commandTimeout: 120);
                        }
                    }
                }
            }
            else
            {
                await conn.ExecuteAsync("VACUUM (ANALYZE) trade_outcomes;", commandTimeout: 120);
                await conn.ExecuteAsync("VACUUM (ANALYZE) subminute_candles;", commandTimeout: 120);
                await conn.ExecuteAsync("VACUUM (ANALYZE) pending_trades;", commandTimeout: 60);
                await conn.ExecuteAsync("VACUUM (ANALYZE) historical_candles;", commandTimeout: 120);
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

        // Drain any emergency buffered trade outcomes once storage headroom has been restored
        if (TradeRepository.EmergencyOutcomesBufferCount > 0)
        {
            try
            {
                int drained = await TradeRepository.DrainEmergencyOutcomesAsync();
                if (drained > 0)
                {
                    BotLogger.Info($"[SmartStorageGovernor] Drained {drained} emergency trade outcomes after storage maintenance.");
                }
            }
            catch (Exception exDrain)
            {
                BotLogger.Warn($"[SmartStorageGovernor] Post-maintenance emergency outcomes drain notice: {exDrain.Message}");
            }
        }

        return telemetry;
    }

    /// <summary>
    /// Critical emergency purge for when filesystem is at >94% (4800 MB+).
    /// </summary>
    private static async Task RunEmergencyCriticalPurgeAsync(System.Data.Common.DbConnection conn, StorageTelemetry telemetry)
    {
        BotLogger.Warn($"[SmartStorageGovernor] EMERGENCY PURGE triggered at {telemetry.DatabaseSizeMb:F1} MB ({telemetry.UsagePercent:F1}%). Keeping only BUY/PUT trades and last 24h candles.");

        // Step 1: Aggressively delete all non-essential shadow/hold records (keep BUY/PUT forever)
        int emergencyShadowDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "trade_outcomes (Emergency: ALL SHADOW/HOLD)",
            @"DELETE FROM trade_outcomes
              WHERE ctid IN (
                  SELECT ctid FROM trade_outcomes
                  WHERE direction NOT IN ('BUY', 'PUT')
                    AND created_at::timestamptz < NOW() - INTERVAL '6 hours'
                  LIMIT 3000
                  FOR UPDATE SKIP LOCKED
              )", new { }, chunkSize: 3000);

        // Step 2: Delete subminute candles older than 4 days (preserve ML invariant of 4 days)
        int emergencySubDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "subminute_candles (Emergency: >4d)",
            @"DELETE FROM subminute_candles
              WHERE ctid IN (
                  SELECT ctid FROM subminute_candles
                  WHERE open_time < @cutoff
                  ORDER BY open_time ASC
                  LIMIT 5000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = DateTime.UtcNow.AddHours(-96).ToString("o") }, chunkSize: 5000);

        // Step 3: Compact ALL features_json immediately (not just old ones)
        int emergencyCompacted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (Emergency: JSON Compaction ALL)",
            @"WITH targets AS (
                  SELECT ctid FROM trade_outcomes
                  WHERE features_json IS NOT NULL
                    AND octet_length(features_json) > 2500
                    AND (features_json LIKE '%""Candles""%' OR features_json LIKE '%""candles""%')
                  LIMIT 3000
                  FOR UPDATE SKIP LOCKED
              )
              UPDATE trade_outcomes
              SET features_json = (features_json::jsonb - 'Candles' - 'candles' - 'MtfCandles' - 'mtfCandles' - 'mtf_candles')::text
              WHERE ctid IN (SELECT ctid FROM targets)",
            new { });

        // Step 4: Delete old historical candles aggressively
        int emergencyHistDeleted = await ExecuteChunkedDeleteSubminuteAsync(conn, "historical_candles (Emergency: >7d)",
            @"DELETE FROM historical_candles
              WHERE ctid IN (
                  SELECT ctid FROM historical_candles
                  WHERE open_time < @cutoff
                  LIMIT 3000
                  FOR UPDATE SKIP LOCKED
              )", new { cutoff = DateTime.UtcNow.AddDays(-7).ToString("o") }, chunkSize: 3000);

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
                    fillfactor = 85,
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
                    fillfactor = 70,
                    autovacuum_vacuum_threshold = 20,
                    autovacuum_vacuum_scale_factor = 0.05,
                    autovacuum_vacuum_cost_limit = 500,
                    autovacuum_vacuum_cost_delay = 2
                );
                ALTER TABLE historical_candles SET (
                    fillfactor = 90,
                    autovacuum_vacuum_threshold = 100,
                    autovacuum_vacuum_scale_factor = 0.05,
                    autovacuum_analyze_scale_factor = 0.02,
                    autovacuum_vacuum_cost_limit = 1000,
                    autovacuum_vacuum_cost_delay = 2
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

        StorageZone zone = ClassifyStorageZone(dbSizeMb);

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

    private static readonly HashSet<string> AllowedVacuumFullTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "trade_outcomes"
    };

    private static readonly HashSet<string> AllowedAnalyzeTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "trade_outcomes",
        "subminute_candles",
        "pending_trades",
        "historical_candles"
    };

    private static async Task<bool> SafeVacuumFullAsync(System.Data.Common.DbConnection conn, string table)
    {
        // Enforce strict positive whitelist: only explicitly designated low-frequency tables may undergo VACUUM FULL
        if (!AllowedVacuumFullTables.Contains(table))
        {
            BotLogger.Warn($"[SmartStorageGovernor] VACUUM FULL {table} rejected: table is not in strict whitelist (streaming ingest tables require non-blocking VACUUM ANALYZE).");
            return false;
        }

        try
        {
            long capacityMb = long.TryParse(Environment.GetEnvironmentVariable("RAILWAY_VOLUME_CAPACITY_MB"), out var cap) ? cap : (long)MaxVolumeCapacityMb;
            long totalCapacityBytes = capacityMb * 1024L * 1024L;
            long dbSizeBytes = await conn.ExecuteScalarAsync<long>("SELECT pg_database_size(current_database());");

            long maxWalBytes = 1073741824L;
            try
            {
                maxWalBytes = await conn.ExecuteScalarAsync<long>("SELECT pg_size_bytes(current_setting('max_wal_size'));");
            }
            catch { }

            long currentWalBytes = maxWalBytes;
            try
            {
                currentWalBytes = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(sum(size), 0)::bigint FROM pg_ls_waldir();");
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42501")
            {
                BotLogger.Warn("[SmartStorageGovernor] pg_ls_waldir permission denied. Defaulting WAL reservation to max_wal_size.");
            }
            catch { }

            long tableSizeBytes = await conn.ExecuteScalarAsync<long>("SELECT pg_total_relation_size(@t::regclass);", new { t = table });

            long estimatedFreeBytes = totalCapacityBytes - dbSizeBytes - currentWalBytes;
            long requiredBytes = (long)(tableSizeBytes * 2.2) + maxWalBytes + (long)(totalCapacityBytes * 0.15);
            if (estimatedFreeBytes < requiredBytes)
            {
                BotLogger.Warn($"[SmartStorageGovernor] VACUUM FULL {table} skipped: free={estimatedFreeBytes >> 20}MB < required={requiredBytes >> 20}MB");
                return false;
            }

            // Protective sub-second lock timeout: abort immediately if active queries would cause lock queuing
            await conn.ExecuteAsync("SET lock_timeout = '1s';");
            await conn.ExecuteAsync("SET statement_timeout = '120s';");
            await conn.ExecuteAsync($"VACUUM FULL \"{table}\";", commandTimeout: 150);
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

    private static async Task<int> ExecuteChunkedDeleteSubminuteAsync(System.Data.Common.DbConnection conn, string tableLabel, string sql, object param, int chunkSize = 3000)
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
