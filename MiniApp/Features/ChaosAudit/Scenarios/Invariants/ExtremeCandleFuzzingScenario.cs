using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

public class ExtremeCandleFuzzingScenario : IChaosScenario
{
    public string Name => "ExtremeCandleFuzzing";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var taEngine = sp.GetRequiredService<IMarketAnalyzer>();
            var mathEngine = sp.GetRequiredService<IMathEngine>();
            var gatekeeper = sp.GetRequiredService<IRiskGatekeeper>();

            // ── Test 1: Flatline Frozen Market (OTC Freeze / Zero Volatility) ──
            var flatCandles = ChaosTestHelper.GenerateFlatlineCandles(160, price: 1.08500);
            var flatPrices = flatCandles.Select(c => c.Close).ToArray();
            var flatVolumes = flatCandles.Select(c => c.Volume).ToArray();

            var (flatAdx, flatPdi, flatMdi) = mathEngine.ComputeTrueAdx("EURUSD", "s5", flatCandles, 7);
            double flatAtr = mathEngine.ComputeAtr("EURUSD", "s5", flatCandles, 7);

            if (double.IsNaN(flatAtr) || double.IsInfinity(flatAtr))
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Flatline ATR produced NaN or Infinity.", "Flat 160 candles with 0 range",
                    $"ATR={flatAtr}", "ATR == 0.0", "Zero division in ATR smoothing",
                    "Add if (atr == 0) guard in MathEngine.ComputeAtr.");
            }

            if (double.IsNaN(flatAdx) || double.IsInfinity(flatAdx))
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Flatline ADX produced NaN or Infinity.", "Flat 160 candles with 0 range",
                    $"ADX={flatAdx}", "ADX finite number (e.g. 0.0 or 20.0)", "Division by zero TrueRange in ADX/DX calculation",
                    "Add TrueRange > 1e-9 check in MathEngine.ComputeTrueAdx.");
            }

            var flatTa = taEngine.ScoreTimeframeDetailed("EURUSD", "s5", flatPrices, flatVolumes, candles: flatCandles, adxOverride: flatAdx, atrOverride: flatAtr, isForex: true);
            if (double.IsNaN(flatTa.Score) || double.IsNaN(flatTa.RsiVal) || double.IsNaN(flatTa.Confidence))
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Flatline TA produced NaN score or RSI.", "Flat 160 candles",
                    $"Score={flatTa.Score}, RSI={flatTa.RsiVal}, Conf={flatTa.Confidence}", "Finite non-NaN values",
                    "Division by zero in RSI/HMA/Score", "Check denominator in RSI calculation.");
            }

            var flatSmc = SmcEngine.AnalyzeSmcStructure("EURUSD", "s5", flatCandles, flatPrices[^1]);
            var flatGate = gatekeeper.ValidateMarketGatekeeper("EURUSD", "s5", flatPrices, flatCandles);
            // Gatekeeper MUST block flatline market
            if (flatGate.IsTradeable)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Risk Gatekeeper allowed trading on 100% frozen flatline market.", "Zero volatility flatline series",
                    "IsTradeable = true", "IsTradeable = false with reason like Flatline/Zero ATR",
                    "Gatekeeper threshold for min ATR / price movement was bypassed",
                    "Ensure RiskGatekeeper checks minimum ATR / price spread.");
            }

            // ── Test 2: Mega Flash Spike (Flash Crash / Wick Fuzzing) ──
            var spikeCandles = ChaosTestHelper.GenerateFlashSpikeCandles(160, basePrice: 1.0500, spikeMultiplier: 100.0);
            var spikePrices = spikeCandles.Select(c => c.Close).ToArray();
            var spikeVolumes = spikeCandles.Select(c => c.Volume).ToArray();

            var (spikeAdx, _, _) = mathEngine.ComputeTrueAdx("EURUSD", "s5", spikeCandles, 7);
            double spikeAtr = mathEngine.ComputeAtr("EURUSD", "s5", spikeCandles, 7);
            var spikeTa = taEngine.ScoreTimeframeDetailed("EURUSD", "s5", spikePrices, spikeVolumes, candles: spikeCandles, adxOverride: spikeAdx, atrOverride: spikeAtr, isForex: true);
            var spikeSmc = SmcEngine.AnalyzeSmcStructure("EURUSD", "s5", spikeCandles, spikePrices[^1]);

            if (double.IsNaN(spikeTa.Score) || double.IsInfinity(spikeTa.Score))
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Flash spike produced NaN or Infinity TA score.", "100x wick candle spike",
                    $"Score={spikeTa.Score}", "Clamped finite score in [-1, +1]", "Math overflow in volatility or momentum normalizer",
                    "Apply Math.Clamp or SafeLimit in scoring.");
            }

            // ── Test 3: Rapid Alternating Zig-Zag ──
            var zigCandles = ChaosTestHelper.GenerateAlternatingCandles(160, low: 1.0500, high: 1.0550);
            var zigPrices = zigCandles.Select(c => c.Close).ToArray();
            var zigVolumes = zigCandles.Select(c => c.Volume).ToArray();

            var zigTa = taEngine.ScoreTimeframeDetailed("EURUSD", "s5", zigPrices, zigVolumes, candles: zigCandles, adxOverride: 20, atrOverride: 0.005, isForex: true);
            var zigState = ContinuousStateEngine.EvaluateContinuousState(zigPrices, "EURUSD", "s5");

            if (double.IsNaN(zigTa.Score) || double.IsInfinity(zigState.VelocityBpsPerSec))
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Alternating zig-zag produced invalid continuous state velocity.", "Oscillating prices",
                    $"Velocity={zigState.VelocityBpsPerSec}", "Finite velocity", "Time delta or denominator issue",
                    "Verify ContinuousStateEngine handles rapid reversals.");
            }

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Extreme candle fuzzing passed: Flatline, Flash Spikes, Zero Volatility, and ZigZag safely handled with zero NaNs.",
                $"FlatATR={flatAtr:F6}, FlatBlocked={!flatGate.IsTradeable}, SpikeScore={spikeTa.Score:F3}, ZigState={zigState.VelocityRegime}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Extreme candle fuzzing triggered an unhandled exception.",
                ex.GetType().Name,
                ex.Message,
                "Clean handling of extreme candle series without throwing exceptions",
                ex.StackTrace ?? "",
                "Add defensive parameter checks and boundary clamps to indicators and engines.",
                ChaosSeverity.Critical
            );
        }
    }
}
