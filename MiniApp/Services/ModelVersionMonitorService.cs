using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ValutaBot.App.MiniApp.Backtesting;

namespace ValutaBot.MiniApp
{
    public class ModelVersionMonitorService : BackgroundService
    {
        private static readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);
        private readonly ILogger<ModelVersionMonitorService> _logger;
        private string _lastSeenVersion = "";

        public ModelVersionMonitorService(ILogger<ModelVersionMonitorService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[Model Monitor] Started background model version scanner (every 5 min)");
            
            // Wait a bit before first check to let the app start
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckModelVersionsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[Model Monitor] Error checking versions: {ex.Message}");
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
        }

        private async Task CheckModelVersionsAsync()
        {
            string asset = "EURUSD";
            string timeframe = "h4";
            
            var candles = await HistoricalDataLoader.LoadAsync(60, "4h", "EUR/USD", forceRefresh: true);
            if (candles == null || candles.Length == 0) return;

            var prediction = await MLPythonService.PredictAsync("EURUSD", "h4", candles, isForex: true);
            if (prediction == null || string.IsNullOrEmpty(prediction.ModelVersion)) return;

            string currentVer = prediction.ModelVersion;

            if (string.IsNullOrEmpty(_lastSeenVersion))
            {
                _lastSeenVersion = currentVer;
                return;
            }

            if (_lastSeenVersion != currentVer)
            {
                _logger.LogInformation($"[Model Monitor] Model version change detected! {asset}/{timeframe}: {_lastSeenVersion} -> {currentVer}");
                
                string accStr    = prediction.Accuracy.HasValue ? $"{prediction.Accuracy.Value * 100:F1}%" : "N/A";
                string aucStr    = prediction.Auc.HasValue ? $"{prediction.Auc.Value:F3}" : "N/A";
                string nTrainStr = prediction.NTrain.HasValue ? $"{prediction.NTrain.Value:N0}" : "N/A";
                string icon      = prediction.Accuracy.HasValue
                    ? (prediction.Accuracy.Value >= 0.57 ? "🟢" : prediction.Accuracy.Value >= 0.54 ? "🟡" : "🔴")
                    : "⚪";

                string report = $"🔄 <b>Глобальное переобучение завершено</b>\n" +
                                $"<i>Фоновый детектор зафиксировал новые веса нейросети</i>\n\n" +
                                $"{icon} <b>{asset}</b> ({timeframe}): Точность <b>{accStr}</b> | AUC <b>{aucStr}</b> | {nTrainStr} свечей";

                await TelegramBotService.SendMessageToAdmins(report);
                
                _lastSeenVersion = currentVer;
            }
        }
    }
}

