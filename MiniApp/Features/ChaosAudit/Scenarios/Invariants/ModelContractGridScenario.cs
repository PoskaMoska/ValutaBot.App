using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

/// <summary>
/// Contract test of the C# -> Python /predict boundary over the full live grid (6 assets x 5 timeframes = 30 cells).
/// Targets real past incidents:
///  - HTTP 422 on s30/m1 (window too short): PredictAsync swallows the status, so only a raw probe sees it.
///  - Dead neural branch / constant output: output must react to input (up vs mirrored down, across cells).
///  - Probabilities out of [0,1], unknown directions, non-determinism for identical input.
/// Read-only: no DB writes, no bot state, one request at a time, hard 60s budget.
/// </summary>
public class ModelContractGridScenario : IChaosScenario
{
    public string Name => "ModelContractGrid";
    public ChaosCategory Category => ChaosCategory.Invariants;

    private static readonly string[] Assets = { "EURUSD", "GBPUSD", "AUDUSD", "USDJPY", "USDCAD", "USDCHF" };
    private static readonly (string Tf, int Sec)[] Tfs = { ("s5", 5), ("s10", 10), ("s15", 15), ("s30", 30), ("m1", 60) };
    private static readonly string[] ValidDirections = { "BUY", "PUT", "NEUTRAL", "HOLD" };

    public async Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            var raws = new List<double>();
            var upByTf = new Dictionary<string, double>();
            var downByTf = new Dictionary<string, double>();
            int probes = 0;

