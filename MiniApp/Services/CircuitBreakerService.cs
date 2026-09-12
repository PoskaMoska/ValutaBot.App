using System;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp.Services
{
    public static class CircuitBreakerService
    {
        private static DateTime? _haltedUntil = null;
        private static readonly object _lock = new object();
        
        // Thresholds
        private const int WINDOW_SIZE = 10;
        private const int MAX_CONSECUTIVE_LOSSES = 3;
        private const double MIN_WIN_RATE = 0.40;
        private const int COOLDOWN_MINUTES = 120; // 2 hours

        public static bool IsHalted()
        {
            lock (_lock)
            {
                if (_haltedUntil.HasValue && DateTime.UtcNow < _haltedUntil.Value)
                {
                    return true;
                }
                
                // If cooldown expired, reset
                if (_haltedUntil.HasValue && DateTime.UtcNow >= _haltedUntil.Value)
                {
                    _haltedUntil = null;
                    BotLogger.Info("[CircuitBreaker] Cooldown expired. Trading resumed.");
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
                    return $"CIRCUIT_BREAKER_ACTIVE (Resumes in {remain:F0}m)";
                }
                return null;
            }
        }

        public static async Task CheckStateAsync()
        {
            // If already halted, no need to check DB
            if (IsHalted()) return;

            try
            {
                var recentOutcomes = await TradeRepository.GetRecentOutcomesAsync(WINDOW_SIZE);
                if (recentOutcomes == null || recentOutcomes.Count == 0) return;

                bool shouldHalt = false;
                string reason = "";

                // Check 1: Consecutive Losses
                int consecutiveLosses = 0;
                foreach (var win in recentOutcomes)
                {
                    if (!win) consecutiveLosses++;
                    else break; // Break on first win (outcomes are DESC, newest first)
                }

                if (consecutiveLosses >= MAX_CONSECUTIVE_LOSSES)
                {
                    shouldHalt = true;
                    reason = $"{MAX_CONSECUTIVE_LOSSES} consecutive losses";
                }

                // Check 2: Winrate over the window
                // Only evaluate if we have enough trades (e.g., at least 5)
                if (!shouldHalt && recentOutcomes.Count >= 5)
                {
                    int wins = recentOutcomes.Count(w => w);
                    double winRate = (double)wins / recentOutcomes.Count;
                    if (winRate < MIN_WIN_RATE)
                    {
                        shouldHalt = true;
                        reason = $"Win rate dropped to {winRate:P0} (last {recentOutcomes.Count} trades)";
                    }
                }

                if (shouldHalt)
                {
                    lock (_lock)
                    {
                        // Double check lock
                        if (!_haltedUntil.HasValue)
                        {
                            _haltedUntil = DateTime.UtcNow.AddMinutes(COOLDOWN_MINUTES);
                            BotLogger.Warn($"[CircuitBreaker] TRADING HALTED for {COOLDOWN_MINUTES}m. Reason: {reason}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[CircuitBreaker] State check failed: {ex.Message}");
            }
        }
    }
}
