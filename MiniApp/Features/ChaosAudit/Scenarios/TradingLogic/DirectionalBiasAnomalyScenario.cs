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
/// Autonomous Audit: Detects directional skew/bias anomalies in real live trading.
/// If predictions on any active asset/timeframe lean > 75% towards BUY or PUT over the last 2 hours
/// without an overwhelming macro trend, flags an immediate logic breach.
/// </summary>
public class DirectionalBiasAnomalyScenario : IChaosScenario
{
    public string Name => "DirectionalBiasAnomaly";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment — directional bias audit skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            const string query = @"
                SELECT asset, timeframe,
                       COUNT(*) as total,
                       SUM(CASE WHEN direction LIKE '%BUY%' THEN 1 ELSE 0 END) as buy_count,
                       SUM(CASE WHEN direction LIKE '%PUT%' THEN 1 ELSE 0 END) as put_count
                FROM trade_outcomes
                WHERE created_at >= NOW() - INTERVAL '2 hours'
                  AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT')
                GROUP BY asset, timeframe
                HAVING COUNT(*) >= 30;";

            var rows = (await conn.QueryAsync(query)).ToList();
            sw.Stop();

            if (rows.Count == 0)
            {
                return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                    "Insufficient recent live trade outcomes (N < 30 per cell in last 2h) for statistical bias assessment.", "insufficient_data");
            }

            var biasedCells = new List<string>();
            double maxDeviation = 0;

            foreach (var r in rows)
            {
                string asset = (string)r.asset;
                string tf = (string)r.timeframe;
                long total = (long)r.total;
                long buyCount = (long)r.buy_count;
                double buyPct = (double)buyCount / total * 100.0;
                double putPct = 100.0 - buyPct;

                double devFromBalance = Math.Abs(buyPct - 50.0);
                if (devFromBalance > maxDeviation) maxDeviation = devFromBalance;

                // Trigger threshold: > 75% BUY or > 75% PUT
                if (buyPct > 75.0 || putPct > 75.0)
                {
                    string dominant = buyPct > 75.0 ? $"BUY ({buyPct:F1}%)" : $"PUT ({putPct:F1}%)";
                    biasedCells.Add($"{asset} {tf}: {dominant} [N={total}]");
                }
            }

            if (biasedCells.Count > 0)
            {
                string observed = string.Join("; ", biasedCells);
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Severe directional bias detected in {biasedCells.Count} asset/timeframe pair(s) over the last 2 hours.",
                    "Live outcome distribution check (last 2h window)",
                    observed,
                    "Directional distribution balanced within [25% - 75%] per asset/tf",
                    "Uncentered model logits, feature normalization drift, or stale directional momentum.",
                    "Check temperature/bias calibration in MLPythonService and review patch_tst_brain logit centering.",
                    ChaosSeverity.Critical,
                    "Систематический слив депозита при развороте рынка из-за однобоких ставок в одном направлении."
                );
            }

            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Directional balance verified across {rows.Count} active asset/timeframe cells.",
                $"Max deviation from 50/50 balance was {maxDeviation:F1}% (safely below 25% threshold)."
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Directional bias query failed: {ex.Message}",
                "DB Query trade_outcomes",
                ex.Message,
                "Query completes cleanly without error",
                ex.StackTrace ?? "",
                "Verify trade_outcomes index on created_at and direction.",
                ChaosSeverity.Warning
            );
        }
    }
}
