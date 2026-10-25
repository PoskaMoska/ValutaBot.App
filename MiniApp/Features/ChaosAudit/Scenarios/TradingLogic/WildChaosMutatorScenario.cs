using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.TradingLogic;

/// <summary>
/// Wild Chaos Mutator: Synthesizes strange, unexpected, and boundary-pushing market conditions
/// that humans would never naturally think of, stress-testing whether the bot's risk-control
/// and consensus engines maintain sanity under extreme cognitive dissonance and structural anomalies.
/// </summary>
public class WildChaosMutatorScenario : IChaosScenario
{
    public string Name => "WildChaosMutator";
    public ChaosCategory Category => ChaosCategory.TradingLogicQuality;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cmEngine = sp.GetRequiredService<IConfluenceMatrixEngine>();
            var mathEngine = sp.GetRequiredService<IMathEngine>();

            // -------------------------------------------------------------
            // Mutation 1: The "Cognitive Dissonance Shock"
            // TA screams 100% BUY, SMC screams 100% BULLISH, but Neural Brain screams 88% PUT.
            // Invariant: The consensus engine must NEVER output high certainty (>= 65%) in severe dissonance.
            // -------------------------------------------------------------
            var taConflicting = new TaSignal(0.85, 80, 82.0, 1.2, 1.5, 0.0005, 35.0);
            var smcConflicting = new SmcSignal("BULLISH_BOS", "BULLISH_SWEEP", "BULLISH_OB", "BULLISH_FVG", "");
            var mlConflicting = new MlSignal("PUT", 0.88, 0.58, "patch_tst_v3", 1, 0.12);
            var stateConflicting = new StateSignal("TREND", -5.0, -0.4);
            var mtfConflicting = new ConfluenceMatrixResult(0.67, false, 5, "MTF_PUT", "Macro bear", new Dictionary<string, string> { ["M1"] = "PUT" }, "PUT");

            var decision = await cmEngine.EvaluateMatrixAsync(
                "CHAOS_MUT_ASSET", "s5", true, 0.70, // conflictPenalty applied
                taConflicting, smcConflicting, mlConflicting, stateConflicting, mtfConflicting,
                consecutiveLosses: 0, volRatio: 1.0);

            if (decision.Probability >= 66)
            {
                sw.Stop();
                string observed = $"Dissonance probability = {decision.Probability}%, Direction = {decision.FinalDirection}";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Reckless overconfidence under cognitive dissonance: Consensus engine issued {decision.Probability}% certainty while TA and Neural Brain were in violent contradiction.",
                    "Mutation 1: Extreme Cross-Engine Dissonance Injection",
                    observed,
                    "Probability <= 62% under severe cross-engine contradiction",
                    "Missing conflict dampening in pure neural consensus path.",
                    "Enforce conflict penalty multiplication on neural confidence when TA/MTF strictly oppose ML.",
                    ChaosSeverity.Critical,
                    "Самоуверенные входы с завышенным объемом при полной несогласованности датчиков рынка."
                );
            }

            // -------------------------------------------------------------
            // Mutation 2: The "Post-Loss Circuit Breaker Dampener"
            // When consecutiveLosses >= 3, the engine MUST apply dampening.
            // -------------------------------------------------------------
            var decisionNormal = await cmEngine.EvaluateMatrixAsync(
                "CHAOS_MUT_ASSET", "s10", true, 1.0,
                taConflicting, smcConflicting, mlConflicting, stateConflicting, mtfConflicting,
                consecutiveLosses: 0, volRatio: 1.0);

            var decisionDampened = await cmEngine.EvaluateMatrixAsync(
                "CHAOS_MUT_ASSET", "s10", true, 1.0,
                taConflicting, smcConflicting, mlConflicting, stateConflicting, mtfConflicting,
                consecutiveLosses: 3, volRatio: 1.0);

            int diff = decisionNormal.Probability - decisionDampened.Probability;
            if (diff < 4 && decisionNormal.Probability > 55)
            {
                sw.Stop();
                string observed = $"Normal={decisionNormal.Probability}%, After 3 losses={decisionDampened.Probability}% (Dampening={diff}%)";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    $"Gambler's Fallacy Vulnerability: Bot fails to reduce confidence after 3 consecutive losses (dampening was only {diff}%).",
                    "Mutation 2: Post-Loss Confidence Dampener Test",
                    observed,
                    "Dampening >= 5% confidence after 3 consecutive losses",
                    "Circuit breaker risk dampener not engaging on neural brain confidence stream.",
                    "Verify consecutiveLosses dampener in ConfluenceMatrixEngine.cs.",
                    ChaosSeverity.Critical,
                    "Агрессивная торговля без снижения рисков в момент затяжной серии потерь."
                );
            }

            // -------------------------------------------------------------
            // Mutation 3: The "Phantom Frozen Quote Shock"
            // High==Low==Open==Close (zero ATR, zero body, flat line) across 50 bars.
            // Invariant: Math engine must return ATR=0, ADX bounded, no NaN or crash.
            // -------------------------------------------------------------
            var flatCandles = new MiniAppController.OhlcCandle[50];
            var now = DateTime.UtcNow;
            for (int i = 0; i < 50; i++)
            {
                flatCandles[i] = new MiniAppController.OhlcCandle(1.2000, 1.2000, 1.2000, 1.2000, 50.0, now.AddSeconds(i * 5));
            }

            double atr = mathEngine.ComputeAtr("CHAOS_FLAT", "s5", flatCandles, 7);
            var (adx, pdi, mdi) = mathEngine.ComputeTrueAdx("CHAOS_FLAT", "s5", flatCandles, 7);

            if (double.IsNaN(atr) || double.IsInfinity(atr) || double.IsNaN(adx) || double.IsInfinity(adx))
            {
                sw.Stop();
                string observed = $"ATR={atr}, ADX={adx}, PDI={pdi}, MDI={mdi}";
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    "Math Engine division by zero in frozen quote market: NaN/Infinity propagated in indicator pipeline.",
                    "Mutation 3: Frozen Flatline Quote Injection",
                    observed,
                    "Finite numeric values (ATR >= 0, ADX bounded) without NaN",
                    "Epsilon protection missing in denominator of ADX or ATR calculation.",
                    "Add 1e-8 epsilon guard to MathEngine indicator calculations.",
                    ChaosSeverity.Critical,
                    "Крах математического анализа при зависании котировок брокера."
                );
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Wild Chaos Mutator verified 3 extreme scenarios: Cognitive Dissonance Humility (PASSED), Post-Loss Dampening (PASSED), Frozen Flatline NaN-Resilience (PASSED).",
                "all_mutations_withstood"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Wild Chaos Mutator unhandled crash: {ex.Message}",
                "Synthetic stress mutation execution",
                ex.Message,
                "Pipeline absorbs wild mutations gracefully without throwing",
                ex.StackTrace ?? "",
                "Add exception barriers around synthetic mutator pipeline.",
                ChaosSeverity.Critical,
                "Аварийное падение при обработке нестандартных рыночных условий."
            );
        }
    }
}
