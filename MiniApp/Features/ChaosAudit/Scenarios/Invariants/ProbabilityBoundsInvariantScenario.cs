using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

public class ProbabilityBoundsInvariantScenario : IChaosScenario
{
    public string Name => "ProbabilityBoundsInvariant";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cmEngine = sp.GetRequiredService<IConfluenceMatrixEngine>();

            // Setup neutral state & MTF dummy
            var state = new StateSignal("TREND", 0.05, 0.1);
            var mtfDummy = new ConfluenceMatrixResult(
                ConfluenceRatio: 0.67,
                IsGoldenSetup: false,
                ProbabilityBoost: 5,
                ConfluenceLabel: "Test",
                SummaryReasoning: "Chaos test",
                TimeframeDirections: new Dictionary<string, string> { ["S5"] = "BUY", ["M1"] = "BUY" },
                DominantDirection: "BUY"
            );

            // ── Test 1: All Bullish Invariant ──
            var taBull = new TaSignal(0.85, 0.90, 68.0, 1.052, 1.5, 0.0002, 35.0);
            var smcBull = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "BULLISH_OB", "BULLISH_FVG", "");
            var mlBull = new MlSignal("BUY", 0.85, 0.65, "patch_tst_v3", 1, 0.85);

            var decBull = await cmEngine.EvaluateMatrixAsync("EURUSD", "s5", isSubMinute: true, conflictPenalty: 1.0,
                taBull, smcBull, mlBull, state, mtfDummy);

            if (decBull.FinalDirection != "BUY")
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Confluence Matrix produced non-BUY verdict when all components (TA, SMC, ML) were strongly Bullish.",
                    $"TA=0.85, SMC=Bullish, ML=BUY(0.85)", $"FinalDirection={decBull.FinalDirection}, Prob={decBull.Probability}",
                    "FinalDirection == 'BUY'", "Weight inversion or logic negation in consensus calculation",
                    "Audit EvaluateMatrixAsync in ConfluenceMatrixEngine.cs.");
            }

            if (decBull.Probability < 50 || decBull.Probability > 100)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Confluence Matrix produced probability out of bounds [0, 100].",
                    $"TA=0.85, SMC=Bullish, ML=BUY(0.85)", $"Prob={decBull.Probability}",
                    "Probability in range [50, 100]", "Unclamped probability addition or boost overflow",
                    "Ensure Math.Clamp(prob, 0, 100) is applied.");
            }

            // ── Test 2: All Bearish Invariant ──
            var mtfBearDummy = mtfDummy with { DominantDirection = "PUT" };
            var taBear = new TaSignal(-0.85, 0.90, 32.0, 1.048, 1.5, 0.0002, 35.0);
            var smcBear = new SmcSignal("BEARISH_BOS", "BEARISH_SWEEP", "BEARISH_OB", "BEARISH_FVG", "");
            var mlBear = new MlSignal("PUT", 0.85, 0.65, "patch_tst_v3", 1, 0.15);

            var decBear = await cmEngine.EvaluateMatrixAsync("EURUSD", "s5", isSubMinute: true, conflictPenalty: 1.0,
                taBear, smcBear, mlBear, state, mtfBearDummy);

            if (decBear.FinalDirection != "PUT")
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Confluence Matrix produced non-PUT verdict when all components were strongly Bearish.",
                    $"TA=-0.85, SMC=Bearish, ML=PUT(0.85)", $"FinalDirection={decBear.FinalDirection}, Prob={decBear.Probability}",
                    "FinalDirection == 'PUT'", "Weight inversion or missing sign flip in Put direction",
                    "Audit ConfluenceMatrixEngine.cs bearish scoring branch.");
            }

            // ── Test 3: Conflict Penalty Monotonicity Invariant ──
            var decBullPenalized = await cmEngine.EvaluateMatrixAsync("EURUSD", "s5", isSubMinute: true, conflictPenalty: 0.6,
                taBull, smcBull, mlBull, state, mtfDummy);

            if (decBullPenalized.Probability > decBull.Probability)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Monotonicity invariant violated: Penalized probability exceeds unpenalized probability.",
                    "conflictPenalty=0.6 vs conflictPenalty=1.0",
                    $"Unpenalized={decBull.Probability}%, Penalized={decBullPenalized.Probability}%",
                    "Penalized <= Unpenalized", "Inverted penalty multiplier (e.g. division instead of multiplication)",
                    "Verify conflictPenalty application in ConfluenceMatrixEngine.cs.");
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Probability and consensus direction bounds invariants strictly verified.",
                $"BullVerdict={decBull.FinalDirection} ({decBull.Probability}%), BearVerdict={decBear.FinalDirection} ({decBear.Probability}%), PenalizedProb={decBullPenalized.Probability}%"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Probability and bounds audit encountered an unhandled exception.",
                ex.GetType().Name,
                ex.Message,
                "Clean evaluation without exceptions",
                ex.StackTrace ?? "",
                "Examine exception trace and add defensive checks.",
                ChaosSeverity.Critical
            );
        }
    }
}
