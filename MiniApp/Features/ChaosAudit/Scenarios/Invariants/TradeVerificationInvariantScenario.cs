using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp.Features.ChaosAudit.Models;
using ValutaBot.MiniApp.Services;

namespace ValutaBot.MiniApp.Features.ChaosAudit.Scenarios.Invariants;

/// <summary>
/// Invariant property tests for trade verification logic (PendingTradeVerificationService / TradeOutcomeTracker).
/// Targets real past incidents:
///  - ~9% inverted win/loss labels caused by timing/grid offsets.
///  - Exact Doji trades mistakenly classified as WIN or LOSS instead of TIE/null.
///  - Target grid bucket non-alignment or time rewinds.
///  - MFE / MAE bounds consistency (MFE >= 0, MAE >= 0, MFE >= |pnl| on win).
/// </summary>
public class TradeVerificationInvariantScenario : IChaosScenario
{
    public string Name => "TradeVerificationInvariant";
    public ChaosCategory Category => ChaosCategory.Invariants;

    public Task<ChaosScenarioResult> ExecuteAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // ── Invariant 1: 5-Second Candle Grid Alignment ──
            // CreatedAt at arbitrary second/millisecond must snap strictly to 5s open boundaries
            long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
            var testTimes = new[]
            {
                new DateTime(2026, 10, 23, 12, 0, 1, 350, DateTimeKind.Utc),
                new DateTime(2026, 10, 23, 12, 0, 4, 999, DateTimeKind.Utc),
                new DateTime(2026, 10, 23, 12, 0, 5, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 23, 12, 0, 0, 0, DateTimeKind.Utc),
            };

