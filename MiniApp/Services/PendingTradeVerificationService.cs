using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp;

public class PendingTradeVerificationService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        BotLogger.Info("[PendingVerifier] Started. Sweeping pending trades every 5s for strict grid verification.");

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
        string verifyInterval = "s5"; 
        
        double secondsOverdue = (DateTime.UtcNow - record.VerifyAt).TotalSeconds;

        try
        {
            string cleanAsset = record.Asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
            DateTime targetStart = record.VerifyAt.AddSeconds(-5);
            
            using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            var candle = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<dynamic>(conn, @"
                SELECT close_price as Close
                FROM subminute_candles
                WHERE asset = @Asset AND interval = @Interval
                  AND open_time >= @Start AND open_time < @End
                ORDER BY open_time ASC LIMIT 1
            ", new { 
                Asset = cleanAsset, 
                Interval = verifyInterval, 
                Start = targetStart.ToString("O"), 
                End = record.VerifyAt.ToString("O") 
            });

            if (candle != null)
            {
                exitPrice = (double)candle.Close;
            }
        }
        catch (Exception ex)
        {
             BotLogger.Warn($"[PendingVerifier] DB fetch failed: {ex.Message}");
        }

        if (!exitPrice.HasValue || exitPrice.Value <= 0)
        {
            if (secondsOverdue > 120)
            {
                BotLogger.Warn($"[PendingVerifier] STALE trade {record.Id} ({record.Asset}/{record.Timeframe}): no DB candle, discarding.");
                await TradeRepository.DeletePendingTradeAsync(record.Id);
                return;
            }
            
            if (secondsOverdue < 120) 
            {
                // Wait for the DB to populate the candle. 
                // Do not use TwelveData prices[^1] as it introduces severe lookahead/lag bias.
                return; 
            }
        }

        if (!exitPrice.HasValue || exitPrice.Value <= 0)
        {
            if (secondsOverdue > 60)
            {
                BotLogger.Warn($"[PendingVerifier] No exit price for {record.Asset} id={record.Id}. Discarding.");
                await TradeRepository.DeletePendingTradeAsync(record.Id);
            }
            return;
        }

        double priceDiff = (exitPrice.Value - record.EntryPrice) / record.EntryPrice;
        bool isCorrect = (record.Direction == "BUY" && exitPrice.Value > record.EntryPrice)
                      || (record.Direction == "PUT" && exitPrice.Value < record.EntryPrice);

        record.ExitPrice = exitPrice.Value;
        record.PnlBps = Math.Round(priceDiff * 10000, 2);
        record.WasCorrect = isCorrect;

        await TradeOutcomeTracker.OnTradeVerifiedAsync(record);
        await TradeRepository.DeletePendingTradeAsync(record.Id);

        BotLogger.Info($"[PendingVerifier] {record.Id}: {record.Direction} {record.Asset}/{record.Timeframe} -> {(isCorrect ? "WIN" : "LOSS")} @ {exitPrice.Value} (Target: {record.VerifyAt:HH:mm:ss})");
    }
}
