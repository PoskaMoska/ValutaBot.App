using System;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Data
{
    public static class DbConnectionFactory
    {
        private static NpgsqlDataSource? _dataSource;
        private static readonly object _initLock = new();

        public static string GetConnectionString()
        {
            var envVar = Environment.GetEnvironmentVariable("DATABASE_URL") ?? "";
            if (string.IsNullOrEmpty(envVar)) return "";

            if (envVar.StartsWith("postgres://") || envVar.StartsWith("postgresql://"))
            {
                var uri = new Uri(envVar);
                var userInfo = uri.UserInfo.Split(':');
                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = uri.Host,
                    Port = uri.Port > 0 ? uri.Port : 5432,
                    Username = userInfo.Length > 0 ? userInfo[0] : "",
                    Password = userInfo.Length > 1 ? userInfo[1] : "",
                    Database = uri.LocalPath.TrimStart('/')
                };
                return builder.ToString();
            }

            return envVar;
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
                            _dataSource = NpgsqlDataSource.Create(connStr);
                    }
                }
            }
            return _dataSource?.CreateConnection() ?? new NpgsqlConnection(GetConnectionString());
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
                    deposit_amount DOUBLE PRECISION NOT NULL
                );

                CREATE TABLE IF NOT EXISTS trade_outcomes (
                    id TEXT PRIMARY KEY,
                    asset TEXT NOT NULL,
                    timeframe TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    entry_price DOUBLE PRECISION NOT NULL,
                    exit_price DOUBLE PRECISION NOT NULL,
                    pnl_bps DOUBLE PRECISION NOT NULL,
                    was_win BOOLEAN NOT NULL,
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

                CREATE TABLE IF NOT EXISTS signal_votes (
                    id SERIAL PRIMARY KEY,
                    signal_name TEXT NOT NULL,
                    was_correct BOOLEAN NOT NULL,
                    created_at TEXT NOT NULL
                );

                
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
                    enable_ml BOOLEAN DEFAULT false,
                    enable_smc BOOLEAN DEFAULT true,
                    enable_of BOOLEAN DEFAULT true
                );

                ALTER TABLE allowed_users ADD COLUMN IF NOT EXISTS created_at TEXT NOT NULL DEFAULT '';
                ALTER TABLE all_users ADD COLUMN IF NOT EXISTS created_at TEXT NOT NULL DEFAULT '';
            ");

            BotLogger.Info("[PostgreSQL DB] Database tables initialized successfully.");

            // Initialize Trade Outcome Online Learning Engine
            await TradeOutcomeTracker.InitializeAsync();
            RealtimeTickCollector.Initialize();
        }
    }
}
