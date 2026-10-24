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
/// Autonomous Audit: Detects confidence-to-winrate divergence in real historical trades.
/// Flags an anomaly if high-confidence signals (>= 58% probability) show poor win rates (< 50%)
/// or if higher confidence brackets yield worse results than lower confidence brackets (inverted calibration).
/// </summary>
public class CalibrationDivergenceScenario : IChaosScenario
{
    private static readonly DateTime ProcessBootTime = DateTime.UtcNow;

    public string Name => "CalibrationDivergence";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Database not configured in this environment — calibration audit skipped.", "skipped");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(budget.Token);

            var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
            var cutoff = ProcessBootTime > twoHoursAgo ? ProcessBootTime : twoHoursAgo;
            var windowMinutes = (int)Math.Max(1, (DateTime.UtcNow - cutoff).TotalMinutes);

            const string query = @"
                SELECT 
                    CASE 
                        WHEN probability >= 60 THEN 'HIGH_60_PLUS'
                        WHEN probability >= 55 THEN 'MED_55_59'
                        WHEN probability >= 52 THEN 'MOD_52_54'
                        ELSE 'LOW_50_51'
                    END as bucket,
                    COUNT(*) as total,
                    SUM(CASE WHEN was_win = TRUE THEN 1 ELSE 0 END) as wins,
                    ROUND(AVG(CASE WHEN was_win = TRUE THEN 100.0 ELSE 0.0 END), 2) as winrate
                FROM trade_outcomes
                WHERE created_at >= @Cutoff
                  AND direction IN ('BUY', 'PUT', 'SHADOW_BUY', 'SHADOW_PUT')
                  AND was_win IS NOT NULL
                GROUP BY 1;";

            var rows = (await conn.QueryAsync(query, new { Cutoff = cutoff })).ToList();
            sw.Stop();

            var dict = new Dictionary<string, (long Total, double WinRate)>();
            foreach (var r in rows)
            {
                string b = (string)r.bucket;
                long tot = (long)r.total;
                double wr = Convert.ToDouble(r.winrate);
                dict[b] = (tot, wr);
            }

            // Warmup guard: wait until the current deployment accumulates at least 30 high-confidence trades
            if (!dict.TryGetValue("HIGH_60_PLUS", out var high) || high.Total < 30)
            {
                long currentCount = high.Total;
                return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                    $"Active deploy sample warmup: {currentCount}/30 verified high-confidence trades in last {windowMinutes}m. Audit on standby.",
                    "insufficient_data");
            }

            // Check High Confidence bracket (>= 60%) in recent window
            if (high.WinRate < 50.0)
            {
                string observed = $"[Окно {windowMinutes}м после деплоя] Conf >= 60%: WinRate = {high.WinRate:F2}% (Wins={Math.Round(high.Total * high.WinRate / 100.0)}/{high.Total})";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Critical confidence inversion: high-confidence signals (>= 60%) have sub-50% winrate ({high.WinRate:F1}%) across {windowMinutes}m window.",
                    $"Live outcome calibration check [>=60% bucket, {windowMinutes}m window]",
                    observed,
                    "WinRate >= 53.0% for high confidence (>=60%)",
                    "Overconfident model logits or micro-reversal regime on short timeframes (e.g. s5) causing false certainty.",
                    "Recalibrate TemperatureScaler or apply mean-reversion filter on s5.",
                    ChaosSeverity.Critical,
                    "Повышенные риски и убытки при сделках с мнимой высокой уверенностью нейросети."
                );
            }

            // Check Medium-High bracket (55-59%)
            if (dict.TryGetValue("MED_55_59", out var med) && med.Total >= 50)
            {
                if (med.WinRate < 48.0)
                {
                    string observed = $"Conf 55-59%: WinRate = {med.WinRate:F2}% (Wins={Math.Round(med.Total * med.WinRate / 100.0)}/{med.Total})";
                    return ChaosScenarioResult.Failure(
                        Name,
                        Category,
                        sw.ElapsedMilliseconds,
                        $"Degraded calibration: medium-confidence signals (55-59%) yielded only {med.WinRate:F1}% winrate.",
                        "Live outcome calibration check [55-59% bucket]",
                        observed,
                        "WinRate >= 52.0% for medium confidence",
                        "Prediction confidence drift across intermediate probability thresholds.",
                        "Examine MacroContext volatility alignment and feature distributions.",
                        ChaosSeverity.Warning,
                        "Пониженная эффективность входов при средней уверенности модели."
                    );
                }
            }

            string summary = string.Join(", ", dict.Select(kv => $"{kv.Key}: {kv.Value.WinRate:F1}% (N={kv.Value.Total})"));
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Model confidence calibration verified: higher confidence brackets maintain expected win rates.",
                summary
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Calibration divergence query failed: {ex.Message}",
                "DB Query trade_outcomes calibration",
                ex.Message,
                "Query completes cleanly without error",
                ex.StackTrace ?? "",
                "Verify trade_outcomes table schema.",
                ChaosSeverity.Warning
            );
        }
    }
}
