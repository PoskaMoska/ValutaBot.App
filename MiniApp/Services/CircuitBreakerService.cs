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
        // ── Autonomous Dataset Collection Mode ────────────────────────────────────────
        // The bot automatically bypasses the Circuit Breaker until it has accumulated
        // enough labeled rows to be worth protecting (_settings.DatasetReadinessThreshold, default 1,000).
        // Once this threshold is crossed, CB activates permanently as a live-trading guard.

        private int  _cachedOutcomeCount     = -1;   // -1 = not yet loaded
        private DateTime _outcomeCacheExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _outcomeCacheLock = new(1, 1);

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
            // ── Autonomous bypass: if we haven't yet reached the dataset readiness
            // threshold, Circuit Breaker is completely inactive. No config needed.
            if (_cachedOutcomeCount >= 0 && _cachedOutcomeCount < _settings.DatasetReadinessThreshold)
            {
                // Kick an async refresh so the count stays up-to-date (fire-and-forget)
                if (DateTime.UtcNow > _outcomeCacheExpiry)
                    _ = Task.Run(RefreshOutcomeCountCacheAsync);
                return false;
            }

            // Outcome count not yet loaded (startup) → trigger async load and allow scanning
            if (_cachedOutcomeCount < 0)
            {
                _ = Task.Run(RefreshOutcomeCountCacheAsync);
                return false;
            }

            lock (_lock)
            {
                // Refresh CB halt state from DB if cache is stale
                var cacheStale = (DateTime.UtcNow - _cacheLoadedAt).TotalSeconds > _settings.CircuitBreakerDbCacheTtlSeconds;
                if (cacheStale)
                {
                    _ = Task.Run(async () => await RefreshCacheFromDbAsync());
                }

                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                    return true;

                // Halt expired → clear state and delete DB row
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
            // During dataset collection show informative progress instead of null
            if (_cachedOutcomeCount >= 0 && _cachedOutcomeCount < _settings.DatasetReadinessThreshold)
                return null; // scanner treats null as "not halted" — correct behaviour

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
            // ── Autonomous bypass: don't activate CB while still building the dataset ──
            await RefreshOutcomeCountCacheAsync();
            if (_cachedOutcomeCount < _settings.DatasetReadinessThreshold)
            {
                BotLogger.Info($"[CircuitBreaker] Dataset collection mode — {_cachedOutcomeCount}/{_settings.DatasetReadinessThreshold} rows. CB inactive.");
                return;
            }

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
                    if (win == false) consecutiveLosses++;
                    else break; // Win or Tie resets the streak
                }
                if (consecutiveLosses >= _settings.CircuitBreakerMaxConsecutiveLosses)
                {
                    shouldHalt = true;
                    reason = $"{consecutiveLosses} consecutive losses";
                }

                // Check 2: Win rate over the window (min 5 trades to evaluate, exclude ties)
                var resolvedTrades = recentOutcomes.Where(w => w.HasValue).Select(w => w!.Value).ToList();
                if (!shouldHalt && resolvedTrades.Count >= 5)
                {
                    int wins = resolvedTrades.Count(w => w);
                    double winRate = (double)wins / resolvedTrades.Count;
                    if (winRate < _settings.CircuitBreakerMinWinRate)
                    {
                        shouldHalt = true;
                        reason = $"Win rate dropped to {winRate:P0} (last {resolvedTrades.Count} resolved trades)";
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

        private async Task RefreshOutcomeCountCacheAsync()
        {
            if (await _outcomeCacheLock.WaitAsync(0))
            {
                try
                {
                    if (DateTime.UtcNow > _outcomeCacheExpiry)
                    {
                        _cachedOutcomeCount = await TradeRepository.GetVerifiedOutcomesCountAsync();
                        _outcomeCacheExpiry = DateTime.UtcNow.AddMinutes(5); // cache for 5 minutes
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn($"[CircuitBreaker] Failed to refresh outcome count: {ex.Message}");
                }
                finally
                {
                    _outcomeCacheLock.Release();
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

        private sealed record CbState(DateTime? halted_until, string? reason);

        private async Task<(DateTime? haltedUntil, string? reason)> ReadHaltFromDbAsync(CancellationToken ct = default)
        {
            await using var conn = _getConnection();
            await conn.OpenAsync(ct);

            var row = await conn.QueryFirstOrDefaultAsync<CbState>(
                "SELECT halted_until, reason FROM circuit_breaker_state WHERE id = 1;"
            );

            if (row == null) return (null, null);
            return (row.halted_until, row.reason);
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
                HaltedUntil = haltedUntil,
                Reason = reason,
                CreatedAt = DateTime.UtcNow
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
