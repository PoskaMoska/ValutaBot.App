using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using ValutaBot.App.MiniApp.Backtesting;

namespace ValutaBot.App.MiniApp.Backtesting
{
    /// <summary>
    /// Walk-Forward бектест полной системы самообучения.
    /// Запускает CalibrationON и CalibrationOFF параллельно на одних данных.
    /// </summary>
    public static class BacktestRunner
    {
        // ── Параметры симуляции ────────────────────────────────────────────────
        private const int    WindowSize       = 60;   // свечей в окне анализа
        private const double MinConfidence    = 0.55; // порог сигнала
        private const int    VerifyHorizonM1  = 5;    // свечей вперёд для M1
        private const int    VerifyHorizonS5  = 60;   // свечей вперёд для S5 (5 мин)
        private const int    EmaAlpha_Denom   = 10;   // EMA α = 1/10 = 0.1
        private const string Asset            = "EUR/USD OTC";

        public static async Task RunAsync(
            MiniAppController.OhlcCandle[] candles,
            string timeframe,
            bool   isS5)
        {
            int horizon = isS5 ? VerifyHorizonS5 : VerifyHorizonM1;
            int total   = candles.Length;

            Console.WriteLine($"[BacktestRunner] Старт | {timeframe} | {total} свечей | горизонт {horizon}");

            // ── Два изолированных инстанса AutoCalibrationEngine ──────────────
            var calibOn  = new AutoCalibrationEngine(); // самообучение включено
            var calibOff = new AutoCalibrationEngine(); // веса заморожены (baseline)

            var wfOn  = new WalkForwardValidationEngine();
            var wfOff = new WalkForwardValidationEngine();

            var taEngine   = new TechnicalAnalysisEngine();
            var metricsOn  = new BacktestMetrics();
            var metricsOff = new BacktestMetrics();

            // ── Глобальное переобучение ML перед тестом ──────────────────────
            Console.WriteLine($"[BacktestRunner] Запуск глобального переобучения на {total} свечах...");
            await MLPythonService.ForceTrainGlobalAsync(Asset, timeframe, candles, isForex: true);

            // ── Walk-forward цикл ─────────────────────────────────────────────
            int processed = 0;

            for (int i = WindowSize; i < total - horizon; i++)
            {
                // Скользящее окно
                var window = new ArraySegment<MiniAppController.OhlcCandle>(candles, i - WindowSize, WindowSize);
                var ohlcSpan = window.Array!.AsSpan(window.Offset, window.Count);

                double[] closePrices = new double[WindowSize];
                double[] volumes     = new double[WindowSize];
                for (int k = 0; k < WindowSize; k++)
                {
                    closePrices[k] = ohlcSpan[k].Close;
                    volumes[k]     = ohlcSpan[k].Volume;
                }

                double currentPrice = ohlcSpan[WindowSize - 1].Close;
                DateTime timestamp  = ohlcSpan[WindowSize - 1].Timestamp;

                // ── TA Engine ────────────────────────────────────────────────
                var (taScore, taConf, rsiVal, hmaVal, volStr, atrVal) =
                    taEngine.ScoreTimeframe(Asset, timeframe,
                        closePrices.AsSpan(), volumes.AsSpan(), ohlcSpan);

                // ── SMC Engine ───────────────────────────────────────────────
                var smcState = taEngine.GetSmcState(Asset, timeframe, ohlcSpan, currentPrice);

                // ── OrderFlow Engine ─────────────────────────────────────────
                var ofResult = OrderFlowEngine.AnalyzeOrderFlow(Asset, timeframe, ohlcSpan, currentPrice);

                // ── ContinuousState Engine ───────────────────────────────────
                var stateResult = ContinuousStateEngine.EvaluateContinuousState(
                    closePrices.AsSpan(), Asset, timeframe);

                // ── ML (LightGBM) ─────────────────
                var mlDir  = "NEUTRAL";
                var mlConf = 0.5;
                var mlPred = await MLPythonService.PredictAsync(Asset, timeframe, ohlcSpan.ToArray(), isForex: true);
                if (mlPred != null)
                {
                    mlDir = mlPred.Direction;
                    mlConf = mlPred.Confidence;
                }

                // ── Детектируем режим рынка и Веса (Adaptive Ensemble) ──────────────
                double volRatio = taEngine.CalculateVolatilityRatio(closePrices.AsSpan());
                var regime = calibOn.DetectMarketRegime(20, volRatio, rsiVal, closePrices.AsSpan());
                string regimeName = regime.ToString();

                double wTA   = calibOn.GetCalibratedRegimeWeight("TechAnalysis", Asset, timeframe, regime);
                double wOF   = calibOn.GetCalibratedRegimeWeight("OrderFlow",    Asset, timeframe, regime);
                double wLGBM = calibOn.GetCalibratedRegimeWeight("LIGHTGBM",     Asset, timeframe, regime);
                double wSMC  = calibOn.GetCalibratedRegimeWeight("SMC",          Asset, timeframe, regime);
                double wSkender = calibOn.GetCalibratedRegimeWeight("SKENDER_MATH", Asset, timeframe, regime);

                // ── Определяем направление сигнала через Ансамбль (Ensemble) ──
                double ensembleScore = 0;
                
                // 1. Math TA (score is typically -1.0 to 1.0)
                ensembleScore += (taScore * wTA);
                
                // 2. OrderFlow
                if (ofResult.ScoreContribution > 0.4) {
                    ensembleScore += ofResult.OrderFlowState.Contains("BULLISH") ? (0.5 * wOF) : (-0.5 * wOF);
                }

                // 3. ML LightGBM
                if (mlDir == "BUY") ensembleScore += (0.8 * wLGBM);
                else if (mlDir == "PUT") ensembleScore -= (0.8 * wLGBM);

                string direction = "NEUTRAL";
                double confidence = 0;

                // Dynamically adaptive threshold
                double triggerThreshold = regime == AutoCalibrationEngine.MarketRegime.HighVolatilityChaos ? 0.7 : 0.4;

                if (ensembleScore > triggerThreshold) {
                    direction = "BUY";
                    confidence = 65.0 + Math.Min(ensembleScore * 10, 25.0);
                } else if (ensembleScore < -triggerThreshold) {
                    direction = "PUT";
                    confidence = 65.0 + Math.Min(Math.Abs(ensembleScore) * 10, 25.0);
                }

                if (direction == "NEUTRAL") continue; // нет сигнала

                // Walk-Forward guard (CalibON)
                var wfResultOn = wfOn.ValidateWalkForward(Asset, timeframe);
                bool cooloffOn = wfResultOn.IsCooloffActive;

                // Walk-Forward guard (CalibOFF)
                var wfResultOff = wfOff.ValidateWalkForward(Asset, timeframe);
                bool cooloffOff = wfResultOff.IsCooloffActive;
                // ── Верификация исхода через горизонт ─────────────────────────
                double exitPrice  = candles[i + horizon].Close;
                bool   isWin      = direction == "BUY"
                    ? exitPrice > currentPrice
                    : exitPrice < currentPrice;

                // ── Записываем исход в обе системы ───────────────────────────
                if (!cooloffOn)
                {
                    // CalibON: реальное самообучение
                    calibOn.RecordSourceOutcome("TechAnalysis", Asset, timeframe,
                        taScore > 0 == (exitPrice > currentPrice));
                    calibOn.RecordSourceOutcome("OrderFlow",    Asset, timeframe,
                        ofResult.OrderFlowState.Contains("BULLISH") == (exitPrice > currentPrice));
                    calibOn.RecordSourceOutcome("SMC",          Asset, timeframe,
                        smcState.BosDirection == direction);
                        
                    // ONLINE REINFORCEMENT LEARNING FOR ML (Скармливаем исход нейросети)
                    await MLPythonService.RecordOnlineTradeOutcomeAsync(Asset, timeframe, currentPrice, exitPrice, direction, isWin, true);

                    wfOn.RecordTradeOutcome(Asset, timeframe, isWin);

                    metricsOn.Record(new BacktestMetrics.TradeRecord(
                        CandleIndex:       i,
                        Timestamp:         timestamp,
                        Direction:         direction,
                        Confidence:        confidence,
                        IsWin:             isWin,
                        Regime:            regimeName,
                        WeightTA:          wTA,
                        WeightSMC:         wSMC,
                        WeightOF:          wOF,
                        WeightLGBM:        wLGBM,
                        WeightSkender:     wSkender,
                        MlDirection:       mlDir,
                        MlConfidence:      mlConf,
                        CalibrationEnabled:true,
                        CooloffActive:     false));
                }

                if (!cooloffOff)
                {
                    // CalibOFF: фиксированные веса 1.0 — никакого обучения
                    wfOff.RecordTradeOutcome(Asset, timeframe, isWin);

                    metricsOff.Record(new BacktestMetrics.TradeRecord(
                        CandleIndex:       i,
                        Timestamp:         timestamp,
                        Direction:         direction,
                        Confidence:        confidence,
                        IsWin:             isWin,
                        Regime:            regimeName,
                        WeightTA:          1.0,
                        WeightSMC:         1.0,
                        WeightOF:          1.0,
                        WeightLGBM:        1.0,
                        WeightSkender:     1.0,
                        MlDirection:       mlDir,
                        MlConfidence:      mlConf,
                        CalibrationEnabled:false,
                        CooloffActive:     false));
                }

                processed++;

                // Прогресс каждые 2000 свечей
                if (i % 2000 == 0)
                {
                    Console.WriteLine($"[BacktestRunner] {i}/{total} | " +
                                      $"Сигналов: {metricsOn.TotalSignals} | " +
                                      $"WR_ON: {metricsOn.WinRate()*100:F1}% | " +
                                      $"WR_OFF: {metricsOff.WinRate()*100:F1}%");
                }
            }

            Console.WriteLine($"[BacktestRunner] Завершено. Обработано {processed} свечей с сигналом.");

            // ── Сохранение результатов ────────────────────────────────────────
            string tag   = $"{timeframe}_{total}";
            string dir   = "Logs";

            await metricsOn.SaveCsvAsync( Path.Combine(dir, $"backtest_{tag}_calibON.csv"));
            await metricsOff.SaveCsvAsync(Path.Combine(dir, $"backtest_{tag}_calibOFF.csv"));
            await BacktestReport.SaveJsonAsync(metricsOn, metricsOff, timeframe, total,
                Path.Combine(dir, $"backtest_{tag}_report.json"));

            BacktestReport.PrintSummary(metricsOn, metricsOff, timeframe, total);
        }
    }
}
