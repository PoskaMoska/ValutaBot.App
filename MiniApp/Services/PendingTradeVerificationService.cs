using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp;

/// <summary>
/// BackgroundService that every 60 seconds reads trades where
/// verify_at &lt; UtcNow and verifies them against current price.
/// On server restart _livePrices is empty — falls back to TwelveData HTTP instead of discarding.
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

        // 1st try: in-memory live price (fastest, available when server is running)
        if (SignalTracker._livePrices.TryGetValue(record.Asset, out double memPrice) && memPrice > 0)
        {
            exitPrice = memPrice;
            BotLogger.Info($"[PendingVerifier] Got exit price for {record.Asset} from memory: {exitPrice}");
        }

        // 2nd try: HTTP fallback — needed when server just restarted and _livePrices is empty
        if (!exitPrice.HasValue || exitPrice.Value <= 0)
        {
            BotLogger.Warn($"[PendingVerifier] No live price for {record.Asset} — fetching via HTTP fallback.");
            try
            {
                var httpResult = await TwelveDataService.FetchCandlesAsync(record.Asset, "1m", limit: 2, cacheTtlSeconds: 0);
                if (httpResult.HasValue && httpResult.Value.prices.Length > 0)
                {
                    exitPrice = httpResult.Value.prices[^1];
                    BotLogger.Info($"[PendingVerifier] HTTP fallback price for {record.Asset}: {exitPrice}");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[PendingVerifier] HTTP fallback failed for {record.Asset}: {ex.Message}");
            }
        }

        // If still no price — discard (no way to verify)
        if (!exitPrice.HasValue || exitPrice.Value <= 0)
        {
            BotLogger.Warn($"[PendingVerifier] No exit price for {record.Asset}/{record.Timeframe} (id={record.Id}) even after HTTP fallback. Discarding.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            return;
        }

        double priceDiff = (exitPrice.Value - record.EntryPrice) / record.EntryPrice;
        bool isDoji = Math.Abs(priceDiff) < 1e-8;
        bool isCorrect = (record.Direction == "BUY" && exitPrice.Value > record.EntryPrice)
                      || (record.Direction == "PUT" && exitPrice.Value < record.EntryPrice);

        // Always write to trade_outcomes — user must see all trades, including Doji
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

        if (isDoji)
        {
            BotLogger.Warn($"[PendingVerifier] Doji for {record.Asset} (entry==exit). Saved to DB but skipping ML/WF feedback.");
            return;
        }

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

        // FIX C-1: Pass entry timestamp so SGD fetches candles BEFORE trade entry,
        // not at verification time (which would be look-ahead bias).
        _ = Task.Run(() => MLPythonService.RecordOnlineTradeOutcomeAsync(
            record.Asset, record.Timeframe, record.EntryPrice, exitPrice.Value,
            record.Direction, wasWin: isCorrect, isForex: record.IsForex,
            entryTime: record.CreatedAt));

        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {(isCorrect ? "WIN" : "LOSS")}");
    }
}
