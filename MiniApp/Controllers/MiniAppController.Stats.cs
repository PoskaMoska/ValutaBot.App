using ValutaBot.App.MiniApp.Data.Repositories;
using System;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace ValutaBot.MiniApp;

public static partial class MiniAppController
{
    public static async Task<IResult> HandleGetStats(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
        if (!isAuthorized)
            return Results.Json(new { error = authError }, statusCode: 401);

        var overall = await SignalTracker.GetOverallStatsAsync();
        var allStatsRaw = await SignalTracker.GetAllStatsAsync();
        var allStats = allStatsRaw
            .Where(s => s.Key != "ALL" && s.Verified > 0)
            .OrderByDescending(s => s.Verified)
            .Select(s => new
            {
                key       = s.Key,
                verified  = s.Verified,
                correct   = s.Correct,
                incorrect = s.Incorrect,
                winRate   = s.WinRate,
                pending   = s.Pending
            });

        var signalStatsRaw = await SignalTracker.GetSignalStatsAsync();
        var signalSources = signalStatsRaw
            .Select(s => new
            {
                name      = s.name,
                agreeRate = s.agreeRatePct,
                weight    = s.weight,
                count     = s.count
            });

        var recentRecords = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.LoadTradeOutcomesAsync(20);
        var recent = recentRecords
            .Select(r => new
            {
                asset     = r.Asset,
                tf        = r.Timeframe,
                direction = r.Direction,
                entry     = r.EntryPrice,
                exit      = r.ExitPrice,
                pnlBps    = r.PnlBps,
                correct   = r.WasWin,
                at        = r.CreatedAt
            });

        var pendingCount = await SignalTracker.GetPendingCountAsync();

        return Results.Json(new
        {
            overall = new
            {
                winRate   = overall.HasData ? overall.WinRate : (double?)null,
                verified  = overall.Verified,
                correct   = overall.Correct,
                incorrect = overall.Incorrect,
                pending   = pendingCount,
                hasData   = overall.HasData
            },
            byAsset       = allStats,
            signalSources,
            recentSignals = recent
        });
    }

    public static async Task<IResult> HandleGetSignalStats(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        var (isAuthorized, authError) = await AuthService.IsRequestAuthorized(context);
        if (!isAuthorized)
            return Results.Json(new { error = authError }, statusCode: 401);

        var overall = await SignalTracker.GetOverallStatsAsync();
        var signals = await SignalTracker.GetSignalStatsAsync();
        return Results.Json(new
        {
            accuracy = overall.WinRate,
            signals = signals
        });
    }
}

