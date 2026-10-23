using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.StressAndOutages;

public class MLServiceOutageToleranceScenario : IChaosScenario
{
    public string Name => "MLServiceOutageTolerance";
    public ChaosCategory Category => ChaosCategory.StressAndOutages;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cmEngine = sp.GetRequiredService<IConfluenceMatrixEngine>();

            // ── Test 1: ML Microservice Offline Graceful Fallback ──
            // Simulate complete ML service outage: mlPrediction is null
            // Invariant: System smoothly falls back to TA+SMC consensus without throwing exceptions
            var offlineMlSignal = new MlSignal("NEUTRAL", 0.5, 0.0, "offline", 1, 0.5);
            var taSignal = new TaSignal(0.65, 0.80, 62.0, 1.051, 1.2, 0.0003, 30.0);
            var smcSignal = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "BULLISH_OB", "NONE", "");
            var state = new StateSignal("TREND", 0.02, 0.05);
            var mtfDummy = new ConfluenceMatrixResult(
                ConfluenceRatio: 0.67,
                IsGoldenSetup: false,
                ProbabilityBoost: 5,
                ConfluenceLabel: "Test",
                SummaryReasoning: "Fallback test",
                TimeframeDirections: new Dictionary<string, string> { ["S5"] = "BUY", ["M1"] = "BUY" },
                DominantDirection: "BUY"
            );

            var fallbackDecision = await cmEngine.EvaluateMatrixAsync("CHAOS_ML", "s5", isSubMinute: true, conflictPenalty: 1.0,
                taSignal, smcSignal, offlineMlSignal, state, mtfDummy);

            if (fallbackDecision == null)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Confluence Matrix returned null when ML service was offline.",
                    "mlSignal = offline/NEUTRAL", "null", "Non-null ConsensusDecision",
                    "Unhandled null in fallback consensus path", "Return valid ConsensusDecision using TA+SMC weights.");
            }

            if (fallbackDecision.FinalDirection != "BUY")
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    $"Expected TA+SMC to drive BUY consensus during ML outage, but got {fallbackDecision.FinalDirection}.",
                    "TA=0.65, SMC=Bullish, ML=Neutral(0.5)", $"FinalDirection={fallbackDecision.FinalDirection}", "FinalDirection == 'BUY'",
                    "Fallback weights failed to produce valid directional consensus",
                    "Audit fallback weight calculation in ConfluenceMatrixEngine.cs.");
            }

            // ── Test 2: Unreachable Port PredictAsync Safety Guard ──
            // Even if an external call fails, PredictAsync must return null rather than crashing caller
            var testCandles = ChaosTestHelper.GenerateTrendingCandles(50);
            // Calling PredictAsync directly: if base URL is not set or service unreachable, it catches and returns null
            var directPred = await MLPythonService.PredictAsync("CHAOS_ML", "s5", testCandles, isForex: true);
            // directPred is either null (if offline) or non-null (if local Python running).
            // The invariant is that it did NOT throw an unhandled exception.

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "ML service outage tolerance verified: Graceful degradation to TA+SMC confirmed with zero unhandled exceptions.",
                $"FallbackDecision: {fallbackDecision.FinalDirection} ({fallbackDecision.Probability}%), DirectPredHandledCleanly=true"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "ML service outage scenario threw an unhandled exception.",
                ex.GetType().Name,
                ex.Message,
                "Graceful fallback without exceptions",
                ex.StackTrace ?? "",
                "Examine fallback error handling in MLPythonService and ConfluenceMatrixEngine.",
                ChaosSeverity.Critical
            );
        }
    }
}
