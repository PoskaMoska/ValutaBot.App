using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp
{
    public static class DriftDetectorService
    {
        private static readonly ConcurrentDictionary<string, DateTime> _lastRetrainTime = new();
        
        private const int WINDOW_SIZE = 20;
        private const double MIN_WIN_RATE = 0.48;
        private const int COOLDOWN_HOURS = 4;

        public static async Task AnalyzeAssetDriftAsync(string asset, string timeframe)
        {
            string key = $"{asset}_{timeframe}";
            
            if (_lastRetrainTime.TryGetValue(key, out var lastRetrain))
            {
                if ((DateTime.UtcNow - lastRetrain).TotalHours < COOLDOWN_HOURS) return;
            }

            try
            {
                var recentOutcomes = await TradeRepository.GetRecentOutcomesForAssetAsync(asset, timeframe, WINDOW_SIZE);
                if (recentOutcomes == null || recentOutcomes.Count < WINDOW_SIZE) return;

                int wins = recentOutcomes.Count(w => w);
                double winRate = (double)wins / recentOutcomes.Count;

                if (winRate < MIN_WIN_RATE)
                {
                    BotLogger.Warn($"[DriftDetector] {asset}/{timeframe} Win Rate dropped to {winRate:P0}. Triggering Auto-Retrain.");
                    _lastRetrainTime[key] = DateTime.UtcNow;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fetcher = new MarketDataFetcher();
                            var candles = await fetcher.FetchOhlcWithFallbackAsync(asset, timeframe, asset, 5000);
                            
                            if (candles != null && candles.Length > 0)
                            {
                                string cleanAsset = AssetSanitizer.Sanitize(asset);
                                bool isForex = AssetSanitizer.IsForexAsset(cleanAsset);
                                bool success = await MLPythonService.ForceTrainGlobalAsync(asset, timeframe, isForex);
                                if (!success) BotLogger.Warn($"[DriftDetector] Failed to Auto-Retrain {asset}/{timeframe}. Cooldown active.");
                            }
                        }
                        catch (Exception ex)
                        {
                            BotLogger.Warn($"[DriftDetector] Error during Auto-Retrain task: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[DriftDetector] Error analyzing drift for {key}: {ex.Message}");
            }
        }
    }
}
