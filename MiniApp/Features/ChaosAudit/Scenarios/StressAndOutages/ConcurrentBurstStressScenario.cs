using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.StressAndOutages;

public class ConcurrentBurstStressScenario : IChaosScenario
{
    public string Name => "ConcurrentBurstStress";
    public ChaosCategory Category => ChaosCategory.StressAndOutages;

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var taEngine = sp.GetRequiredService<IMarketAnalyzer>();
            var mathEngine = sp.GetRequiredService<IMathEngine>();
            var cmEngine = sp.GetRequiredService<IConfluenceMatrixEngine>();

            const int concurrencyLevel = 40;
            // Isolated sandbox tickers: synthetic candles must never touch live IndicatorCache keys
            var assets = new[] { "CHAOS_BURST_1", "CHAOS_BURST_2", "CHAOS_BURST_3", "CHAOS_BURST_4", "CHAOS_BURST_5", "CHAOS_BURST_6" };
            var tfs = new[] { "s5", "s10", "s15", "s30", "m1" };

            var tasks = new List<Task<(bool success, string error)>>();
            long initialMemMb = GC.GetTotalMemory(false) / (1024 * 1024);

            for (int i = 0; i < concurrencyLevel; i++)
            {
                int index = i;
                string asset = assets[index % assets.Length];
                string tf = tfs[index % tfs.Length];

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var candles = ChaosTestHelper.GenerateTrendingCandles(160, startPrice: 1.0500 + index * 0.001);
                        var prices = candles.Select(c => c.Close).ToArray();
                        var volumes = candles.Select(c => c.Volume).ToArray();

                        // 1. MathEngine ADX + ATR
                        var (adx, pdi, mdi) = mathEngine.ComputeTrueAdx(asset, tf, candles, 7);
                        double atr = mathEngine.ComputeAtr(asset, tf, candles, 7);

                        // 2. TA Engine Scoring
                        var ta = taEngine.ScoreTimeframeDetailed(asset, tf, prices, volumes, candles: candles, adxOverride: adx, atrOverride: atr, isForex: true);

                        // 3. SMC Analysis
                        var smc = SmcEngine.AnalyzeSmcStructure(asset, tf, candles, prices[^1]);

                        // 4. Continuous State
                        var state = ContinuousStateEngine.EvaluateContinuousState(prices, asset, tf);

                        // 5. Confluence Consensus
                        var taSig = new TaSignal(ta.Score, ta.Confidence, ta.RsiVal, ta.HmaVal, ta.VolStrengthVal, atr, adx);
                        var smcSig = new SmcSignal(smc.BosDirection ?? "", smc.SweepDirection ?? "", smc.OrderBlockType ?? "", smc.FvgType ?? "", "");
                        var mlSig = new MlSignal("NEUTRAL", 0.5, 0.6, "offline", 1, 0.5);
                        var stateSig = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);
                        var mtfDummy = new ConfluenceMatrixResult(0.67, false, 0, "Stress", "Stress", new Dictionary<string, string>(), "NEUTRAL");

                        var consensus = await cmEngine.EvaluateMatrixAsync(asset, tf, tf.StartsWith("s"), 1.0, taSig, smcSig, mlSig, stateSig, mtfDummy);

                        if (consensus == null || double.IsNaN(ta.Score))
                            return (false, "Consensus null or NaN score");

                        return (true, string.Empty);
                    }
                    catch (Exception ex)
                    {
                        return (false, $"{ex.GetType().Name}: {ex.Message}");
                    }
                }, ct));
            }

            var results = await Task.WhenAll(tasks);
            sw.Stop();

            long finalMemMb = GC.GetTotalMemory(false) / (1024 * 1024);
            long memDeltaMb = Math.Max(0, finalMemMb - initialMemMb);

            int failedTasks = results.Count(r => !r.success);
            if (failedTasks > 0)
            {
                var firstError = results.First(r => !r.success).error;
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    $"{failedTasks}/{concurrencyLevel} tasks failed during concurrent burst stress test.",
                    $"Concurrency={concurrencyLevel}", $"Failed={failedTasks}, FirstError={firstError}", "0 failed tasks",
                    "Thread contention, shared mutable state, or race condition",
                    "Ensure indicator calculations and caching dictionaries are thread-safe.",
                    ChaosSeverity.Critical);
            }

            if (sw.ElapsedMilliseconds > 6000)
            {
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    $"Concurrent burst exceeded SLA latency limit ({sw.ElapsedMilliseconds}ms > 6000ms).",
                    $"Concurrency={concurrencyLevel}", $"Duration={sw.ElapsedMilliseconds}ms", "< 6000ms",
                    "Lock contention or thread pool starvation",
                    "Audit locking in indicator cache and state engines.",
                    ChaosSeverity.Warning);
            }

            return ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Concurrent burst stress test passed: {concurrencyLevel} simultaneous pipelines completed successfully.",
                $"Tasks={concurrencyLevel}, Duration={sw.ElapsedMilliseconds}ms, MemoryDelta={memDeltaMb}MB"
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Concurrent burst stress test threw an unhandled exception.",
                ex.GetType().Name,
                ex.Message,
                "Clean completion without exceptions",
                ex.StackTrace ?? "",
                "Examine exception trace and add synchronization.",
                ChaosSeverity.Critical
            );
        }
    }
}
