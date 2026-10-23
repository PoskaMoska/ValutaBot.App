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
    private readonly TimeSpan _poSubminuteRetention = TimeSpan.FromDays(30);      // 30 дней для subminute_candles *_PO (~1.38 ГБ)
    private readonly TimeSpan _poTicksRetention = TimeSpan.FromHours(24);         // 24 часа для сырых po_ticks (~130 МБ)
    private readonly TimeSpan _fullJsonCandlesRetention = TimeSpan.FromHours(24); // 24 часа полных Candles/MtfCandles внутри features_json, старше — компактный Macro+SMC+TA
    private readonly TimeSpan _shadowOutcomesRetention = TimeSpan.FromDays(30);   // 30 дней для сбалансированных SHADOW-исходов (REAL хранятся бессрочно)
    private readonly TimeSpan _legacySubminuteRetention = TimeSpan.FromDays(1);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[DataRetention] Service started. 30-day smart retention: PO subminute (30d), compacted SHADOW outcomes (30d), full JSON candles (24h), po_ticks (24h).");
        
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
        var compactCutoff = DateTime.UtcNow.Subtract(_fullJsonCandlesRetention);
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

        // 1. Старше 24ч: удаляем избыточные дублирующиеся тики s5/s10/s15 у SHADOW, оставляя гармонизированную сетку (s5: 1/5, s10: 1/3, s15: 1/2, s30/m1: 100%)
        int unstridedShadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (unstrided HF SHADOW >24h)",
            @"DELETE FROM trade_outcomes
              WHERE direction NOT IN ('BUY', 'PUT')
                AND created_at::timestamptz < @cutoff
                AND NOT (
                    (LOWER(timeframe) = 's5' AND MOD(ABS(hashtext(id)), 5) = 0)
                    OR (LOWER(timeframe) = 's10' AND MOD(ABS(hashtext(id)), 3) = 0)
                    OR (LOWER(timeframe) = 's15' AND MOD(ABS(hashtext(id)), 2) = 0)
                    OR LOWER(timeframe) NOT IN ('s5', 's10', 's15')
                )",
            new { cutoff = compactCutoff });

        // 2. Старше 24ч: очищаем массивы Candles/MtfCandles внутри features_json (экономия 95% места, свечи берутся из subminute_candles), сохраняя MacroContext, Smc и Ta
        int compactedJsonCount = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (compact JSON >24h)",
            @"UPDATE trade_outcomes
              SET features_json = (features_json::jsonb - 'Candles' - 'candles' - 'MtfCandles' - 'mtfCandles' - 'mtf_candles')::text
              WHERE created_at::timestamptz < @cutoff
                AND features_json IS NOT NULL
                AND features_json LIKE '%""Candles""%'",
            new { cutoff = compactCutoff });

        // 3. Старше 30 дней: удаляем старые SHADOW-строки (REAL BUY/PUT хранятся бессрочно)
        int shadowDeleted = await ExecuteSafeDeleteAsync(conn, "trade_outcomes (SHADOW >30d)",
            "DELETE FROM trade_outcomes WHERE direction NOT IN ('BUY', 'PUT') AND created_at::timestamptz < @cutoff",
            new { cutoff = shadowCutoff });

        int stalePendingDeleted = await ExecuteSafeDeleteAsync(conn, "pending_trades",
            "DELETE FROM pending_trades WHERE verify_at < @cutoff",
            new { cutoff = stalePendingCutoff });
        int tickDeleted = await ExecuteSafeDeleteAsync(conn, "po_ticks",
            "DELETE FROM po_ticks WHERE ts < @cutoff",
            new { cutoff = tickCutoffEpoch });
        
        if (histDeleted > 0 || subDeleted > 0 || tickDeleted > 0 || shadowDeleted > 0 || unstridedShadowDeleted > 0 || compactedJsonCount > 0 || stalePendingDeleted > 0)
        {
            BotLogger.Info($"[DataRetention] Maintenance: {unstridedShadowDeleted} HF-dups pruned (>24h), {compactedJsonCount} JSONs compacted (>24h), {shadowDeleted} SHADOW (>30d), {subPoDeleted} PO subminute (>30d), {tickDeleted} po_ticks (>24h).");
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
