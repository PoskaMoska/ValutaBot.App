using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ValutaBot.Core;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.StressAndOutages;

namespace ValutaBot.MiniApp.Features.ChaosAudit;

public class ChaosAuditService : IChaosAuditService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChaosAuditService>? _logger;
    private readonly List<IChaosScenario> _scenarios = [];
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private ChaosAuditReport? _latestReport;

    public ChaosAuditService(IServiceProvider serviceProvider, ILogger<ChaosAuditService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Register scenarios in order:
        // 1. Invariants & Property-Tests (cheapest, catches fundamental bugs)
        _scenarios.Add(new SymmetryMirrorInvariantScenario());
        _scenarios.Add(new ExtremeCandleFuzzingScenario());
        _scenarios.Add(new ProbabilityBoundsInvariantScenario());
        _scenarios.Add(new TradeTimeoutInvariantScenario());
        _scenarios.Add(new ModelContractGridScenario());

        // 2. Data & Database Fault Injection
        _scenarios.Add(new StaleDataAndGapFaultScenario());
        _scenarios.Add(new CircuitBreakerQuarantineFaultScenario());
        _scenarios.Add(new DatabaseResilienceFaultScenario());
        _scenarios.Add(new DatabaseSchemaContractScenario());

        // 3. Service Outages & Load / Stress
        _scenarios.Add(new MLServiceOutageToleranceScenario());
        _scenarios.Add(new ConcurrentBurstStressScenario());
    }

    public ChaosAuditReport? GetLatestReport() => _latestReport;

    public async Task<ChaosAuditReport> RunCategoryAuditAsync(ChaosCategory category, CancellationToken ct = default)
    {
        var targetScenarios = _scenarios.Where(s => s.Category == category).ToList();
        return await ExecuteScenariosAsync(targetScenarios, $"Chaos Audit ({category})", ct);
    }

    public async Task<ChaosAuditReport> RunFullAuditAsync(CancellationToken ct = default)
    {
        return await ExecuteScenariosAsync(_scenarios, "Full Autonomous Chaos Audit", ct);
    }

    private async Task<ChaosAuditReport> ExecuteScenariosAsync(
        List<IChaosScenario> scenariosToRun,
        string suiteTitle,
        CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var report = new ChaosAuditReport
            {
                TotalScenarios = scenariosToRun.Count
            };

            BotLogger.Info($"[CHAOS-AUDIT] Starting {suiteTitle} with {scenariosToRun.Count} scenarios...");

            using var scope = _serviceProvider.CreateScope();

            foreach (var scenario in scenariosToRun)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    BotLogger.Info($"[CHAOS-AUDIT] Running scenario: {scenario.Name} ({scenario.Category})...");
                    var result = await scenario.ExecuteAsync(scope.ServiceProvider, ct);
                    report.Results.Add(result);

                    if (result.Passed)
                    {
                        report.PassedCount++;
                        BotLogger.Info($"[CHAOS-AUDIT] PASS: {scenario.Name} ({result.DurationMs}ms)");
                    }
                    else if (result.Severity == ChaosSeverity.Warning)
                    {
                        report.WarningCount++;
                        BotLogger.Warn($"[CHAOS-AUDIT] WARN: {scenario.Name} ({result.DurationMs}ms) - {result.Description}");
                    }
                    else
                    {
                        report.FailedCount++;
                        BotLogger.Error($"[CHAOS-AUDIT] FAIL: {scenario.Name} ({result.DurationMs}ms) - {result.Description}", null);
                    }
                }
                catch (Exception ex)
                {
                    report.FailedCount++;
                    var errorResult = ChaosScenarioResult.Failure(
                        scenario.Name,
                        scenario.Category,
                        0,
                        $"Unhandled exception during execution: {ex.Message}",
                        "Scenario execution",
                        ex.Message,
                        "Scenario handles errors gracefully",
                        ex.StackTrace ?? "",
                        "Audit scenario runner.",
                        ChaosSeverity.Critical
                    );
                    report.Results.Add(errorResult);
                    BotLogger.Error($"[CHAOS-AUDIT] CRITICAL: {scenario.Name} threw exception: {ex.Message}", ex);
                }
            }

            sw.Stop();
            report.TotalDurationMs = sw.ElapsedMilliseconds;

            // Compute overall status
            if (report.FailedCount > 0)
                report.Status = "CRITICAL";
            else if (report.WarningCount > 0)
                report.Status = "DEGRADED";
            else
                report.Status = "PASS";

            report.Summary = $"{suiteTitle} completed in {report.TotalDurationMs}ms. Status: {report.Status}. Passed: {report.PassedCount}/{report.TotalScenarios}, Warnings: {report.WarningCount}, Failures: {report.FailedCount}.";
            BotLogger.Info($"[CHAOS-AUDIT] {report.Summary}");

            _latestReport = report;

            // Dispatch Telegram notification if any issues detected
            if (report.FailedCount > 0 || report.WarningCount > 0)
            {
                await NotifyAdminsOfChaosFindingsAsync(report);
            }

            return report;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static async Task NotifyAdminsOfChaosFindingsAsync(ChaosAuditReport report)
    {
        try
        {
            string icon = report.FailedCount > 0 ? "🚨" : "🟡";
            var sb = new StringBuilder();
            sb.AppendLine($"{icon} <b>Chaos Audit: Обнаружены замечания системы</b>");
            sb.AppendLine($"Статус: <b>{report.Status}</b> (Пройдено: {report.PassedCount}/{report.TotalScenarios}, Замечаний: {report.WarningCount}, Ошибок: {report.FailedCount})");
            sb.AppendLine();

            foreach (var r in report.Results.Where(r => !r.Passed))
            {
                string itemIcon = r.Severity == ChaosSeverity.Critical ? "❌" : "⚠️";
                sb.AppendLine($"{itemIcon} <b>{r.ScenarioName}</b> ({r.Category})");
                sb.AppendLine($"• <b>Аномалия:</b> {r.Description}");
                if (!string.IsNullOrEmpty(r.TriggerDetails))
                    sb.AppendLine($"• <b>Триггер:</b> <code>{r.TriggerDetails}</code>");
                if (!string.IsNullOrEmpty(r.ObservedOutput))
                    sb.AppendLine($"• <b>Получено:</b> <code>{r.ObservedOutput}</code>");
                if (!string.IsNullOrEmpty(r.ExpectedOutput))
                    sb.AppendLine($"• <b>Ожидалось:</b> <code>{r.ExpectedOutput}</code>");
                if (!string.IsNullOrEmpty(r.RootCause))
                    sb.AppendLine($"• <b>Причина:</b> {r.RootCause}");
                if (!string.IsNullOrEmpty(r.FixRecommendation))
                    sb.AppendLine($"• <b>Рекомендация:</b> <i>{r.FixRecommendation}</i>");
                sb.AppendLine();
            }

            await TelegramBotService.SendMessageToAdmins(sb.ToString());
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[CHAOS-AUDIT] Could not send Telegram alert: {ex.Message}");
        }
    }
}
