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

        // STALE CHECK: If verify_at was > 2 minutes ago, the server restarted after the trade expired.
        // Using current live price now would give a random result (price moved on since then).
        // Better to discard the trade cleanly than to poison the AI training set with a random win/loss.
        double staleThresholdSeconds = 120;
        double secondsOverdue = (DateTime.UtcNow - record.VerifyAt).TotalSeconds;
        if (secondsOverdue > staleThresholdSeconds)
        {
            BotLogger.Warn($"[PendingVerifier] STALE trade {record.Id} ({record.Asset}/{record.Timeframe}): " +
                           $"verify_at was {secondsOverdue:F0}s ago (threshold={staleThresholdSeconds}s). " +
                           $"Discarding to prevent corrupt win/loss recording after server restart.");
            await TradeRepository.DeletePendingTradeAsync(record.Id);
            return;
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

        // Delegate all post-trade telemetry (DB, ML RL, Calibration, WalkForward, ConsecutiveLosses)
        record.ExitPrice = exitPrice.Value;
        record.PnlBps = Math.Round(priceDiff * 10000, 2);
        record.WasCorrect = isCorrect;

        await TradeOutcomeTracker.OnTradeVerifiedAsync(record);
        await TradeRepository.DeletePendingTradeAsync(record.Id);

        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {(isCorrect ? "WIN" : "LOSS")}");
    }
}
