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
        bool IsSymbolQuarantined(string asset, string timeframe, out string? reason);
        Task CheckSymbolStateAsync(string asset, string timeframe, bool? latestWasWin = null, bool isShadow = false, CancellationToken ct = default);
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
        private DateTime? _lastCooldownExpiredUtc;
        private string _haltReason = string.Empty;
        private DateTime _cacheLoadedAt = DateTime.MinValue;
        private int _isRefreshingCache = 0;

        // Per-Symbol/Timeframe Shadow Quarantine (isolates toxic pairs without halting profitable pairs)
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime QuarantinedUntil, string Reason)> _symbolQuarantine =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _shadowRecoveryStreak =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _symbolQuarantineWatermarks =
            new(StringComparer.OrdinalIgnoreCase);

        private static string GetSymbolKey(string asset, string timeframe)
            => $"{AssetSanitizer.Sanitize(asset)}_{timeframe.Trim().ToLowerInvariant()}";

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
                // Refresh CB halt state from DB if cache is stale
                var cacheStale = (DateTime.UtcNow - _cacheLoadedAt).TotalSeconds > _settings.CircuitBreakerDbCacheTtlSeconds;
                if (cacheStale && Interlocked.CompareExchange(ref _isRefreshingCache, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await RefreshCacheFromDbAsync(); }
                        finally { Interlocked.Exchange(ref _isRefreshingCache, 0); }
                    });
                }

                // 1. ABSOLUTE PRIORITY: if system is halted (memory or DB), trading is ALWAYS forbidden!
                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                    return true;

                // Halt expired → record expiration watermark, clear state, and delete DB row
                if (_haltedUntil.HasValue && DateTime.UtcNow >= _haltedUntil.Value)
                {
                    _lastCooldownExpiredUtc = _haltedUntil.Value;
                    _haltedUntil = null;
                    _haltReason = string.Empty;
                    BotLogger.Info($"[CircuitBreaker] Cooldown expired. Fresh evaluation window starts after {_lastCooldownExpiredUtc:HH:mm:ss} UTC.");
                    _ = Task.Run(async () => await DeleteHaltFromDbAsync());
                }
            }

            // 2. Autonomous bypass: only active when NO explicit Halt is in effect
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

            return false;
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
            }

            // During dataset collection show informative progress instead of null
            if (_cachedOutcomeCount >= 0 && _cachedOutcomeCount < _settings.DatasetReadinessThreshold)
                return null; // scanner treats null as "not halted" — correct behaviour

            return null;
        }

        public bool IsSymbolQuarantined(string asset, string timeframe, out string? reason)
        {
            reason = null;
            string cleanAsset = AssetSanitizer.Sanitize(asset);

            // 1. Check Pair-level quarantine (highest priority: protects all timeframes of toxic asset)
            if (_symbolQuarantine.TryGetValue(cleanAsset, out var qPair))
            {
                if (DateTime.UtcNow < qPair.QuarantinedUntil)
                {
                    var remain = (qPair.QuarantinedUntil - DateTime.UtcNow).TotalMinutes;
                    reason = $"PAIR_QUARANTINE ({cleanAsset}: resumes in {remain:F0}m — {qPair.Reason})";
                    return true;
                }

                _symbolQuarantineWatermarks[cleanAsset] = DateTime.UtcNow;
                _symbolQuarantine.TryRemove(cleanAsset, out _);
                _shadowRecoveryStreak.TryRemove(cleanAsset, out _);
                BotLogger.Info($"[CircuitBreaker] Pair-level quarantine expired for {cleanAsset}. Resuming live trading.");
            }

            // 2. Check Timeframe-specific quarantine
            string key = GetSymbolKey(asset, timeframe);
            if (_symbolQuarantine.TryGetValue(key, out var q))
            {
                if (DateTime.UtcNow < q.QuarantinedUntil)
                {
                    var remain = (q.QuarantinedUntil - DateTime.UtcNow).TotalMinutes;
                    reason = $"SYMBOL_QUARANTINE ({key}: resumes in {remain:F0}m — {q.Reason})";
                    return true;
                }

                _symbolQuarantineWatermarks[key] = DateTime.UtcNow;
                _symbolQuarantine.TryRemove(key, out _);
                _shadowRecoveryStreak.TryRemove(key, out _);
                BotLogger.Info($"[CircuitBreaker] Per-symbol quarantine expired for {key}. Resuming live trading.");
            }
            return false;
        }

        public async Task CheckSymbolStateAsync(string asset, string timeframe, bool? latestWasWin = null, bool isShadow = false, CancellationToken ct = default)
        {
            if (latestWasWin == null) return;
            string key = GetSymbolKey(asset, timeframe);
            string cleanAsset = AssetSanitizer.Sanitize(asset);

            if (isShadow)
            {
                // Shadow healing for pair-level quarantine
                if (_symbolQuarantine.TryGetValue(cleanAsset, out var activePairQ) && DateTime.UtcNow < activePairQ.QuarantinedUntil)
                {
                    if (latestWasWin == true)
                    {
                        int streak = _shadowRecoveryStreak.AddOrUpdate(cleanAsset, 1, (_, c) => c + 1);
                        if (streak >= 2)
                        {
                            _symbolQuarantineWatermarks[cleanAsset] = DateTime.UtcNow;
                            _symbolQuarantine.TryRemove(cleanAsset, out _);
                            _shadowRecoveryStreak.TryRemove(cleanAsset, out _);
                            BotLogger.Info($"[CircuitBreaker] Early self-healing recovery for pair {cleanAsset}: 2 consecutive SHADOW wins! Lifting pair quarantine.");
                        }
                    }
                    else
                    {
                        _shadowRecoveryStreak[cleanAsset] = 0;
                    }
                }

                // Shadow healing for timeframe-specific quarantine
                if (_symbolQuarantine.TryGetValue(key, out var activeQ) && DateTime.UtcNow < activeQ.QuarantinedUntil)
                {
                    if (latestWasWin == true)
                    {
                        int streak = _shadowRecoveryStreak.AddOrUpdate(key, 1, (_, c) => c + 1);
                        if (streak >= 2)
                        {
                            _symbolQuarantineWatermarks[key] = DateTime.UtcNow;
                            _symbolQuarantine.TryRemove(key, out _);
                            _shadowRecoveryStreak.TryRemove(key, out _);
                            BotLogger.Info($"[CircuitBreaker] Early self-healing recovery for {key}: 2 consecutive SHADOW wins! Lifting quarantine.");
                        }
                    }
                    else
                    {
                        _shadowRecoveryStreak[key] = 0;
                    }
                }
                return;
            }

            if (IsSymbolQuarantined(asset, timeframe, out _)) return;

            try
            {
                DateTime? keyWm = _symbolQuarantineWatermarks.TryGetValue(key, out var wm1) ? wm1 : null;
                DateTime? pairWm = _symbolQuarantineWatermarks.TryGetValue(cleanAsset, out var wm2) ? wm2 : null;
                DateTime? sinceUtc = (keyWm.HasValue && pairWm.HasValue)
                    ? (keyWm.Value > pairWm.Value ? keyWm.Value : pairWm.Value)
                    : (keyWm ?? pairWm);
                // ИСПРАВЛЕНО: Передаем cleanAsset вместо asset для совпадения с базой trade_outcomes
                var recentAssetOutcomes = await TradeRepository.GetRecentOutcomesForAssetAsync(cleanAsset, timeframe, 12, sinceUtc);
                if (recentAssetOutcomes == null || recentAssetOutcomes.Count < 2) return;

                int consecutiveLosses = 0;
                foreach (bool win in recentAssetOutcomes)
                {
                    if (!win) consecutiveLosses++;
                    else break;
                }

                bool shouldQuarantine = false;
                string qReason = string.Empty;

                if (consecutiveLosses >= 2)
                {
                    shouldQuarantine = true;
                    qReason = $"{consecutiveLosses} consecutive losses on {key}";
                }
                else if (recentAssetOutcomes.Count >= 10)
                {
                    int wins = recentAssetOutcomes.Count(w => w);
                    double winRate = (double)wins / recentAssetOutcomes.Count;
                    if (winRate < 0.42)
                    {
                        shouldQuarantine = true;
                        qReason = $"Symbol WinRate {winRate:P0} ({wins}/{recentAssetOutcomes.Count}) < 42%";
                    }
                }

                if (shouldQuarantine)
                {
                    var until = DateTime.UtcNow.AddMinutes(30);
                    _symbolQuarantine[key] = (until, qReason);
                    _shadowRecoveryStreak[key] = 0;

                    // If 2+ consecutive losses, quarantine the entire asset pair across all timeframes
                    if (consecutiveLosses >= 2)
                    {
                        _symbolQuarantine[cleanAsset] = (until, qReason);
                        _shadowRecoveryStreak[cleanAsset] = 0;
                        BotLogger.Warn($"[CircuitBreaker] PAIR-LEVEL QUARANTINE activated for {cleanAsset} across all timeframes (30m). Reason: {qReason}");
                    }
                    else
                    {
                        BotLogger.Warn($"[CircuitBreaker] PER-SYMBOL QUARANTINE activated for {key} (30m or 2 shadow wins). Reason: {qReason}");
                    }
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] CheckSymbolStateAsync failed for {key}: {ex.Message}");
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
                DateTime? sinceUtc;
                lock (_lock) { sinceUtc = _lastCooldownExpiredUtc; }

                var recentOutcomes = await TradeRepository.GetRecentOutcomesAsync(_settings.CircuitBreakerWindowSize, sinceUtc);
                if (recentOutcomes == null || recentOutcomes.Count == 0) return;

                bool shouldHalt = false;
                string reason = string.Empty;

                // Check 1: Consecutive losses (outcomes are DESC, newest first)
                int consecutiveLosses = 0;
                foreach (var win in recentOutcomes)
                {
                    if (win == false) 
                    {
                        consecutiveLosses++;
                    }
                    else if (win == true) 
                    {
                        break; // Only real WIN resets the loss streak
                    }
                    // win == null (Doji/Tie) -> continue (does NOT break the loss streak)
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
                _haltedUntil = newHaltUntil;
                _haltReason = reason;
                _cacheLoadedAt = DateTime.UtcNow;
            }

            BotLogger.Warn($"[CircuitBreaker] TRADING HALTED for {_settings.CircuitBreakerCooldownMinutes}m. Reason: {reason}");

            // Persist to DB (write-through)
            try
            {
                await EnsureTableAsync(ct);
                await WriteHaltToDbAsync(newHaltUntil, reason, ct);
            }
            catch (Exception ex)
            {
                // Non-fatal: halt is already active in memory, but DB write failed.
                BotLogger.Warn($"[CircuitBreaker] Failed to persist halt to DB (halt IS active in memory): {ex.Message}");
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
                    else if (haltedUntil.HasValue && DateTime.UtcNow >= haltedUntil.Value)
                    {
                        _lastCooldownExpiredUtc = haltedUntil.Value;
                        _haltedUntil = null;
                        _haltReason = string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] Background refresh from DB failed: {ex.Message}");
                lock (_lock)
                {
                    _cacheLoadedAt = DateTime.UtcNow.AddSeconds(-_settings.CircuitBreakerDbCacheTtlSeconds + 5);
                }
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
