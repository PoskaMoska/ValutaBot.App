using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp;

/// <summary>
/// FIX #6: Zombie pending trades on bot restart.
/// BackgroundService that every 60 seconds reads trades where
/// verify_at &lt; UtcNow and verifies them against current price.
/// </summary>
public class PendingTradeVerificationService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        BotLogger.Info("[PendingVerifier] Started. Sweeping zombie pending trades every 60s.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepExpiredTradesAsync(stoppingToken); }
            catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Sweep error: {ex.Message}"); }
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private static async Task SweepExpiredTradesAsync(CancellationToken ct)
    {
        List<SignalTracker.PredictionRecord> expired;
        try { expired = await TradeRepository.GetPendingTradesToVerifyAsync(DateTime.UtcNow); }
        catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Could not load pending trades: {ex.Message}"); return; }

        if (expired.Count == 0) return;
        BotLogger.Info($"[PendingVerifier] Found {expired.Count} expired pending trade(s).");

        foreach (var record in expired)
        {
            if (ct.IsCancellationRequested) break;
            try { await VerifyTradeAsync(record); }
            catch (Exception ex) { BotLogger.Warn($"[PendingVerifier] Failed to verify {record.Id}: {ex.Message}"); }
        }
    }

    private static async Task VerifyTradeAsync(SignalTracker.PredictionRecord record)
    {
        double? exitPrice = null;

        if (SignalTracker._livePrices.TryGetValue(record.Asset, out double memPrice) && memPrice > 0)
            exitPrice = memPrice;
        

        if (!exitPrice.HasValue || exitPrice.Value <= 0)
        {
            BotLogger.Warn($"[PendingVerifier] No exit price for {record.Asset}/{record.Timeframe} (id={record.Id}). Discarding stale pending trade.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            return;
        }

        double priceDiff = (exitPrice.Value - record.EntryPrice) / record.EntryPrice;

        // FIX C-17: skip doji (entry==exit) — no meaningful outcome to learn from.
        if (Math.Abs(priceDiff) < 1e-8)
        {
            BotLogger.Warn($"[PendingVerifier] Doji detected for {record.Asset}. Skipping SGD feedback.");
            await TradeRepository.DeletePendingTradeAsync(record.Id); // BUGFIX: was missing, caused infinite retry loop
            return;
        }

        bool isCorrect = (record.Direction == "BUY" && exitPrice.Value > record.EntryPrice)
                      || (record.Direction == "PUT" && exitPrice.Value < record.EntryPrice);

        await TradeRepository.SaveTradeOutcomeAsync(new TradeOutcomeRecord
        {
            Id         = record.Id,
            Direction  = record.Direction,
            Asset      = record.Asset,
            Timeframe  = record.Timeframe,
            EntryPrice = record.EntryPrice,
            ExitPrice  = exitPrice.Value,
            PnlBps     = Math.Round(priceDiff * 10000, 2),
            WasWin     = isCorrect,
            CreatedAt  = record.CreatedAt.ToString("O"),
            VerifiedAt = DateTime.UtcNow.ToString("O")
        });
        await TradeRepository.DeletePendingTradeAsync(record.Id);

        foreach (var kvp in record.SourceDirections)
        {
            if (kvp.Value == "NEUTRAL") continue;
            bool isSourceCorrect = (kvp.Value == "BUY" && exitPrice.Value > record.EntryPrice)
                                || (kvp.Value == "PUT" && exitPrice.Value < record.EntryPrice);
            await TradeRepository.RecordSignalVoteAsync(kvp.Key, isSourceCorrect);
        }

        if (TradeOutcomeTracker.WfEngine != null)
            TradeOutcomeTracker.WfEngine.RecordTradeOutcome(record.Asset, record.Timeframe, isCorrect);
        if (TradeOutcomeTracker.CalibrationEngine != null)
            TradeOutcomeTracker.CalibrationEngine.RecordSourceOutcome("ENSEMBLE", record.Asset, record.Timeframe, isCorrect);

        // FIX C-16: this is the SINGLE place where SGD feedback is sent.
        // Removed duplicate call from SignalTracker in-memory path.
        _ = Task.Run(() => MLPythonService.RecordOnlineTradeOutcomeAsync(
            record.Asset, record.Timeframe, record.EntryPrice, exitPrice.Value,
            record.Direction, wasWin: isCorrect, isForex: record.IsForex));

        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {(isCorrect ? "WIN" : "LOSS")}");
    }
}
