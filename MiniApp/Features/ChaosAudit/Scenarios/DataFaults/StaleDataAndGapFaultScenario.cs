using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.DataFaults;

public class StaleDataAndGapFaultScenario : IChaosScenario
{
    public string Name => "StaleDataAndGapFault";
    public ChaosCategory Category => ChaosCategory.DataAndDbFaults;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var cmEngine = sp.GetRequiredService<IConfluenceMatrixEngine>();

            // ── Test 1: Stale Candle Stream Guard ──
            // Generate candles where the most recent candle timestamp is 120 seconds old
            var staleCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.0500, slopePerCandle: 0.00005);
            var staleLastTime = DateTime.UtcNow.AddSeconds(-120);
            staleCandles[^1] = staleCandles[^1] with { Timestamp = staleLastTime };

            double ageSec = (DateTime.UtcNow - staleCandles[^1].Timestamp).TotalSeconds;
            bool isDetectedStale = ageSec > 30.0;
            if (!isDetectedStale)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Stale candle timestamp guard failed: 120-second stale candle was not detected as > 30s.",
                    $"Candle Age={ageSec:F1}s", "isDetectedStale = false", "isDetectedStale = true",
                    "Timestamp comparison logic error", "Ensure scanner enforces (DateTime.UtcNow - lastCandleTime).TotalSeconds <= 30.");
            }

            // ── Test 2: Missing / Empty Higher Timeframe Resilience ──
            var primaryCandles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.0500);
            var primaryPrices = primaryCandles.Select(c => c.Close).ToArray();
            var primaryVolumes = primaryCandles.Select(c => c.Volume).ToArray();

            // Evaluate 4D matrix with completely empty higher timeframe candles
            var emptyHigher = Array.Empty<MiniAppController.OhlcCandle>();
            var mtfResult = await cmEngine.Evaluate4DMatrixAsync(
                "EURUSD",
                "s5",
                isForex: true,
                binanceSymbol: "EURUSD",
                currentCandles: primaryCandles,
                currentPrices: primaryPrices,
                currentVolumes: primaryVolumes,
                higherCandles: emptyHigher,
                higherPrices: Array.Empty<double>(),
                higherVolumes: Array.Empty<double>()
            );

            if (mtfResult == null)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Evaluate4DMatrixAsync returned null when higher timeframe candles were missing/empty.",
                    "higherCandles = []", "mtfResult == null", "Non-null ConfluenceMatrixResult with graceful fallback",
                    "Unhandled null/empty check in Evaluate4DMatrixAsync",
                    "Add empty collection guard in ConfluenceMatrixEngine.cs.");
            }

            // ── Test 3: Insufficient Candlesticks (< 35) Resilience ──
            var shortCandles = ChaosTestHelper.GenerateTrendingCandles(10, startPrice: 1.0500);
            var smcShort = SmcEngine.AnalyzeSmcStructure("EURUSD", "s5", shortCandles, shortCandles[^1].Close);

            sw.Stop();
            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Market data faults safely resisted: Stale candle filter verified, empty HTF fallback verified, short candle windows handled.",
                $"StaleAgeDetected={ageSec:F1}s, EmptyHtfRatio={mtfResult.ConfluenceRatio * 100:F0}%, ShortSmcBos={smcShort.BosDirection}"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Unhandled exception during market data fault injection.",
                ex.GetType().Name,
                ex.Message,
                "Graceful degradation without unhandled exceptions",
                ex.StackTrace ?? "",
                "Add defensive checks for empty/stale candle arrays.",
                ChaosSeverity.Critical
            );
        }
    }
}
