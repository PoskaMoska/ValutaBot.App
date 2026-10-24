using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.TradingLogic;

/// <summary>
/// Autonomous Audit: Detects confidence entropy collapse (model blindness).
/// Flags a breach if > 92% of recent predictions are squashed into a narrow [50.0%, 51.5%] band
/// or if probability standard deviation drops near zero, indicating the model has lost discriminatory power.
/// </summary>
public class ConfidenceEntropyScenario : IChaosScenario
{
    public string Name => "ConfidenceEntropy";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    private sealed class EntropyRow
    {
        public long total { get; set; }
        public double std_prob { get; set; }
        public double avg_prob { get; set; }
        public double min_prob { get; set; }
        public double max_prob { get; set; }
        public long flat_count { get; set; }
    }

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment — confidence entropy audit skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            const string query = @"
                SELECT 
                    COUNT(*) as total,
                    COALESCE(STDDEV(probability), 0) as std_prob,
                    COALESCE(AVG(probability), 50.0) as avg_prob,
                    COALESCE(MIN(probability), 50.0) as min_prob,
                    COALESCE(MAX(probability), 50.0) as max_prob,
                    SUM(CASE WHEN probability BETWEEN 50.0 AND 51.5 THEN 1 ELSE 0 END) as flat_count
                FROM trade_outcomes
                WHERE created_at >= NOW() - INTERVAL '2 hours'
                  AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT');";

            var row = await conn.QueryFirstOrDefaultAsync<EntropyRow>(query);
            sw.Stop();

            if (row == null || row.total < 100)
            {
                long total = row?.total ?? 0;
                return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                    $"Insufficient trade volume in last 2h (N={total} < 100) for entropy calculation.", "insufficient_data");
            }

            long totalTrades = row.total;
            double stdProb = row.std_prob;
            double avgProb = row.avg_prob;
            double minProb = row.min_prob;
            double maxProb = row.max_prob;
            long flatCount = row.flat_count;
            double flatPct = (double)flatCount / totalTrades * 100.0;

            if (flatPct > 92.0 || stdProb < 0.6)
            {
                string observed = $"{flatPct:F1}% of predictions are flat (50.0-51.5%). std={stdProb:F2}, range=[{minProb:F1}% - {maxProb:F1}%] (N={totalTrades})";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Confidence entropy collapse: Neural Brain output is squashed into near-uniform probabilities ({flatPct:F1}% flat).",
                    "Live outcome probability variance check",
                    observed,
                    "Healthy confidence variance: std >= 1.2, flat ratio < 85%",
                    "Excessive temperature scaling or saturated softmax layers destroying network sensitivity.",
                    "Verify TemperatureScaler calibration parameter in patch_tst_brain_v3.pt or train_brain.py.",
                    ChaosSeverity.Critical,
                    "Бот теряет избирательность и входит в сделки случайным образом без реальной уверенности."
                );
            }

            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Model confidence entropy healthy: predictions show good dispersion and sensitivity to market setups.",
                $"std={stdProb:F2}, avg={avgProb:F1}%, min={minProb:F1}%, max={maxProb:F1}%, flat ratio={flatPct:F1}% (N={totalTrades})"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Confidence entropy query failed: {ex.Message}",
                "DB Query trade_outcomes probability",
                ex.Message,
                "Query completes cleanly without error",
                ex.StackTrace ?? "",
                "Verify trade_outcomes index on created_at and probability.",
                ChaosSeverity.Warning
            );
        }
    }
}
