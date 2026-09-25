using ValutaBot.Core;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ValutaBot.App.MiniApp.Services;
using ValutaBot.App.MiniApp.Models;

namespace ValutaBot.MiniApp.Features.MarketAnalysis;

public class MarketAnalysisOrchestrator : IMarketAnalysisOrchestrator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastSeenModelVersions = new();
    private static readonly System.Threading.SemaphoreSlim _csvSemaphore = new(1, 1);
    
    private readonly MarketDataFetcher _fetcher;
    private readonly IRiskGatekeeper _riskGatekeeper;
    private readonly IMathEngine _mathEngine;
    private readonly IMarketAnalyzer _marketAnalyzer;
    private readonly IConfluenceMatrixEngine _cmEngine;
    private readonly ITradeTimeoutEngine _timeoutEngine;
    private readonly TradingBotSettings _settings;
    private readonly ILogger<MarketAnalysisOrchestrator> _logger;

    public MarketAnalysisOrchestrator(
        MarketDataFetcher fetcher,
        IRiskGatekeeper riskGatekeeper,
        IMathEngine mathEngine,
        IMarketAnalyzer marketAnalyzer,
        IConfluenceMatrixEngine cmEngine,
        ITradeTimeoutEngine timeoutEngine,
        Microsoft.Extensions.Options.IOptions<TradingBotSettings> settings,
        ILogger<MarketAnalysisOrchestrator> logger
    )
    {
        _fetcher = fetcher;
        _riskGatekeeper = riskGatekeeper;
        _mathEngine = mathEngine;
        _marketAnalyzer = marketAnalyzer;
        _cmEngine = cmEngine;
        _timeoutEngine = timeoutEngine;
        _settings = settings.Value;
        _logger = logger;
    }

    private double GetSafeLimit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        if (value > 1e6) return 1e6;
        if (value < -1e6) return -1e6;
        return value;
    }

    public async Task<AnalysisResponseDto> ExecuteAnalysisAsync(string asset, string timeframe, ValutaBot.App.MiniApp.Data.Repositories.UserSettings? userSettings = null)
    {
        var sw = Stopwatch.StartNew();
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
        var traceLines = new List<string>();

        // Local helper for Data Integrity hash
        string ComputeHash(MiniAppController.OhlcCandle[] cands)
        {
            long sumBits = 0;
            foreach (var c in cands) sumBits ^= BitConverter.DoubleToInt64Bits(c.Close);
            return sumBits.ToString("X8").Substring(0, 8);
        }

        _logger.LogInformation("[TRACE {TraceId}] Analysis started for Asset: {Asset}, TF: {Timeframe}", traceId, asset, timeframe);

        // 1. Sanitize (Immutable Step)
        string cleanAsset = asset.Replace(" OTC", "").Replace("OTC", "").Trim();
        string clean = AssetSanitizer.Sanitize(cleanAsset);
        DayOfWeek day = DateTime.UtcNow.DayOfWeek;
        string? symbol = AssetSanitizer.MapSymbolByDayOfWeek(clean, day);
        bool isForex = AssetSanitizer.IsForexAsset(clean);
        
        string tfLower = timeframe.ToLower().Trim();
        int limit = (tfLower.StartsWith("s") || tfLower.StartsWith("m1") || tfLower.StartsWith("m5")) ? 160 : 200;

        // 2. Fetch Data (Locals only, no class fields)
        var fetchSw = Stopwatch.StartNew();
        var candles = await _fetcher.FetchOhlcWithFallbackAsync(symbol, timeframe, cleanAsset, limit);
        fetchSw.Stop();

        if (candles == null || candles.Length == 0)
        {
            _logger.LogWarning("[TRACE {TraceId}] Failed to fetch data after {Ms}ms", traceId, fetchSw.ElapsedMilliseconds);
            throw new Exception("Не удалось получить свечные данные от API.");
        }
        
        double[] mainPrices = candles.Select(c => c.Close).ToArray();
        double currentLivePrice = mainPrices[^1];
        string dataHash = ComputeHash(candles);
        traceLines.Add($"[1. Источник Данных] {candles.Length} свечей. Live Цена: {currentLivePrice} | Массив Hash: [{dataHash}] -> {fetchSw.ElapsedMilliseconds}ms");

        // Prepare closed candles
        int intervalSecs = _fetcher.TimeframeSeconds(timeframe);
        bool isLastClosed = intervalSecs > 0 && candles[^1].Timestamp.AddSeconds(intervalSecs) <= DateTime.UtcNow;
        var closedCandles = isLastClosed ? candles : candles.Take(candles.Length - 1).ToArray();
        double[] closedPrices = closedCandles.Select(c => c.Close).ToArray();
        double[] closedVolumes = closedCandles.Select(c => c.Volume).ToArray();

        // 3. Risk Gatekeeper
        var gatekeeperSw = Stopwatch.StartNew();
        var gatekeeper = _riskGatekeeper.ValidateMarketGatekeeper(cleanAsset, timeframe, mainPrices, candles);
        gatekeeperSw.Stop();
        
        if (!gatekeeper.IsTradeable)
        {
            _logger.LogWarning("[TRACE {TraceId}] Risk Gatekeeper blocked trade: {Reason}", traceId, gatekeeper.Reason);
            throw new Exception(gatekeeper.Reason);
        }
        traceLines.Add($"[2. Gatekeeper]      Риск-контроль пройден ({(string.IsNullOrEmpty(gatekeeper.Reason) ? "Волатильность в норме" : gatekeeper.Reason)}) -> {gatekeeperSw.ElapsedMilliseconds}ms");

        // 4. Continuous State
        var state = ContinuousStateEngine.EvaluateContinuousState(mainPrices, cleanAsset, timeframe);
        
        // 5. Higher TF Data
        string? higherTf = _fetcher.HigherTf(timeframe);
        MiniAppController.OhlcCandle[]? higherCandles = null;
        if (higherTf != null) {
            higherCandles = await _fetcher.FetchOhlcWithFallbackAsync(symbol, higherTf, cleanAsset, 100);
        }
        var closedHigherCandles = (higherCandles != null && higherCandles.Length > 1 && higherTf != null) 
            ? ((_fetcher.TimeframeSeconds(higherTf) > 0 && higherCandles[^1].Timestamp.AddSeconds(_fetcher.TimeframeSeconds(higherTf)) <= DateTime.UtcNow) ? higherCandles : higherCandles.Take(higherCandles.Length - 1).ToArray())
            : Array.Empty<MiniAppController.OhlcCandle>();

        // 6. Engines (Parallel)
        var engSw = Stopwatch.StartNew();
        var smcTask = Task.Run(() => SmcEngine.AnalyzeSmcStructure(cleanAsset, timeframe, candles, currentLivePrice));
        var ofTask = Task.Run(() => OrderFlowEngine.AnalyzeOrderFlow(cleanAsset, timeframe, closedCandles, currentLivePrice));
        
        // TA Scoring
        var taSw = Stopwatch.StartNew();
        var (mainAdx, mainPdi, mainMdi) = closedCandles.Length > 0 ? _mathEngine.ComputeTrueAdx(cleanAsset, timeframe, closedCandles) : (20.0, 0.0, 0.0);
        double mainAtr = closedCandles.Length > 0 ? _mathEngine.ComputeAtr(cleanAsset, timeframe, closedCandles) : 0;
        var taResult = _marketAnalyzer.ScoreTimeframe(cleanAsset, timeframe, closedPrices, closedVolumes, candles: closedCandles, adxOverride: mainAdx, atrOverride: mainAtr, isForex: isForex, pdiOverride: mainPdi, mdiOverride: mainMdi);
        taSw.Stop();
        traceLines.Add($"[3. Расчеты TA]      Индикаторы, ADX ({mainAdx:F1}) и ATR вычислены -> {taSw.ElapsedMilliseconds}ms");

        await Task.WhenAll(smcTask, ofTask);
        var smcResult = await smcTask;
        var ofResult = await ofTask;
        engSw.Stop();
        traceLines.Add($"[4. Структура]       SMC и OrderFlow отрисованы -> {engSw.ElapsedMilliseconds}ms");

        // ML
        var mlSw = Stopwatch.StartNew();
        var mlPrediction = await MLPythonService.PredictAsync(cleanAsset, timeframe, closedCandles, isForex, closedHigherCandles, smcResult, ofResult);
        string lgbmDir = "NEUTRAL";
        double lgbmConf = 0.5;
        if (mlPrediction != null) {
            lgbmDir = mlPrediction.Direction;
            lgbmConf = mlPrediction.Confidence;
        }
        mlSw.Stop();
        if (mlPrediction != null) {
            string hasOb = smcResult.OrderBlockType != null ? "Yes" : "No";
            string mlLgbmTrace = mlPrediction.TopFeatures != null && mlPrediction.TopFeatures.Count >= 3 
                ? $"Scenario (BOS={smcResult.BosDirection ?? "None"}, OB={hasOb}). Verdict: {lgbmDir} ({lgbmConf*100:F1}%) | Explainer: {mlPrediction.TopFeatures[0]}, {mlPrediction.TopFeatures[1]}, {mlPrediction.TopFeatures[2]}" 
                : $"Scenario (BOS={smcResult.BosDirection ?? "None"}, OB={hasOb}). Verdict: {lgbmDir} ({lgbmConf*100:F1}%)";
            traceLines.Add($"[5. PREDICT ML] {mlLgbmTrace} -> {mlSw.ElapsedMilliseconds}ms");
        } else {
            traceLines.Add($"[5. PREDICT ML] Python result (conf: {lgbmConf:F2}) -> {mlSw.ElapsedMilliseconds}ms");
        }
        
        // 7. Matrix & Consensus
        var matrixSw = Stopwatch.StartNew();
        double conflictPenalty = 1.0;
        if (closedHigherCandles.Length > 0 && higherTf != null) {
            var hAdx = _mathEngine.ComputeTrueAdx(cleanAsset, higherTf, closedHigherCandles);
            var hAtr = _mathEngine.ComputeAtr(cleanAsset, higherTf, closedHigherCandles);
            var hResult = _marketAnalyzer.ScoreTimeframe(cleanAsset, higherTf, closedHigherCandles.Select(c=>c.Close).ToArray(), closedHigherCandles.Select(c=>c.Volume).ToArray(), candles: closedHigherCandles, adxOverride: hAdx.adx, atrOverride: hAtr, isForex: isForex);
            conflictPenalty *= (taResult.score * hResult.score < -0.01) ? 0.7 : 1.0;
        }

        var mtfResult = await _cmEngine.Evaluate4DMatrixAsync(cleanAsset, timeframe, isForex, symbol, closedCandles, closedPrices, closedVolumes, closedHigherCandles, closedHigherCandles.Select(c=>c.Close).ToArray(), closedHigherCandles.Select(c=>c.Volume).ToArray());
        
        var taSignal = new TaSignal(taResult.score, taResult.confidence, taResult.rsiVal, taResult.hmaVal, taResult.volStrengthVal, mainAtr, mainAdx);
        var smcSignal = new SmcSignal(smcResult.BosDirection ?? "", smcResult.SweepDirection ?? "", smcResult.OrderBlockType ?? "", smcResult.FvgType ?? "", "");
        var ofSignal = new OrderflowSignal(ofResult.ScoreContribution, ofResult.Description);
        var mlSignal = new MlSignal(lgbmDir, lgbmConf, mlPrediction?.Accuracy, mlPrediction?.ModelVersion ?? "offline", mlPrediction?.HorizonCandles, mlPrediction?.RawConfidence);
        var stateSignal = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);

        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));
        matrixSw.Stop();
        traceLines.Add($"[6. Консенсус]       Матрица сведена (Фаза: {state.VelocityRegime ?? "UNKNOWN"}) -> {matrixSw.ElapsedMilliseconds}ms");

        // 8. Final Formatting & Data Integrity
        var dbSw = Stopwatch.StartNew();
        var timeout = _timeoutEngine.CalculateTimeout(cleanAsset, timeframe, mainAtr, 1.0, smcResult, currentLivePrice, state, isForex);

        string finalHash = ComputeHash(candles);
        if (finalHash == dataHash) {
            traceLines.Add($"[7. Data Integrity]  Проверка: Hash [{finalHash}] совпадает. Искажений нет. -> {dbSw.ElapsedMilliseconds}ms");
        } else {
            traceLines.Add($"[7. Data Integrity]  [ВНИМАНИЕ! ДАННЫЕ ИСКАЖЕНЫ] Ожидался {dataHash}, получен {finalHash} -> {dbSw.ElapsedMilliseconds}ms");
        }

        // FIX: Передаём направления каждого источника для per-source калибровки
        var sourceDirections = new Dictionary<string, string>
        {
            ["TechAnalysis"] = DirectionExtensions.FromScore(consensus.TaScore).ToSignal(),
            ["OrderFlow"]    = DirectionExtensions.FromScore(consensus.OfScore, 0.05).ToSignal(),
            ["SMC"]          = DirectionExtensions.FromScore(consensus.SmcScore).ToSignal(),
            ["LIGHTGBM"]     = lgbmDir,
        };

        // RECORD (Fire and forget)
        int targetHorizon = timeout.TimeoutCandles;
        _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw);
        dbSw.Stop();
        traceLines.Add($"[8. База данных]     Записан Entry Price: {currentLivePrice} (Ожидание экспирации: {targetHorizon} свечей) -> {dbSw.ElapsedMilliseconds}ms");

        sw.Stop();
        
        // Print Pipeline Trace Block
        var sbTrace = new System.Text.StringBuilder();
        sbTrace.AppendLine("=================================================");
        sbTrace.AppendLine($"[PIPELINE TRACE: {traceId}] Запрос сигнала: {cleanAsset} {timeframe}");
        foreach (var line in traceLines) {
            sbTrace.AppendLine(line);
        }
        sbTrace.AppendLine("-------------------------------------------------");
        sbTrace.AppendLine($"ИТОГ: Время: {sw.ElapsedMilliseconds}ms | Результат: {consensus.FinalDirection} {consensus.Probability}%");
        sbTrace.AppendLine("=================================================");
        Console.WriteLine(sbTrace.ToString());

        var stats = await SignalTracker.GetOverallStatsAsync();
        var assetStats = await SignalTracker.GetStatsAsync(cleanAsset, timeframe);

        string uiMarketSession = "ВНЕБИРЖЕВАЯ (OTC)";
        if (!cleanAsset.Contains("BTC") && !cleanAsset.Contains("ETH") && !cleanAsset.Contains("SOL"))
        {
            int h = DateTime.UtcNow.Hour;
            if (h >= 21 || h < 2) uiMarketSession = "Ночь (Тихий рынок)";
            else if (h >= 2 && h < 8) uiMarketSession = "Азия (Пила)";
            else if (h >= 8 && h < 13) uiMarketSession = "Лондон (Начало)";
            else if (h >= 13 && h < 17) uiMarketSession = "Нью-Йорк (Объемы)";
            else if (h >= 17 && h < 21) uiMarketSession = "Нью-Йорк (Вечер)";
        }
        else 
        {
            uiMarketSession = "КРИПТО";
        }

        string uiMarketPhase = "Боковик (Флэт)";
        string regime = state.VelocityRegime ?? "";
        if (regime.Contains("UP")) uiMarketPhase = "Бычий импульс";
        else if (regime.Contains("DOWN")) uiMarketPhase = "Медвежий импульс";
        else if (regime == "DECELERATING") uiMarketPhase = "Замедление (Коррекция)";
        else if (taResult.rsiVal >= 62) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Перекупленность (Сброс)" : "Бычий тренд (Пологий)";
        else if (taResult.rsiVal <= 38) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Перепроданность (Отскок)" : "Медвежий тренд (Пологий)";
        else if (taResult.rsiVal >= 54) uiMarketPhase = "Умеренный рост";
        else if (taResult.rsiVal <= 46) uiMarketPhase = "Умеренное падение";
        else uiMarketPhase = "Истинный Флэт";

        string uiMarketEntropy = "В норме (Безопасно)";
        double vel = Math.Abs(state.VelocityBpsPerSec);
        bool isSub = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase);
        double dangerVel = isSub ? 0.3 : 3.0; 
        double deadVel   = isSub ? 0.02 : 0.1;

        if (vel >= dangerVel) uiMarketEntropy = "ВЫСОКАЯ (Хаос / Опасно!)";
        else if (vel < deadVel) uiMarketEntropy = "Мертвый рынок";

        return new AnalysisResponseDto
        {
            tfConflict = conflictPenalty < 1.0,
            uiMarketSession = uiMarketSession,
            uiMarketPhase = uiMarketPhase,
            uiMarketEntropy = uiMarketEntropy,
            direction = consensus.FinalDirection,
            probability = consensus.Probability,
            duration = timeout.TimeoutText,
            expiryCandles = timeout.TimeoutCandles,
            adaptiveReasoning = consensus.CombinedReasoningText,
            taDirection = consensus.TaScore > 0.02 ? "BUY" : consensus.TaScore < -0.02 ? "PUT" : "NEUTRAL",
            taConfidence = (int)Math.Clamp(Math.Abs(consensus.TaScore * 100), 0, 100),
            ofDirection = ofSignal.ScoreContribution > 0.02 ? "BUY" : ofSignal.ScoreContribution < -0.02 ? "PUT" : "NEUTRAL",
            ofConfidence = (int)Math.Clamp(Math.Abs(ofSignal.ScoreContribution * 100), 0, 100),
            smcDirection = 
                (smcSignal.SweepDirection ?? "").Contains("BULLISH") ? "BUY" : 
                (smcSignal.SweepDirection ?? "").Contains("BEARISH") ? "PUT" : 
                (smcSignal.BosDirection ?? "").Contains("BULLISH") ? "BUY" : 
                (smcSignal.BosDirection ?? "").Contains("BEARISH") ? "PUT" : 
                (smcSignal.OrderBlockType ?? "").Contains("BULLISH") ? "BUY" : 
                (smcSignal.OrderBlockType ?? "").Contains("BEARISH") ? "PUT" : 
                (smcSignal.FvgType ?? "").Contains("BULLISH") ? "BUY" : 
                (smcSignal.FvgType ?? "").Contains("BEARISH") ? "PUT" : 
                "NEUTRAL",
            smcConfidence = (int)Math.Clamp(Math.Abs(consensus.SmcScore * 100), 0, 100),
            lgbmDirection = mlSignal.Direction,
            lgbmConfidence = (int)(mlSignal.Confidence * 100),
            winRateOverall = stats.WinRate,
            winRateAsset = assetStats.WinRate,
            signalsVerifiedAsset = assetStats.Verified,
            // TA indicators for UI display (resRsi, resEma, resVol elements)
            rsi = Math.Round(GetSafeLimit(taResult.rsiVal), 1),
            ema = Math.Round(GetSafeLimit(taResult.hmaVal), isForex ? 5 : 2),
            volumeStrength = Math.Round(GetSafeLimit(taResult.volStrengthVal), 2),
            atr = Math.Round(GetSafeLimit(mainAtr), isForex ? 5 : 2),
            chartData = mainPrices,
            chartOhlc = candles.TakeLast(80).Select(c => new { o = Math.Round(c.Open, 8), h = Math.Round(c.High, 8), l = Math.Round(c.Low, 8), c = Math.Round(c.Close, 8), v = Math.Round(c.Volume, 2) }).ToArray(),
            goldenSetup = mtfResult.IsGoldenSetup,
            confluenceLabel = mtfResult.ConfluenceLabel,
            confluenceRatio = mtfResult.ConfluenceRatio
        };
    }
}


