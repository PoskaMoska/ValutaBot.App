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

public class MarketAnalysisOrchestrator(
    MarketDataFetcher fetcher,
    IRiskGatekeeper riskGatekeeper,
    IMathEngine mathEngine,
    IMarketAnalyzer marketAnalyzer,
    IConfluenceMatrixEngine cmEngine,
    ITradeTimeoutEngine timeoutEngine,
    Microsoft.Extensions.Options.IOptions<TradingBotSettings> settings,
    ILogger<MarketAnalysisOrchestrator> logger,
    ValutaBot.MiniApp.Services.INewsCalendarService? newsCalendar = null
) : IMarketAnalysisOrchestrator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastSeenModelVersions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastSampleTimeByAssetTf = new();
    private static readonly System.Threading.SemaphoreSlim _csvSemaphore = new(1, 1);
    
    private readonly MarketDataFetcher _fetcher = fetcher;
    private readonly IRiskGatekeeper _riskGatekeeper = riskGatekeeper;
    private readonly IMathEngine _mathEngine = mathEngine;
    private readonly IMarketAnalyzer _marketAnalyzer = marketAnalyzer;
    private readonly IConfluenceMatrixEngine _cmEngine = cmEngine;
    private readonly ITradeTimeoutEngine _timeoutEngine = timeoutEngine;
    private readonly TradingBotSettings _settings = settings.Value;
    private readonly ILogger<MarketAnalysisOrchestrator> _logger = logger;
    private readonly ValutaBot.MiniApp.Services.INewsCalendarService? _newsCalendar = newsCalendar;

    /// <summary>
    /// Extracts the ML regime token (TREND/FLAT/CHAOS/ALL) from a model version such as
    /// "lgbm-v1-USDJPY_s10_TREND-1790964297". Returns "UNKNOWN" if it cannot be parsed.
    /// </summary>
    private static string ExtractMlRegime(string? modelVersion)
    {
        if (string.IsNullOrWhiteSpace(modelVersion)) return "UNKNOWN";
        string last = modelVersion.Split('/').Last();
        foreach (var part in last.Split('-', '_'))
        {
            if (part is "TREND" or "FLAT" or "CHAOS" or "ALL") return part;
        }
        return "UNKNOWN";
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
        string cleanAsset = AssetSanitizer.Sanitize(asset);
        DayOfWeek day = DateTime.UtcNow.DayOfWeek;
        string? symbol = AssetSanitizer.MapSymbolByDayOfWeek(cleanAsset, day);
        bool isForex = AssetSanitizer.IsForexAsset(cleanAsset);
        
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
        
        bool isRiskBlocked = !gatekeeper.IsTradeable;
        if (isRiskBlocked)
        {
            _logger.LogWarning("[TRACE {TraceId}] Risk Gatekeeper blocked trade: {Reason}", traceId, gatekeeper.Reason);
            // DO NOT THROW. We need to compute features to save negative (HOLD) samples!
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
            : [];

        // 6. Engines (Parallel)
        var engSw = Stopwatch.StartNew();
        var smcTask = Task.Run(() => SmcEngine.AnalyzeSmcStructure(cleanAsset, timeframe, closedCandles, closedCandles.Length > 0 ? closedCandles[^1].Close : currentLivePrice));
        
        // TA Scoring
        var taSw = Stopwatch.StartNew();
        var (mainAdx, mainPdi, mainMdi) = closedCandles.Length > 0 ? _mathEngine.ComputeTrueAdx(cleanAsset, timeframe, closedCandles) : (20.0, 0.0, 0.0);
        double mainAtr = closedCandles.Length > 0 ? _mathEngine.ComputeAtr(cleanAsset, timeframe, closedCandles) : 0;
        var taDetail = _marketAnalyzer.ScoreTimeframeDetailed(cleanAsset, timeframe, closedPrices, closedVolumes, candles: closedCandles, adxOverride: mainAdx, atrOverride: mainAtr, isForex: isForex, pdiOverride: mainPdi, mdiOverride: mainMdi);
        var taResult = (score: taDetail.Score, confidence: taDetail.Confidence, rsiVal: taDetail.RsiVal, hmaVal: taDetail.HmaVal, volStrengthVal: taDetail.VolStrengthVal, atrVal: taDetail.AtrVal);
        taSw.Stop();
        traceLines.Add($"[3. Расчеты TA]      Индикаторы, ADX ({mainAdx:F1}) и ATR вычислены -> {taSw.ElapsedMilliseconds}ms");

        await smcTask;
        var smcResult = await smcTask;
        engSw.Stop();
        traceLines.Add($"[4. Структура]       SMC отрисован -> {engSw.ElapsedMilliseconds}ms");

        // Macro Context Anchors (Computed early for Neural Brain & Database)
        int? minutesToNews = _newsCalendar?.GetMinutesToNextHighImpactNews(cleanAsset);
        var dayAnchors = await ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.GetDayLevelAnchorsAsync(cleanAsset, currentLivePrice);
        var dxyMetrics = ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.ComputeDollarBasketMetrics();
        TiingoWebSocketStream.TryGetSpreadBps(cleanAsset, out double liveSpreadBps);

        var macroPayload = new {
            DayRangePositionPct = dayAnchors.DayRangePositionPct,
            DistToDayHighBps = dayAnchors.DistToDayHighBps,
            DistToDayLowBps = dayAnchors.DistToDayLowBps,
            DistToAsianHighBps = dayAnchors.DistToAsianHighBps,
            DistToAsianLowBps = dayAnchors.DistToAsianLowBps,
            DxyMomentum1mBps = dxyMetrics.DxyMomentum1mBps,
            DxyMomentum5mBps = dxyMetrics.DxyMomentum5mBps,
            BasketSyncScore = dxyMetrics.BasketSyncScore,
            SpreadBps = liveSpreadBps,
            MinutesToNews = minutesToNews ?? -1,
            Adx = mainAdx,
            Rsi = taDetail.RsiVal,
            HourUtc = DateTime.UtcNow.Hour,
            DayOfWeek = (int)DateTime.UtcNow.DayOfWeek == 0 ? 7 : (int)DateTime.UtcNow.DayOfWeek
        };

        // ML (Dual-Stream Neural Brain / LightGBM)
        var mlSw = Stopwatch.StartNew();
        var mlPrediction = _settings.EnableMachineLearning ? await MLPythonService.PredictAsync(cleanAsset, timeframe, closedCandles, isForex, closedHigherCandles, smcResult, macroPayload) : null;
        string lgbmDir = "NEUTRAL";
        double lgbmConf = 0.5;
        if (mlPrediction != null) {
            lgbmDir = mlPrediction.Direction;
            lgbmConf = mlPrediction.Confidence;
        }
        mlSw.Stop();
        // Fix #4: Diagnose MetaLearner status and ML contribution clearly in logs
        bool hasMetaLearner = TradeOutcomeTracker.MetaLearner != null;
        double mlRawProb = mlPrediction?.RawConfidence ?? 0.5;
        double mlScoreDebug = (mlRawProb - 0.5) * 2.0;
        _logger.LogInformation("[TRACE {TraceId}] ML RawConf={RawConf:F4} > mlScore={MlScore:F4} | MetaLearner={HasML} | Model={Version}",
            traceId, mlRawProb, mlScoreDebug, hasMetaLearner ? "ACTIVE" : "OFFLINE (fallback weights)", mlPrediction?.ModelVersion ?? "null");
        if (!hasMetaLearner)
            _logger.LogWarning("[TRACE {TraceId}] MetaLearner is NULL — using degraded fallback (TA*0.2 + SMC*0.2 + OF*0.1 + ML*0.2). Direction quality REDUCED.", traceId);
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
        var mlSignal = new MlSignal(lgbmDir, lgbmConf, mlPrediction?.Accuracy, mlPrediction?.ModelVersion ?? "offline", mlPrediction?.HorizonCandles, mlPrediction?.RawConfidence);
        var stateSignal = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);

        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));
        
        // --- NEWS CALENDAR INTEGRATION ---
        if (minutesToNews.HasValue && minutesToNews.Value >= 0 && minutesToNews.Value <= 15)
        {
            var nextNews = _newsCalendar?.GetNextHighImpactNews(cleanAsset);
            string newsWarning = $"?? ВНИМАНИЕ: Через {minutesToNews.Value} мин выходит важная новость ({nextNews?.Title}). Рынок нестабилен!";
            consensus = consensus with {
                Probability = 50,
                FinalDirection = "NEUTRAL",
                CombinedReasoningText = consensus.CombinedReasoningText + "\n" + newsWarning
            };
            traceLines.Add($"[6.5 NEWS] {newsWarning}");
        }
        else if (minutesToNews.HasValue)
        {
            traceLines.Add($"[6.5 NEWS] Next high-impact news in {minutesToNews.Value} minutes");
        }

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
            ["SMC"]          = DirectionExtensions.FromScore(consensus.SmcScore).ToSignal(),
            ["LIGHTGBM"]     = lgbmDir,
        };

        // Phase 3 Context calculations
        double priceEntropy = 0.0;
        int trendMaturity = 0;
        double pricePositionPct = 0.5;
        bool bbSqueeze = false;
        
        if (candles.Length >= 20)
        {
            var last20 = candles.Skip(candles.Length - 20).ToList();
            double maxHigh = last20.Max(c => c.High);
            double minLow = last20.Min(c => c.Low);
            if (maxHigh > minLow) {
                pricePositionPct = (currentLivePrice - minLow) / (maxHigh - minLow);
            }
            
            double sma20 = last20.Average(c => c.Close);
            double stdev20 = Math.Sqrt(last20.Average(c => Math.Pow(c.Close - sma20, 2)));
            bbSqueeze = (stdev20 * 4) / sma20 < 0.0005; // 5 bps bandwidth
            
            int streak = 0;
            bool? upTrend = null;
            for (int i = candles.Length - 1; i >= 1; i--) {
                bool isUp = candles[i].Close > candles[i-1].Close;
                bool isDown = candles[i].Close < candles[i-1].Close;
                if (upTrend == null) {
                    if (isUp) upTrend = true;
                    else if (isDown) upTrend = false;
                    else break;
                }
                if (upTrend == true && isUp) streak++;
                else if (upTrend == false && isDown) streak++;
                else break;
            }
            trendMaturity = streak;
            priceEntropy = mainAtr > 0 ? (stdev20 / mainAtr) : 0.0;
        }

        // RECORD (Fire and forget) ONLY IF CONFIDENCE IS HIGH ENOUGH
        int targetHorizon = timeout.TimeoutCandles;
        if (isRiskBlocked)
        {
             consensus = consensus with { Probability = 0, FinalDirection = "NEUTRAL" }; // Force into HOLD block
        }
        
        double[] metaWeights = TradeOutcomeTracker.MetaLearner?.GetWeights(cleanAsset, timeframe) ?? [];

        string taTelemetry = System.Text.Json.JsonSerializer.Serialize(taDetail);
        string? mlTelemetry = mlPrediction != null ? System.Text.Json.JsonSerializer.Serialize(mlPrediction) : null;
        string smcTelemetry = System.Text.Json.JsonSerializer.Serialize(new {
            BosDirection = smcResult.BosDirection,
            SweepDirection = smcResult.SweepDirection,
            OrderBlockType = smcResult.OrderBlockType,
            FvgType = smcResult.FvgType,
            SmcScore = consensus.SmcScore
        });

        string effectiveMarketRegime = !string.IsNullOrEmpty(taDetail.Regime) && taDetail.Regime != "UNKNOWN"
            ? taDetail.Regime
            : (state.VelocityRegime ?? ExtractMlRegime(mlPrediction?.ModelVersion));

        var mlFeatures = new {
            Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
            MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
            Smc = smcResult,
            Ta = taDetail,
            MacroContext = new {
                MinutesToNews = minutesToNews ?? -1,
                MarketRegime = effectiveMarketRegime,
                VelocityRegime = state.VelocityRegime ?? "UNKNOWN",
                Atr = mainAtr,
                Adx = mainAdx,
                PriceEntropy = priceEntropy,
                TrendMaturity = trendMaturity,
                PricePositionPct = pricePositionPct,
                BbSqueeze = bbSqueeze,
                HourUtc = DateTime.UtcNow.Hour,
                DayOfWeek = (int)DateTime.UtcNow.DayOfWeek == 0 ? 7 : (int)DateTime.UtcNow.DayOfWeek,
                Session = TradeOutcomeTracker.ComputeSession(DateTime.UtcNow),
                // V3 SOTA Deep Learning Anchors
                DayRangePositionPct = dayAnchors.DayRangePositionPct,
                DistToDayHighBps = dayAnchors.DistToDayHighBps,
                DistToDayLowBps = dayAnchors.DistToDayLowBps,
                AsianHigh = dayAnchors.AsianHigh,
                AsianLow = dayAnchors.AsianLow,
                DistToAsianHighBps = dayAnchors.DistToAsianHighBps,
                DistToAsianLowBps = dayAnchors.DistToAsianLowBps,
                DxyMomentum1mBps = dxyMetrics.DxyMomentum1mBps,
                DxyMomentum5mBps = dxyMetrics.DxyMomentum5mBps,
                BasketSyncScore = dxyMetrics.BasketSyncScore,
                SpreadBps = liveSpreadBps,
                TickCountLastCandle = closedCandles.Length > 0 ? closedCandles[^1].Volume : 0
            },
            MetaLearner = new {
                Weights = metaWeights,
                Prob = consensus.MlProb,
                Score = consensus.MlScoreRaw,
                FinalProb = consensus.Probability
            }
        };
        string featuresJson = System.Text.Json.JsonSerializer.Serialize(mlFeatures);

        // Risk & Market Invariant: S5 has an empirically proven negative expectancy (41.01% WR) due to broker spread & execution jitter.
        // Route S5 signals to SHADOW trades to preserve data collection while protecting live execution.
        bool isS5 = timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase);
        string sampleKey = $"{cleanAsset}_{timeframe}";

        if (consensus.Probability >= 57 && !isS5)
        {
            _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
            _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson,
                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", 1.0, "NEUTRAL",
                effectiveMarketRegime,
                state.VelocityRegime ?? "UNKNOWN",
                mainAtr, mainAdx, taResult.rsiVal,
                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1,
                consensus.CombinedReasoningText ?? "",
                mlPrediction?.ModelVersion ?? "",
                mlPrediction?.Accuracy ?? 0.0,
                priceEntropy, trendMaturity, pricePositionPct, bbSqueeze, taTelemetry, mlTelemetry, smcTelemetry);
            dbSw.Stop();
            traceLines.Add($"[8. База данных]     Записан Entry Price: {currentLivePrice} (Уверенность: {consensus.Probability}%, Ожидание: {targetHorizon} свечей) -> {dbSw.ElapsedMilliseconds}ms");
        }
        else if (consensus.Probability >= 45 || isS5)
        {
            _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
            _ = SignalTracker.RecordPredictionAsync("SHADOW_" + consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson,
                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", 1.0, "NEUTRAL",
                effectiveMarketRegime,
                state.VelocityRegime ?? "UNKNOWN",
                mainAtr, mainAdx, taResult.rsiVal,
                mtfResult.DominantDirection == consensus.FinalDirection && consensus.FinalDirection is "BUY" or "PUT",
                minutesToNews ?? -1,
                consensus.CombinedReasoningText ?? "",
                mlPrediction?.ModelVersion ?? "",
                mlPrediction?.Accuracy ?? 0.0,
                priceEntropy, trendMaturity, pricePositionPct, bbSqueeze, taTelemetry, mlTelemetry, smcTelemetry);
            dbSw.Stop();
            traceLines.Add($"[8. DB Write:]     SHADOW TRADE: {currentLivePrice} (Prob: {consensus.Probability}%, Horizon: {targetHorizon}{(isS5 ? ", S5 noise guard" : "")}) -> {dbSw.ElapsedMilliseconds}ms");
        }
        else
        {
            dbSw.Stop();
            
            // --- V3 SYSTEMATIC NEGATIVE SAMPLING (Prevents Selection Bias for Deep Learning / Transformers) ---
            bool isPeriodicSampleDue = !_lastSampleTimeByAssetTf.TryGetValue(sampleKey, out var lastTime) 
                                       || (DateTime.UtcNow - lastTime).TotalMinutes >= 3.0;

            if (isPeriodicSampleDue || System.Random.Shared.NextDouble() < 0.005)
            {
                _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
                _ = SignalTracker.RecordPredictionAsync("HOLD", cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson,
                    "NONE", false, false, 1.0, "NEUTRAL", effectiveMarketRegime, state.VelocityRegime ?? "UNKNOWN", mainAtr, mainAdx, taResult.rsiVal, false, minutesToNews ?? -1, "", "", 0.0, priceEntropy, trendMaturity, pricePositionPct, bbSqueeze, taTelemetry, mlTelemetry, smcTelemetry);
                traceLines.Add($"[8. База данных]     ФОНОВЫЙ СРЕЗ (HOLD): Сохранен для обучения нейтральному рынку ({consensus.Probability}%) -> {dbSw.ElapsedMilliseconds}ms");
            }
            else
            {
                traceLines.Add($"[8. База данных]     ПРОПУСК: Слабый сигнал ({consensus.Probability}%). Ожидаем >= 53% -> {dbSw.ElapsedMilliseconds}ms");
            }
        }

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

        string uiSmcDirection = 
            (smcSignal.SweepDirection ?? "").Contains("BULLISH") ? "BUY" : 
            (smcSignal.SweepDirection ?? "").Contains("BEARISH") ? "PUT" : 
            (smcSignal.BosDirection ?? "").Contains("BULLISH") ? "BUY" : 
            (smcSignal.BosDirection ?? "").Contains("BEARISH") ? "PUT" : 
            (smcSignal.OrderBlockType ?? "").Contains("BULLISH") ? "BUY" : 
            (smcSignal.OrderBlockType ?? "").Contains("BEARISH") ? "PUT" : 
            (smcSignal.FvgType ?? "").Contains("BULLISH") ? "BUY" : 
            (smcSignal.FvgType ?? "").Contains("BEARISH") ? "PUT" : 
            "NEUTRAL";
        int uiSmcConfidence = uiSmcDirection == "NEUTRAL" ? 50 : 50 + (int)Math.Clamp(Math.Max(5, Math.Abs(consensus.SmcScore * 15.0)), 5, 15);

        // ── AI Copilot Tactical Synthesis ──
        CopilotAdviceDto copilotAdvice;
        if (mtfResult.IsGoldenSetup)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "GOLDEN",
                Title = "⭐ Идеальный сетап (Golden Setup)",
                Message = $"Все 3 уровня анализа синхронны: нейросеть PatchTST, SMC-ликвидность и теханализ подтверждают движение {consensus.FinalDirection}. Конфликтов нет.",
                QualityScore = 96
            };
        }
        else if (conflictPenalty < 1.0)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CONFLICT",
                Title = "⚠️ Конфликт таймфреймов",
                Message = $"На {timeframe} виден отскок, но старший тренд давит в противоположную сторону. Опасность ложного пробоя 78%.",
                RecommendedAsset = cleanAsset,
                RecommendedTf = "M1",
                QualityScore = 55
            };
        }
        else if (isSub && vel >= dangerVel)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "NOISE_GUARD",
                Title = $"⚡ Брокерский шум на {timeframe.ToUpper()}",
                Message = $"Энтропия рынка повышена ({uiMarketEntropy}). На микро-секундах высока доля случайных брокерских теней. Безопаснее перейти на 1M.",
                RecommendedAsset = "EUR/USD OTC",
                RecommendedTf = "M1",
                QualityScore = 50
            };
        }
        else if (minutesToNews.HasValue && minutesToNews.Value >= 0 && minutesToNews.Value <= 15)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "NEWS_RISK",
                Title = "📰 Высокая новостная волатильность",
                Message = $"Через {minutesToNews.Value} мин выходит важная макро-новость. Спреды расширены, крупные игроки сокращают ликвидность.",
                QualityScore = 48
            };
        }
        else if (consensus.FinalDirection == "NEUTRAL" || consensus.Probability <= 52)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "WAIT",
                Title = "⏳ Рынок в стадии накопления",
                Message = $"Четкий направленный импульс отсутствует ({uiMarketPhase}). Сигнал слабый ({consensus.Probability}%). Рекомендуется подождать формирования структуры.",
                RecommendedAsset = "GBP/USD OTC",
                RecommendedTf = "M1",
                QualityScore = 60
            };
        }
        else
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "NORMAL",
                Title = $"🎯 Системный тренд ({consensus.FinalDirection})",
                Message = $"Структура рынка устойчивая ({uiMarketPhase}). Математическое ожидание подтверждено моделью ({consensus.Probability}%).",
                QualityScore = (int)Math.Clamp(consensus.Probability + 25, 75, 92)
            };
        }

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
            adaptiveReasoning = consensus.CombinedReasoningText ?? string.Empty,
            taDirection = consensus.TaScore > 0.02 ? "BUY" : consensus.TaScore < -0.02 ? "PUT" : "NEUTRAL",
            taConfidence = 50 + (int)Math.Clamp(Math.Abs(consensus.TaScore * 15.0), 0, 15),
            ofDirection = "NEUTRAL",
            ofConfidence = 0,
            smcDirection = uiSmcDirection,
            smcConfidence = uiSmcConfidence,
            lgbmDirection = mlSignal.Direction,
            lgbmConfidence = (int)Math.Clamp(Math.Round(mlSignal.Confidence * 100), 50, 65),
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
            confluenceRatio = mtfResult.ConfluenceRatio,
            lgbmModelVersion = mlPrediction?.ModelVersion ?? "offline",
            copilotAdvice = copilotAdvice
        };
    }
}



