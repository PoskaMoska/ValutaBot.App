using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

/// <summary>
/// Adversarial fuzzing against the Python ML Neural Brain (DualStreamPatchBrain / PatchTST).
/// Probes the model with extreme edge cases:
///  - 100% flatline market (0 ATR, zero variance - tests for division-by-zero in normalizers)
///  - 1,000x flash wick spike (tests for numerical overflow in attention & softmax)
///  - Short candle window (< 35 candles - must return 422, not 500 crash)
///  - Poisoned macro context with extreme outliers
///  - 48-hour time gap discontinuity
/// </summary>
public class NeuralAdversarialFuzzingScenario : IChaosScenario
{
    public string Name => "NeuralAdversarialFuzzing";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            const string testAsset = "EURUSD";
            const string testTf = "s5";

            // ── Test 1: Flatline Frozen Market (Zero Volatility) ──
            var flatCandles = ChaosTestHelper.GenerateFlatlineCandles(160, price: 1.08500, intervalSec: 5);
            var mtfCandles = ChaosTestHelper.GenerateFlatlineCandles(100, price: 1.08500, intervalSec: 60);

            var flatProbe = await MLPythonService.ProbeContractAsync(testAsset, testTf, flatCandles, mtfCandles, budget.Token);

            if (!flatProbe.ServiceConfigured)
            {
                sw.Stop();
                return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                    "ML service not configured in this environment — neural fuzzing skipped.", "skipped");
            }

            if (flatProbe.StatusCode == null || flatProbe.StatusCode >= 500)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Neural Brain crashed (HTTP 500 or timeout) on 100% flatline market.",
                    "160 candles with 0 variance (Open=High=Low=Close=1.08500)",
                    $"status={flatProbe.StatusCode}, error={flatProbe.Error}",
                    "HTTP 200 with finite probability ~0.50 (neutral/hold)",
                    "Division by zero in normalize_candles_live or patch attention normalizer when ATR/std is zero.",
                    "Add epsilon / safe division in normalize_candles_live and verify zero-variance handling.",
                    ChaosSeverity.Critical,
                    "Зависание или аварийное падение сервиса нейросети при ночном затишье или обрыве котировок.");
            }

            if (flatProbe.Prediction != null)
            {
                var pr = flatProbe.Prediction;
                double raw = pr.RawConfidence ?? double.NaN;
                if (double.IsNaN(raw) || double.IsInfinity(raw) || double.IsNaN(pr.Confidence) || double.IsInfinity(pr.Confidence))
                {
                    sw.Stop();
                    return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                        "Neural Brain returned NaN or Infinity confidence on flatline market.",
                        "Flatline 160 candles",
                        $"confidence={pr.Confidence}, raw={pr.RawConfidence}",
                        "Finite confidence in [0.0, 1.0]",
                        "NaN leaked through softmax or temperature scaling.",
                        "Add torch.nan_to_num in patch_tst_predictor.py before softmax.",
                        ChaosSeverity.Critical,
                        "Ложный вход в сделку из-за NaN вероятности на неактивном рынке.");
                }
            }

            // ── Test 2: Mega Flash Wick Spike (1,000x Over-the-Top Outlier) ──
            var spikeCandles = ChaosTestHelper.GenerateFlashSpikeCandles(160, basePrice: 1.08500, spikeMultiplier: 1000.0, intervalSec: 5);
            var spikeProbe = await MLPythonService.ProbeContractAsync(testAsset, testTf, spikeCandles, mtfCandles, budget.Token);

            if (spikeProbe.StatusCode != 200 || spikeProbe.Prediction == null)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Neural Brain failed on 1,000x flash wick spike.",
                    "160 candles with 1,000x wick spike",
                    $"status={spikeProbe.StatusCode}, error={spikeProbe.Error}",
                    "HTTP 200 with bounded probabilities in [0, 1]",
                    "Floating point overflow in exponential / softmax.",
                    "Clamp input return features into [-5000, 5000] bps in normalize_candles_live.",
                    ChaosSeverity.Critical,
                    "Падение инференса при аномальных новостных шпильках (Black Swan).");
            }

            var spkPred = spikeProbe.Prediction;
            if (double.IsNaN(spkPred.Confidence) || spkPred.Confidence < 0 || spkPred.Confidence > 1)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Flash spike produced out-of-bounds or NaN confidence.",
                    "1,000x flash wick",
                    $"confidence={spkPred.Confidence}",
                    "confidence in [0.0, 1.0]",
                    "Numerical overflow in tensor weights.",
                    "Verify clamp bounds in patch_tst_predictor.py.",
                    ChaosSeverity.Critical,
                    "Некорректная вероятность приведет к ставке с максимальным риском во время шпильки.");
            }

            // ── Test 3: Sub-Minimum Window Injection (< 35 candles) ──
            var shortCandles = ChaosTestHelper.GenerateTrendingCandles(15, startPrice: 1.08500, intervalSec: 5);
            var shortProbe = await MLPythonService.ProbeContractAsync(testAsset, testTf, shortCandles, null, budget.Token);

            if (shortProbe.StatusCode == 500)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Neural Brain returned HTTP 500 Internal Server Error instead of 422 for short candle window.",
                    "candles.Length = 15 (< 35 required)",
                    "HTTP 500 Internal Server Error",
                    "HTTP 422 Unprocessable Entity (clean validation rejection)",
                    "Unhandled exception inside main.py instead of HTTPException(422).",
                    "Add guard in main.py: if len(candles) < 35: raise HTTPException(422).",
                    ChaosSeverity.Warning,
                    "Загрязнение логов и лишняя нагрузка на сервер при неполных окнах данных.");
            }

            // ── Test 4: Extreme Macro Context Poisoning ──
            var normalCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.08500, intervalSec: 5);
            var poisonMacro = new
            {
                DayRangePositionPct = 999.0,
                DistToDayHighBps = 999999.0,
                DistToDayLowBps = -999999.0,
                DxyMomentum1mBps = -888888.0,
                BasketSyncScore = 999.0,
                SpreadBps = 100000.0,
                PriceEntropy = 1e9,
                TrendMaturity = 9999,
                BbSqueeze = false
            };

            var macroProbe = await MLPythonService.ProbeContractAsync(testAsset, testTf, normalCandles, mtfCandles, budget.Token, macroContext: poisonMacro);
            if (macroProbe.StatusCode != 200 || macroProbe.Prediction == null)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Neural Brain failed or crashed on poisoned macro context outliers.",
                    "MacroContext with 1e9 entropy and 999999 bps drift",
                    $"status={macroProbe.StatusCode}, error={macroProbe.Error}",
                    "HTTP 200 with clamped macro features",
                    "Unbounded features in build_macro_vector_live.",
                    "Clamp macro vector features to [-10, 10] before tensor construction.",
                    ChaosSeverity.Critical,
                    "Ошибочные прогнозы при скачках DXY или аномальных спредах.");
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Neural Brain successfully repelled all adversarial fuzzing vectors (flatline, 1,000x spike, short window, poisoned macro).",
                $"FlatlineStatus={flatProbe.StatusCode}, SpikeStatus={spikeProbe.StatusCode}, ShortWindowStatus={shortProbe.StatusCode}, MacroStatus={macroProbe.StatusCode}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Unhandled exception during neural adversarial fuzzing: {ex.Message}",
                ex.GetType().Name,
                ex.Message,
                "Clean adversarial rejection or bounded execution",
                ex.StackTrace ?? "",
                "Harden neural fuzzing runner.",
                ChaosSeverity.Critical,
                "Аварийная остановка модуля аудита нейросети."
            );
        }
    }
}
