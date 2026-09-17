using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Data.Repositories
{
    public static class RiskStateRepository
    {
        private static readonly string _savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "risk_state.json");
        private static readonly SemaphoreSlim _saveLock = new(1, 1);

        private static ConcurrentDictionary<string, int> _consecutiveLosses = new();
        private static ConcurrentDictionary<string, DateTime> _cooldowns = new();

        static RiskStateRepository()
        {
            Load();
        }

        public static int GetConsecutiveLosses(string asset, string timeframe)
        {
            string key = $"{asset}_{timeframe}";
            return _consecutiveLosses.TryGetValue(key, out int count) ? count : 0;
        }

        public static void ResetConsecutiveLosses(string asset, string timeframe)
        {
            string key = $"{asset}_{timeframe}";
            _consecutiveLosses[key] = 0;
            _ = SaveAsync();
        }

        public static void IncrementConsecutiveLosses(string asset, string timeframe)
        {
            string key = $"{asset}_{timeframe}";
            _consecutiveLosses.AddOrUpdate(key, 1, (_, count) => count + 1);
            _ = SaveAsync();
        }

        public static bool IsOnCooldownAndSet(string asset, string timeframe, int cooldownSeconds)
        {
            string key = $"{asset}_{timeframe}";
            DateTime now = DateTime.UtcNow;
            bool isOnCooldown = true;

            _cooldowns.AddOrUpdate(key,
                _ => { isOnCooldown = false; return now; },
                (_, lastSignalAt) =>
                {
                    if ((now - lastSignalAt).TotalSeconds >= cooldownSeconds)
                    {
                        isOnCooldown = false;
                        return now;
                    }
                    return lastSignalAt;
                });

            // Evict expired entries to prevent memory leak
            if (!isOnCooldown && _cooldowns.Count > 50)
            {
                var expiredKeys = new List<string>();
                foreach (var kvp in _cooldowns)
                {
                    if ((now - kvp.Value).TotalSeconds > cooldownSeconds * 2)
                        expiredKeys.Add(kvp.Key);
                }
                foreach (var k in expiredKeys)
                    _cooldowns.TryRemove(k, out _);
            }

            if (!isOnCooldown)
            {
                _ = SaveAsync();
            }

            return isOnCooldown;
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_savePath))
                {
                    string json = File.ReadAllText(_savePath);
                    var data = JsonSerializer.Deserialize<RiskStateData>(json);
                    if (data != null)
                    {
                        if (data.ConsecutiveLosses != null)
                            _consecutiveLosses = new ConcurrentDictionary<string, int>(data.ConsecutiveLosses);
                        if (data.Cooldowns != null)
                            _cooldowns = new ConcurrentDictionary<string, DateTime>(data.Cooldowns);
                    }
                    BotLogger.Info($"[RiskStateRepository] Loaded risk state from disk.");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Error($"[RiskStateRepository] Failed to load risk state: {ex.Message}");
            }
        }

        private static async Task SaveAsync()
        {
            if (!await _saveLock.WaitAsync(0)) 
                return; // non-blocking, skip if already saving

            try
            {
                // Run on background thread
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100); // Batch saves together
                        
                        var data = new RiskStateData
                        {
                            ConsecutiveLosses = new Dictionary<string, int>(_consecutiveLosses),
                            Cooldowns = new Dictionary<string, DateTime>(_cooldowns)
                        };
                        string json = JsonSerializer.Serialize(data);
                        
                        var dir = Path.GetDirectoryName(_savePath);
                        if (dir != null && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        await File.WriteAllTextAsync(_savePath + ".tmp", json);
                        File.Move(_savePath + ".tmp", _savePath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        BotLogger.Error($"[RiskStateRepository] Save failed: {ex.Message}");
                    }
                    finally
                    {
                        _saveLock.Release();
                    }
                });
            }
            catch
            {
                _saveLock.Release();
            }
        }

        private class RiskStateData
        {
            public Dictionary<string, int>? ConsecutiveLosses { get; set; }
            public Dictionary<string, DateTime>? Cooldowns { get; set; }
        }
    }
}
