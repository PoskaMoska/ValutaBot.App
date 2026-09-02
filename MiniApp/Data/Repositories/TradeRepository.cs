using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Data.Repositories
{
    public class TradeOutcomeRecord
    {
        public string Id { get; set; } = "";
        public string Asset { get; set; } = "";
        public string Timeframe { get; set; } = "";
        public string Direction { get; set; } = "";
        public double EntryPrice { get; set; }
        public double ExitPrice { get; set; }
        public double PnlBps { get; set; }
        public bool WasWin { get; set; }
        public string CreatedAt { get; set; } = "";
        public string VerifiedAt { get; set; } = "";
    }

    public static class TradeRepository
    {
        public static async Task SaveTradeOutcomeAsync(TradeOutcomeRecord outcome)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.ExecuteAsync(@"
                    INSERT INTO trade_outcomes 
                    (id, asset, timeframe, direction, entry_price, exit_price, pnl_bps, was_win, created_at, verified_at)
                    VALUES (@Id, @Asset, @Timeframe, @Direction, @EntryPrice, @ExitPrice, @PnlBps, @WasWin, @CreatedAt, @VerifiedAt)
                    ON CONFLICT (id) DO UPDATE SET
                        asset = EXCLUDED.asset,
                        timeframe = EXCLUDED.timeframe,
                        direction = EXCLUDED.direction,
                        entry_price = EXCLUDED.entry_price,
                        exit_price = EXCLUDED.exit_price,
                        pnl_bps = EXCLUDED.pnl_bps,
                        was_win = EXCLUDED.was_win,
                        created_at = EXCLUDED.created_at,
                        verified_at = EXCLUDED.verified_at
                ", new
                {
                    outcome.Id,
                    outcome.Asset,
                    outcome.Timeframe,
                    outcome.Direction,
                    outcome.EntryPrice,
                    outcome.ExitPrice,
                    outcome.PnlBps,
                    outcome.WasWin,
                    outcome.CreatedAt,
                    outcome.VerifiedAt
                });
            }
            catch (Exception ex)
            {
                BotLogger.Error("[PostgreSQL DB] Failed to save trade outcome record", ex);
            }
        }

        public static async Task<List<TradeOutcomeRecord>> LoadTradeOutcomesAsync(int limit = 1000)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<TradeOutcomeRecord>();
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                var rows = await conn.QueryAsync(@"
                    SELECT id as "Id", asset as "Asset", timeframe as "Timeframe", direction as "Direction",
                           entry_price as "EntryPrice", exit_price as ExitPrice, pnl_bps as PnlBps,
                           was_win as WasWin, created_at as CreatedAt, verified_at as VerifiedAt
                    FROM trade_outcomes
                    ORDER BY verified_at DESC
                    LIMIT @limit
                ", new { limit });

                return rows.Select(r => new TradeOutcomeRecord
                {
                    Id = r.Id,
                    Asset = r.Asset,
                    Timeframe = r.Timeframe,
                    Direction = r.Direction,
                    EntryPrice = r.EntryPrice != null ? Convert.ToDouble(r.EntryPrice) : 0.0,
                    ExitPrice = r.ExitPrice != null ? Convert.ToDouble(r.ExitPrice) : 0.0,
                    PnlBps = r.PnlBps != null ? Convert.ToDouble(r.PnlBps) : 0.0,
                    WasWin = Convert.ToBoolean(r.WasWin),
                    CreatedAt = r.CreatedAt ?? "",
                    VerifiedAt = r.VerifiedAt ?? ""
                }).ToList();
            }
            catch (Exception ex)
            {
                BotLogger.Error("[PostgreSQL DB] Failed to load trade outcomes", ex);
                return new List<TradeOutcomeRecord>();
            }
        }

        public static async Task SavePendingTradeAsync(SignalTracker.PredictionRecord record)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync(@"
                INSERT INTO pending_trades (id, direction, asset, timeframe, binance_symbol, entry_price, created_at, verify_at, is_forex, source_directions)
                VALUES (@Id, @Direction, @Asset, @Timeframe, @BinanceSymbol, @EntryPrice, @CreatedAtStr, @VerifyAtStr, @IsForex, @SourceDirectionsStr)
                ON CONFLICT (id) DO NOTHING", 
                new {
                    record.Id,
                    record.Direction,
                    record.Asset,
                    record.Timeframe,
                    record.BinanceSymbol,
                    record.EntryPrice,
                    CreatedAtStr = record.CreatedAt.ToString("o"),
                    VerifyAtStr = record.VerifyAt.ToString("o"),
                    record.IsForex,
                    SourceDirectionsStr = System.Text.Json.JsonSerializer.Serialize(record.SourceDirections, ValutaBotJsonContext.Default.DictionaryStringString)
                });
        }

        public static async Task<List<SignalTracker.PredictionRecord>> GetPendingTradesToVerifyAsync(DateTime upTo)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<SignalTracker.PredictionRecord>();
            using var conn = DbConnectionFactory.GetConnection();
            var rows = await conn.QueryAsync(@"
                SELECT id as "Id", direction as "Direction", asset as "Asset", timeframe as "Timeframe", 
                       binance_symbol as "BinanceSymbol", entry_price as "EntryPrice", 
                       created_at as "CreatedAtStr", verify_at as "VerifyAtStr", 
                       is_forex as "IsForex", source_directions as "SourceDirectionsStr"
                FROM pending_trades 
                WHERE verify_at <= @UpToStr", 
                new { UpToStr = upTo.ToString("o") });

            return rows.Select(r => new SignalTracker.PredictionRecord
            {
                Id = r.Id,
                Direction = r.Direction ?? "",
                Asset = r.Asset ?? "",
                Timeframe = r.Timeframe ?? "",
                BinanceSymbol = r.BinanceSymbol ?? "",
                EntryPrice = r.EntryPrice != null ? Convert.ToDouble(r.EntryPrice) : 0.0,
                CreatedAt = string.IsNullOrEmpty(r.CreatedAtStr) ? DateTime.MinValue : DateTime.Parse(r.CreatedAtStr).ToUniversalTime(),
                VerifyAt = string.IsNullOrEmpty(r.VerifyAtStr) ? DateTime.MinValue : DateTime.Parse(r.VerifyAtStr).ToUniversalTime(),
                IsForex = r.IsForex != null ? Convert.ToBoolean(r.IsForex) : false,
                SourceDirections = string.IsNullOrEmpty(r.SourceDirectionsStr) ? new Dictionary<string, string>() : 
                    System.Text.Json.JsonSerializer.Deserialize(r.SourceDirectionsStr, ValutaBotJsonContext.Default.DictionaryStringString) ?? new Dictionary<string, string>()
            }).Where(r => r.CreatedAt != DateTime.MinValue).ToList();
        }

        public static async Task DeletePendingTradeAsync(string id)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("DELETE FROM pending_trades WHERE id = @id", new { id });
        }

        public static async Task RecordSignalVoteAsync(string signalName, bool wasCorrect)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            using var conn = DbConnectionFactory.GetConnection();
            await conn.ExecuteAsync("INSERT INTO signal_votes (signal_name, was_correct, created_at) VALUES (@signalName, @wasCorrect, @now)", 
                new { signalName, wasCorrect, now = DateTime.UtcNow.ToString("o") });
        }

        public static async Task<(int Total, int Verified, int Correct)> GetOverallStatsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return (0, 0, 0);
            using var conn = DbConnectionFactory.GetConnection();
            int pending = (int)(await conn.ExecuteScalarAsync<long?>("SELECT COUNT(*) FROM pending_trades") ?? 0L);
            var res = await conn.QueryFirstOrDefaultAsync(
                "SELECT COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_win THEN 1 ELSE 0 END), 0) as Correct FROM trade_outcomes");
            
            int verified = res != null ? Convert.ToInt32(res.Verified) : 0;
            int correct = res != null ? Convert.ToInt32(res.Correct) : 0;
            return (pending + verified, verified, correct);
        }

        public static async Task<(int Total, int Verified, int Correct)> GetStatsAsync(string asset, string timeframe)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return (0, 0, 0);
            using var conn = DbConnectionFactory.GetConnection();
            int pending = (int)(await conn.ExecuteScalarAsync<long?>("SELECT COUNT(*) FROM pending_trades WHERE asset = @asset AND timeframe = @timeframe", new { asset, timeframe }) ?? 0L);
            var res = await conn.QueryFirstOrDefaultAsync(
                "SELECT COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_win THEN 1 ELSE 0 END), 0) as Correct FROM trade_outcomes WHERE asset = @asset AND timeframe = @timeframe",
                new { asset, timeframe });
            
            int verified = res != null ? Convert.ToInt32(res.Verified) : 0;
            int correct = res != null ? Convert.ToInt32(res.Correct) : 0;
            return (pending + verified, verified, correct);
        }

        public static async Task<List<(string asset, string timeframe, int total, int verified, int correct)>> GetAllStatsAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<(string, string, int, int, int)>();
            using var conn = DbConnectionFactory.GetConnection();
            
            var pendingRows = await conn.QueryAsync(
                "SELECT asset, timeframe, COUNT(*) as Pending FROM pending_trades GROUP BY asset, timeframe");
            
            var rows = await conn.QueryAsync(
                "SELECT asset, timeframe, COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_win THEN 1 ELSE 0 END), 0) as Correct FROM trade_outcomes GROUP BY asset, timeframe");
            
            var result = new List<(string, string, int, int, int)>();
            foreach (var r in rows)
            {
                int verified = Convert.ToInt32(r.verified);
                int pending = 0;
                foreach (var p in pendingRows)
                {
                    if (p.asset == r.asset && p.timeframe == r.timeframe)
                    {
                        pending = Convert.ToInt32(p.pending);
                        break;
                    }
                }
                result.Add((r.asset, r.timeframe, verified + pending, verified, Convert.ToInt32(r.correct)));
            }
            return result;
        }

        public static async Task<List<(string signalName, int verified, int correct)>> GetAllSignalVotesAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return new List<(string, int, int)>();
            using var conn = DbConnectionFactory.GetConnection();
            var rows = await conn.QueryAsync(
                "SELECT signal_name, COUNT(*) as Verified, COALESCE(SUM(CASE WHEN was_correct THEN 1 ELSE 0 END), 0) as Correct FROM signal_votes GROUP BY signal_name");
            
            var result = new List<(string, int, int)>();
            foreach (var r in rows)
            {
                result.Add((r.signal_name, Convert.ToInt32(r.verified), Convert.ToInt32(r.correct)));
            }
            return result;
        }

        // ── L2-FIX: Персистентность EMA-весов AutoCalibrationEngine ─────────────

        /// <summary>
        /// Сохраняет EMA-состояние калибровщика в PostgreSQL.
        /// Вызывается из TradeOutcomeTracker после каждой обработки сделки.
        /// </summary>
        public static async Task SaveCalibrationStateAsync(string sourceName, string asset, string timeframe, int totalTrades, double emaWinRate)
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.ExecuteAsync(@"
                    INSERT INTO calibration_state (source_name, asset, timeframe, total_trades, ema_win_rate, updated_at)
                    VALUES (@sourceName, @asset, @timeframe, @totalTrades, @emaWinRate, @updatedAt)
                    ON CONFLICT (source_name, asset, timeframe) DO UPDATE SET
                        total_trades = EXCLUDED.total_trades,
                        ema_win_rate = EXCLUDED.ema_win_rate,
                        updated_at   = EXCLUDED.updated_at",
                    new { sourceName, asset, timeframe, totalTrades, emaWinRate, updatedAt = DateTime.UtcNow.ToString("o") });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] SaveCalibrationState notice: {ex.Message}");
            }
        }

        /// <summary>
        /// Загружает сохранённые EMA-веса при старте бота.
        /// Возвращает список записей для восстановления AutoCalibrationEngine._statsMap.
        /// </summary>
        public static async Task<List<(string sourceName, string asset, string timeframe, int totalTrades, double emaWinRate)>> LoadCalibrationStateAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
                return new List<(string, string, string, int, double)>();
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                var rows = await conn.QueryAsync(
                    "SELECT source_name, asset, timeframe, total_trades, ema_win_rate FROM calibration_state");
                var result = new List<(string, string, string, int, double)>();
                foreach (var r in rows)
                {
                    result.Add((
                        (string)r.source_name,
                        (string)r.asset,
                        (string)r.timeframe,
                        Convert.ToInt32(r.total_trades),
                        Convert.ToDouble(r.ema_win_rate)
                    ));
                }
                return result;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] LoadCalibrationState notice: {ex.Message}");
                return new List<(string, string, string, int, double)>();
            }
        }

        /// <summary>
        /// Создаёт таблицу calibration_state если не существует.
        /// </summary>
        public static async Task EnsureCalibrationTableAsync()
        {
            if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString())) return;
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.ExecuteAsync(@"
                    CREATE TABLE IF NOT EXISTS calibration_state (
                        source_name  TEXT NOT NULL,
                        asset        TEXT NOT NULL,
                        timeframe    TEXT NOT NULL,
                        total_trades INT  NOT NULL DEFAULT 0,
                        ema_win_rate DOUBLE PRECISION NOT NULL DEFAULT 0.5,
                        updated_at   TEXT NOT NULL,
                        PRIMARY KEY (source_name, asset, timeframe)
                    )");
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TradeRepository] EnsureCalibrationTable notice: {ex.Message}");
            }
        }
    }
}
