using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Data.Repositories;
using Microsoft.Extensions.Options;

namespace ValutaBot.MiniApp.Services
{
    // DI-first service. Stores and checks Circuit Breaker state with PostgreSQL persistence and in-memory TTL cache.
    public interface ICircuitBreakerService
    {
        Task InitializeAsync(CancellationToken ct = default);
        bool IsHalted();
        string? GetHaltedReason();
        Task CheckStateAsync(CancellationToken ct = default);
        Task ActivateHaltAsync(string reason, CancellationToken ct = default);
    }

    public sealed class CircuitBreakerService : ICircuitBreakerService
    {
        private readonly TradingBotSettings _settings;
        private readonly Func<NpgsqlConnection> _getConnection;
        private readonly object _lock = new();

        // In-memory cached state (with TTL)
        private DateTime? _haltedUntil;
        private string _haltReason = string.Empty;
        private DateTime _cacheLoadedAt = DateTime.MinValue;

        public CircuitBreakerService(IOptions<TradingBotSettings> options, Func<NpgsqlConnection> connectionFactory)
        {
            _settings = options.Value ?? throw new ArgumentNullException(nameof(options));
            _getConnection = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        // Backward-compat shim to avoid breaking static call from legacy startup. No-op; use InitializeAsync via DI.
        [Obsolete("Use ICircuitBreakerService.InitializeAsync() via DI. This method is a no-op kept for compatibility.")]
        public static async Task LoadFromDbAsync()
        {
            try
            {
                // Ensure table exists (idempotent) to avoid errors during startup migration order
                await using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                await conn.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS circuit_breaker_state (
                    id INTEGER PRIMARY KEY,
                    halted_until TIMESTAMPTZ NULL,
                    reason TEXT NULL,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
                );");
                BotLogger.Warn("[CircuitBreaker] Legacy static LoadFromDbAsync called. No-op. Use DI service to initialize.");
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] Legacy LoadFromDbAsync failed (non-fatal): {ex.Message}");
            }
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            try
            {
                await EnsureTableAsync(ct);
                await RefreshCacheFromDbAsync(ct);
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] Initialize failed (non-fatal): {ex.Message}");
            }
        }

        public bool IsHalted()
        {
            lock (_lock)
            {
                // Refresh from DB if cache is stale
                var cacheStale = (DateTime.UtcNow - _cacheLoadedAt).TotalSeconds > _settings.CircuitBreakerDbCacheTtlSeconds;
                if (cacheStale)
                {
                    _ = Task.Run(async () => await RefreshCacheFromDbAsync());
                }

                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                    return true;

                // Halt expired -> clear state and delete DB row
                if (_haltedUntil.HasValue && DateTime.UtcNow >= _haltedUntil.Value)
                {
                    _haltedUntil = null;
                    _haltReason = string.Empty;
                    BotLogger.Info("[CircuitBreaker] Cooldown expired. Trading resumed.");
                    _ = Task.Run(async () => await DeleteHaltFromDbAsync());
                }
                return false;
            }
        }

        public string? GetHaltedReason()
        {
            lock (_lock)
            {
                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                {
                    var remain = (_haltedUntil.Value - DateTime.UtcNow).TotalMinutes;
                    return $"CIRCUIT_BREAKER_ACTIVE (Resumes in {remain:F0}m — {_haltReason})";
                }
                return null;
            }
        }

        public async Task CheckStateAsync(CancellationToken ct = default)
        {
            // If already halted, skip the DB outcome query entirely
            if (IsHalted()) return;

            try
            {
                var recentOutcomes = await TradeRepository.GetRecentOutcomesAsync(_settings.CircuitBreakerWindowSize);
                if (recentOutcomes == null || recentOutcomes.Count == 0) return;

                bool shouldHalt = false;
                string reason = string.Empty;

                // Check 1: Consecutive losses (outcomes are DESC, newest first)
                int consecutiveLosses = 0;
                foreach (var win in recentOutcomes)
                {
                    if (!win) consecutiveLosses++;
                    else break;
                }
                if (consecutiveLosses >= _settings.CircuitBreakerMaxConsecutiveLosses)
                {
                    shouldHalt = true;
                    reason = $"{consecutiveLosses} consecutive losses";
                }

                // Check 2: Win rate over the window (min 5 trades to evaluate)
                if (!shouldHalt && recentOutcomes.Count >= 5)
                {
                    int wins = recentOutcomes.Count(w => w);
                    double winRate = (double)wins / recentOutcomes.Count;
                    if (winRate < _settings.CircuitBreakerMinWinRate)
                    {
                        shouldHalt = true;
                        reason = $"Win rate dropped to {winRate:P0} (last {recentOutcomes.Count} trades)";
                    }
                }

                if (shouldHalt)
                {
                    await ActivateHaltAsync(reason, ct);
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] State check failed: {ex.Message}");
            }
        }

