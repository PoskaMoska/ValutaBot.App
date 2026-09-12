using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp.Services
{
    /// <summary>
    /// Monitors recent trade outcomes and halts trading when thresholds are breached.
    ///
    /// FIX 1 (2026-09-13): Halt state is now persisted in PostgreSQL (circuit_breaker_state table).
    /// Previously _haltedUntil was volatile in-memory: any process restart (crash, Railway redeploy,
    /// OOM kill) silently reset the halt and trading resumed immediately.
    ///
    /// Architecture:
    ///   - In-memory cache (_haltedUntil) avoids DB hit on every IsHalted() call.
    ///   - Cache considered stale after DB_CACHE_TTL_SECONDS (30s).
    ///   - LoadFromDbAsync() restores any active halt on startup.
    ///   - Halt activation: DB written first, then memory updated (write-through).
    ///   - Halt expiry:     DB row deleted, memory cleared.
    /// </summary>
    public static class CircuitBreakerService
    {
        private static DateTime? _haltedUntil = null;
        private static string _haltReason = string.Empty;
        private static DateTime _cacheLoadedAt = DateTime.MinValue;
        private static readonly object _lock = new object();

        // How long the in-memory cache is trusted before considering a DB refresh.
        private const int DB_CACHE_TTL_SECONDS = 30;

        // Thresholds
        private const int WINDOW_SIZE = 10;
        private const int MAX_CONSECUTIVE_LOSSES = 3;
        private const double MIN_WIN_RATE = 0.40;
        private const int COOLDOWN_MINUTES = 120; // 2 hours

        // ── Startup ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Called once at application startup to reload any active halt from the DB.
        /// Ensures a process restart does not silently clear an active CircuitBreaker halt.
        /// </summary>
        public static async Task LoadFromDbAsync()
        {
            try
            {
                var (haltedUntil, reason) = await ReadHaltFromDbAsync();
                lock (_lock)
                {
                    if (haltedUntil.HasValue && DateTime.UtcNow < haltedUntil.Value)
                    {
                        _haltedUntil   = haltedUntil;
                        _haltReason    = reason ?? string.Empty;
                        BotLogger.Warn($"[CircuitBreaker] Restored active halt from DB. Resumes at {_haltedUntil:u}. Reason: {_haltReason}");
                    }
                    else if (haltedUntil.HasValue)
                    {
                        // Stored halt already expired — clean up the stale DB row.
                        _ = Task.Run(DeleteHaltFromDbAsync);
                        BotLogger.Info("[CircuitBreaker] Stored halt had already expired. Cleared.");
                    }
                    else
                    {
                        BotLogger.Info("[CircuitBreaker] No active halt in DB. Trading is open.");
                    }
                    _cacheLoadedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] LoadFromDb failed (non-fatal): {ex.Message}");
            }
        }

        // ── Public API ───────────────────────────────────────────────────────────

        public static bool IsHalted()
        {
            lock (_lock)
            {
                // If cache is stale and no halt is currently known, trigger a background
                // DB refresh. The next call (within 30s) will see the updated value.
                bool cacheStale = (DateTime.UtcNow - _cacheLoadedAt).TotalSeconds > DB_CACHE_TTL_SECONDS;
                if (cacheStale && _haltedUntil == null)
                    _ = Task.Run(RefreshCacheFromDbAsync);

                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                    return true;

                // Halt expired in memory → clear state and remove DB row.
                if (_haltedUntil.HasValue && DateTime.UtcNow >= _haltedUntil.Value)
                {
                    _haltedUntil = null;
                    _haltReason  = string.Empty;
                    BotLogger.Info("[CircuitBreaker] Cooldown expired. Trading resumed.");
                    _ = Task.Run(DeleteHaltFromDbAsync);
                }

                return false;
            }
        }

        public static string? GetHaltedReason()
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

        public static async Task CheckStateAsync()
        {
            // If already halted, skip the DB outcome query entirely.
            if (IsHalted()) return;

            try
            {
                var recentOutcomes = await TradeRepository.GetRecentOutcomesAsync(WINDOW_SIZE);
                if (recentOutcomes == null || recentOutcomes.Count == 0) return;

                bool shouldHalt = false;
                string reason   = string.Empty;

                // Check 1: Consecutive Losses (outcomes are DESC, newest first)
                int consecutiveLosses = 0;
                foreach (var win in recentOutcomes)
                {
                    if (!win) consecutiveLosses++;
                    else break;
                }

                if (consecutiveLosses >= MAX_CONSECUTIVE_LOSSES)
                {
                    shouldHalt = true;
                    reason = $"{consecutiveLosses} consecutive losses";
                }

                // Check 2: Win rate over the window (min 5 trades to evaluate)
                if (!shouldHalt && recentOutcomes.Count >= 5)
                {
                    int wins       = recentOutcomes.Count(w => w);
                    double winRate = (double)wins / recentOutcomes.Count;
                    if (winRate < MIN_WIN_RATE)
                    {
                        shouldHalt = true;
                        reason = $"Win rate dropped to {winRate:P0} (last {recentOutcomes.Count} trades)";
                    }
                }

                if (shouldHalt)
                    await ActivateHaltAsync(reason);
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] State check failed: {ex.Message}");
            }
        }

        // ── Internal helpers ─────────────────────────────────────────────────────

        private static async Task ActivateHaltAsync(string reason)
        {
            DateTime newHaltUntil;
            lock (_lock)
            {
                // Double-check: another task may have already activated the halt.
                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                    return;

                newHaltUntil   = DateTime.UtcNow.AddMinutes(COOLDOWN_MINUTES);
                _haltedUntil   = newHaltUntil;
                _haltReason    = reason;
                _cacheLoadedAt = DateTime.UtcNow;
            }

            BotLogger.Warn($"[CircuitBreaker] TRADING HALTED for {COOLDOWN_MINUTES}m. Reason: {reason}");

            // Persist to DB so the halt survives a process restart.
            try
            {
                await WriteHaltToDbAsync(newHaltUntil, reason);
            }
            catch (Exception ex)
            {
                // Non-fatal: halt is active in memory. On restart, CheckStateAsync will
                // re-evaluate outcomes and re-activate if needed.
                BotLogger.Warn($"[CircuitBreaker] Failed to persist halt to DB (halt IS active in memory): {ex.Message}");
            }
        }

        private static async Task RefreshCacheFromDbAsync()
        {
            try
            {
                var (haltedUntil, reason) = await ReadHaltFromDbAsync();
                lock (_lock)
                {
                    _cacheLoadedAt = DateTime.UtcNow;
                    if (haltedUntil.HasValue && DateTime.UtcNow < haltedUntil.Value)
                    {
                        _haltedUntil = haltedUntil;
                        _haltReason  = reason ?? string.Empty;
                    }
                }
            }
            catch { /* Non-fatal background refresh — will retry on next stale cycle */ }
        }

        private static async Task<(DateTime? haltedUntil, string? reason)> ReadHaltFromDbAsync()
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            var row = await conn.QueryFirstOrDefaultAsync(
                "SELECT halted_until, reason FROM circuit_breaker_state WHERE id = 1;");

            if (row == null) return (null, null);

            string raw = (string)row.halted_until;
            if (DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                return (dt, (string?)row.reason);
            }
            return (null, null);
        }

        private static async Task WriteHaltToDbAsync(DateTime haltedUntil, string reason)
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO circuit_breaker_state (id, halted_until, reason, created_at)
                VALUES (1, @HaltedUntil, @Reason, @CreatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    halted_until = EXCLUDED.halted_until,
                    reason       = EXCLUDED.reason,
                    created_at   = EXCLUDED.created_at;",
                new
                {
                    HaltedUntil = haltedUntil.ToString("o"),
                    Reason      = reason,
                    CreatedAt   = DateTime.UtcNow.ToString("o")
                });
        }

        private static async Task DeleteHaltFromDbAsync()
        {
            try
            {
                using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    "DELETE FROM circuit_breaker_state WHERE id = 1;");
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] Failed to clear halt from DB: {ex.Message}");
            }
        }
    }
}



