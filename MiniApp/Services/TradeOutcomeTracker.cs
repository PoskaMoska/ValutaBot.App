using ValutaBot.App.MiniApp.Data.Repositories;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

public static class TradeOutcomeTracker
{
    public static IWalkForwardValidationEngine? WfEngine { get; set; }
    public static IAutoCalibrationEngine? CalibrationEngine { get; set; }
    private static volatile bool _initialized = false;
    private static readonly SemaphoreSlim _initSemaphore = new(1, 1);
    private static readonly SemaphoreSlim _csvSemaphore = new(1, 1); // B5-FIX: Concurrent CSV write lock
private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _consecutiveLosses = new();

public static int GetConsecutiveLosses(string asset, string timeframe)
{
    string key = $"{asset}_{timeframe}";
    return _consecutiveLosses.TryGetValue(key, out int count) ? count : 0;
}
    private static int _eurusdTradeCounter = 0;

    public static async Task InitializeAsync()
    {
        if (_initialized) return;
        
        await _initSemaphore.WaitAsync();
        try
        {
            if (_initialized) return;

            if (CalibrationEngine == null)
            {
                BotLogger.Warn("[TradeOutcomeTracker] CalibrationEngine not yet injected. Delaying initialization.");
                return;
            }

            // L2-FIX: Создаём таблицу calibration_state если не существует
            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.EnsureCalibrationTableAsync();

            // L2-FIX: Загружаем сохранённые EMA-веса из PostgreSQL
            var calibStates = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.LoadCalibrationStateAsync();
            foreach (var state in calibStates)
            {
                CalibrationEngine.RestoreState(state.sourceName, state.asset, state.timeframe, state.totalTrades, state.emaWinRate);
            }
            BotLogger.Info($"[TradeOutcomeTracker] Restored {calibStates.Count} EMA calibration states from PostgreSQL.");

            var outcomes = await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.LoadTradeOutcomesAsync(1000);
            BotLogger.Info($"[TradeOutcomeTracker] Loaded {outcomes.Count} historical outcomes from PostgreSQL DB (for reporting only).");

            _initialized = true;
            BotLogger.Info("[TradeOutcomeTracker] Online Reinforcement Learning engine initialized successfully.");
        }
        catch (Exception ex)
        {
            BotLogger.Error("[TradeOutcomeTracker] Failed to initialize trade outcome tracker", ex);
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    public static async Task OnTradeVerifiedAsync(SignalTracker.PredictionRecord record)
    {
        await InitializeAsync();

        try
        {
            var outcomeRecord = new TradeOutcomeRecord
            {
                Id = record.Id,
                Asset = record.Asset,
                Timeframe = record.Timeframe,
                Direction = record.Direction,
                EntryPrice = record.EntryPrice,
                ExitPrice = record.ExitPrice ?? record.EntryPrice,
                PnlBps = record.PnlBps,
                WasWin = record.WasCorrect ?? false,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };

            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SaveTradeOutcomeAsync(outcomeRecord);

            bool wasCorrect = record.WasCorrect ?? false;
double exitPriceVal = record.ExitPrice ?? record.EntryPrice;

string lossKey = $"{record.Asset}_{record.Timeframe}";
if (wasCorrect)
{
    _consecutiveLosses[lossKey] = 0;
}
else
{
    _consecutiveLosses.AddOrUpdate(lossKey, 1, (_, count) => count + 1);
}

            // РО (Pocket Option) Fix: The old secondary 'noise threshold' filter (0.00005 = 5 pips) was completely removed here.
            // On Pocket Option, over 60% of 1-minute trades close within a 1-4 pip margin.
            // By returning early, the ML RL loop was being starved of its most critical data.
            // Since SignalTracker.cs already filters exact Refunds, every trade reaching this method is a guaranteed binary Win/Loss and must be processed.

            if (record.SourceDirections != null && record.SourceDirections.Count > 0)
            {
                string winDirection = exitPriceVal > record.EntryPrice ? "BUY" : "PUT";
                foreach (var kv in record.SourceDirections)
                {
                    if (kv.Value != "NEUTRAL")
                    {
                        bool wasSourceCorrect = (kv.Value == winDirection);
                        CalibrationEngine?.RecordSourceOutcome(kv.Key, record.Asset, record.Timeframe, wasSourceCorrect);
                    }
                }
            }
            else
            {
                // Старая сделка без source_directions. Только глобальный исход.
                CalibrationEngine?.RecordSourceOutcome("GLOBAL", record.Asset, record.Timeframe, wasCorrect);
            }

            // L2-FIX: Сохраняем актуальное EMA-состояние в БД (асинхронно, чтобы не блокировать основной поток)
            _ = Task.Run(async () =>
            {
                try
                {
                    if (CalibrationEngine != null)
                    {
                        foreach (var stat in CalibrationEngine.GetAllStats())
                        {
                            await ValutaBot.App.MiniApp.Data.Repositories.TradeRepository.SaveCalibrationStateAsync(
                                stat.key.Source, stat.key.Asset, stat.key.Timeframe,
                                stat.totalTrades, stat.emaWinRate);
                        }
                    }
                }
                catch (Exception persistEx)
                {
                    BotLogger.Warn($"[TradeOutcomeTracker] Calibration persist notice: {persistEx.Message}");
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await MLPythonService.RecordOnlineTradeOutcomeAsync(
                        record.Asset,
                        record.Timeframe,
                        record.EntryPrice,
                        exitPriceVal,
                        record.Direction,
                        wasCorrect,
                        AssetSanitizer.IsForexAsset(record.Asset)
                    );
                }
                catch (Exception mlEx)
                {
                    Console.WriteLine($"[TradeOutcomeTracker] Online ML update notice: {mlEx.Message}");
                }
            });

            WfEngine?.RecordTradeOutcome(record.Asset, record.Timeframe, wasCorrect);

            BotLogger.Info($"[TradeOutcomeTracker] Verified trade {record.Id} ({record.Asset} {record.Timeframe}) -> {(wasCorrect ? "WIN" : "LOSS")}. Online RL weights & Walk-Forward state updated.");

            // ── ML Telemetry: Continuous Calibration ──
            if (record.Asset == "EUR/USD OTC")
            {
                int currentCount = Interlocked.Increment(ref _eurusdTradeCounter);
                if (currentCount % 20 == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var globalStats = CalibrationEngine?.GetStatsReport("GLOBAL", record.Asset, record.Timeframe) ?? "No Global Stats";
                            var ofStats = CalibrationEngine?.GetStatsReport("OrderFlow", record.Asset, record.Timeframe) ?? "No OF Stats";
                            var taStats = CalibrationEngine?.GetStatsReport("TechAnalysis", record.Asset, record.Timeframe) ?? "No TA Stats";
                            
                            string report = $"[📊 ML Self-Learning]\nAsset: {record.Asset} | Trades: {currentCount}\n\n" +
                                            $"🔹 GLOBAL: {globalStats}\n" +
                                            $"🔹 OrderFlow: {ofStats}\n" +
                                            $"🔹 TechAnalysis: {taStats}";
                            
                            await TelegramBotService.SendMessageToAdmins(report);
                            
                            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                            Directory.CreateDirectory(logDir);
                            string logFile = Path.Combine(logDir, "ml_calibration.csv");
                            
                            await _csvSemaphore.WaitAsync(); // B5-FIX: Prevent IOException when multiple trades complete at same time
                            try
                            {
                                bool writeHeader = !System.IO.File.Exists(logFile);
                                using var writer = new System.IO.StreamWriter(logFile, append: true);
                                if (writeHeader) await writer.WriteLineAsync("Timestamp,Iteration,Asset,GlobalStats,OrderFlowStats,TechAnalysisStats");
                                await writer.WriteLineAsync($"{DateTime.UtcNow:O},{currentCount},{record.Asset},{globalStats},{ofStats},{taStats}");
                            }
                            finally
                            {
                                _csvSemaphore.Release();
                            }
                        }
                        catch (Exception tEx)
                        {
                            BotLogger.Error("[TradeOutcomeTracker] Error sending ML telemetry", tEx);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[TradeOutcomeTracker] Error processing trade outcome for {record.Id}", ex);
        }
    }
}




