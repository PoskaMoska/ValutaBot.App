using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;

/// <summary>
/// Verifies the live database still satisfies the contract that the bot's own SQL relies on.
/// Why: DataRetentionService swallows query errors (ExecuteSafeDeleteAsync), so a renamed/missing column would
/// silently disable cleanup and the volume would fill up unnoticed. Read-only, single connection, 10s budget.
/// </summary>
public class DatabaseSchemaContractScenario : IChaosScenario
{
    public string Name => "DatabaseSchemaContract";
    public ChaosCategory Category => ChaosCategory.DataAndDbFaults;

    // table -> columns used by retention / governor / verification SQL
    private static readonly Dictionary<string, string[]> Contract = new()
    {
        ["trade_outcomes"] = new[] { "id", "created_at", "direction", "smc_bos_dir", "smc_has_ob", "pnl_bps", "features_json" },
        ["subminute_candles"] = new[] { "asset", "open_time" },
        ["historical_candles"] = new[] { "open_time" },
        ["pending_trades"] = new[] { "verify_at" },
    };

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment - schema contract skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            var pingSw = Stopwatch.StartNew();
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: budget.Token));
            pingSw.Stop();

            var rows = await conn.QueryAsync<(string table_name, string column_name)>(new CommandDefinition(
                "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = ANY(@tables)",
                new { tables = Contract.Keys.ToArray() }, cancellationToken: budget.Token));

            var present = rows.GroupBy(r => r.table_name).ToDictionary(g => g.Key, g => g.Select(x => x.column_name).ToHashSet());
            var missing = new List<string>();
            foreach (var (table, cols) in Contract)
            {
                if (!present.TryGetValue(table, out var have)) { missing.Add($"{table}(table)"); continue; }
                foreach (var c in cols.Where(c => !have.Contains(c))) missing.Add($"{table}.{c}");
            }

            sw.Stop();
            if (missing.Count > 0)
            {
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Database schema no longer satisfies the SQL the bot depends on (cleanup/verification will silently fail).",
                    "information_schema.columns", $"missing: {string.Join(", ", missing)}",
                    "all contract columns present", "Migration drift or a renamed column",
                    "Restore the column or update DataRetentionService / repositories.", ChaosSeverity.Critical);
            }

            if (pingSw.ElapsedMilliseconds > 2000)
            {
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Database round-trip is very slow (SELECT 1).", "SELECT 1", $"{pingSw.ElapsedMilliseconds}ms", "< 2000ms",
                    "DB under load (vacuum, bloat) or disk pressure", "Check volume usage and long-running queries.", ChaosSeverity.Warning);
            }

            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database schema contract verified and connection healthy.",
                $"tables={Contract.Count}, ping={pingSw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                "Database contract probe timed out (10s).", "SELECT 1 + information_schema", $"{sw.ElapsedMilliseconds}ms", "< 10000ms",
                "DB unreachable or locked", "Check Railway Postgres health and volume capacity.", ChaosSeverity.Warning);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                "Database contract probe failed.", ex.GetType().Name, ex.Message, "clean probe",
                "DB connectivity problem", "Check DATABASE_URL and Postgres status.", ChaosSeverity.Warning);
        }
    }
}
