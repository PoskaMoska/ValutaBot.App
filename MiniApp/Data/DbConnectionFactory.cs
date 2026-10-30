using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Data
{
    public static class DbConnectionFactory
    {
        private static volatile NpgsqlDataSource? _dataSource;
        private static volatile string? _cachedRawEnv;
        private static volatile string? _cachedConnectionString;
        private static readonly object _initLock = new();

        public static string GetConnectionString()
        {
            var envVar = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "";
            if (string.IsNullOrEmpty(envVar)) return "";
            if (_cachedConnectionString != null && _cachedRawEnv == envVar)
            {
                return _cachedConnectionString;
            }

            NpgsqlConnectionStringBuilder builder;
            if (envVar.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || 
                envVar.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(envVar);
                string username = "";
                string password = "";

                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var parts = uri.UserInfo.Split(':', 2);
                    username = Uri.UnescapeDataString(parts[0]);
                    password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
                }

                builder = new NpgsqlConnectionStringBuilder
                {
                    Host = uri.Host,
                    Port = uri.Port > 0 ? uri.Port : 5432,
                    Username = username,
                    Password = password,
                    Database = uri.LocalPath.TrimStart('/')
                };
            }
            else
            {
                builder = new NpgsqlConnectionStringBuilder(envVar);
            }

            // TCP Keepalive & Socket options optimized for Railway cloud proxy
            builder.TcpKeepAlive = true;
            builder.TcpKeepAliveTime = 10;
            builder.TcpKeepAliveInterval = 5;
            builder.KeepAlive = 0;                    // Application keepalive probe every 10s (well before proxy's 60s idle timeout)
            builder.ConnectionLifetime = 300;          // Rotate pooled connections every 5m
            builder.ConnectionIdleLifetime = 15;       // Evict idle connections after 15s so proxy cannot sever them silently
            builder.ConnectionPruningInterval = 3;     // Prune every 3s so idle sockets never linger
            builder.Timeout = 15;                      // 15s connection timeout
            builder.CommandTimeout = 30;

            // Pool capacity 50 to prevent pool exhaustion under high concurrency
            builder.MaxPoolSize = 50;
            builder.MinPoolSize = 0;

            string built = builder.ToString();
            _cachedRawEnv = envVar;
            _cachedConnectionString = built;
            return built;
        }

        public static NpgsqlConnection GetConnection() 
        {
            if (_dataSource == null)
            {
                lock (_initLock)
                {
                    if (_dataSource == null)
                    {
                        string connStr = GetConnectionString();
                        if (!string.IsNullOrEmpty(connStr))
                        {
                            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
                            _dataSource = dataSourceBuilder.Build();
                        }
                    }
                }
            }
            return _dataSource?.CreateConnection() ?? new NpgsqlConnection(GetConnectionString());
        }

        public static async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
        {
            if (_dataSource == null)
            {
                lock (_initLock)
                {
                    if (_dataSource == null)
                    {
                        string connStr = GetConnectionString();
                        if (!string.IsNullOrEmpty(connStr))
                        {
                            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
                            _dataSource = dataSourceBuilder.Build();
                        }
                    }
                }
            }
            if (_dataSource != null)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        return await _dataSource.OpenConnectionAsync(ct);
                    }
                    catch (Exception ex) when (attempt == 1 && (ex is NpgsqlException || ex is System.IO.IOException))
                    {
                        BotLogger.Warn($"[DbConnectionFactory] Evicted stale pooled connection ({ex.Message}). Re-acquiring fresh socket...");
                        await Task.Delay(100, ct);
                    }
                }
                return await _dataSource.OpenConnectionAsync(ct);
            }
            var conn = new NpgsqlConnection(GetConnectionString());
            await conn.OpenAsync(ct);
            return conn;
        }

                /// <summary>
        /// Executes an idempotent database operation with automatic transient retry.
        /// Drops poisoned/stale socket connections from the pool on network severance.
        /// </summary>
        public static async Task<T> WithRetryAsync<T>(Func<NpgsqlConnection, Task<T>> action, CancellationToken ct = default)
        {
            for (int attempt = 1; ; attempt++)
            {
                await using var conn = await OpenConnectionAsync(ct);
                try
                {
                    return await action(conn);
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested &&
                    (ex is System.IO.IOException ||
                     (ex is NpgsqlException ne && (ne.IsTransient || ne.InnerException is System.IO.IOException))))
                {
                    BotLogger.Warn($"[DbConnectionFactory] Transient DB error on attempt {attempt} ({ex.Message}). Clearing connection pool and retrying...");
                    NpgsqlConnection.ClearPool(conn);
                    await Task.Delay(100 * attempt, ct);
                }
            }
        }

        public static Task WithRetryAsync(Func<NpgsqlConnection, Task> action, CancellationToken ct = default)
        {
            return WithRetryAsync(async conn =>
            {
                await action(conn);
                return true;
            }, ct);
        }

        public static async Task InitializeAsync()
        {
            if (string.IsNullOrEmpty(GetConnectionString())) return;

            using var conn = GetConnection();
            await conn.OpenAsync();
            
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS allowed_users (
                    chat_id BIGINT PRIMARY KEY,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS admins (
                    chat_id BIGINT PRIMARY KEY
                );

                CREATE TABLE IF NOT EXISTS all_users (
                    chat_id BIGINT PRIMARY KEY,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS registrations (
                    pocket_id TEXT PRIMARY KEY,
                    chat_id BIGINT NOT NULL,
                    has_registered BOOLEAN NOT NULL,
                    has_deposited BOOLEAN NOT NULL,
                    deposit_amount DOUBLE PRECISION NOT NULL,
                    trial_started_at TIMESTAMPTZ NULL,
                    trial_ends_at TIMESTAMPTZ NULL,
                    reminder_day2_sent BOOLEAN NOT NULL DEFAULT FALSE,
                    reminder_day3_sent BOOLEAN NOT NULL DEFAULT FALSE,
                    trial_expired_sent BOOLEAN NOT NULL DEFAULT FALSE
                );

                CREATE TABLE IF NOT EXISTS trade_outcomes (
                    id TEXT PRIMARY KEY,
                    asset TEXT NOT NULL,
                    timeframe TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    entry_price DOUBLE PRECISION NOT NULL,
                    exit_price DOUBLE PRECISION NOT NULL,
                    pnl_bps DOUBLE PRECISION NOT NULL,
                    was_win BOOLEAN,
                    created_at TEXT NOT NULL,
                    verified_at TEXT NOT NULL
                );
                
                CREATE TABLE IF NOT EXISTS pending_trades (
                    id TEXT PRIMARY KEY,
                    direction TEXT NOT NULL,
                    asset TEXT NOT NULL,
                    timeframe TEXT NOT NULL,
                    binance_symbol TEXT NOT NULL,
                    entry_price DOUBLE PRECISION NOT NULL,
                    created_at TEXT NOT NULL,
                    verify_at TEXT NOT NULL,
                    is_forex BOOLEAN NOT NULL,
                    source_directions TEXT NOT NULL
                );

                DO $$
                BEGIN
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ta_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN smc_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_prob DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    -- Rich LightGBM features (migration-safe)
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN smc_bos_dir TEXT NOT NULL DEFAULT 'NONE'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN smc_has_ob BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN smc_has_fvg BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_delta_ratio DOUBLE PRECISION NOT NULL DEFAULT 1.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_state TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    -- Rich market context columns for analytics and ML feature engineering
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN market_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN velocity_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN atr_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN adx_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN rsi_at_signal DOUBLE PRECISION NOT NULL DEFAULT 50.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN higher_tf_aligned BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN minutes_to_news INTEGER NOT NULL DEFAULT -1; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN session TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN day_of_week INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN hour_utc INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    -- Phase 1: Complete Decision Snapshot
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ta_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN smc_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN of_direction TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN conflict_count INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN confidence_bucket TEXT NOT NULL DEFAULT '50-60'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN was_close_call BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN consecutive_losses_before INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN seconds_since_last_trade INTEGER NOT NULL DEFAULT -1; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN reasoning_text TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_model_version TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN ml_model_accuracy DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN max_favorable_bps DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN max_adverse_bps DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    -- pending_trades: reasoning + ml metadata for pipeline
                    BEGIN ALTER TABLE pending_trades ADD COLUMN reasoning_text TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ml_model_version TEXT NOT NULL DEFAULT ''; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ml_model_accuracy DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    
                    -- Phase 3: Entropy and Market Phase context
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN price_entropy DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN trend_maturity INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN price_position_pct DOUBLE PRECISION NOT NULL DEFAULT 0.5; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN bb_squeeze BOOLEAN NOT NULL DEFAULT false; EXCEPTION WHEN duplicate_column THEN END;
                    
                    BEGIN ALTER TABLE pending_trades ADD COLUMN price_entropy DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN trend_maturity INTEGER NOT NULL DEFAULT 0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN price_position_pct DOUBLE PRECISION NOT NULL DEFAULT 0.5; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN bb_squeeze BOOLEAN NOT NULL DEFAULT false; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN dynamic_horizon INT NOT NULL DEFAULT 3; EXCEPTION WHEN duplicate_column THEN END;
                    
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ta_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN of_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN smc_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ml_prob DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN ml_score DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN smc_bos_dir TEXT NOT NULL DEFAULT 'NONE'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN smc_has_ob BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN smc_has_fvg BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN of_delta_ratio DOUBLE PRECISION NOT NULL DEFAULT 1.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN of_state TEXT NOT NULL DEFAULT 'NEUTRAL'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN dynamic_horizon INTEGER NOT NULL DEFAULT 3; EXCEPTION WHEN duplicate_column THEN END;
                    -- Rich market context (pending_trades → trade_outcomes pipeline)
                    BEGIN ALTER TABLE pending_trades ADD COLUMN market_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN velocity_regime TEXT NOT NULL DEFAULT 'UNKNOWN'; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN atr_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN adx_at_signal DOUBLE PRECISION NOT NULL DEFAULT 0.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN rsi_at_signal DOUBLE PRECISION NOT NULL DEFAULT 50.0; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN higher_tf_aligned BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE pending_trades ADD COLUMN minutes_to_news INTEGER NOT NULL DEFAULT -1; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE trade_outcomes ADD COLUMN features_json TEXT; EXCEPTION WHEN duplicate_column THEN END;
                END $$;

                CREATE TABLE IF NOT EXISTS subminute_candles (
                    id SERIAL PRIMARY KEY,
                    asset TEXT NOT NULL,
                    interval TEXT NOT NULL,
                    open_time TEXT NOT NULL,
                    open_price DOUBLE PRECISION NOT NULL,
                    high_price DOUBLE PRECISION NOT NULL,
                    low_price DOUBLE PRECISION NOT NULL,
                    close_price DOUBLE PRECISION NOT NULL,
                    volume DOUBLE PRECISION NOT NULL DEFAULT 0,
                    UNIQUE(asset, interval, open_time)
                );

                CREATE TABLE IF NOT EXISTS user_settings (
                    chat_id BIGINT PRIMARY KEY,
                    enable_ml BOOLEAN DEFAULT true,
                    enable_smc BOOLEAN DEFAULT true,
                    enable_of BOOLEAN DEFAULT true,
                    trade_amount DECIMAL(18,2) DEFAULT 1.0,
                    custom_config JSONB DEFAULT '{}'
                );

                CREATE TABLE IF NOT EXISTS historical_candles (
                    id        SERIAL PRIMARY KEY,
                    asset     TEXT   NOT NULL,
                    interval  TEXT   NOT NULL,
                    open_time TEXT   NOT NULL,
                    open      DOUBLE PRECISION NOT NULL,
                    high      DOUBLE PRECISION NOT NULL,
                    low       DOUBLE PRECISION NOT NULL,
                    close     DOUBLE PRECISION NOT NULL,
                    volume    DOUBLE PRECISION NOT NULL DEFAULT 0,
                    UNIQUE(asset, interval, open_time)
                );

                CREATE INDEX IF NOT EXISTS idx_trade_outcomes_created_dir ON trade_outcomes(created_at DESC, direction);
                CREATE INDEX IF NOT EXISTS idx_trade_outcomes_verified_dir ON trade_outcomes(verified_at DESC, direction);
                CREATE INDEX IF NOT EXISTS idx_trade_outcomes_asset_tf_verified ON trade_outcomes(asset, timeframe, verified_at DESC);
                CREATE INDEX IF NOT EXISTS idx_trade_outcomes_real_trades ON trade_outcomes(created_at DESC) WHERE direction IN ('BUY', 'PUT') AND was_win IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_trade_outcomes_uncompacted_json ON trade_outcomes(created_at) WHERE features_json IS NOT NULL AND octet_length(features_json) > 2500;
                CREATE INDEX IF NOT EXISTS idx_trade_outcomes_ml_export ON trade_outcomes(created_at ASC) WHERE features_json IS NOT NULL AND was_win IS NOT NULL AND entry_price > 0 AND exit_price > 0;
                CREATE INDEX IF NOT EXISTS idx_pending_trades_asset_tf ON pending_trades(asset, timeframe);
                CREATE INDEX IF NOT EXISTS idx_pending_trades_verify_at ON pending_trades(verify_at ASC);
                CREATE INDEX IF NOT EXISTS idx_subminute_candles_opentime ON subminute_candles(open_time DESC);
                DROP INDEX IF EXISTS idx_trade_outcomes_asset_tf;
                DROP INDEX IF EXISTS idx_trade_outcomes_created_at;
                DROP INDEX IF EXISTS idx_trade_outcomes_verified_at;
                DROP INDEX IF EXISTS idx_subminute_candles_lookup;
                DROP INDEX IF EXISTS idx_hist_candles_asset;
                DROP TABLE IF EXISTS neutral_signals;
                DROP INDEX IF EXISTS idx_subminute_candles_opentime_tz;
                DROP INDEX IF EXISTS idx_pending_trades_verify_tz;
                DROP INDEX IF EXISTS idx_trade_outcomes_created_tz;
                DROP INDEX IF EXISTS idx_trade_outcomes_verified_tz;
                DROP INDEX IF EXISTS idx_trade_outcomes_asset_tf_verified_tz;

                -- Table-level high-load tuning: fillfactor=80 enables HOT (Heap-Only Tuple) in-place updates for real-time ticks
                DO $$
                BEGIN
                    BEGIN
                        ALTER TABLE subminute_candles SET (
                            fillfactor = 80,
                            autovacuum_vacuum_scale_factor = 0.02,
                            autovacuum_analyze_scale_factor = 0.01,
                            autovacuum_vacuum_cost_limit = 2000,
                            autovacuum_vacuum_cost_delay = 2
                        );
                    EXCEPTION WHEN others THEN END;
                    BEGIN
                        ALTER TABLE trade_outcomes SET (
                            autovacuum_vacuum_scale_factor = 0.03,
                            autovacuum_analyze_scale_factor = 0.02,
                            autovacuum_vacuum_cost_limit = 2000,
                            autovacuum_vacuum_cost_delay = 2,
                            toast.autovacuum_vacuum_scale_factor = 0.03,
                            toast.autovacuum_vacuum_cost_limit = 2000
                        );
                    EXCEPTION WHEN others THEN END;
                    BEGIN
                        ALTER TABLE pending_trades SET (
                            autovacuum_vacuum_scale_factor = 0.01,
                            autovacuum_vacuum_cost_limit = 1000
                        );
                    EXCEPTION WHEN others THEN END;
                END $$;

                -- Preserve module_diagnostics across C# restarts (populated nightly by Python _daily_diagnostics_loop)
                CREATE TABLE IF NOT EXISTS module_diagnostics (
                    id SERIAL PRIMARY KEY,
                    created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
                    module_name VARCHAR(50) NOT NULL,
                    timeframe VARCHAR(20) NOT NULL,
                    failed_trades_count INTEGER NOT NULL,
                    insight_text TEXT NOT NULL,
                    suggested_filter JSONB
                );

                CREATE TABLE IF NOT EXISTS circuit_breaker_state (
                    id          INTEGER PRIMARY KEY DEFAULT 1,
                    halted_until TIMESTAMPTZ NULL,
                    reason      TEXT NULL,
                    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
                );

                DO $$ 
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns 
                        WHERE table_name = 'circuit_breaker_state' 
                          AND column_name = 'halted_until' 
                          AND data_type = 'text'
                    ) THEN
                        ALTER TABLE circuit_breaker_state 
                            ALTER COLUMN halted_until TYPE TIMESTAMPTZ USING NULLIF(halted_until, '')::timestamptz;
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns 
                        WHERE table_name = 'circuit_breaker_state' 
                          AND column_name = 'created_at' 
                          AND data_type = 'text'
                    ) THEN
                        ALTER TABLE circuit_breaker_state 
                            ALTER COLUMN created_at TYPE TIMESTAMPTZ USING NULLIF(created_at, '')::timestamptz;
                    END IF;
                END $$;

                ALTER TABLE allowed_users ADD COLUMN IF NOT EXISTS created_at TEXT NOT NULL DEFAULT '';
                ALTER TABLE all_users ADD COLUMN IF NOT EXISTS created_at TEXT NOT NULL DEFAULT '';

                -- 3-day Trial columns for registrations
                DO $$
                BEGIN
                    BEGIN ALTER TABLE registrations ADD COLUMN trial_started_at TIMESTAMPTZ NULL; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE registrations ADD COLUMN trial_ends_at TIMESTAMPTZ NULL; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE registrations ADD COLUMN reminder_day2_sent BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE registrations ADD COLUMN reminder_day3_sent BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                    BEGIN ALTER TABLE registrations ADD COLUMN trial_expired_sent BOOLEAN NOT NULL DEFAULT FALSE; EXCEPTION WHEN duplicate_column THEN END;
                END $$;
            ");

            BotLogger.Info("[PostgreSQL DB] Database tables initialized successfully.");

            // Initialize Trade Outcome Online Learning Engine
            await TradeOutcomeTracker.InitializeAsync();
            RealtimeTickCollector.Initialize();
        }
    }
}
