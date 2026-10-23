using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ValutaBot.Core;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;
using ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;
using ValutaBot.MiniApp.Services;
using Xunit;

namespace ValutaBot.Tests.Engines;

public class ChaosAuditServiceTests
{
    private readonly IServiceProvider _serviceProvider;

    public ChaosAuditServiceTests()
    {
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
        Assert.Equal(0, report.FailedCount);
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
}