        public async Task ActivateHaltAsync(string reason, CancellationToken ct = default)
        {
            DateTime newHaltUntil;
            lock (_lock)
            {
                // Avoid duplicating halt if already active
                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                    return;
                newHaltUntil = DateTime.UtcNow.AddMinutes(_settings.CircuitBreakerCooldownMinutes);
            }

            BotLogger.Warn($"[CircuitBreaker] TRADING HALTED for {_settings.CircuitBreakerCooldownMinutes}m. Reason: {reason}");

            // Persist to DB first (write-through), then update memory state
            try
            {
                await EnsureTableAsync(ct);
                await WriteHaltToDbAsync(newHaltUntil, reason, ct);

                lock (_lock)
                {
                    _haltedUntil = newHaltUntil;
                    _haltReason = reason;
                    _cacheLoadedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: halt is active in memory, but DB write failed. On restart, InitializeAsync will re-evaluate.
                BotLogger.Warn($"[CircuitBreaker] Failed to persist halt to DB (halt IS active in memory): {ex.Message}");
                lock (_lock)
                {
                    _haltedUntil = newHaltUntil;
                    _haltReason = reason;
                    _cacheLoadedAt = DateTime.UtcNow;
                }
            }
        }

        private async Task RefreshCacheFromDbAsync(CancellationToken ct = default)
        {
            try
            {
                var (haltedUntil, reason) = await ReadHaltFromDbAsync(ct);
                lock (_lock)
                {
                    _cacheLoadedAt = DateTime.UtcNow;
                    if (haltedUntil.HasValue && DateTime.UtcNow < haltedUntil.Value)
                    {
                        _haltedUntil = haltedUntil;
                        _haltReason = reason ?? string.Empty;
                    }
                }
            }
            catch
            {
                // background refresh failure -> will retry on next IsHalted() call
            }
        }

        private async Task<(DateTime? haltedUntil, string? reason)> ReadHaltFromDbAsync(CancellationToken ct = default)
        {
            await using var conn = _getConnection();
            await conn.OpenAsync(ct);

            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT halted_until, reason FROM circuit_breaker_state WHERE id = 1;"
            );

            if (row == null) return (null, null);

            string raw = (string?)row.halted_until;
            if (DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                return (dt, (string?)row.reason);
            }
            return (null, null);
        }

        private async Task EnsureTableAsync(CancellationToken ct = default)
        {
            await using var conn = _getConnection();
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(@"CREATE TABLE IF NOT EXISTS circuit_breaker_state (
                id INTEGER PRIMARY KEY,
                halted_until TIMESTAMPTZ NULL,
                reason TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );");
        }

        private async Task WriteHaltToDbAsync(DateTime haltedUntil, string reason, CancellationToken ct = default)
        {
            await using var conn = _getConnection();
            await conn.OpenAsync(ct);
            await conn.ExecuteAsync(@"
                INSERT INTO circuit_breaker_state (id, halted_until, reason, created_at)
                VALUES (1, @HaltedUntil, @Reason, @CreatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    halted_until = EXCLUDED.halted_until,
                    reason       = EXCLUDED.reason,
                    created_at   = EXCLUDED.created_at;
            ", new
            {
                HaltedUntil = haltedUntil.ToString("o"),
                Reason = reason,
                CreatedAt = DateTime.UtcNow.ToString("o")
            });
        }

        private async Task DeleteHaltFromDbAsync(CancellationToken ct = default)
        {
            try
            {
                await using var conn = _getConnection();
                await conn.OpenAsync(ct);
                await conn.ExecuteAsync("DELETE FROM circuit_breaker_state WHERE id = 1;");
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] Failed to clear halt from DB: {ex.Message}");
            }
        }
    }
}
