using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp.Services;

/// <summary>
/// 24/7 Paper Trading Simulator backed by PostgreSQL (`paper_accounts`, `paper_trades`).
/// Maintains two isolated virtual $100 accounts ($1 flat stake per trade) across s5..m1:
///  - WEEKDAY: mirrors real `canExecuteReal` signals during active weekday markets (24/7 day &amp; night).
///  - WEEKEND_SIM: simulates live night/day bets during Fri 20:44Z - Sun 21:00Z using uncapped ML probabilities (&gt;=55%)
///    without altering live execution gates, UI signals, or weekday ML training labels.
/// Models realistic Pocket Option Quick Trading execution (~250ms broker RTT delay, post-delay fill price,
/// SlippageGate &lt;= 1.5 bps requote check, and exact millisecond tick expiration `t_fill + duration`).
/// </summary>
public static class PaperTradingService
{
    public const string AccountWeekday = "WEEKDAY";
    public const string AccountWeekendSim = "WEEKEND_SIM";
    public const double DefaultInitialBalanceUsd = 100.0;
    public const double DefaultStakeUsd = 1.0;
    public const int DefaultSimulatedDelayMs = 250;
    public const double MaxAdverseSlippageBps = 1.5;

    private static readonly ConcurrentDictionary<string, long> _cooldownSw =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _verifyLock = new(1, 1);

    private sealed record PendingPaperRow(
        int Id,
        string AccountMode,
        string Asset,
        string Timeframe,
        string OpenTime,
        string VerifyAt,
        string Direction,
        double PayoutPct,
        double StakeUsd,
        double EntryPrice
    );

    private sealed record PaperAccountRow(
        string AccountMode,
        double InitialBalance,
        double BalanceUsd,
        double PeakBalanceUsd,
        double MaxDrawdownUsd,
        double StakeUsd,
        int TotalTrades,
        int Wins,
        int Losses,
        int Ties,
        int Gaps,
        int CurrentStreak,
        int BestStreak,
        int WorstStreak,
        string UpdatedAt
    );

    private sealed record PeriodStatsRow(
        int Total,
        int Wins,
        int Losses,
        int Ties,
        double PnlUsd,
        double AvgPayout
    );

    private sealed record TimeframeBreakdownRow(
        string Timeframe,
        int Wins,
        int Losses,
        int Ties,
        double PnlUsd
    );

    private sealed record RecentTradeRow(
        string AccountMode,
        string Asset,
        string Timeframe,
        string Direction,
        double PayoutPct,
        string Result,
        double PnlUsd,
        double BalanceAfter,
        string VerifiedAt
    );

    public readonly record struct AccountTransition(
        double BalanceUsd,
        double PeakBalanceUsd,
        double MaxDrawdownUsd,
        int TotalTrades,
        int Wins,
        int Losses,
        int Ties,
        int Gaps,
        int CurrentStreak,
        int BestStreak,
        int WorstStreak
    );

    internal static async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;

