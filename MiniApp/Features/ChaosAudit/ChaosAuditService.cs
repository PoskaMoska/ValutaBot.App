using System;
using System.Collections.Concurrent;
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
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.TradingLogic;

namespace ValutaBot.MiniApp.Features.ChaosAudit;

/// <summary>
/// Continuous In-Process Saboteur (Chaos Monkey & Adversarial Fault Injector):
///  - Attacks the bot's runtime, pipeline, and Neural Brain continuously from the inside.
///  - Operates on isolated sandboxes (CHAOS_*) to guarantee 0% interference with live trading.
///  - Absolutely silent when attacks are safely repelled (no spam reports).
///  - Instantly alerts Telegram when a defense is breached or unhandled crash occurs,
///    acting as an AI Explainer with root cause, danger diagnosis, and code fix location.
/// </summary>
public class ChaosAuditService : IChaosAuditService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ChaosAuditService>? _logger;
    private readonly List<IChaosScenario> _scenarios = [];
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private ChaosAuditReport? _latestReport;

    // Saboteur execution state & telemetry
    private int _currentScenarioIndex = 0;
    private int _totalAttacksLaunched = 0;
    private int _attacksRepelled = 0;
    private int _attacksBreached = 0;
    private readonly DateTime _saboteurStartedUtc = DateTime.UtcNow;
    private DateTime? _lastAttackUtc;
    private string? _lastAttackScenario;
    private ChaosScenarioResult? _lastBreachResult;

    // Deduplication tracking to prevent spamming the exact same unchanged breach within 24h
    private static readonly ConcurrentDictionary<string, (string Fingerprint, DateTime AlertedUtc)> _alertedFindings = new();

    public ChaosAuditService(IServiceProvider serviceProvider, ILogger<ChaosAuditService>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        // Register scenarios in order:
        // 1. Invariants & Neural Brain Fuzzing
        _scenarios.Add(new SymmetryMirrorInvariantScenario());
        _scenarios.Add(new ExtremeCandleFuzzingScenario());
        _scenarios.Add(new ProbabilityBoundsInvariantScenario());
        _scenarios.Add(new TradeTimeoutInvariantScenario());
        _scenarios.Add(new TradeVerificationInvariantScenario());
        _scenarios.Add(new ModelContractGridScenario());
        _scenarios.Add(new NeuralAdversarialFuzzingScenario());
        _scenarios.Add(new ClockDriftAndSkewScenario());

        // 2. Data & Database Fault Injection & Pipeline Poisoning
        _scenarios.Add(new PipelinePoisoningScenario());
        _scenarios.Add(new SpreadExplosionAndLiquidityShockScenario());
        _scenarios.Add(new MalformedWebSocketPayloadScenario());
        _scenarios.Add(new StaleDataAndGapFaultScenario());
        _scenarios.Add(new CircuitBreakerQuarantineFaultScenario());
        _scenarios.Add(new DatabaseResilienceFaultScenario());
        _scenarios.Add(new DatabaseSchemaContractScenario());

        // 3. Service Outages & Load / Stress
        _scenarios.Add(new MLServiceOutageToleranceScenario());
        _scenarios.Add(new ConcurrentBurstStressScenario());

        // 4. Autonomous Trading Logic & Model Quality Invariants (Auditing real DB trades & live predictions)
        _scenarios.Add(new DirectionalBiasAnomalyScenario());
        _scenarios.Add(new CalibrationDivergenceScenario());
        _scenarios.Add(new TemporalDataLeakageScenario());
        _scenarios.Add(new ConfidenceEntropyScenario());
        _scenarios.Add(new ConsecutiveLossStreakScenario());
    }

    public ChaosAuditReport? GetLatestReport() => _latestReport;

    public SabotageStatsDto GetSaboteurStats() => new()
    {
        TotalAttacksLaunched = _totalAttacksLaunched,
        AttacksRepelled = _attacksRepelled,
        AttacksBreached = _attacksBreached,
        SaboteurStartedUtc = _saboteurStartedUtc,
        LastAttackUtc = _lastAttackUtc,
        LastAttackScenario = _lastAttackScenario,
        LastBreach = _lastBreachResult
    };

    /// <summary>
    /// Executes a single micro-sabotage assault chosen sequentially/shuffled across registered scenarios.
    /// Runs low-priority in the background. If repelled -> complete silence. If breached -> immediate Telegram alert.
    /// </summary>
    public async Task<ChaosScenarioResult?> RunSingleSabotageRoundAsync(CancellationToken ct = default)
    {
        if (_scenarios.Count == 0) return null;

        await _runLock.WaitAsync(ct);
        try
        {
            int idx = (int)((uint)Interlocked.Increment(ref _currentScenarioIndex) % (uint)_scenarios.Count);
            var scenario = _scenarios[idx];
            _lastAttackScenario = scenario.Name;
            _lastAttackUtc = DateTime.UtcNow;
            Interlocked.Increment(ref _totalAttacksLaunched);

            BotLogger.Info($"[SABOTEUR] Micro-assault #{_totalAttacksLaunched} launched: {scenario.Name} ({scenario.Category})...");

            using var scope = _serviceProvider.CreateScope();
            ChaosScenarioResult result;
            try
            {
                result = await scenario.ExecuteAsync(scope.ServiceProvider, ct);
            }
            catch (Exception ex)
            {
                result = ChaosScenarioResult.Failure(
                    scenario.Name,
                    scenario.Category,
                    0,
                    $"Unhandled exception during execution: {ex.Message}",
                    "Internal Saboteur micro-probe",
                    ex.Message,
                    "Scenario handles errors gracefully",
                    ex.StackTrace ?? "",
                    "Add exception guards in the scenario runner.",
                    ChaosSeverity.Critical,
                    "Неконтролируемое падение рабочего потока бота"
                );
            }

            if (result.Passed)
            {
                Interlocked.Increment(ref _attacksRepelled);
                BotLogger.Info($"[SABOTEUR] Attack #{_totalAttacksLaunched} REPELLED by guards: {scenario.Name} ({result.DurationMs}ms)");
                // SILENCE: When the bot withstands the attack, send ZERO messages to Telegram.
            }
            else
            {
                Interlocked.Increment(ref _attacksBreached);
                _lastBreachResult = result;
                BotLogger.Warn($"[SABOTEUR] Attack #{_totalAttacksLaunched} BREACHED guards: {scenario.Name} - {result.Description}");

                // Immediate Alert with AI Explainer persona
                await NotifyAdminsOfBreachAsync(result);
            }

            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

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
                    _lastAttackScenario = scenario.Name;
                    _lastAttackUtc = DateTime.UtcNow;
                    Interlocked.Increment(ref _totalAttacksLaunched);

                    BotLogger.Info($"[CHAOS-AUDIT] Running scenario: {scenario.Name} ({scenario.Category})...");
                    var result = await scenario.ExecuteAsync(scope.ServiceProvider, ct);
                    report.Results.Add(result);

                    if (result.Passed)
                    {
                        report.PassedCount++;
                        Interlocked.Increment(ref _attacksRepelled);
                        BotLogger.Info($"[CHAOS-AUDIT] PASS: {scenario.Name} ({result.DurationMs}ms)");
                    }
                    else if (result.Severity == ChaosSeverity.Warning)
                    {
                        report.WarningCount++;
                        Interlocked.Increment(ref _attacksBreached);
                        _lastBreachResult = result;
                        BotLogger.Warn($"[CHAOS-AUDIT] WARN: {scenario.Name} ({result.DurationMs}ms) - {result.Description}");
                    }
                    else
                    {
                        report.FailedCount++;
                        Interlocked.Increment(ref _attacksBreached);
                        _lastBreachResult = result;
                        BotLogger.Error($"[CHAOS-AUDIT] FAIL: {scenario.Name} ({result.DurationMs}ms) - {result.Description}", null);
                    }
                }
                catch (Exception ex)
                {
                    report.FailedCount++;
                    Interlocked.Increment(ref _attacksBreached);
                    var errorResult = ChaosScenarioResult.Failure(
                        scenario.Name,
                        scenario.Category,
                        0,
                        $"Unhandled exception during execution: {ex.Message}",
                        "Scenario execution",
                        ex.Message,
                        "Scenario handles errors gracefully",
                        ex.StackTrace ?? "",
                        "Fix unhandled exception in scenario runner.",
                        ChaosSeverity.Critical,
                        "Неконтролируемое падение рабочего потока бота"
                    );
                    _lastBreachResult = errorResult;
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

            // Dispatch Telegram notification ONLY if there are actionable breaches (deduplicated)
            // If all passed: COMPLETE SILENCE. No recovery messages!
            await NotifyAdminsOfChaosFindingsAsync(report);

            return report;
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// Formats a single scenario breach into the AI Saboteur Explainer message.
    /// </summary>
    private static string FormatBreachMessage(ChaosScenarioResult r)
    {
        string icon = r.Severity == ChaosSeverity.Critical ? "🚨" : "🟡";
        string statusText = r.Severity == ChaosSeverity.Critical ? "КРИТИЧЕСКИЙ СБОЙ" : "ЗАМЕЧАНИЕ / ДЕГРАДАЦИЯ";

        var sb = new StringBuilder();
        sb.AppendLine($"👾 <b>[ВНУТРЕННИЙ ДИВЕРСАНТ] ОБНАРУЖЕНА УЯЗВИМОСТЬ В СИСТЕМЕ!</b>");
        sb.AppendLine($"Статус: <b>{icon} {statusText}</b>");
        sb.AppendLine();
        sb.AppendLine($"💥 <b>Сценарий атаки:</b> {r.ScenarioName} (<i>{r.Category}</i>)");
        if (!string.IsNullOrEmpty(r.TriggerDetails))
            sb.AppendLine($"🧪 <b>Внедренный яд / Мутация:</b>\n<code>{r.TriggerDetails}</code>\n");

        sb.AppendLine($"⚠️ <b>Симптом сбоя:</b> {r.Description}");
        if (!string.IsNullOrEmpty(r.ObservedOutput))
            sb.AppendLine($"📥 <b>Фактический результат:</b> <code>{r.ObservedOutput}</code>");
        if (!string.IsNullOrEmpty(r.ExpectedOutput))
            sb.AppendLine($"🎯 <b>Ожидаемое поведение:</b> <code>{r.ExpectedOutput}</code>");

        sb.AppendLine();
        if (!string.IsNullOrEmpty(r.FinancialRisk))
            sb.AppendLine($"💸 <b>Опасность для депозита:</b>\n<i>{r.FinancialRisk}</i>\n");

        sb.AppendLine($"🔍 <b>AI-Диагноз и локация в коде:</b>");
        if (!string.IsNullOrEmpty(r.RootCause))
            sb.AppendLine($"• <b>Причина:</b> {r.RootCause}");
        if (!string.IsNullOrEmpty(r.FixRecommendation))
            sb.AppendLine($"• <b>Где и как чинить:</b> <code>{r.FixRecommendation}</code>");

        return sb.ToString();
    }

    private static async Task NotifyAdminsOfBreachAsync(ChaosScenarioResult r)
    {
        try
        {
            string key = r.ScenarioName;
            string fingerprint = $"{r.Severity}:{r.Description}:{r.TriggerDetails}";
            var now = DateTime.UtcNow;

            if (_alertedFindings.TryGetValue(key, out var existing))
            {
                if (existing.Fingerprint == fingerprint && (now - existing.AlertedUtc) < TimeSpan.FromHours(24))
                {
                    BotLogger.Info($"[SABOTEUR] Breach '{key}' unchanged within 24h — suppressing repeat Telegram alert.");
                    return;
                }
            }

            _alertedFindings[key] = (fingerprint, now);
            string msg = FormatBreachMessage(r);
            await TelegramBotService.SendMessageToAdmins(msg);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[SABOTEUR] Could not send Telegram alert: {ex.Message}");
        }
    }

    private static async Task NotifyAdminsOfChaosFindingsAsync(ChaosAuditReport report)
    {
        try
        {
            var unpassed = report.Results.Where(r => !r.Passed).ToList();
            if (unpassed.Count == 0)
            {
                // NO NOTIFICATION. Complete silence when all passed.
                return;
            }

            // Deduplication: only alert on findings that are new or changed within 24h
            var now = DateTime.UtcNow;
            var newOrChanged = new List<ChaosScenarioResult>();

            foreach (var r in unpassed)
            {
                string key = r.ScenarioName;
                string fingerprint = $"{r.Severity}:{r.Description}:{r.TriggerDetails}";

                if (_alertedFindings.TryGetValue(key, out var existing))
                {
                    if (existing.Fingerprint == fingerprint && (now - existing.AlertedUtc) < TimeSpan.FromHours(24))
                    {
                        continue; // Duplicate within 24 hours, suppress repeat alert
                    }
                }

                _alertedFindings[key] = (fingerprint, now);
                newOrChanged.Add(r);
            }

            if (newOrChanged.Count == 0)
            {
                BotLogger.Info("[CHAOS-AUDIT] Active findings unchanged within 24h — suppressing repeat Telegram alert.");
                return;
            }

            foreach (var r in newOrChanged)
            {
                string msg = FormatBreachMessage(r);
                await TelegramBotService.SendMessageToAdmins(msg);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[CHAOS-AUDIT] Could not send Telegram alert: {ex.Message}");
        }
    }
}
