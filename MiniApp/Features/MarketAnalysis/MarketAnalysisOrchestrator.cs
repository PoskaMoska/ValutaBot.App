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
    private readonly IMonteCarloEngine _mcEngine;
    private readonly TradingBotSettings _settings;
    private readonly ILogger<MarketAnalysisOrchestrator> _logger;

    public MarketAnalysisOrchestrator(
        MarketDataFetcher fetcher,
        IRiskGatekeeper riskGatekeeper,
        IMathEngine mathEngine,
        IMarketAnalyzer marketAnalyzer,
        IConfluenceMatrixEngine cmEngine,
        ITradeTimeoutEngine timeoutEngine,
        IMonteCarloEngine mcEngine,
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
        _mcEngine = mcEngine;
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
        if (candles == null || candles.Length == 0)
        {
            _logger.LogWarning("[TRACE {TraceId}] Failed to fetch data after {Ms}ms", traceId, fetchSw.ElapsedMilliseconds);
            throw new Exception("Не удалось получить данные от API.");
        }
        _logger.LogInformation("[TRACE {TraceId}] Data fetched: {Count} candles in {Ms}ms", traceId, candles.Length, fetchSw.ElapsedMilliseconds);

        double[] mainPrices = candles.Select(c => c.Close).ToArray();
        double currentLivePrice = mainPrices[^1];
        
        // Prepare closed candles
        int intervalSecs = _fetcher.TimeframeSeconds(timeframe);
        bool isLastClosed = candles[^1].Timestamp.AddSeconds(intervalSecs) <= DateTime.UtcNow;
        var closedCandles = isLastClosed ? candles : candles.Take(candles.Length - 1).ToArray();
        double[] closedPrices = closedCandles.Select(c => c.Close).ToArray();
        double[] closedVolumes = closedCandles.Select(c => c.Volume).ToArray();

        // 3. Risk Gatekeeper
        var gatekeeper = _riskGatekeeper.ValidateMarketGatekeeper(cleanAsset, timeframe, mainPrices, candles);
        if (!gatekeeper.IsTradeable)
        {
            _logger.LogWarning("[TRACE {TraceId}] Risk Gatekeeper blocked trade: {Reason}", traceId, gatekeeper.Reason);
            throw new Exception(gatekeeper.Reason);
        }

        // 4. Continuous State
        var state = ContinuousStateEngine.EvaluateContinuousState(mainPrices, cleanAsset, timeframe);
        
        // 5. Higher TF Data
        string? higherTf = _fetcher.HigherTf(timeframe);
        MiniAppController.OhlcCandle[]? higherCandles = null;
        if (higherTf != null) {
            higherCandles = await _fetcher.FetchOhlcWithFallbackAsync(symbol, higherTf, cleanAsset, 100);
        }
        var closedHigherCandles = (higherCandles != null && higherCandles.Length > 1) 
            ? (higherCandles[^1].Timestamp.AddSeconds(_fetcher.TimeframeSeconds(higherTf)) <= DateTime.UtcNow ? higherCandles : higherCandles.Take(higherCandles.Length - 1).ToArray())
            : Array.Empty<MiniAppController.OhlcCandle>();

        // 6. Engines (Parallel)
        var engSw = Stopwatch.StartNew();
        var smcTask = Task.Run(() => SmcEngine.AnalyzeSmcStructure(cleanAsset, timeframe, candles, currentLivePrice));
        var ofTask = Task.Run(() => OrderFlowEngine.AnalyzeOrderFlow(cleanAsset, timeframe, closedCandles, currentLivePrice));
        // TA Scoring
        var (mainAdx, mainPdi, mainMdi) = closedCandles.Length > 0 ? _mathEngine.ComputeTrueAdx(cleanAsset, timeframe, closedCandles) : (20.0, 0.0, 0.0);
        double mainAtr = closedCandles.Length > 0 ? _mathEngine.ComputeAtr(cleanAsset, timeframe, closedCandles) : 0;
        var taResult = _marketAnalyzer.ScoreTimeframe(cleanAsset, timeframe, closedPrices, closedVolumes, candles: closedCandles, adxOverride: mainAdx, atrOverride: mainAtr, isForex: isForex, pdiOverride: mainPdi, mdiOverride: mainMdi);

        // ML
        var mlPrediction = await MLPythonService.PredictAsync(cleanAsset, timeframe, closedCandles, isForex, closedHigherCandles);
        string lgbmDir = "NEUTRAL";
        double lgbmConf = 0.5;
        if (mlPrediction != null) {
            lgbmDir = mlPrediction.Direction;
            lgbmConf = mlPrediction.Confidence;
        }

        await Task.WhenAll(smcTask, ofTask);
        var smcResult = await smcTask;
        var ofResult = await ofTask;
        
        _logger.LogInformation("[TRACE {TraceId}] Engines finished in {Ms}ms. TA={TAScore:F2}, SMC={SMCDir}, OF={OFScore:F2}, ML={MLDir}({MLConf:F0}%)", 
            traceId, engSw.ElapsedMilliseconds, taResult.score, smcResult.BosDirection, ofResult.ScoreContribution, lgbmDir, lgbmConf);

        // 7. Matrix & Consensus
        double conflictPenalty = 1.0;
        if (closedHigherCandles.Length > 0 && higherTf != null) {
            var hAdx = _mathEngine.ComputeTrueAdx(cleanAsset, higherTf, closedHigherCandles);
            var hAtr = _mathEngine.ComputeAtr(cleanAsset, higherTf, closedHigherCandles);
            var hResult = _marketAnalyzer.ScoreTimeframe(cleanAsset, higherTf, closedHigherCandles.Select(c=>c.Close).ToArray(), closedHigherCandles.Select(c=>c.Volume).ToArray(), candles: closedHigherCandles, adxOverride: hAdx.adx, atrOverride: hAtr, isForex: isForex);
            conflictPenalty *= (taResult.score * hResult.score < -0.01) ? 0.7 : 1.0;
        }

        var mtfResult = await _cmEngine.Evaluate4DMatrixAsync(cleanAsset, timeframe, isForex, symbol, closedCandles, closedPrices, closedVolumes, closedHigherCandles, closedHigherCandles.Select(c=>c.Close).ToArray(), closedHigherCandles.Select(c=>c.Volume).ToArray());
        
        var taSignal = new TaSignal(taResult.score, taResult.confidence, taResult.rsiVal, taResult.hmaVal, taResult.volStrengthVal, mainAtr, mainAdx);
        var smcSignal = new SmcSignal(smcResult.BosDirection, smcResult.SweepDirection, smcResult.OrderBlockType, smcResult.FvgType, "");
        var ofSignal = new OrderflowSignal(ofResult.ScoreContribution, ofResult.Description);
        var mlSignal = new MlSignal(lgbmDir, lgbmConf, mlPrediction?.Accuracy, mlPrediction?.ModelVersion ?? "offline");
        var stateSignal = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);

        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));

        _logger.LogInformation("[TRACE {TraceId}] Consensus reached: {Dir} ({Prob}%). Total pipeline time: {Ms}ms", 
            traceId, consensus.FinalDirection, consensus.Probability, sw.ElapsedMilliseconds);

        // 8. Final Formatting & UI Fix
        var timeout = _timeoutEngine.CalculateTimeout(cleanAsset, timeframe, mainAtr, 1.0, smcResult, currentLivePrice, state, isForex);
        var mc = new MonteCarloResult(1000, 0, 0, 0, "", "", ""); // Placeholder

        // FIX: Передаём направления каждого источника для per-source калибровки
        var sourceDirections = new Dictionary<string, string>
        {
            ["TechAnalysis"] = DirectionExtensions.FromScore(consensus.TaScore).ToSignal(),
            ["OrderFlow"]    = DirectionExtensions.FromScore(consensus.OfScore, 0.05).ToSignal(),
            ["SMC"]          = DirectionExtensions.FromScore(consensus.SmcScore).ToSignal(),
            ["LIGHTGBM"]     = lgbmDir,
        };

        // RECORD (Fire and forget)
        _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, timeout.TimeoutCandles, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw);

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
        if (regime.Contains("UP")) uiMarketPhase = "Бычий импульс (Резкий)";
        else if (regime.Contains("DOWN")) uiMarketPhase = "Медвежий импульс (Резкий)";
        else if (regime == "DECELERATING") uiMarketPhase = "Замедление (Разворот)";
        else if (taResult.rsiVal > 62) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Перекупленность (Откат)" : "Бычий тренд (Плавный)";
        else if (taResult.rsiVal < 38) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Перепроданность (Отскок)" : "Медвежий тренд (Плавный)";

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