        try
        {
            await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                await conn.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS paper_accounts (
                        account_mode TEXT PRIMARY KEY,
                        initial_balance DOUBLE PRECISION NOT NULL DEFAULT 100.0,
                        balance_usd DOUBLE PRECISION NOT NULL DEFAULT 100.0,
                        peak_balance_usd DOUBLE PRECISION NOT NULL DEFAULT 100.0,
                        max_drawdown_usd DOUBLE PRECISION NOT NULL DEFAULT 0.0,
                        stake_usd DOUBLE PRECISION NOT NULL DEFAULT 1.0,
                        total_trades INTEGER NOT NULL DEFAULT 0,
                        wins INTEGER NOT NULL DEFAULT 0,
                        losses INTEGER NOT NULL DEFAULT 0,
                        ties INTEGER NOT NULL DEFAULT 0,
                        gaps INTEGER NOT NULL DEFAULT 0,
                        current_streak INTEGER NOT NULL DEFAULT 0,
                        best_streak INTEGER NOT NULL DEFAULT 0,
                        worst_streak INTEGER NOT NULL DEFAULT 0,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS paper_trades (
                        id SERIAL PRIMARY KEY,
                        account_mode TEXT NOT NULL,
                        asset TEXT NOT NULL,
                        timeframe TEXT NOT NULL,
                        open_time TEXT NOT NULL,
                        verify_at TEXT NOT NULL,
                        direction TEXT NOT NULL,
                        probability INTEGER NOT NULL,
                        payout_pct DOUBLE PRECISION NOT NULL DEFAULT 82.0,
                        stake_usd DOUBLE PRECISION NOT NULL DEFAULT 1.0,
                        entry_price DOUBLE PRECISION NOT NULL,
                        exit_price DOUBLE PRECISION NULL,
                        result TEXT NULL,
                        pnl_usd DOUBLE PRECISION NULL,
                        balance_after DOUBLE PRECISION NULL,
                        model_version TEXT NOT NULL DEFAULT '',
                        session_name TEXT NOT NULL DEFAULT 'UNKNOWN',
                        created_at TEXT NOT NULL,
                        verified_at TEXT NULL,
                        UNIQUE(account_mode, asset, timeframe, open_time)
                    );
                    CREATE INDEX IF NOT EXISTS idx_paper_trades_pending
                        ON paper_trades(verify_at ASC) WHERE result IS NULL;
                    CREATE INDEX IF NOT EXISTS idx_paper_trades_settled
                        ON paper_trades(account_mode, verified_at DESC) WHERE result IS NOT NULL;

                    INSERT INTO paper_accounts (account_mode, initial_balance, balance_usd, peak_balance_usd, stake_usd, updated_at)
                    VALUES (
                        'WEEKDAY',
                        100.0,
                        100.0,
                        100.0,
                        1.0,
                        to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD') || 'T' || to_char(NOW() AT TIME ZONE 'UTC', 'HH24:MI:SS') || 'Z'
                    )
                    ON CONFLICT (account_mode) DO NOTHING;

                    INSERT INTO paper_accounts (account_mode, initial_balance, balance_usd, peak_balance_usd, stake_usd, updated_at)
                    VALUES (
                        'WEEKEND_SIM',
                        100.0,
                        100.0,
                        100.0,
                        1.0,
                        to_char(NOW() AT TIME ZONE 'UTC', 'YYYY-MM-DD') || 'T' || to_char(NOW() AT TIME ZONE 'UTC', 'HH24:MI:SS') || 'Z'
                    )
                    ON CONFLICT (account_mode) DO NOTHING;
                ", commandTimeout: 30);
            });
            BotLogger.Info("[PaperTrading] paper_accounts & paper_trades initialized.");
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PaperTrading] Initialization notice: {ex.Message}");
        }
    }

    internal static (bool ShouldEnter, string Direction, int Probability) EvaluateWeekendSimCandidate(
        MLPythonService.MLPythonPrediction? prediction,
        string fallbackDir,
        int fallbackProbPct,
        int requiredProbPct,
        int currentPayout,
        bool isAssetOpen,
        bool isSubminuteImpulseOk)
    {
        if (!isAssetOpen || currentPayout < 70 || !isSubminuteImpulseOk)
        {
            return (false, "NEUTRAL", 50);
        }

        string dir;
        int probPct;
        if (prediction?.RawUncappedProb is double pUp && double.IsFinite(pUp))
        {
            double clamped = Math.Clamp(pUp, 0.0, 1.0);
            if (clamped >= 0.505)
            {
                dir = "BUY";
                probPct = (int)Math.Clamp(Math.Round(clamped * 100.0), 50, 92);
            }
            else if (clamped <= 0.495)
            {
                dir = "PUT";
                probPct = (int)Math.Clamp(Math.Round((1.0 - clamped) * 100.0), 50, 92);
            }
            else
            {
                return (false, "NEUTRAL", 50);
            }
        }
        else if (fallbackDir is "BUY" or "PUT")
        {
            dir = fallbackDir;
            probPct = fallbackProbPct;
        }
        else
        {
            return (false, "NEUTRAL", 50);
        }

        // Waive only the artificial weekend <=52% clamp; simulate active high-conviction setups (>=55%)
        int weekendSimFloor = Math.Max(55, Math.Min(requiredProbPct, 58) - 3);
        return probPct >= weekendSimFloor ? (true, dir, probPct) : (false, "NEUTRAL", probPct);
    }

    internal static (string Result, double PnlUsd) ResolvePaperOutcome(
        string predictedDir,
        double stakeUsd,
        double payoutPct,
        double entryPrice,
        double exitPrice)
    {
        if (stakeUsd <= 0 || !double.IsFinite(stakeUsd)) stakeUsd = DefaultStakeUsd;
        if (!double.IsFinite(payoutPct) || payoutPct <= 0) payoutPct = 80.0;

        if (entryPrice <= 1e-8 || exitPrice <= 1e-8 || !double.IsFinite(entryPrice) || !double.IsFinite(exitPrice))
        {
            return ("TIE", 0.0);
        }

        double rawDiff = (exitPrice - entryPrice) / entryPrice;
        if (Math.Abs(rawDiff) < 1e-8)
        {
            return ("TIE", 0.0);
        }

        bool win = (predictedDir == "BUY" && exitPrice > entryPrice)
                || (predictedDir == "PUT" && exitPrice < entryPrice);
        double pnl = win
            ? Math.Round(stakeUsd * (payoutPct / 100.0), 4)
            : -Math.Round(stakeUsd, 4);
        return (win ? "WIN" : "LOSS", pnl);
    }

    internal static AccountTransition ComputeUpdatedAccountMetrics(
        double balanceUsd,
        double peakBalanceUsd,
        double maxDrawdownUsd,
        int totalTrades,
        int wins,
        int losses,
        int ties,
        int gaps,
        int currentStreak,
        int bestStreak,
        int worstStreak,
        string result,
        double pnlUsd)
    {
        double nextBalance = Math.Round(Math.Max(0.0, balanceUsd + pnlUsd), 4);
        double nextPeak = Math.Max(peakBalanceUsd, nextBalance);
        double nextDd = Math.Max(maxDrawdownUsd, Math.Round(nextPeak - nextBalance, 4));
        int nextTotal = totalTrades + 1;

        int nextWins = wins;
        int nextLosses = losses;
        int nextTies = ties;
        int nextGaps = gaps;
        int nextStreak = currentStreak;
        int nextBest = bestStreak;
        int nextWorst = worstStreak;

        switch (result)
        {
            case "WIN":
                nextWins++;
                nextStreak = currentStreak > 0 ? currentStreak + 1 : 1;
                nextBest = Math.Max(bestStreak, nextStreak);
                break;
            case "LOSS":
                nextLosses++;
                nextStreak = currentStreak < 0 ? currentStreak - 1 : -1;
                nextWorst = Math.Min(worstStreak, nextStreak);
                break;
            case "GAP":
                nextGaps++;
                break;
            default:
                nextTies++;
                break;
        }

        return new AccountTransition(
            nextBalance, nextPeak, nextDd,
            nextTotal, nextWins, nextLosses, nextTies, nextGaps,
            nextStreak, nextBest, nextWorst);
    }

    internal static string ComputeUtcSession(DateTime utc)
    {
        int h = utc.Hour;
        if (h >= 12 && h < 16) return "OVERLAP";
        if (h >= 7 && h < 16) return "LONDON";
        if (h >= 12 && h < 21) return "NY";
        if (h >= 0 && h < 9) return "TOKYO";
        return "QUIET";
    }

    /// <summary>
    /// Validates post-delay execution quote against SlippageGate (&lt;= 1.5 bps adverse move).
    /// Favorable movement passes and fills at the live post-delay tick; larger adverse spikes reject as requotes.
    /// </summary>
    internal static (bool Accepted, double FillPrice, double AdverseBps) EvaluatePostDelayFill(
        string direction,
        double signalPrice,
        double postDelayPrice,
        double maxAdverseBps = MaxAdverseSlippageBps)
    {
        if (direction is not ("BUY" or "PUT")) return (false, 0.0, 0.0);
        if (signalPrice <= 1e-8 || !double.IsFinite(signalPrice) ||
            postDelayPrice <= 1e-8 || !double.IsFinite(postDelayPrice))
        {
            return (false, 0.0, 0.0);
        }

        double adverseBps = direction == "BUY"
            ? (postDelayPrice - signalPrice) / signalPrice * 10000.0
            : (signalPrice - postDelayPrice) / signalPrice * 10000.0;

        return adverseBps <= maxAdverseBps
            ? (true, postDelayPrice, adverseBps)
            : (false, postDelayPrice, adverseBps);
    }

    public static async Task TryRecordPaperTradeAsync(
        string accountMode,
        string cleanAsset,
        string timeframe,
        int intervalSecs,
        int targetHorizon,
        DateTime barOpenUtc,
        double entryPrice,
        string direction,
        int probability,
        double payoutPct,
        string modelVersion,
        int simulatedDelayMs = DefaultSimulatedDelayMs)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        if (direction is not ("BUY" or "PUT")) return;
        if (entryPrice <= 1e-8 || !double.IsFinite(entryPrice) || intervalSecs <= 0) return;

        string normTf = TimeframeNormalizer.Normalize(timeframe).ToLowerInvariant();
        int horizonBars = Math.Max(1, targetHorizon);
        int tradeDurationSecs = Math.Max(horizonBars * intervalSecs, intervalSecs);
        double cooldownSecs = intervalSecs <= 5 ? 20.0 : Math.Max(tradeDurationSecs + 2.0, 15.0);

        string cooldownKey = $"{accountMode}:{cleanAsset}:{normTf}";
        long nowSw = System.Diagnostics.Stopwatch.GetTimestamp();
        long newExpirySw = nowSw + (long)(cooldownSecs * System.Diagnostics.Stopwatch.Frequency);

        while (true)
        {
            if (_cooldownSw.TryGetValue(cooldownKey, out long prevExpirySw))
            {
                if (nowSw < prevExpirySw) return;
                if (_cooldownSw.TryUpdate(cooldownKey, newExpirySw, prevExpirySw)) break;
            }
            else if (_cooldownSw.TryAdd(cooldownKey, newExpirySw))
            {
                break;
            }
        }

        if (simulatedDelayMs > 0)
        {
            await Task.Delay(simulatedDelayMs).ConfigureAwait(false);
        }

        DateTime utcFallback = barOpenUtc.Kind == DateTimeKind.Utc ? barOpenUtc : barOpenUtc.ToUniversalTime();
        double executedPrice = entryPrice;
        DateTime fillUtc;

        if (PriceFeed.UsePocketOption)
        {
            if (!PocketOptionWebSocketStream.TryGetLiveTick(cleanAsset, out double postPrice, out _, maxAgeSeconds: 2.5))
            {
                return;
            }

            var fillEval = EvaluatePostDelayFill(direction, entryPrice, postPrice, MaxAdverseSlippageBps);
            if (!fillEval.Accepted)
            {
                return;
            }

            executedPrice = fillEval.FillPrice;
            fillUtc = PocketOptionWebSocketStream.GetBrokerTimeUtc();
        }
        else
        {
            fillUtc = simulatedDelayMs > 0 ? DateTime.UtcNow : utcFallback;
        }

        DateTime verifyAtUtc = fillUtc.AddSeconds(tradeDurationSecs);
        double safePayout = double.IsFinite(payoutPct) && payoutPct > 0 ? Math.Clamp(payoutPct, 50.0, 99.0) : 82.0;

        try
        {
            await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                double stake = await conn.QueryFirstOrDefaultAsync<double?>(
                    "SELECT stake_usd FROM paper_accounts WHERE account_mode = @Mode;",
                    new { Mode = accountMode }, commandTimeout: 4) ?? DefaultStakeUsd;
                if (stake <= 0 || !double.IsFinite(stake)) stake = DefaultStakeUsd;

                await conn.ExecuteAsync(@"
                    INSERT INTO paper_trades (
                        account_mode, asset, timeframe, open_time, verify_at,
                        direction, probability, payout_pct, stake_usd, entry_price,
                        model_version, session_name, created_at
                    )
                    VALUES (
                        @AccountMode, @Asset, @Timeframe, @OpenTime, @VerifyAt,
                        @Direction, @Probability, @PayoutPct, @StakeUsd, @EntryPrice,
                        @ModelVersion, @SessionName, @CreatedAt
                    )
                    ON CONFLICT (account_mode, asset, timeframe, open_time) DO NOTHING;",
                    new
                    {
                        AccountMode = accountMode,
                        Asset = cleanAsset,
                        Timeframe = normTf,
                        OpenTime = fillUtc.ToString("o"),
                        VerifyAt = verifyAtUtc.ToString("o"),
                        Direction = direction,
                        Probability = probability,
                        PayoutPct = safePayout,
                        StakeUsd = stake,
                        EntryPrice = executedPrice,
                        ModelVersion = modelVersion ?? "",
                        SessionName = ComputeUtcSession(fillUtc),
                        CreatedAt = DateTime.UtcNow.ToString("o")
                    }, commandTimeout: 6);
            });
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PaperTrading] Failed to record {accountMode} bet for {cleanAsset}/{normTf}: {ex.Message}");
        }
    }

    public static async Task VerifyPendingPaperTradesAsync(DateTime brokerNowUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
        if (!await _verifyLock.WaitAsync(0, ct)) return;

        try
        {
            DateTime effectiveNowUtc = brokerNowUtc.Kind == DateTimeKind.Utc ? brokerNowUtc : brokerNowUtc.ToUniversalTime();
            DateTime cutoff = effectiveNowUtc.AddMilliseconds(-1500);

            var pending = await DbConnectionFactory.WithRetryAsync(async conn =>
            {
                var rows = await conn.QueryAsync<PendingPaperRow>(new CommandDefinition(@"
                    SELECT id as ""Id"",
                           account_mode as ""AccountMode"",
                           asset as ""Asset"",
                           timeframe as ""Timeframe"",
                           open_time as ""OpenTime"",
                           verify_at as ""VerifyAt"",
                           direction as ""Direction"",
                           payout_pct as ""PayoutPct"",
                           stake_usd as ""StakeUsd"",
                           entry_price as ""EntryPrice""
                    FROM paper_trades
                    WHERE result IS NULL
                      AND verify_at::timestamptz <= @Cutoff
                    ORDER BY verify_at ASC
                    LIMIT 50;",
                    new { Cutoff = cutoff }, cancellationToken: ct, commandTimeout: 8));
                return rows.ToList();
            }, ct);

            if (pending.Count == 0) return;

            long s5Ticks = TimeSpan.FromSeconds(5).Ticks;
            foreach (var row in pending)
            {
                if (ct.IsCancellationRequested) break;
                if (!DateTime.TryParse(row.VerifyAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var verifyUtc))
                {
                    continue;
                }

                string candleAsset = PriceFeed.CandleKey(row.Asset);
                double? exitPrice = null;

                bool streamFlowingNow = PriceFeed.UsePocketOption &&
                    (PocketOptionWebSocketStream.TryGetLivePrice(candleAsset, out _, 3.0) ||
                     PocketOptionWebSocketStream.IsLiveStreamFlowing(3.0));

                if (RealtimeTickCollector.TryGetTickPriceAtOrBefore(candleAsset, verifyUtc, streamFlowingNow, out double tickExitPrice))
                {
                    exitPrice = tickExitPrice;
                }

                if (exitPrice == null || exitPrice.Value <= 1e-8)
                {
                    long nearestGridTicks = ((verifyUtc.Ticks + (s5Ticks / 2)) / s5Ticks) * s5Ticks;
                    DateTime targetGrid = new DateTime(nearestGridTicks, DateTimeKind.Utc);
                    try
                    {
                        exitPrice = await DbConnectionFactory.WithRetryAsync(
                            conn => PendingTradeVerificationService.FindStrictS5ClosePriceAsync(
                                conn, candleAsset, targetGrid, includeWeekendArchiveFallback: true, ct),
                            ct);
                    }
                    catch { }

                    if (exitPrice == null || exitPrice.Value <= 1e-8)
                    {
                        var recent = await RealtimeTickCollector.GetRecentCandles(candleAsset, "s5", 10);
                        var match = recent?.FirstOrDefault(c => Math.Abs((c.Timestamp.AddSeconds(5) - targetGrid).TotalSeconds) <= 2.5);
                        if (match != null && match.Close > 1e-8)
                        {
                            exitPrice = match.Close;
                        }
                    }
                }

                string result;
                double pnlUsd;
                double? settledExitPrice = exitPrice;

                if (exitPrice == null || exitPrice.Value <= 1e-8)
                {
                    double overdueSec = (effectiveNowUtc - verifyUtc).TotalSeconds;
                    if (overdueSec < 180) continue;

                    result = "GAP";
                    pnlUsd = 0.0;
                    settledExitPrice = null;
                }
                else
                {
                    (result, pnlUsd) = ResolvePaperOutcome(row.Direction, row.StakeUsd, row.PayoutPct, row.EntryPrice, exitPrice.Value);
                }

                await SettleSinglePaperTradeAsync(row, settledExitPrice, result, pnlUsd, ct);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PaperTrading] Verification notice: {ex.Message}");
        }
        finally
        {
            _verifyLock.Release();
        }
    }

    private static async Task SettleSinglePaperTradeAsync(
        PendingPaperRow row,
        double? exitPrice,
        string result,
        double pnlUsd,
        CancellationToken ct)
    {
        await DbConnectionFactory.WithRetryAsync(async (conn, tx) =>
        {
            var acc = await conn.QueryFirstOrDefaultAsync<PaperAccountRow>(new CommandDefinition(@"
                SELECT account_mode as ""AccountMode"",
                       initial_balance as ""InitialBalance"",
                       balance_usd as ""BalanceUsd"",
                       peak_balance_usd as ""PeakBalanceUsd"",
                       max_drawdown_usd as ""MaxDrawdownUsd"",
                       stake_usd as ""StakeUsd"",
                       total_trades as ""TotalTrades"",
                       wins as ""Wins"",
                       losses as ""Losses"",
                       ties as ""Ties"",
                       gaps as ""Gaps"",
                       current_streak as ""CurrentStreak"",
                       best_streak as ""BestStreak"",
                       worst_streak as ""WorstStreak"",
                       updated_at as ""UpdatedAt""
                FROM paper_accounts
                WHERE account_mode = @Mode
                FOR UPDATE;", new { Mode = row.AccountMode }, tx, cancellationToken: ct, commandTimeout: 6));

            if (acc == null) return;

            var next = ComputeUpdatedAccountMetrics(
                acc.BalanceUsd, acc.PeakBalanceUsd, acc.MaxDrawdownUsd,
                acc.TotalTrades, acc.Wins, acc.Losses, acc.Ties, acc.Gaps,
                acc.CurrentStreak, acc.BestStreak, acc.WorstStreak,
                result, pnlUsd);

            string nowIso = DateTime.UtcNow.ToString("o");
            int updatedTrades = await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE paper_trades
                SET exit_price = @ExitPrice,
                    result = @Result,
                    pnl_usd = @PnlUsd,
                    balance_after = @BalanceAfter,
                    verified_at = @VerifiedAt
                WHERE id = @Id AND result IS NULL;",
                new
                {
                    row.Id,
                    ExitPrice = exitPrice,
                    Result = result,
                    PnlUsd = pnlUsd,
                    BalanceAfter = next.BalanceUsd,
                    VerifiedAt = nowIso
                }, tx, cancellationToken: ct, commandTimeout: 6));

            if (updatedTrades == 1)
            {
                await conn.ExecuteAsync(new CommandDefinition(@"
                    UPDATE paper_accounts
                    SET balance_usd = @BalanceUsd,
                        peak_balance_usd = @PeakBalanceUsd,
                        max_drawdown_usd = @MaxDrawdownUsd,
                        total_trades = @TotalTrades,
                        wins = @Wins,
                        losses = @Losses,
                        ties = @Ties,
                        gaps = @Gaps,
                        current_streak = @CurrentStreak,
                        best_streak = @BestStreak,
                        worst_streak = @WorstStreak,
                        updated_at = @UpdatedAt
                    WHERE account_mode = @Mode;",
                    new
                    {
                        Mode = row.AccountMode,
                        next.BalanceUsd,
                        next.PeakBalanceUsd,
                        next.MaxDrawdownUsd,
                        next.TotalTrades,
                        next.Wins,
                        next.Losses,
                        next.Ties,
                        next.Gaps,
                        next.CurrentStreak,
                        next.BestStreak,
                        next.WorstStreak,
                        UpdatedAt = nowIso
                    }, tx, cancellationToken: ct, commandTimeout: 6));
            }
        }, ct);
    }

    public static async Task<string> BuildTelegramSummaryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            return "⚠️ База данных не подключена.";
        }

        return await DbConnectionFactory.WithRetryAsync(async conn =>
        {
            var accounts = (await conn.QueryAsync<PaperAccountRow>(new CommandDefinition(@"
                SELECT account_mode as ""AccountMode"",
                       initial_balance as ""InitialBalance"",
                       balance_usd as ""BalanceUsd"",
                       peak_balance_usd as ""PeakBalanceUsd"",
                       max_drawdown_usd as ""MaxDrawdownUsd"",
                       stake_usd as ""StakeUsd"",
                       total_trades as ""TotalTrades"",
                       wins as ""Wins"",
                       losses as ""Losses"",
                       ties as ""Ties"",
                       gaps as ""Gaps"",
                       current_streak as ""CurrentStreak"",
                       best_streak as ""BestStreak"",
                       worst_streak as ""WorstStreak"",
                       updated_at as ""UpdatedAt""
                FROM paper_accounts
                ORDER BY account_mode ASC;", cancellationToken: ct, commandTimeout: 8))).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("💼 <b>Paper-симулятор ставок (RTT ~250мс + тиковая экспирация)</b>\n");

            if (accounts.Count == 0)
            {
                sb.AppendLine("<i>Счета ещё не инициализированы.</i>");
                return sb.ToString();
            }

            foreach (var acc in accounts)
            {
                string label = acc.AccountMode == AccountWeekday
                    ? "📈 <b>WEEKDAY (будни 24/7 · боевые входы)</b>"
                    : "🧪 <b>WEEKEND_SIM (выходные · ≥55%, проскальз. ≤1.5 bps)</b>";
                sb.AppendLine(label);

                double netPnl = acc.BalanceUsd - acc.InitialBalance;
                double roiPct = acc.InitialBalance > 0 ? netPnl / acc.InitialBalance * 100.0 : 0.0;
                int decided = acc.Wins + acc.Losses;
                double wr = decided > 0 ? (double)acc.Wins / decided * 100.0 : 0.0;
                string pnlSign = netPnl >= 0 ? "+" : "-";

                sb.AppendLine($"• Баланс: <b>${acc.BalanceUsd:F2}</b> (старт ${acc.InitialBalance:F0}, ставка ${acc.StakeUsd:F2}) | PnL: <b>{pnlSign}${Math.Abs(netPnl):F2}</b> ({roiPct:+0.0;-0.0;0.0}%)");
                sb.AppendLine($"• Всего сд.: <b>{acc.TotalTrades}</b> (✅{acc.Wins} ❌{acc.Losses} ➖{acc.Ties}) | WR без ничьих: <b>{wr:F1}%</b>");
                sb.AppendLine($"• Макс. просадка: <b>${acc.MaxDrawdownUsd:F2}</b> | Серия: <b>{acc.CurrentStreak:+0;-0;0}</b>");

                var last12h = await conn.QueryFirstOrDefaultAsync<PeriodStatsRow>(new CommandDefinition(@"
                    SELECT COUNT(*) as ""Total"",
                           COUNT(*) FILTER (WHERE result = 'WIN') as ""Wins"",
                           COUNT(*) FILTER (WHERE result = 'LOSS') as ""Losses"",
                           COUNT(*) FILTER (WHERE result = 'TIE') as ""Ties"",
                           COALESCE(SUM(pnl_usd), 0.0) as ""PnlUsd"",
                           COALESCE(AVG(payout_pct), 0.0) as ""AvgPayout""
                    FROM paper_trades
                    WHERE account_mode = @Mode
                      AND result IS NOT NULL
                      AND verified_at::timestamptz >= NOW() - INTERVAL '12 hours';",
                    new { Mode = acc.AccountMode }, cancellationToken: ct, commandTimeout: 8))
                    ?? new PeriodStatsRow(0, 0, 0, 0, 0.0, 0.0);

                int pendingCnt = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM paper_trades WHERE account_mode = @Mode AND result IS NULL;",
                    new { Mode = acc.AccountMode }, cancellationToken: ct, commandTimeout: 6));

                int decided12 = last12h.Wins + last12h.Losses;
                double wr12 = decided12 > 0 ? (double)last12h.Wins / decided12 * 100.0 : 0.0;
                string p12Sign = last12h.PnlUsd >= 0 ? "+" : "-";
                sb.AppendLine($"• За 12ч: <b>{last12h.Total}</b> сд. (✅{last12h.Wins} ❌{last12h.Losses} ➖{last12h.Ties}) | WR: <b>{wr12:F1}%</b> | PnL 12ч: <b>{p12Sign}${Math.Abs(last12h.PnlUsd):F2}</b> (ср. выпл. {last12h.AvgPayout:F0}%) | В работе: <b>{pendingCnt}</b>");

                var byTf = (await conn.QueryAsync<TimeframeBreakdownRow>(new CommandDefinition(@"
                    SELECT timeframe as ""Timeframe"",
                           COUNT(*) FILTER (WHERE result = 'WIN') as ""Wins"",
                           COUNT(*) FILTER (WHERE result = 'LOSS') as ""Losses"",
                           COUNT(*) FILTER (WHERE result = 'TIE') as ""Ties"",
                           COALESCE(SUM(pnl_usd), 0.0) as ""PnlUsd""
                    FROM paper_trades
                    WHERE account_mode = @Mode
                      AND result IS NOT NULL
                      AND verified_at::timestamptz >= NOW() - INTERVAL '24 hours'
                    GROUP BY timeframe
                    ORDER BY timeframe ASC;",
                    new { Mode = acc.AccountMode }, cancellationToken: ct, commandTimeout: 8))).ToList();

                if (byTf.Count > 0)
                {
                    var parts = byTf.Select(t =>
                    {
                        string ts = t.PnlUsd >= 0 ? "+" : "-";
                        return $"<code>{t.Timeframe}</code> {t.Wins}✅/{t.Losses}❌ ({ts}${Math.Abs(t.PnlUsd):F2})";
                    });
                    sb.AppendLine($"• По ТФ (24ч): {string.Join(" | ", parts)}");
                }
                sb.AppendLine();
            }

            var recent = (await conn.QueryAsync<RecentTradeRow>(new CommandDefinition(@"
                SELECT account_mode as ""AccountMode"",
                       asset as ""Asset"",
                       timeframe as ""Timeframe"",
                       direction as ""Direction"",
                       payout_pct as ""PayoutPct"",
                       result as ""Result"",
                       COALESCE(pnl_usd, 0.0) as ""PnlUsd"",
                       COALESCE(balance_after, 100.0) as ""BalanceAfter"",
                       verified_at as ""VerifiedAt""
                FROM paper_trades
                WHERE result IS NOT NULL
                ORDER BY verified_at DESC
                LIMIT 5;", cancellationToken: ct, commandTimeout: 8))).ToList();

            if (recent.Count > 0)
            {
                sb.AppendLine("🧾 <b>Последние 5 закрытых ставок:</b>");
                foreach (var t in recent)
                {
                    string icon = t.Result switch
                    {
                        "WIN" => "✅",
                        "LOSS" => "❌",
                        "GAP" => "⚠️",
                        _ => "➖"
                    };
                    string ps = t.PnlUsd >= 0 ? "+" : "-";
                    string modeTag = t.AccountMode == AccountWeekendSim ? "WKND" : "MAIN";
                    sb.AppendLine($"• {icon} [{modeTag}] <code>{t.Asset}</code> {t.Timeframe} {t.Direction} ({t.PayoutPct:F0}%) | <b>{ps}${Math.Abs(t.PnlUsd):F2}</b> -> ${t.BalanceAfter:F2}");
                }
            }

            sb.AppendLine("\n<i>Сбросить виртуальные балансы к $100:</i> <code>/paper reset</code>");
            return sb.ToString();
        }, ct);
    }

    public static async Task ResetAccountsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;

        await DbConnectionFactory.WithRetryAsync(async (conn, tx) =>
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM paper_trades;", tx, cancellationToken: ct, commandTimeout: 15));
            await conn.ExecuteAsync(new CommandDefinition(@"
                UPDATE paper_accounts
                SET balance_usd = initial_balance,
                    peak_balance_usd = initial_balance,
                    max_drawdown_usd = 0.0,
                    total_trades = 0,
                    wins = 0,
                    losses = 0,
                    ties = 0,
                    gaps = 0,
                    current_streak = 0,
                    best_streak = 0,
                    worst_streak = 0,
                    updated_at = @NowIso;",
                new { NowIso = nowIso }, tx, cancellationToken: ct, commandTimeout: 8));
        }, ct);
    }
}
