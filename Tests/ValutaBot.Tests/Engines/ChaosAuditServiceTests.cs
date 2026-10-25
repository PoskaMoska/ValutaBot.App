using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ValutaBot.Core;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.TradingLogic;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class ChaosAuditServiceTests
{
    private readonly IServiceProvider _serviceProvider;

    public ChaosAuditServiceTests()
    {
        ValutaBot.MiniApp.MLPythonService.SetFactory(null);
        Environment.SetEnvironmentVariable("DATABASE_URL", "");
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<MarketDataFetcher>();
        services.AddSingleton<TechnicalAnalysisEngine>();
        services.AddSingleton<ITechnicalAnalysisEngine>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        services.AddSingleton<IMathEngine>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        services.AddSingleton<IMarketAnalyzer>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        services.AddSingleton<IRiskGatekeeper>(sp => sp.GetRequiredService<TechnicalAnalysisEngine>());
        services.AddSingleton<AutoCalibrationEngine>();
        services.AddSingleton<IAutoCalibrationEngine>(sp => sp.GetRequiredService<AutoCalibrationEngine>());

        services.AddSingleton<IConfluenceMatrixEngine>(sp => new ConfluenceMatrixEngine(
            sp.GetRequiredService<MarketDataFetcher>(),
            sp.GetRequiredService<IMarketAnalyzer>(),
            sp.GetRequiredService<IAutoCalibrationEngine>()));

        services.AddSingleton<TradeTimeoutEngine>();
        services.AddSingleton<ITradeTimeoutEngine>(sp => sp.GetRequiredService<TradeTimeoutEngine>());

        var settings = new TradingBotSettings { DatasetCollectionMode = false, DatasetReadinessThreshold = 0 };
        services.AddSingleton(Options.Create(settings));
        services.AddSingleton<ICircuitBreakerService>(sp =>
            new CircuitBreakerService(
                sp.GetRequiredService<IOptions<TradingBotSettings>>(),
                () => null!
            ));

        services.AddSingleton<IChaosAuditService, ChaosAuditService>();

        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task RunFullAuditAsync_AllScenariosExecuteAndPass()
    {
        var chaosService = _serviceProvider.GetRequiredService<IChaosAuditService>();

        var report = await chaosService.RunFullAuditAsync();

        Assert.NotNull(report);
        Assert.True(report.TotalScenarios >= 7, $"Expected >= 7 scenarios, got {report.TotalScenarios}");
        var failures = string.Join("; ", report.Results.Where(r => !r.Passed).Select(r => $"{r.ScenarioName}: {r.Description} ({r.RootCause})"));
        Assert.True(report.FailedCount == 0, $"Failures ({report.FailedCount}): {failures}");
        Assert.Equal("PASS", report.Status);
        Assert.NotNull(chaosService.GetLatestReport());
    }

    [Fact]
    public async Task RunCategoryAuditAsync_FiltersBySpecifiedCategory()
    {
        var chaosService = _serviceProvider.GetRequiredService<IChaosAuditService>();

        var invReport = await chaosService.RunCategoryAuditAsync(ChaosCategory.Invariants);
        Assert.NotNull(invReport);
        Assert.All(invReport.Results, r => Assert.Equal(ChaosCategory.Invariants, r.Category));
        Assert.True(invReport.TotalScenarios >= 4);

        var dataReport = await chaosService.RunCategoryAuditAsync(ChaosCategory.DataAndDbFaults);
        Assert.NotNull(dataReport);
        Assert.All(dataReport.Results, r => Assert.Equal(ChaosCategory.DataAndDbFaults, r.Category));

        var stressReport = await chaosService.RunCategoryAuditAsync(ChaosCategory.StressAndOutages);
        Assert.NotNull(stressReport);
        Assert.All(stressReport.Results, r => Assert.Equal(ChaosCategory.StressAndOutages, r.Category));

        var qualityReport = await chaosService.RunCategoryAuditAsync(ChaosCategory.TradingLogicQuality);
        Assert.NotNull(qualityReport);
        Assert.All(qualityReport.Results, r => Assert.Equal(ChaosCategory.TradingLogicQuality, r.Category));
        Assert.True(qualityReport.TotalScenarios >= 5);
    }

    [Fact]
    public async Task SymmetryMirrorInvariant_VerifiesReflectionProperty()
    {
        var scenario = new SymmetryMirrorInvariantScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Symmetry invariant failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task ExtremeCandleFuzzing_ResistsFlatlineAndSpikesWithoutNaN()
    {
        var scenario = new ExtremeCandleFuzzingScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Extreme candle fuzzing failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task ProbabilityBoundsInvariant_GuaranteesValidVerdicts()
    {
        var scenario = new ProbabilityBoundsInvariantScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Probability bounds invariant failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task TradeVerificationInvariant_VerifiesGridAndOutcomeConsistency()
    {
        var scenario = new TradeVerificationInvariantScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Trade verification invariant failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task PipelinePoisoning_RejectsInvertedAndCorruptedCandles()
    {
        var scenario = new PipelinePoisoningScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Pipeline poisoning test failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task NeuralAdversarialFuzzing_ExecutesGracefully()
    {
        var scenario = new NeuralAdversarialFuzzingScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Neural fuzzing failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task ClockDriftAndSkew_SafelyResistsTemporalDistortions()
    {
        var scenario = new ClockDriftAndSkewScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Clock drift test failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task SpreadExplosionAndLiquidityShock_BlocksExcessiveSqueezes()
    {
        var scenario = new SpreadExplosionAndLiquidityShockScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Spread explosion test failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task MalformedWebSocketPayload_RejectsPoisonTicksWithoutCorruptingAccumulators()
    {
        var scenario = new MalformedWebSocketPayloadScenario();
        var result = await scenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(result);
        Assert.True(result.Passed, $"Malformed WS payload test failed: {result.Description} ({result.RootCause})");
    }

    [Fact]
    public async Task RunSingleSabotageRoundAsync_ExecutesAndTracksTelemetry()
    {
        var chaosService = _serviceProvider.GetRequiredService<IChaosAuditService>();

        var result = await chaosService.RunSingleSabotageRoundAsync();
        Assert.NotNull(result);

        var stats = chaosService.GetSaboteurStats();
        Assert.NotNull(stats);
        Assert.True(stats.TotalAttacksLaunched >= 1);
        Assert.True(stats.AttacksRepelled >= 1);
        Assert.NotNull(stats.LastAttackScenario);
        Assert.NotNull(stats.LastAttackUtc);
    }

    [Fact]
    public async Task TradingLogicQualityScenarios_ExecuteGracefully()
    {
        var biasScenario = new DirectionalBiasAnomalyScenario();
        var calScenario = new CalibrationDivergenceScenario();
        var leakScenario = new TemporalDataLeakageScenario();
        var entropyScenario = new ConfidenceEntropyScenario();
        var streakScenario = new ConsecutiveLossStreakScenario();
        var minerScenario = new AutonomousAnomalyMinerScenario();
        var mutatorScenario = new WildChaosMutatorScenario();

        var r1 = await biasScenario.ExecuteAsync(_serviceProvider);
        var r2 = await calScenario.ExecuteAsync(_serviceProvider);
        var r3 = await leakScenario.ExecuteAsync(_serviceProvider);
        var r4 = await entropyScenario.ExecuteAsync(_serviceProvider);
        var r5 = await streakScenario.ExecuteAsync(_serviceProvider);
        var r6 = await minerScenario.ExecuteAsync(_serviceProvider);
        var r7 = await mutatorScenario.ExecuteAsync(_serviceProvider);

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.NotNull(r3);
        Assert.NotNull(r4);
        Assert.NotNull(r5);
        Assert.NotNull(r6);
        Assert.NotNull(r7);

        Assert.Equal(ChaosCategory.TradingLogicQuality, r1.Category);
        Assert.Equal(ChaosCategory.TradingLogicQuality, r2.Category);
        Assert.Equal(ChaosCategory.TradingLogicQuality, r3.Category);
        Assert.Equal(ChaosCategory.TradingLogicQuality, r4.Category);
        Assert.Equal(ChaosCategory.TradingLogicQuality, r5.Category);
        Assert.Equal(ChaosCategory.TradingLogicQuality, r6.Category);
        Assert.Equal(ChaosCategory.TradingLogicQuality, r7.Category);
    }
}
