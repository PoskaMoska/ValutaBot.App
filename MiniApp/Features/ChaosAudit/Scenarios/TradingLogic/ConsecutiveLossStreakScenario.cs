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
/// Autonomous Audit: Checks for toxic consecutive loss streaks (>= 5 losses)
/// on any active asset and timeframe in recent verified live outcomes.
/// </summary>
public class ConsecutiveLossStreakScenario : IChaosScenario
{
    public string Name => "ConsecutiveLossStreak";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment — loss streak audit skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            const string query = @"
                WITH ranked_trades AS (
                    SELECT asset, timeframe, was_win, verified_at,
                           ROW_NUMBER() OVER (PARTITION BY asset, timeframe ORDER BY verified_at DESC) as rn
                    FROM trade_outcomes
                    WHERE created_at >= NOW() - INTERVAL '2 hours'
                      AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT')
                      AND was_win IS NOT NULL
                )
                SELECT asset, timeframe, was_win, rn
                FROM ranked_trades
                WHERE rn <= 10
                ORDER BY asset, timeframe, rn ASC;";

            var rows = (await conn.QueryAsync(query)).ToList();
            sw.Stop();

            if (rows.Count == 0)
            {
                return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                    "No verified trade outcomes in the last 2 hours for loss streak analysis.", "insufficient_data");
            }

            var grouped = rows.GroupBy(r => $"{r.asset}_{r.timeframe}");
            var toxicStreaks = new List<string>();
            int globalMaxStreak = 0;

            foreach (var group in grouped)
            {
                int currentStreak = 0;
                // rn is ordered ASC (1 is latest trade, 2 is previous, etc.)
                foreach (var trade in group.OrderBy(x => (long)x.rn))
                {
                    bool wasWin = (bool)trade.was_win;
                    if (!wasWin)
                    {
                        currentStreak++;
                    }
                    else
                    {
                        break; // Streak broken by a win
                    }
                }

                if (currentStreak > globalMaxStreak) globalMaxStreak = currentStreak;

                if (currentStreak >= 5)
                {
                    toxicStreaks.Add($"{group.Key}: {currentStreak} losses in a row");
                }
            }

            if (toxicStreaks.Count > 0)
            {
                string observed = string.Join("; ", toxicStreaks);
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Critical loss streak detected: {toxicStreaks.Count} asset/timeframe pair(s) suffered >= 5 consecutive losses.",
                    "Live outcome sequence analysis (most recent verified trades)",
                    observed,
                    "Consecutive loss streak < 5 before circuit breaker quarantine",
                    "Persistent counter-trend prediction or market regime mismatch during high volatility.",
                    "Verify CircuitBreaker quarantine activation for the affected symbol/timeframe.",
                    ChaosSeverity.Critical,
                    "Лавинообразная просадка депозита при продолжении торговли на неблагоприятном инструменте."
                );
            }

            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"All active trading pairs are within safe loss streak boundaries.",
                $"Max active loss streak across {grouped.Count()} pairs is {globalMaxStreak} (below critical threshold of 5)."
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Loss streak query failed: {ex.Message}",
                "DB Query ranked_trades",
                ex.Message,
                "Query completes cleanly without error",
                ex.StackTrace ?? "",
                "Verify trade_outcomes index on verified_at.",
                ChaosSeverity.Warning
            );
        }
    }
}
