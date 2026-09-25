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
            throw new Exception("РќРµ СѓРґР°Р»РѕСЃСЊ РїРѕР»СѓС‡РёС‚СЊ СЃРІРµС‡РЅС‹Рµ РґР°РЅРЅС‹Рµ РѕС‚ API.");
        }
        
        double[] mainPrices = candles.Select(c => c.Close).ToArray();
        double currentLivePrice = mainPrices[^1];
        string dataHash = ComputeHash(candles);
        traceLines.Add($"[1. РСЃС‚РѕС‡РЅРёРє Р”Р°РЅРЅС‹С…] {candles.Length} СЃРІРµС‡РµР№. Live Р¦РµРЅР°: {currentLivePrice} | РњР°СЃСЃРёРІ Hash: [{dataHash}] -> {fetchSw.ElapsedMilliseconds}ms");

        // Prepare closed candles
        int intervalSecs = _fetcher.TimeframeSeconds(timeframe);
        bool isLastClosed = candles[^1].Timestamp.AddSeconds(intervalSecs) <= DateTime.UtcNow;
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
        traceLines.Add($"[2. Gatekeeper]      Р РёСЃРє-РєРѕРЅС‚СЂРѕР»СЊ РїСЂРѕР№РґРµРЅ ({(string.IsNullOrEmpty(gatekeeper.Reason) ? "Р’РѕР»Р°С‚РёР»СЊРЅРѕСЃС‚СЊ РІ РЅРѕСЂРјРµ" : gatekeeper.Reason)}) -> {gatekeeperSw.ElapsedMilliseconds}ms");

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
        var taSw = Stopwatch.StartNew();
        var (mainAdx, mainPdi, mainMdi) = closedCandles.Length > 0 ? _mathEngine.ComputeTrueAdx(cleanAsset, timeframe, closedCandles) : (20.0, 0.0, 0.0);
        double mainAtr = closedCandles.Length > 0 ? _mathEngine.ComputeAtr(cleanAsset, timeframe, closedCandles) : 0;
        var taResult = _marketAnalyzer.ScoreTimeframe(cleanAsset, timeframe, closedPrices, closedVolumes, candles: closedCandles, adxOverride: mainAdx, atrOverride: mainAtr, isForex: isForex, pdiOverride: mainPdi, mdiOverride: mainMdi);
        taSw.Stop();
        traceLines.Add($"[3. Р Р°СЃС‡РµС‚С‹ TA]      РРЅРґРёРєР°С‚РѕСЂС‹, ADX ({mainAdx:F1}) Рё ATR РІС‹С‡РёСЃР»РµРЅС‹ -> {taSw.ElapsedMilliseconds}ms");

        await Task.WhenAll(smcTask, ofTask);
        var smcResult = await smcTask;
        var ofResult = await ofTask;
        engSw.Stop();
        traceLines.Add($"[4. РЎС‚СЂСѓРєС‚СѓСЂР°]       SMC Рё OrderFlow РѕС‚СЂРёСЃРѕРІР°РЅС‹ -> {engSw.ElapsedMilliseconds}ms");

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
        if (mlPrediction != null) { traceLines.Add($"[5. ПРЕДИКТ ML] Сценарий (Слом={smcResult.BosDirection}, OB={(smcResult.OrderBlockType != null ? ""Да"" : ""Нет"")}). Вердикт: {lgbmDir} ({lgbmConf*100:F1}%) -> {mlSw.ElapsedMilliseconds}ms"); } else { traceLines.Add($"[5. ПРЕДИКТ ML] Python выдал ответ (уверенность: {lgbmConf:F2}) -> {mlSw.ElapsedMilliseconds}ms"); }
        
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
        var smcSignal = new SmcSignal(smcResult.BosDirection, smcResult.SweepDirection, smcResult.OrderBlockType, smcResult.FvgType, "");
        var ofSignal = new OrderflowSignal(ofResult.ScoreContribution, ofResult.Description);
        var mlSignal = new MlSignal(lgbmDir, lgbmConf, mlPrediction?.Accuracy, mlPrediction?.ModelVersion ?? "offline", mlPrediction?.HorizonCandles, mlPrediction?.RawConfidence);
        var stateSignal = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);

        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));
        matrixSw.Stop();
        traceLines.Add($"[6. РљРѕРЅСЃРµРЅСЃСѓСЃ]       РњР°С‚СЂРёС†Р° СЃРІРµРґРµРЅР° (Р¤Р°Р·Р°: {state.VelocityRegime ?? "UNKNOWN"}) -> {matrixSw.ElapsedMilliseconds}ms");

        // 8. Final Formatting & Data Integrity
        var dbSw = Stopwatch.StartNew();
        var timeout = _timeoutEngine.CalculateTimeout(cleanAsset, timeframe, mainAtr, 1.0, smcResult, currentLivePrice, state, isForex);

        string finalHash = ComputeHash(candles);
        if (finalHash == dataHash) {
            traceLines.Add($"[7. Data Integrity]  РџСЂРѕРІРµСЂРєР°: Hash [{finalHash}] СЃРѕРІРїР°РґР°РµС‚. РСЃРєР°Р¶РµРЅРёР№ РЅРµС‚. -> {dbSw.ElapsedMilliseconds}ms");
        } else {
            traceLines.Add($"[7. Data Integrity]  [Р’РќРРњРђРќРР•! Р”РђРќРќР«Р• РРЎРљРђР–Р•РќР«] РћР¶РёРґР°Р»СЃСЏ {dataHash}, РїРѕР»СѓС‡РµРЅ {finalHash} -> {dbSw.ElapsedMilliseconds}ms");
        }

        // FIX: РџРµСЂРµРґР°С‘Рј РЅР°РїСЂР°РІР»РµРЅРёСЏ РєР°Р¶РґРѕРіРѕ РёСЃС‚РѕС‡РЅРёРєР° РґР»СЏ per-source РєР°Р»РёР±СЂРѕРІРєРё
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
        traceLines.Add($"[8. Р‘Р°Р·Р° РґР°РЅРЅС‹С…]     Р—Р°РїРёСЃР°РЅ Entry Price: {currentLivePrice} (РћР¶РёРґР°РЅРёРµ СЌРєСЃРїРёСЂР°С†РёРё: {targetHorizon} СЃРІРµС‡РµР№) -> {dbSw.ElapsedMilliseconds}ms");

        sw.Stop();
        
        // Print Pipeline Trace Block
        var sbTrace = new System.Text.StringBuilder();
        sbTrace.AppendLine("=================================================");
        sbTrace.AppendLine($"[PIPELINE TRACE: {traceId}] Р—Р°РїСЂРѕСЃ СЃРёРіРЅР°Р»Р°: {cleanAsset} {timeframe}");
        foreach (var line in traceLines) {
            sbTrace.AppendLine(line);
        }
        sbTrace.AppendLine("-------------------------------------------------");
        sbTrace.AppendLine($"РРўРћР“: Р’СЂРµРјСЏ: {sw.ElapsedMilliseconds}ms | Р РµР·СѓР»СЊС‚Р°С‚: {consensus.FinalDirection} {consensus.Probability}%");
        sbTrace.AppendLine("=================================================");
        Console.WriteLine(sbTrace.ToString());

        var stats = await SignalTracker.GetOverallStatsAsync();
        var assetStats = await SignalTracker.GetStatsAsync(cleanAsset, timeframe);

        string uiMarketSession = "Р’РќР•Р‘РР Р–Р•Р’РђРЇ (OTC)";
        if (!cleanAsset.Contains("BTC") && !cleanAsset.Contains("ETH") && !cleanAsset.Contains("SOL"))
        {
            int h = DateTime.UtcNow.Hour;
            if (h >= 21 || h < 2) uiMarketSession = "РќРѕС‡СЊ (РўРёС…РёР№ СЂС‹РЅРѕРє)";
            else if (h >= 2 && h < 8) uiMarketSession = "РђР·РёСЏ (РџРёР»Р°)";
            else if (h >= 8 && h < 13) uiMarketSession = "Р›РѕРЅРґРѕРЅ (РќР°С‡Р°Р»Рѕ)";
            else if (h >= 13 && h < 17) uiMarketSession = "РќСЊСЋ-Р™РѕСЂРє (РћР±СЉРµРјС‹)";
            else if (h >= 17 && h < 21) uiMarketSession = "РќСЊСЋ-Р™РѕСЂРє (Р’РµС‡РµСЂ)";
        }
        else 
        {
            uiMarketSession = "РљР РРџРўРћ";
        }

        string uiMarketPhase = "Р‘РѕРєРѕРІРёРє (Р¤Р»СЌС‚)";
        string regime = state.VelocityRegime ?? "";
        if (regime.Contains("UP")) uiMarketPhase = "Р‘С‹С‡РёР№ РёРјРїСѓР»СЊСЃ";
        else if (regime.Contains("DOWN")) uiMarketPhase = "РњРµРґРІРµР¶РёР№ РёРјРїСѓР»СЊСЃ";
        else if (regime == "DECELERATING") uiMarketPhase = "Р—Р°РјРµРґР»РµРЅРёРµ (РљРѕСЂСЂРµРєС†РёСЏ)";
        else if (taResult.rsiVal >= 62) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "РџРµСЂРµРєСѓРїР»РµРЅРЅРѕСЃС‚СЊ (РЎР±СЂРѕСЃ)" : "Р‘С‹С‡РёР№ С‚СЂРµРЅРґ (РџРѕР»РѕРіРёР№)";
        else if (taResult.rsiVal <= 38) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "РџРµСЂРµРїСЂРѕРґР°РЅРЅРѕСЃС‚СЊ (РћС‚СЃРєРѕРє)" : "РњРµРґРІРµР¶РёР№ С‚СЂРµРЅРґ (РџРѕР»РѕРіРёР№)";
        else if (taResult.rsiVal >= 54) uiMarketPhase = "РЈРјРµСЂРµРЅРЅС‹Р№ СЂРѕСЃС‚";
        else if (taResult.rsiVal <= 46) uiMarketPhase = "РЈРјРµСЂРµРЅРЅРѕРµ РїР°РґРµРЅРёРµ";
        else uiMarketPhase = "РСЃС‚РёРЅРЅС‹Р№ Р¤Р»СЌС‚";

        string uiMarketEntropy = "Р’ РЅРѕСЂРјРµ (Р‘РµР·РѕРїР°СЃРЅРѕ)";
        double vel = Math.Abs(state.VelocityBpsPerSec);
        bool isSub = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase);
        double dangerVel = isSub ? 0.3 : 3.0; 
        double deadVel   = isSub ? 0.02 : 0.1;

        if (vel >= dangerVel) uiMarketEntropy = "Р’Р«РЎРћРљРђРЇ (РҐР°РѕСЃ / РћРїР°СЃРЅРѕ!)";
        else if (vel < deadVel) uiMarketEntropy = "РњРµСЂС‚РІС‹Р№ СЂС‹РЅРѕРє";

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


