using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.TradingLogic;

/// <summary>
/// Autonomous Anomaly Miner: An intelligent hypothesis-generating crawler.
/// Instead of testing a single static metric, it autonomously mines multi-dimensional slices
/// of live trading data (asset isolation, cross-engine conflict, temporal sessions)
/// to detect dark zones and hidden money-losing clusters that humans didn't anticipate.
/// </summary>
public class AutonomousAnomalyMinerScenario : IChaosScenario
{
    public string Name => "AutonomousAnomalyMiner";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    private sealed class AnomalyRow
    {
        public string? asset { get; set; }
        public string? timeframe { get; set; }
        public long total { get; set; }
        public long wins { get; set; }
        public double winrate { get; set; }
        public double buy_pct { get; set; }
    }

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment — anomaly mining skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            // Vector 1: Isolated Asset-Timeframe Bleed (Dark Zone Detection)
            const string queryBleed = @"
                SELECT asset, timeframe,
                       COUNT(*) as total,
                       SUM(CASE WHEN was_win = TRUE THEN 1 ELSE 0 END) as wins,
                       ROUND(AVG(CASE WHEN was_win = TRUE THEN 100.0 ELSE 0.0 END), 2) as winrate
                FROM trade_outcomes
                WHERE created_at >= NOW() - INTERVAL '3 hours'
                  AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT')
                  AND was_win IS NOT NULL
                GROUP BY asset, timeframe
                HAVING COUNT(*) >= 25 AND AVG(CASE WHEN was_win = TRUE THEN 100.0 ELSE 0.0 END) < 41.5
                ORDER BY winrate ASC
                LIMIT 1;";

            var bleed = await conn.QueryFirstOrDefaultAsync<AnomalyRow>(queryBleed);
            if (bleed != null)
            {
                sw.Stop();
                string observed = $"Isolated Bleed: {bleed.asset} {bleed.timeframe} -> WinRate = {bleed.winrate:F1}% (Wins={bleed.wins}/{bleed.total})";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Autonomous Miner discovered hidden dark zone: {bleed.asset} {bleed.timeframe} has severely degraded winrate ({bleed.winrate:F1}%) on recent volume.",
                    $"Multi-dimensional slice [{bleed.asset} {bleed.timeframe}]",
                    observed,
                    "WinRate >= 48.0% per isolated asset/timeframe cell",
                    "Asset-specific regime breakdown or localized micro-trend trap.",
                    $"Quarantine {bleed.asset} {bleed.timeframe} or review spread/liquidity dynamics for this pair.",
                    ChaosSeverity.Critical,
                    $"Систематический слив депозита на изолированной паре {bleed.asset} {bleed.timeframe}."
                );
            }

            // Vector 2: Cross-Engine Cognitive Dissonance (When TA and Neural Brain fight)
            const string queryDissonance = @"
                SELECT 
                    COUNT(*) as total,
                    SUM(CASE WHEN was_win = TRUE THEN 1 ELSE 0 END) as wins,
                    ROUND(AVG(CASE WHEN was_win = TRUE THEN 100.0 ELSE 0.0 END), 2) as winrate
                FROM trade_outcomes
                WHERE created_at >= NOW() - INTERVAL '3 hours'
                  AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT')
                  AND was_win IS NOT NULL
                  AND ta_score * ml_score < -0.05;";

            var diss = await conn.QueryFirstOrDefaultAsync<AnomalyRow>(queryDissonance);
            if (diss != null && diss.total >= 30 && diss.winrate < 43.0)
            {
                sw.Stop();
                string observed = $"Engine Conflict: WinRate = {diss.winrate:F1}% (Wins={diss.wins}/{diss.total}) when TA and ML had opposing signs";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Cognitive Dissonance trap discovered: Trades taken when TA and ML had opposing signs collapsed to {diss.winrate:F1}% winrate.",
                    "Cross-engine conflict slice (ta_score * ml_score < -0.05)",
                    observed,
                    "WinRate >= 48.0% during cross-engine divergence",
                    "Consensus engine allows trading through irreconcilable disagreement between neural and math engines.",
                    "Enforce strict neutrality veto when TA and ML directionally clash.",
                    ChaosSeverity.Critical,
                    "Убыточные входы в условиях взаимного противоречия индикаторов и нейросети."
                );
            }

            // Vector 3: Extreme Multi-Pair Directional Lock
            const string queryLock = @"
                SELECT asset, timeframe,
                       COUNT(*) as total,
                       ROUND(SUM(CASE WHEN direction LIKE '%BUY%' THEN 100.0 ELSE 0.0 END) / COUNT(*), 1) as buy_pct
                FROM trade_outcomes
                WHERE created_at >= NOW() - INTERVAL '2 hours'
                  AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT')
                GROUP BY asset, timeframe
                HAVING COUNT(*) >= 40 AND (
                    SUM(CASE WHEN direction LIKE '%BUY%' THEN 100.0 ELSE 0.0 END) / COUNT(*) > 82.0
                    OR SUM(CASE WHEN direction LIKE '%BUY%' THEN 100.0 ELSE 0.0 END) / COUNT(*) < 18.0
                )
                ORDER BY buy_pct DESC
                LIMIT 1;";

            var lockRow = await conn.QueryFirstOrDefaultAsync<AnomalyRow>(queryLock);
            if (lockRow != null)
            {
                sw.Stop();
                string dirLabel = lockRow.buy_pct > 50 ? $"BUY ({lockRow.buy_pct:F1}%)" : $"PUT ({100.0 - lockRow.buy_pct:F1}%)";
                string observed = $"Directional Lock: {lockRow.asset} {lockRow.timeframe} locked {dirLabel} over N={lockRow.total} trades";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Autonomous Miner detected one-way bias lock: {lockRow.asset} {lockRow.timeframe} is heavily skewed to {dirLabel}.",
                    $"Directional skew slice [{lockRow.asset} {lockRow.timeframe}]",
                    observed,
                    "Balanced directional participation [20% - 80%]",
                    "Uncentered logit drift or momentum accumulator feedback loop.",
                    "Verify logit centering and clear directional accumulator state.",
                    ChaosSeverity.Warning,
                    "Риск массовых потерь при развороте тренда из-за заклинивания в одну сторону."
                );
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Autonomous Anomaly Miner completed multi-dimensional scan across asset isolation, cross-engine conflict, and directional locks. No hidden dark zones found.",
                "all_slices_healthy"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Autonomous Anomaly Miner failed during execution: {ex.Message}",
                "DB Multi-factor slice query",
                ex.Message,
                "Query completes cleanly without error",
                ex.StackTrace ?? "",
                "Verify trade_outcomes schema integrity.",
                ChaosSeverity.Warning
            );
        }
    }
}
