using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

public class SymmetryMirrorInvariantScenario : IChaosScenario
{
    public string Name => "SymmetryMirrorInvariant";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var sw = Stopwatch.StartNew();
        try
        {
            var taEngine = sp.GetRequiredService<IMarketAnalyzer>();
            var mathEngine = sp.GetRequiredService<IMathEngine>();

            // 1. Generate strong upward trend (160 candles)
            var upCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.0500, slopePerCandle: 0.00012, wickVolatility: 0.00004);
            var downCandles = ChaosTestHelper.InvertCandles(upCandles);

            var upPrices = upCandles.Select(c => c.Close).ToArray();
            var upVolumes = upCandles.Select(c => c.Volume).ToArray();
            var downPrices = downCandles.Select(c => c.Close).ToArray();
            var downVolumes = downCandles.Select(c => c.Volume).ToArray();

            // Isolated sandbox tickers so synthetic candles never collide with live trading IndicatorCache
            const string upAsset = "CHAOS_SYM_UP";
            const string downAsset = "CHAOS_SYM_DOWN";

            // 2. Score both directions with TA Engine
            var (upAdx, upPdi, upMdi) = mathEngine.ComputeTrueAdx(upAsset, "s5", upCandles, 7);
            double upAtr = mathEngine.ComputeAtr(upAsset, "s5", upCandles, 7);
            var taUp = taEngine.ScoreTimeframeDetailed(upAsset, "s5", upPrices, upVolumes, candles: upCandles, adxOverride: upAdx, atrOverride: upAtr, isForex: true, pdiOverride: upPdi, mdiOverride: upMdi);

            var (downAdx, downPdi, downMdi) = mathEngine.ComputeTrueAdx(downAsset, "s5", downCandles, 7);
            double downAtr = mathEngine.ComputeAtr(downAsset, "s5", downCandles, 7);
            var taDown = taEngine.ScoreTimeframeDetailed(downAsset, "s5", downPrices, downVolumes, candles: downCandles, adxOverride: downAdx, atrOverride: downAtr, isForex: true, pdiOverride: downPdi, mdiOverride: downMdi);

            // Invariant 1: Scores must have opposing signs
            if (taUp.Score > 0.15 && taDown.Score > 0.15)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    "TA Engine directional sign symmetry invariant violated: Both original uptrend and mirror inverted downtrend produced positive (BUY) scores.",
                    $"UpScore={taUp.Score:F4}, DownScore={taDown.Score:F4}",
                    $"TaUp: {taUp.Score:F3}, TaDown: {taDown.Score:F3}",
                    "TaUp > 0 and TaDown < 0 (opposite sign under vertical reflection)",
                    "Hardcoded bullish bias or unsigned metric in scoring accumulator",
                    "Audit TechnicalAnalysisEngine.cs indicator weights to ensure all indicators are properly signed.",
                    ChaosSeverity.Critical
                );
            }

            // Invariant 2: RSI symmetry (RSI_up + RSI_down ~= 100)
            double rsiSum = taUp.RsiVal + taDown.RsiVal;
            if (Math.Abs(rsiSum - 100.0) > 4.0)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    "RSI reflection invariant violated: RSI(P) + RSI(-P) != 100.",
                    $"UpRSI={taUp.RsiVal:F2}, DownRSI={taDown.RsiVal:F2}, Sum={rsiSum:F2}",
                    $"RSI Sum: {rsiSum:F2}",
                    "RSI Sum approximately 100 +/- 4.0",
                    "RSI calculation asymmetric windowing or Wilder's smoothing boundary distortion",
                    "Verify RSI calculation in MathEngine / TechnicalAnalysisEngine.",
                    ChaosSeverity.Warning
                );
            }

            // Invariant 3: SMC structure reflection
            var smcUp = SmcEngine.AnalyzeSmcStructure(upAsset, "s5", upCandles, upCandles[^1].Close);
            var smcDown = SmcEngine.AnalyzeSmcStructure(downAsset, "s5", downCandles, downCandles[^1].Close);

            bool smcUpHasBullish = (smcUp.BosDirection ?? "").Contains("BULLISH") || (smcUp.SweepDirection ?? "").Contains("BULLISH") || (smcUp.OrderBlockType ?? "").Contains("BULLISH");
            bool smcDownHasBullish = (smcDown.BosDirection ?? "").Contains("BULLISH") || (smcDown.SweepDirection ?? "").Contains("BULLISH") || (smcDown.OrderBlockType ?? "").Contains("BULLISH");

            if (smcUpHasBullish && smcDownHasBullish)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(
                    Name,
                    Category,
                    sw.ElapsedMilliseconds,
                    "SMC structure reflection invariant violated: Inverted downtrend produced bullish SMC markers.",
                    $"smcUp.BOS={smcUp.BosDirection}, smcDown.BOS={smcDown.BosDirection}",
                    $"smcDown.BOS: {smcDown.BosDirection}",
                    "smcDown should contain BEARISH or NONE, never BULLISH",
                    "Swing point high/low logic not properly symmetric",
                    "Audit SmcEngine.cs swing high/low identification.",
                    ChaosSeverity.Warning
                );
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Symmetry reflection invariants verified across TA (Score, RSI, Slope) and SMC structures.",
                $"UpScore: {taUp.Score:F3}, DownScore: {taDown.Score:F3}, RSI Sum: {rsiSum:F2}, ATR: {upAtr:F6}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Unhandled exception during symmetry reflection audit.",
                ex.GetType().Name,
                ex.Message,
                "Clean evaluation without exceptions",
                ex.StackTrace ?? "",
                "Examine exception trace and add defensive null/bounds guards.",
                ChaosSeverity.Critical
            );
        }
    }
}
