using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.TradingLogic;

/// <summary>
/// Autonomous Audit: Checks for temporal data leakage (future candle contamination).
/// Verifies that no candle in features_json has an openTime exceeding the trade's created_at epoch.
/// Prevents the deadly lookahead bias where closed candles from the future are fed to the model.
/// </summary>
public class TemporalDataLeakageScenario : IChaosScenario
{
    public string Name => "TemporalDataLeakage";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment — temporal leakage audit skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            const string query = @"
                SELECT id, asset, timeframe, created_at,
                       (features_json::json->'candles'->-1->>'openTime')::bigint as last_candle_sec,
                       EXTRACT(EPOCH FROM created_at)::bigint as trade_created_sec
                FROM trade_outcomes
                WHERE created_at >= NOW() - INTERVAL '2 hours'
                  AND features_json IS NOT NULL
                  AND (features_json::json->'candles'->-1->>'openTime')::bigint > EXTRACT(EPOCH FROM created_at)::bigint
                LIMIT 5;";

            var violations = (await conn.QueryAsync(query)).ToList();
            sw.Stop();

            if (violations.Count > 0)
            {
                var first = violations[0];
                long diff = (long)first.last_candle_sec - (long)first.trade_created_sec;
                string observed = $"Found {violations.Count} leaked trade(s). Example: {first.asset} {first.timeframe} (id={first.id}) candle openTime is {diff}s ahead of trade created_at.";

                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Temporal data leakage detected! Candle sequence contains future candle(s) ahead of trade creation.",
                    "Live feature snapshot temporal validation",
                    observed,
                    "All candle openTime <= trade created_at epoch",
                    "Realtime candle slicing includes unclosed current candle or incorrectly timestamped buffer.",
                    "Fix closed candle filtering in MarketAnalysisOrchestrator to enforce open_time <= created_at - duration.",
                    ChaosSeverity.Critical,
                    "Искажение обучения модели: нейросеть обучается на иллюзорных данных из будущего, что обрушивает винрейт на реальном рынке."
                );
            }

            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Temporal integrity verified: 0 future candle leaks found in recent live trade features.",
                "All candle open times are strictly prior to trade creation timestamps."
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Temporal leakage query failed: {ex.Message}",
                "DB Query trade_outcomes features_json",
                ex.Message,
                "Query completes cleanly without error",
                ex.StackTrace ?? "",
                "Verify features_json schema in PostgreSQL.",
                ChaosSeverity.Warning
            );
        }
    }
}
