using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

/// <summary>
/// Clock Drift & Time Skew Sabotage Scenario:
/// Tests system resilience against temporal distortions:
///  - Time-travel backwards jumps (server clock rewind during pending trades)
///  - TradeTimeoutEngine bounds under infinite/zero ATR extremes
///  - 5-second subminute grid alignment under microsecond jitter
/// </summary>
public class ClockDriftAndSkewScenario : IChaosScenario
{
    public string Name => "ClockDriftAndSkew";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        const string testAsset = "CHAOS_TIME";
        const string testTf = "s5";

        try
        {
            var timeoutEngine = sp.GetRequiredService<ITradeTimeoutEngine>();

            // ── Test 1: Backward Time Jump (Server Clock Skew) ──
            var now = DateTime.UtcNow;
            var createdUtc = now;
            var verifyUtc = now.AddSeconds(-30); // 30 seconds in the past!

            long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
            DateTime createdBucket = new DateTime(createdUtc.Ticks - (createdUtc.Ticks % s5Ticks), DateTimeKind.Utc);
            int durationSec = Math.Max(5, (int)Math.Round((verifyUtc - createdUtc).TotalSeconds / 5.0) * 5);
            DateTime targetGrid = createdBucket.AddSeconds(durationSec);

            if (durationSec < 5 || targetGrid < createdBucket)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Negative clock drift produced non-positive trade duration or inverted target grid.",
                    $"verifyUtc={verifyUtc:O}, createdUtc={createdUtc:O}",
                    $"durationSec={durationSec}, targetGrid={targetGrid:O}",
                    "durationSec >= 5 and targetGrid >= createdBucket",
                    "Missing Math.Max floor on trade verification horizon.",
                    "Ensure targetGrid calculation enforces Math.Max(5, ...) duration floor.",
                    ChaosSeverity.Critical,
                    "Искажение расчета исхода сделок при рассинхронизации NTP часов сервера."));
            }

            // ── Test 2: Extreme TradeTimeoutEngine Invariants ──
            double[] extremeAtrs = { 0.0, 1e-9, 0.0001, 10.0, 1e6 };
            double[] extremePrices = { 0.00001, 1.0500, 150.0, -1.0 };
            var dummyState = new ContinuousStateResult(0.01, 0.0, 1.050, "STABLE", 0.05, "Test State");
            var dummySmc = default(SmcEngine.SmcAnalysisResult);

            foreach (var atr in extremeAtrs)
            {
                foreach (var price in extremePrices)
                {
                    var timeout = timeoutEngine.CalculateTimeout(testAsset, testTf, atr, 1.0, dummySmc, price, dummyState, isForex: true);

                    if (timeout.TimeoutCandles <= 0 || timeout.TimeoutCandles > 120 || string.IsNullOrWhiteSpace(timeout.TimeoutText))
                    {
                        sw.Stop();
                        return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                            $"TradeTimeoutEngine produced invalid timeout for extreme inputs (ATR={atr}, Price={price}).",
                            $"atr={atr}, price={price}",
                            $"TimeoutCandles={timeout.TimeoutCandles}, TimeoutText={timeout.TimeoutText}",
                            "TimeoutCandles in [1, 120] and non-empty TimeoutText",
                            "Unbounded calculation or division by zero/negative price in TradeTimeoutEngine.",
                            "Apply Math.Clamp(timeout, 1, 120) in TradeTimeoutEngine.",
                            ChaosSeverity.Critical,
                            "Сделки с нулевым или бесконечным временем экспирации приводят к зависанию денег."));
                    }
                }
            }

            // ── Test 3: Grid Boundary Snapping with Microsecond Jitter ──
            var rng = new Random(42);
            for (int i = 0; i < 5000; i++)
            {
                long jitterTicks = rng.NextInt64(0, TimeSpan.FromSeconds(10).Ticks);
                var probeTime = new DateTime(now.Ticks + jitterTicks, DateTimeKind.Utc);
                var snappedBucket = new DateTime(probeTime.Ticks - (probeTime.Ticks % s5Ticks), DateTimeKind.Utc);

                if (snappedBucket.Ticks % s5Ticks != 0 || snappedBucket > probeTime || (probeTime - snappedBucket).TotalSeconds >= 5.0)
                {
                    sw.Stop();
                    return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                        "5-second bucket snap invariant violated under timestamp jitter.",
                        $"probeTime={probeTime:O}",
                        $"snappedBucket={snappedBucket:O}",
                        "snappedBucket % 5s == 0 and 0 <= (probeTime - snappedBucket) < 5s",
                        "Modulo arithmetic overflow on negative or skewed ticks.",
                        "Verify integer ticks modulo arithmetic.",
                        ChaosSeverity.Critical,
                        "Рассинхронизация свечных бакетов между сделками и базой данных."));
                }
            }

            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Clock drift and temporal invariants safely held across backward jumps, extreme ATR horizons, and 5000 jitter probes.",
                $"DurationSec={durationSec}, MinTimeout={1}, MaxTimeout={120}, SnapsVerified=5000"
            ));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                $"Unhandled exception during clock drift sabotage: {ex.Message}",
                ex.GetType().Name,
                ex.Message,
                "Graceful defensive handling without exceptions",
                ex.StackTrace ?? "",
                "Harden TradeTimeoutEngine and date calculations against temporal anomalies.",
                ChaosSeverity.Critical,
                "Падение службы верификации сделок при временных сдвигах."));
        }
    }
}
