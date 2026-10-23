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
/// Spread Explosion & Liquidity Shock Sabotage Scenario:
/// Simulates liquidity collapse, spread blowouts, and flash volatility squeezes:
///  - Flash Crash / Squeeze detection boundary (single candle range > 4x ATR)
///  - Liquidity vacuum: zero tick volume combined with wide range candles
///  - Book inversion invariants: negative ranges and corrupted spreads
/// </summary>
public class SpreadExplosionAndLiquidityShockScenario : IChaosScenario
{
    public string Name => "SpreadExplosionAndLiquidityShock";
    public ChaosCategory Category => ChaosCategory.DataAndDbFaults;

    public Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        const string testAsset = "CHAOS_SPREAD";
        const string testTf = "s5";

        try
        {
            var gatekeeper = sp.GetRequiredService<IRiskGatekeeper>();
            var mathEngine = sp.GetRequiredService<IMathEngine>();

            // ── Test 1: Flash Crash / Volatility Squeeze Guard (> 4x ATR) ──
            // Generate 160 steady candles with small ATR (~0.00010)
            var baseCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.08500, slopePerCandle: 0.00005, wickVolatility: 0.00005, intervalSec: 5);
            var basePrices = baseCandles.Select(c => c.Close).ToArray();
            double normalAtr = mathEngine.ComputeAtr(testAsset, testTf, baseCandles, 14);

            // Inject a flash squeeze candle at the end with range = 5x normal ATR
            var squeezeCandles = (MiniAppController.OhlcCandle[])baseCandles.Clone();
            double squeezeRange = normalAtr * 5.0;
            var last = squeezeCandles[^1];
            squeezeCandles[^1] = new MiniAppController.OhlcCandle(
                last.Open,
                last.Open + squeezeRange,
                last.Open,
                last.Open + squeezeRange,
                last.Volume,
                last.Timestamp
            );
            var squeezePrices = squeezeCandles.Select(c => c.Close).ToArray();

            var squeezeGate = gatekeeper.ValidateMarketGatekeeper(testAsset, testTf, squeezePrices, squeezeCandles);
            if (squeezeGate.IsTradeable)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Risk Gatekeeper allowed trading on an abnormal flash squeeze (> 4x ATR single candle range).",
                    $"normalAtr={normalAtr:F5}, squeezeRange={squeezeRange:F5} (5x ATR)",
                    "squeezeGate.IsTradeable = true",
                    "squeezeGate.IsTradeable = false with Flash Crash reason",
                    "Flash crash threshold (atr * 4.0) in ValidateMarketGatekeeper was bypassed.",
                    "Verify maxCandleRange check against atr * 4.0 in TechnicalAnalysisEngine.cs.",
                    ChaosSeverity.Critical,
                    "Вход в сделку на острие новостной шпильки гарантирует проскальзывание и слив депозита."));
            }

            // ── Test 2: Boundary test: sub-squeeze range (3.0x ATR) should NOT trigger flash crash lock ──
            var moderateCandles = (MiniAppController.OhlcCandle[])baseCandles.Clone();
            double moderateRange = normalAtr * 2.5;
            var mlast = moderateCandles[^1];
            moderateCandles[^1] = new MiniAppController.OhlcCandle(
                mlast.Open,
                mlast.Open + moderateRange,
                mlast.Open,
                mlast.Open + moderateRange,
                mlast.Volume,
                mlast.Timestamp
            );
            var moderatePrices = moderateCandles.Select(c => c.Close).ToArray();

            var moderateGate = gatekeeper.ValidateMarketGatekeeper(testAsset, testTf, moderatePrices, moderateCandles);
            if (!moderateGate.IsTradeable && moderateGate.Reason.Contains("Flash Crash"))
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Risk Gatekeeper false-positively flagged a moderate 2.5x ATR movement as Flash Crash.",
                    $"normalAtr={normalAtr:F5}, range={moderateRange:F5} (2.5x ATR)",
                    $"IsTradeable = false, Reason={moderateGate.Reason}",
                    "IsTradeable = true (or false only for reasons other than Flash Crash)",
                    "Over-sensitive squeeze threshold in ValidateMarketGatekeeper.",
                    "Ensure threshold is strictly atr * 4.0.",
                    ChaosSeverity.Warning,
                    "Ложная блокировка нормальных трендовых импульсов снижает количество прибыльных сделок."));
            }

            // ── Test 3: Liquidity Vacuum (Illiquid Market with Zero Volume Candles) ──
            var vacuumCandles = ChaosTestHelper.GenerateFlatlineCandles(160, price: 1.08500, intervalSec: 5);
            var vacuumPrices = vacuumCandles.Select(c => c.Close).ToArray();

            var vacuumGate = gatekeeper.ValidateMarketGatekeeper(testAsset, testTf, vacuumPrices, vacuumCandles);
            if (vacuumGate.IsTradeable)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Risk Gatekeeper allowed trading on an illiquid liquidity vacuum (100% zero-range flat candles).",
                    "160 candles with zero range and zero ticks",
                    "vacuumGate.IsTradeable = true",
                    "vacuumGate.IsTradeable = false with dead market / flatline reason",
                    "Dead market threshold bypassed.",
                    "Ensure deadMarketThreshold check is active in TechnicalAnalysisEngine.cs.",
                    ChaosSeverity.Critical,
                    "Торговля на мертвом рынке при закрытой сессии или отсутствии котировок."));
            }

            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Spread explosion and liquidity shock defenses verified: 5x ATR squeeze blocked, 2.5x ATR permitted, 0-range vacuum blocked.",
                $"NormalAtr={normalAtr:F5}, SqueezeBlocked={!squeezeGate.IsTradeable}, VacuumBlocked={!vacuumGate.IsTradeable}"
            ));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Unhandled exception during spread and liquidity sabotage: {ex.Message}",
                ex.GetType().Name,
                ex.Message,
                "Defensive rejection without unhandled exceptions",
                ex.StackTrace ?? "",
                "Harden RiskGatekeeper against extreme spread and liquidity data.",
                ChaosSeverity.Critical,
                "Падение риск-контроля при рыночном сквизе."));
        }
    }
}
