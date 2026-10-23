using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Models;

public enum ChaosCategory
{
    Invariants,
    DataAndDbFaults,
    StressAndOutages
}

public enum ChaosSeverity
{
    Info,
    Warning,
    Critical
}

public class ChaosScenarioResult
{
    public string ScenarioName { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChaosCategory Category { get; set; }
    public bool Passed { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChaosSeverity Severity { get; set; } = ChaosSeverity.Info;
    public long DurationMs { get; set; }
    public string Description { get; set; } = string.Empty;
    public string TriggerDetails { get; set; } = string.Empty;
    public string ObservedOutput { get; set; } = string.Empty;
    public string ExpectedOutput { get; set; } = string.Empty;
    public string RootCause { get; set; } = string.Empty;
    public string FixRecommendation { get; set; } = string.Empty;
    public string FinancialRisk { get; set; } = string.Empty;

    public static ChaosScenarioResult Success(
        string name,
        ChaosCategory category,
        long durationMs,
        string description,
        string observed) => new()
    {
        ScenarioName = name,
        Category = category,
        Passed = true,
        Severity = ChaosSeverity.Info,
        DurationMs = durationMs,
        Description = description,
        ObservedOutput = observed,
        ExpectedOutput = "Matches system invariants and safety guardrails"
    };

    public static ChaosScenarioResult Failure(
        string name,
        ChaosCategory category,
        long durationMs,
        string description,
        string trigger,
        string observed,
        string expected,
        string rootCause,
        string fixRecommendation,
        ChaosSeverity severity = ChaosSeverity.Critical,
        string financialRisk = "") => new()
    {
        ScenarioName = name,
        Category = category,
        Passed = false,
        Severity = severity,
        DurationMs = durationMs,
        Description = description,
        TriggerDetails = trigger,
        ObservedOutput = observed,
        ExpectedOutput = expected,
        RootCause = rootCause,
        FixRecommendation = fixRecommendation,
        FinancialRisk = string.IsNullOrWhiteSpace(financialRisk)
            ? (severity == ChaosSeverity.Critical
                ? "Высокий риск искажения торговых решений или слива депозита на боевом счёте."
                : "Риск снижения точности прогнозов или пропуска валидных входов.")
            : financialRisk
    };
}

public class SabotageStatsDto
{
    public int TotalAttacksLaunched { get; set; }
    public int AttacksRepelled { get; set; }
    public int AttacksBreached { get; set; }
    public double RepelRatePct => TotalAttacksLaunched > 0 ? (AttacksRepelled * 100.0 / TotalAttacksLaunched) : 100.0;
    public DateTime SaboteurStartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttackUtc { get; set; }
    public string? LastAttackScenario { get; set; }
    public ChaosScenarioResult? LastBreach { get; set; }
}

public class ChaosAuditReport
{
    public string AuditId { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public long TotalDurationMs { get; set; }
    public int TotalScenarios { get; set; }
    public int PassedCount { get; set; }
    public int WarningCount { get; set; }
    public int FailedCount { get; set; }
    public string Status { get; set; } = "PASS"; // PASS, DEGRADED, CRITICAL
    public string Summary { get; set; } = string.Empty;
    public List<ChaosScenarioResult> Results { get; set; } = [];
}
