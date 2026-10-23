using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

public class TradeTimeoutInvariantScenario : IChaosScenario
{
    public string Name => "TradeTimeoutInvariant";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var timeoutEngine = sp.GetRequiredService<ITradeTimeoutEngine>();
            var dummySmc = default(SmcEngine.SmcAnalysisResult);
            var dummyState = new ContinuousStateResult(0.01, 0.0, 1.050, "STABLE", 0.05, "Test State");

            var tfs = new[] { "s5", "s10", "s15", "s30", "m1", "m3", "m5", "m15", "m30" };
            var atrs = new[] { 0.0, 0.0001, 0.05, 1000.0, -1.0 };

            foreach (var tf in tfs)
            {
                foreach (var atr in atrs)
                {
                    var res = timeoutEngine.CalculateTimeout("CHAOS_TIMEOUT", tf, atr, 1.0, dummySmc, 1.0500, dummyState, isForex: true);

                    if (res.TimeoutCandles < 1)
                    {
                        sw.Stop();
                        return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                            $"TimeoutCandles must be >= 1, but received {res.TimeoutCandles} for {tf} with ATR={atr}.",
                            $"TF={tf}, ATR={atr}", $"TimeoutCandles={res.TimeoutCandles}", "TimeoutCandles >= 1",
                            "Invalid candle multiplier or calculation returned 0 or negative",
                            "Ensure TradeTimeoutEngine enforces baseCandles >= 1."));
                    }

                    if (string.IsNullOrWhiteSpace(res.TimeoutText))
                    {
                        sw.Stop();
                        return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                            $"TimeoutText was empty or null for {tf}.",
                            $"TF={tf}, ATR={atr}", "TimeoutText is null/empty", "Non-empty string like '5 сек' or '1 мин'",
                            "Format string logic error",
                            "Fix FormatSeconds in TradeTimeoutEngine.cs."));
                    }
                }
            }

            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Success(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Trade timeout invariants strictly verified across all timeframes and extreme ATR values.",
                $"Tested {tfs.Length} timeframes and {atrs.Length} ATR extremes. All produced valid positive horizons."
            ));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Failure(
                Name,
                Category,
                sw.ElapsedMilliseconds,
                "Unhandled exception in TradeTimeout invariant scenario.",
                ex.GetType().Name,
                ex.Message,
                "Clean evaluation without exceptions",
                ex.StackTrace ?? "",
                "Examine exception in TradeTimeoutEngine.cs.",
                ChaosSeverity.Critical
            ));
        }
    }
}