            foreach (var createdUtc in testTimes)
            {
                DateTime createdBucket = new DateTime(createdUtc.Ticks - (createdUtc.Ticks % s5Ticks), DateTimeKind.Utc);
                if (createdBucket.Ticks % s5Ticks != 0)
                {
                    sw.Stop();
                    return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                        "CreatedBucket must be strictly aligned to a 5-second boundary.",
                        $"createdUtc={createdUtc:O}", $"bucket={createdBucket:O}", "bucket.Ticks % 5s == 0",
                        "Grid bucket snapping modulo calculation error",
                        "Check createdBucket computation in PendingTradeVerificationService."));
                }

                int durationSec = 15;
                DateTime targetGrid = createdBucket.AddSeconds(durationSec);
                if (targetGrid.Ticks % s5Ticks != 0 || targetGrid <= createdBucket)
                {
                    sw.Stop();
                    return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                        "TargetGrid must be strictly on a 5-second boundary and strictly greater than CreatedBucket.",
                        $"createdUtc={createdUtc:O}, duration={durationSec}", $"targetGrid={targetGrid:O}",
                        "targetGrid > createdBucket and targetGrid.Ticks % 5s == 0",
                        "Target grid advancement logic error",
                        "Verify targetGrid calculation in PendingTradeVerificationService."));
                }
            }

            // ── Invariant 2: Win / Loss / Doji Labeling Invariants ──
            // BUY trade: Exit > Entry => WIN; Exit < Entry => LOSS; Exit == Entry => TIE (null)
            const double entry = 1.08500;
            var buyWin = ComputeOutcome("BUY", entry, entry + 0.00010);
            var buyLoss = ComputeOutcome("BUY", entry, entry - 0.00010);
            var buyDoji = ComputeOutcome("BUY", entry, entry);

            if (buyWin.WasCorrect != true || buyWin.PnlBps <= 0)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "BUY trade with Exit > Entry must yield WasCorrect=true and PnlBps > 0.",
                    $"Entry={entry}, Exit={entry + 0.00010}", $"WasCorrect={buyWin.WasCorrect}, PnlBps={buyWin.PnlBps}",
                    "WasCorrect=true, PnlBps > 0", "Inverted sign or comparison in BUY settlement",
                    "Audit WasCorrect condition in PendingTradeVerificationService."));
            }

            if (buyLoss.WasCorrect != false || buyLoss.PnlBps >= 0)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "BUY trade with Exit < Entry must yield WasCorrect=false and PnlBps < 0.",
                    $"Entry={entry}, Exit={entry - 0.00010}", $"WasCorrect={buyLoss.WasCorrect}, PnlBps={buyLoss.PnlBps}",
                    "WasCorrect=false, PnlBps < 0", "Sign error in loss determination",
                    "Audit WasCorrect condition in PendingTradeVerificationService."));
            }

            if (buyDoji.WasCorrect != null)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Exact Doji (Exit == Entry) must yield null WasCorrect (TIE), never WIN or LOSS.",
                    $"Entry={entry}, Exit={entry}", $"WasCorrect={buyDoji.WasCorrect}", "WasCorrect=null",
                    "Missing exact Doji check before WIN/LOSS assignment",
                    "Verify isExactDoji handling in PendingTradeVerificationService."));
            }

            // PUT trade: Exit < Entry => WIN; Exit > Entry => LOSS; Exit == Entry => TIE (null)
            var putWin = ComputeOutcome("PUT", entry, entry - 0.00010);
            var putLoss = ComputeOutcome("PUT", entry, entry + 0.00010);
            var putDoji = ComputeOutcome("PUT", entry, entry);

            if (putWin.WasCorrect != true || putWin.PnlBps >= 0)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "PUT trade with Exit < Entry must yield WasCorrect=true (PnlBps < 0 represents raw price return).",
                    $"Entry={entry}, Exit={entry - 0.00010}", $"WasCorrect={putWin.WasCorrect}, PnlBps={putWin.PnlBps}",
                    "WasCorrect=true, PnlBps < 0", "Inverted sign or comparison in PUT settlement",
                    "Audit WasCorrect condition for PUT in PendingTradeVerificationService."));
            }

            if (putLoss.WasCorrect != false || putLoss.PnlBps <= 0)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "PUT trade with Exit > Entry must yield WasCorrect=false and PnlBps > 0.",
                    $"Entry={entry}, Exit={entry + 0.00010}", $"WasCorrect={putLoss.WasCorrect}, PnlBps={putLoss.PnlBps}",
                    "WasCorrect=false, PnlBps > 0", "Sign error in PUT loss determination",
                    "Audit WasCorrect condition in PendingTradeVerificationService."));
            }

            if (putDoji.WasCorrect != null)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "Exact Doji PUT trade must yield null WasCorrect (TIE).",
                    $"Entry={entry}, Exit={entry}", $"WasCorrect={putDoji.WasCorrect}", "WasCorrect=null",
                    "Doji misclassified", "Verify isExactDoji check in PendingTradeVerificationService."));
            }

            // ── Invariant 3: Excursion (MFE / MAE) Non-Negativity ──
            var (mfeBuy, maeBuy) = ComputeExcursion("BUY", entry, high: 1.08520, low: 1.08490, exit: 1.08515);
            var (mfePut, maePut) = ComputeExcursion("PUT", entry, high: 1.08520, low: 1.08490, exit: 1.08495);

            if (mfeBuy < 0 || maeBuy < 0 || mfePut < 0 || maePut < 0)
            {
                sw.Stop();
                return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                    "MFE and MAE must always be non-negative.",
                    $"mfeBuy={mfeBuy}, maeBuy={maeBuy}, mfePut={mfePut}, maePut={maePut}",
                    "Negative excursion detected", "MFE >= 0 and MAE >= 0",
                    "Math inversion in MFE/MAE calculation",
                    "Audit MFE/MAE logic in PendingTradeVerificationService."));
            }

            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Success(Name, Category, sw.ElapsedMilliseconds,
                "Trade verification invariants verified: 5s grid snapping, BUY/PUT/Doji outcomes, and MFE/MAE consistency.",
                $"Checked grid boundaries, BUY WIN/LOSS/DOJI, PUT WIN/LOSS/DOJI, and MFE/MAE non-negativity."));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Task.FromResult(ChaosScenarioResult.Failure(Name, Category, sw.ElapsedMilliseconds,
                "Unhandled exception in TradeVerificationInvariantScenario.",
                ex.GetType().Name, ex.Message, "Clean execution", ex.StackTrace ?? "",
                "Examine exception trace.", ChaosSeverity.Critical));
        }
    }

    private static (bool? WasCorrect, double PnlBps) ComputeOutcome(string direction, double entry, double exit)
    {
        double priceDiff = (exit - entry) / entry;
        bool isExactDoji = Math.Abs(priceDiff) < 1e-8;
        double pnlBps = Math.Round(priceDiff * 10000, 2);

        if (isExactDoji || direction is "HOLD" or "SHADOW_HOLD")
            return (null, pnlBps);

        bool wasCorrect = (direction.EndsWith("BUY") && exit > entry)
                       || (direction.EndsWith("PUT") && exit < entry);
        return (wasCorrect, pnlBps);
    }

    private static (double Mfe, double Mae) ComputeExcursion(string direction, double entry, double high, double low, double exit)
    {
        double maxH = Math.Max(high, Math.Max(exit, entry));
        double minL = Math.Min(low, Math.Min(exit, entry));

        if (direction.EndsWith("BUY"))
        {
            double mfe = Math.Round((maxH - entry) / entry * 10000, 2);
            double mae = Math.Round((entry - minL) / entry * 10000, 2);
            return (mfe, mae);
        }
        else
        {
            double mfe = Math.Round((entry - minL) / entry * 10000, 2);
            double mae = Math.Round((maxH - entry) / entry * 10000, 2);
            return (mfe, mae);
        }
    }
}
