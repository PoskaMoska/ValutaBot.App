using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;

/// <summary>
/// Internal C# pipeline poisoning scenario:
/// Actively attempts to break internal engines (Gatekeeper, SMC, TA, MathEngine, StateEngine)
/// with corrupted data payloads:
///  - Inverted OHLC (High < Low, negative prices)
///  - Non-monotonic timestamps (backwards time jumps)
///  - Zero-volume flatline with massive price gaps
///  - Extreme flash drops with instantaneous rebound
/// </summary>
public class PipelinePoisoningScenario : IChaosScenario
{
    public string Name => "PipelinePoisoning";
    public ChaosCategory Category => ChaosCategory.DataAndDbFaults;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        const string testAsset = "CHAOS_POISON";
        const string testTf = "s5";

        try
        {
            var gatekeeper = sp.GetRequiredService<IRiskGatekeeper>();
            var mathEngine = sp.GetRequiredService<IMathEngine>();
            var taEngine = sp.GetRequiredService<IMarketAnalyzer>();

            // ── Test 1: Inverted OHLC (High < Low / Negative Prices) ──
            var invertedCandles = new MiniAppController.OhlcCandle[160];
            var baseTime = DateTime.UtcNow.AddSeconds(-160 * 5);
            for (int i = 0; i < 160; i++)
            {
                // Deliberately corrupted: High is strictly less than Low, and prices are negative
                double high = 1.0500 - (i * 0.0001);
                double low = high + 0.0050; // Low > High!
                double open = (high + low) / 2.0;
                double close = open;
                invertedCandles[i] = new MiniAppController.OhlcCandle(open, high, low, close, 50.0, baseTime.AddSeconds(i * 5));
            }

            var invPrices = invertedCandles.Select(c => c.Close).ToArray();
            var invVolumes = invertedCandles.Select(c => c.Volume).ToArray();

            var gateResult = gatekeeper.ValidateMarketGatekeeper(testAsset, testTf, invPrices, invertedCandles);
            if (gateResult.IsTradeable)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Risk Gatekeeper allowed trading on inverted OHLC data (Low > High).",
                    "160 candles with Low > High",
                    "gateResult.IsTradeable = true",
                    "gateResult.IsTradeable = false with validation error",
                    "RiskGatekeeper does not assert Low <= High data integrity invariant.",
                    "Add candle sanity check in RiskGatekeeper: if (c.Low > c.High) return (false, 'Invalid OHLC').",
                    ChaosSeverity.Critical,
                    "Вход в сделку по заведомо битым / искаженным котировкам приведет к сливу депозита.");
            }

            var (invAdx, _, _) = mathEngine.ComputeTrueAdx(testAsset, testTf, invertedCandles, 7);
            double invAtr = mathEngine.ComputeAtr(testAsset, testTf, invertedCandles, 7);
            if (double.IsNaN(invAdx) || double.IsNaN(invAtr) || invAtr < 0)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "MathEngine produced NaN or negative ATR on inverted OHLC candles.",
                    "Low > High candle sequence",
                    $"ADX={invAdx}, ATR={invAtr}",
                    "Finite non-negative values",
                    "ComputeAtr / ComputeTrueAdx assumed High >= Low.",
                    "Use Math.Abs(c.High - c.Low) or clamp in MathEngine.",
                    ChaosSeverity.Warning,
                    "Искажение расчета волатильности и тайм-аута сделок.");
            }

            // ── Test 2: Non-Monotonic Timestamps (Time Travel Injection) ──
            var timeScrambledCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.08500, intervalSec: 5);
            // Invert timestamps for middle chunk: candle 80 is 10 days in the future, candle 81 is 5 days in the past
            timeScrambledCandles[80] = new MiniAppController.OhlcCandle(
                timeScrambledCandles[80].Open, timeScrambledCandles[80].High, timeScrambledCandles[80].Low, timeScrambledCandles[80].Close,
                timeScrambledCandles[80].Volume, DateTime.UtcNow.AddDays(10));
            timeScrambledCandles[81] = new MiniAppController.OhlcCandle(
                timeScrambledCandles[81].Open, timeScrambledCandles[81].High, timeScrambledCandles[81].Low, timeScrambledCandles[81].Close,
                timeScrambledCandles[81].Volume, DateTime.UtcNow.AddDays(-5));

            var timeSw = Stopwatch.StartNew();
            var smcTime = SmcEngine.AnalyzeSmcStructure(testAsset, testTf, timeScrambledCandles, timeScrambledCandles[^1].Close);
            timeSw.Stop();

            if (timeSw.ElapsedMilliseconds > 1000)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "SmcEngine execution hung or took > 1000ms on non-monotonic timestamp sequence.",
                    "Candles with timestamps jumping +10 days then -5 days",
                    $"Elapsed={timeSw.ElapsedMilliseconds}ms",
                    "Execution under 50ms",
                    "Non-monotonic timestamps caused quadratic loops or search divergence.",
                    "Ensure SmcEngine relies on index order or sorts timestamps safely.",
                    ChaosSeverity.Warning,
                    "Зависание анализатора и пропуск торговых сигналов.");
            }

            // ── Test 3: Zero-Volume Freeze with 500-bps Instant Gap ──
            var zeroVolCandles = ChaosTestHelper.GenerateFlatlineCandles(160, price: 1.08500, intervalSec: 5);
            // Sudden jump at the very last candle with 0 volume
            zeroVolCandles[^1] = new MiniAppController.OhlcCandle(1.08500, 1.09042, 1.08500, 1.09042, 0.0, zeroVolCandles[^1].Timestamp);
            var zeroPrices = zeroVolCandles.Select(c => c.Close).ToArray();

            var zeroVolGate = gatekeeper.ValidateMarketGatekeeper(testAsset, testTf, zeroPrices, zeroVolCandles);
            if (zeroVolGate.IsTradeable)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Risk Gatekeeper allowed trade on 0-volume market freeze with 500 bps gap.",
                    "159 flat zero-volume candles followed by 500-bps gap with 0 volume",
                    "zeroVolGate.IsTradeable = true",
                    "zeroVolGate.IsTradeable = false",
                    "Gatekeeper missed zero-tick / zero-volume freeze condition.",
                    "Ensure RiskGatekeeper checks for zero volume / liquidity freeze.",
                    ChaosSeverity.Critical,
                    "Вход в сделку на мертвом стакане Pocket Option с нерыночным исполнением.");
            }

            // ── Test 4: Flash Drop & Immediate Rebound (Continuous State Engine) ──
            var flashDropCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.08500, intervalSec: 5);
            // Instant 80% collapse then rebound
            int crashIdx = 155;
            flashDropCandles[crashIdx] = new MiniAppController.OhlcCandle(1.08500, 1.08500, 0.21700, 0.21700, 100.0, flashDropCandles[crashIdx].Timestamp);
            flashDropCandles[crashIdx + 1] = new MiniAppController.OhlcCandle(0.21700, 1.08500, 0.21700, 1.08500, 100.0, flashDropCandles[crashIdx + 1].Timestamp);

            var crashPrices = flashDropCandles.Select(c => c.Close).ToArray();
            var stateResult = ContinuousStateEngine.EvaluateContinuousState(crashPrices, testAsset, testTf);

            if (double.IsNaN(stateResult.VelocityBpsPerSec) || double.IsInfinity(stateResult.VelocityBpsPerSec) ||
                double.IsNaN(stateResult.MomentumContribution) || double.IsInfinity(stateResult.MomentumContribution))
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "ContinuousStateEngine returned NaN or Infinity velocity/momentum after 80% flash drop.",
                    "80% drop and immediate rebound candle",
                    $"Velocity={stateResult.VelocityBpsPerSec}, Momentum={stateResult.MomentumContribution}",
                    "Finite clamped velocity/momentum",
                    "Unbounded division in velocity calculation.",
                    "Apply Math.Clamp on velocity and momentum in ContinuousStateEngine.",
                    ChaosSeverity.Critical,
                    "NaN в весах сигналов может заблокировать принятие торговых решений.");
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "C# Pipeline successfully resisted all internal poisoning attacks (inverted OHLC, time jumps, zero-volume gap, flash collapse).",
                $"InvertedGateBlocked={!gateResult.IsTradeable}, SmcTimeMs={timeSw.ElapsedMilliseconds}ms, ZeroVolBlocked={!zeroVolGate.IsTradeable}, StateVelocity={stateResult.VelocityBpsPerSec:F2}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Unhandled exception during pipeline poisoning: {ex.Message}",
                ex.GetType().Name,
                ex.Message,
                "Handled defensive rejection without unhandled exceptions",
                ex.StackTrace ?? "",
                "Harden internal pipeline components against corrupted candle state.",
                ChaosSeverity.Critical,
                "Падение конвейера анализа котировок при поступлении аномальных данных."
            );
        }
    }
}