            ChaosScenarioResult Fail(string desc, string trigger, string observed, string expected, string cause, string fix, ChaosSeverity sev)
            {
                sw.Stop();
                return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds, desc, trigger, observed, expected, cause, fix, sev);
            }

            foreach (var asset in Assets)
            {
                foreach (var (tf, sec) in Tfs)
                {
                    budget.Token.ThrowIfCancellationRequested();

                    // Same window the live orchestrator sends: 160 closed candles (+100 higher-TF candles)
                    var up = ChaosTestHelper.GenerateTrendingCandles(160, 1.0500, 0.00012, 0.00004, sec);
                    var mtf = ChaosTestHelper.GenerateTrendingCandles(100, 1.0500, 0.0003, 0.0001, 60);

                    var p = await MLPythonService.ProbeContractAsync(asset, tf, up, mtf, budget.Token);
                    probes++;

                    if (!p.ServiceConfigured)
                    {
                        sw.Stop();
                        return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                            "ML service not configured in this environment - contract grid skipped.", "skipped");
                    }

                    // Outage / transient failure is the domain of MLServiceOutageTolerance: stop probing, do not hammer a sick service.
                    if (p.StatusCode == null || p.StatusCode >= 500)
                    {
                        sw.Stop();
                        return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                            "ML service unreachable or returning 5xx during contract probe (grid aborted to avoid load).",
                            $"cell={asset}/{tf}", $"status={p.StatusCode?.ToString() ?? "none"}, err={p.Error}",
                            "HTTP 200 for every cell", "ML service down, restarting or model not loaded",
                            "Check ml_service health and Railway logs.", ChaosSeverity.Warning);
                    }

                    if (p.StatusCode != 200 || p.Prediction == null)
                    {
                        return Fail($"Contract violation: /predict rejected a live-shaped request for {asset}/{tf}.",
                            $"cell={asset}/{tf}, candles=160, mtf=100", $"status={p.StatusCode}, detail={p.Error}",
                            "HTTP 200 with prediction", "C# window size / payload schema differs from what ml_service validates",
                            "Align candle window in MarketAnalysisOrchestrator with ml_service/main.py validation.", ChaosSeverity.Critical);
                    }

                    var pr = p.Prediction;
                    double raw = pr.RawConfidence ?? double.NaN;
                    if (double.IsNaN(raw) || double.IsInfinity(raw) || raw < 0 || raw > 1
                        || double.IsNaN(pr.Confidence) || pr.Confidence < 0 || pr.Confidence > 1)
                    {
                        return Fail($"Probability out of [0,1] or NaN for {asset}/{tf}.",
                            $"cell={asset}/{tf}", $"confidence={pr.Confidence}, raw={pr.RawConfidence}",
                            "finite values in [0,1]", "Missing sigmoid/clamp or NaN leaking from the network",
                            "Inspect PatchTSTPredictor.predict output path.", ChaosSeverity.Critical);
                    }

                    if (!ValidDirections.Contains((pr.Direction ?? "").ToUpperInvariant()))
                    {
                        return Fail($"Unknown direction label '{pr.Direction}' for {asset}/{tf}.",
                            $"cell={asset}/{tf}", $"direction={pr.Direction}", "BUY|PUT|NEUTRAL|HOLD",
                            "Label mapping drift between Python and C#", "Audit direction mapping in ml_service.", ChaosSeverity.Warning);
                    }

                    raws.Add(raw);
                    if (asset == "EURUSD")
                    {
                        upByTf[tf] = raw;

                        // Mirrored market on the same cell: output must not be identical (dead-branch / constant-output check)
                        var down = ChaosTestHelper.InvertCandles(up);
                        var pd = await MLPythonService.ProbeContractAsync(asset, tf, down, mtf, budget.Token);
                        probes++;
                        if (pd.StatusCode == 200 && pd.Prediction?.RawConfidence is double rd)
                        {
                            downByTf[tf] = rd;
                            raws.Add(rd);
                        }
                    }
                }
            }

            // Determinism: identical input twice -> identical output
            {
                var c = ChaosTestHelper.GenerateTrendingCandles(160, 1.0500, 0.00012, 0.00004, 5);
                var a = await MLPythonService.ProbeContractAsync("EURUSD", "s5", c, null, budget.Token);
                var b = await MLPythonService.ProbeContractAsync("EURUSD", "s5", c, null, budget.Token);
                probes += 2;
                if (a.Prediction?.RawConfidence is double ra && b.Prediction?.RawConfidence is double rb && Math.Abs(ra - rb) > 1e-4)
                {
                    return Fail("Non-deterministic inference: identical input produced different outputs.",
                        "EURUSD/s5, same 160 candles twice", $"raw1={ra:F6}, raw2={rb:F6}", "|raw1-raw2| <= 1e-4",
                        "Dropout left on in eval mode, or hidden mutable state between requests",
                        "Ensure model.eval() and no stateful buffers on /predict.", ChaosSeverity.Warning);
                }
            }

            // Sensitivity: a model whose output ignores its input is a dead branch (cf. the 0.002 muffler incident)
            double spread = raws.Count > 0 ? raws.Max() - raws.Min() : 0;
            bool anyMirrorDiff = upByTf.Keys.Any(k => downByTf.TryGetValue(k, out var d) && Math.Abs(d - upByTf[k]) > 1e-4);
            if (spread < 1e-3 || !anyMirrorDiff)
            {
                return Fail("Model output is insensitive to input (possible dead/muffled branch).",
                    $"{raws.Count} probes over the 30-cell grid + mirrored variants",
                    $"spread(raw)={spread:F6}, mirrorDiffersInAnyTf={anyMirrorDiff}",
                    "spread >= 1e-3 and up/down differ in at least one timeframe",
                    "Branch gating/scaling collapsed output to a constant",
                    "Check branch contribution to the final logit in the DualStream head.", ChaosSeverity.Warning);
            }

            sw.Stop();
            return ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "ML contract verified on all 30 live cells: HTTP 200, probabilities in [0,1], deterministic, input-sensitive.",
                $"probes={probes}, rawSpread={spread:F4}");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                "Contract grid exceeded its 60s budget.", "35+ sequential probes", $"elapsed={sw.ElapsedMilliseconds}ms",
                "< 60000ms", "ML service slow (CPU starvation / thread cap)", "Check inference latency.", ChaosSeverity.Warning);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                "Unhandled exception in ModelContractGridScenario.", ex.GetType().Name, ex.Message,
                "Clean evaluation", ex.StackTrace ?? "", "Examine exception trace.", ChaosSeverity.Critical);
        }
    }
}
