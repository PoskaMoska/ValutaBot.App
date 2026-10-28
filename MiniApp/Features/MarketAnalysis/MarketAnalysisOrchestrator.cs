using ValutaBot.Core;
using Prometheus;
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
    ILogger<MarketAnalysisOrchestrator> logger
) : IMarketAnalysisOrchestrator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastSeenModelVersions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastSampleTimeByAssetTf = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastRealTradeTimeByAsset = new();
    private static readonly System.Threading.SemaphoreSlim _csvSemaphore = new(1, 1);
    
    private readonly MarketDataFetcher _fetcher = fetcher;
    private readonly IRiskGatekeeper _riskGatekeeper = riskGatekeeper;
    private readonly IMathEngine _mathEngine = mathEngine;
    private readonly IMarketAnalyzer _marketAnalyzer = marketAnalyzer;
    private readonly IConfluenceMatrixEngine _cmEngine = cmEngine;
    private readonly ITradeTimeoutEngine _timeoutEngine = timeoutEngine;
    private readonly TradingBotSettings _settings = settings.Value;
    private readonly ILogger<MarketAnalysisOrchestrator> _logger = logger;

    /// <summary>
    /// Extracts the ML regime token (TREND/FLAT/CHAOS/ALL) from a model version such as
    /// "patch-tst-v3-USDJPY_s10_TREND-1790964297". Returns "UNKNOWN" if it cannot be parsed.
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

    public async Task<AnalysisResponseDto> ExecuteAnalysisAsync(string asset, string timeframe, ValutaBot.App.MiniApp.Data.Repositories.UserSettings? userSettings = null, MiniAppController.OhlcCandle[]? preloadedCandles = null)
    {
        using var timer = ValutaMetrics.PipelineDuration.NewTimer();
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
        string? symbol = PriceFeed.UsePocketOption ? null : AssetSanitizer.MapSymbolByDayOfWeek(cleanAsset, day);
        bool isForex = AssetSanitizer.IsForexAsset(cleanAsset);
        
        string tfLower = TimeframeNormalizer.Normalize(timeframe);
        int limit = (tfLower.StartsWith("s") || tfLower.StartsWith("m1") || tfLower.StartsWith("m5")) ? 160 : 200;
        int fetchLimit = limit + 1; // +1 so stripping the unclosed live candle leaves a full 160 closed-candle window

        // 2. Fetch Data (Locals only, no class fields - fast-path uses preloaded candles)
        var fetchSw = Stopwatch.StartNew();
        var candles = (preloadedCandles != null && preloadedCandles.Length > 0)
            ? preloadedCandles
            : await _fetcher.FetchOhlcWithFallbackAsync(symbol, tfLower, cleanAsset, fetchLimit);
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

        // Prepare closed candles (cap at `limit` after dropping forming candle)
        int intervalSecs = _fetcher.TimeframeSeconds(timeframe);
        DateTime referenceNowUtc = (PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue)
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        bool isLastClosed = intervalSecs > 0 && candles[^1].Timestamp.AddSeconds(intervalSecs) <= referenceNowUtc;
        var closedCandles = isLastClosed ? candles : candles.Take(candles.Length - 1).ToArray();
        if (closedCandles.Length > limit)
            closedCandles = closedCandles.TakeLast(limit).ToArray();
        double[] closedPrices = closedCandles.Select(c => c.Close).ToArray();
        double[] closedVolumes = closedCandles.Select(c => c.Volume).ToArray();

        // 3. Risk Gatekeeper
        var gatekeeperSw = Stopwatch.StartNew();
        var gatekeeper = _riskGatekeeper.ValidateMarketGatekeeper(cleanAsset, timeframe, mainPrices, candles);
        gatekeeperSw.Stop();
        
        bool isRiskBlocked = !gatekeeper.IsTradeable;
        if (isRiskBlocked)
        {
            ValutaMetrics.GatekeeperBlocksTotal.WithLabels(cleanAsset, timeframe, string.IsNullOrEmpty(gatekeeper.Reason) ? "UNKNOWN" : gatekeeper.Reason).Inc();
            _logger.LogWarning("[TRACE {TraceId}] Risk Gatekeeper blocked trade: {Reason}", traceId, gatekeeper.Reason);
            // DO NOT THROW. We need to compute features to save negative (HOLD) samples!
        }
        traceLines.Add($"[2. Gatekeeper]      Риск-контроль пройден ({(string.IsNullOrEmpty(gatekeeper.Reason) ? "Волатильность в норме" : gatekeeper.Reason)}) -> {gatekeeperSw.ElapsedMilliseconds}ms");

        // 4. Continuous State
        var state = ContinuousStateEngine.EvaluateContinuousState(closedPrices, cleanAsset, timeframe, isAlreadyClosed: true);
        
        // 5. Higher TF Data
        string? higherTf = _fetcher.HigherTf(timeframe);
        MiniAppController.OhlcCandle[]? higherCandles = null;
        if (higherTf != null) {
            higherCandles = await _fetcher.FetchOhlcWithFallbackAsync(symbol, higherTf, cleanAsset, 100);
        }
        var closedHigherCandles = (higherCandles != null && higherCandles.Length > 1 && higherTf != null) 
            ? ((_fetcher.TimeframeSeconds(higherTf) > 0 && higherCandles[^1].Timestamp.AddSeconds(_fetcher.TimeframeSeconds(higherTf)) <= referenceNowUtc) ? higherCandles : higherCandles.Take(higherCandles.Length - 1).ToArray())
            : [];

        // 6. Engines (Parallel)
        var engSw = Stopwatch.StartNew();
        bool isSubminute = tfLower.StartsWith("s");
        var smcTask = Task.Run(() => SmcEngine.AnalyzeSmcStructure(cleanAsset, timeframe, closedCandles, closedCandles.Length > 0 ? closedCandles[^1].Close : currentLivePrice));
        
        // TA Scoring (align ADX/ATR period with sub-minute vs minute timeframe: 7 for s5-s30, 14 for m1+)
        var taSw = Stopwatch.StartNew();
        int adxAtrPeriod = tfLower.StartsWith("s") ? 7 : 14;
        var (mainAdx, mainPdi, mainMdi) = closedCandles.Length > 0 ? _mathEngine.ComputeTrueAdx(cleanAsset, timeframe, closedCandles, adxAtrPeriod) : (20.0, 0.0, 0.0);
        double mainAtr = closedCandles.Length > 0 ? _mathEngine.ComputeAtr(cleanAsset, timeframe, closedCandles, adxAtrPeriod) : 0;
        var taDetail = _marketAnalyzer.ScoreTimeframeDetailed(cleanAsset, timeframe, closedPrices, closedVolumes, candles: closedCandles, adxOverride: mainAdx, atrOverride: mainAtr, isForex: isForex, pdiOverride: mainPdi, mdiOverride: mainMdi);
        var taResult = (score: taDetail.Score, confidence: taDetail.Confidence, rsiVal: taDetail.RsiVal, hmaVal: taDetail.HmaVal, volStrengthVal: taDetail.VolStrengthVal, atrVal: taDetail.AtrVal);
        taSw.Stop();
        traceLines.Add($"[3. Расчеты TA]      Индикаторы, ADX ({mainAdx:F1}) и ATR вычислены -> {taSw.ElapsedMilliseconds}ms");

        await smcTask;
        var smcResult = await smcTask;
        engSw.Stop();
        traceLines.Add($"[4. Структура]       Анализ структуры завершен -> {engSw.ElapsedMilliseconds}ms");

        // Macro & Mathematical Sensors (Computed BEFORE Neural Brain so live inference receives real PriceEntropy & context)
        double priceEntropy = 0.0;
        int trendMaturity = 0;
        double pricePositionPct = 0.5;
        bool bbSqueeze = false;

        if (closedCandles.Length >= 20)
        {
            var last20 = closedCandles.Skip(closedCandles.Length - 20).ToList();
            double maxHigh = last20.Max(c => c.High);
            double minLow = last20.Min(c => c.Low);
            if (maxHigh > minLow) {
                pricePositionPct = Math.Clamp((currentLivePrice - minLow) / (maxHigh - minLow), 0.0, 1.0);
            }

            double sma20 = last20.Average(c => c.Close);
            double stdev20 = Math.Sqrt(last20.Average(c => Math.Pow(c.Close - sma20, 2)));
            bbSqueeze = sma20 > 1e-6 && (stdev20 * 4.0) / sma20 < 0.0005; // 5 bps bandwidth

            int streak = 0;
            bool? upTrend = null;
            for (int i = closedCandles.Length - 1; i >= 1; i--) {
                bool isUp = closedCandles[i].Close > closedCandles[i-1].Close;
                bool isDown = closedCandles[i].Close < closedCandles[i-1].Close;
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
            priceEntropy = mainAtr > 1e-6 ? Math.Clamp(stdev20 / mainAtr, 0.0, 5.0) : 0.0;
        }

        int? minutesToNews = -1;
        var dayAnchors = await ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.GetDayLevelAnchorsAsync(cleanAsset, currentLivePrice);
        var dxyMetrics = ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.ComputeDollarBasketMetrics();
        double liveSpreadBps = 0.0;
        if (!PriceFeed.UsePocketOption)
            TiingoWebSocketStream.TryGetSpreadBps(cleanAsset, out liveSpreadBps);

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
            PriceEntropy = priceEntropy,
            TrendMaturity = trendMaturity,
            PricePositionPct = pricePositionPct,
            BbSqueeze = bbSqueeze,
            MinutesToNews = minutesToNews ?? -1,
            Adx = mainAdx,
            Rsi = taDetail.RsiVal,
            HourUtc = DateTime.UtcNow.Hour,
            DayOfWeek = (int)DateTime.UtcNow.DayOfWeek == 0 ? 7 : (int)DateTime.UtcNow.DayOfWeek,
            TickCountLastCandle = closedCandles.Length > 0 ? closedCandles[^1].Volume : 0,
            Ta = new {
                Score = taDetail.Score,
                MicroVel = taDetail.MicroVel,
                HmaSlope = taDetail.HmaSlope,
                VolRatio = taDetail.VolStrengthVal
            }
        };

        // ML (Dual-Stream Neural Brain / PatchTST)
        var mlSw = Stopwatch.StartNew();
        var mlPrediction = _settings.EnableMachineLearning ? await MLPythonService.PredictAsync(cleanAsset, timeframe, closedCandles, isForex, closedHigherCandles, smcResult, macroPayload) : null;
        string mlDir = "NEUTRAL";
        double mlConf = 0.5;
        if (mlPrediction != null) {
            mlDir = mlPrediction.Direction;
            mlConf = mlPrediction.Confidence;
        }
        mlSw.Stop();
        ValutaMetrics.MlDuration.Observe(mlSw.Elapsed.TotalSeconds);
        double mlRawProb = mlPrediction?.RawConfidence ?? 0.5;
        double mlScoreDebug = (mlRawProb - 0.5) * 2.0;
        _logger.LogInformation("[TRACE {TraceId}] Neural Brain: Verdict={Dir} Conf={Conf:F4} (RawConf={RawConf:F4}) | Model={Version}",
            traceId, mlDir, mlConf, mlRawProb, mlPrediction?.ModelVersion ?? "null");
        if (mlPrediction != null) {
            string hasOb = smcResult.OrderBlockType != null ? "Yes" : "No";
            string mlTrace = mlPrediction.TopFeatures != null && mlPrediction.TopFeatures.Count >= 3 
                ? $"Scenario (BOS={smcResult.BosDirection ?? "None"}, OB={hasOb}). Verdict: {mlDir} ({mlConf*100:F1}%) | Explainer: {mlPrediction.TopFeatures[0]}, {mlPrediction.TopFeatures[1]}, {mlPrediction.TopFeatures[2]}" 
                : $"Scenario (BOS={smcResult.BosDirection ?? "None"}, OB={hasOb}). Verdict: {mlDir} ({mlConf*100:F1}%)";
            traceLines.Add($"[5. PREDICT ML] {mlTrace} -> {mlSw.ElapsedMilliseconds}ms");
        } else {
            traceLines.Add($"[5. PREDICT ML] Neural Brain offline (conf: {mlConf:F2}) -> {mlSw.ElapsedMilliseconds}ms");
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
        var mlSignal = new MlSignal(mlDir, mlConf, mlPrediction?.Accuracy, mlPrediction?.ModelVersion ?? "offline", mlPrediction?.HorizonCandles, mlPrediction?.RawConfidence);
        var stateSignal = new StateSignal(state.VelocityRegime, state.VelocityBpsPerSec, state.MomentumContribution);

        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));
        
        // News calendar decoupled (Pocket Option OTC is 24/7 algorithmic feed unaffected by FX calendar)

        matrixSw.Stop();
        traceLines.Add($"[6. Риск-контроль]    Нейросеть и фильтры сведены (Фаза: {state.VelocityRegime ?? "UNKNOWN"}) -> {matrixSw.ElapsedMilliseconds}ms");

        var noiseAudit = SubminuteNoiseGatekeeper.AuditCandleStream(closedCandles, timeframe);
        traceLines.Add($"[6.8 Noise Gate]     {noiseAudit.RegimeDescription} (TR: {noiseAudit.LocalAtrBps:F2} bps, Порог: {noiseAudit.NoiseFloorBps:F1} bps, Z: {noiseAudit.ZScore:F2})");

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
            ["NeuralBrain"]    = mlDir,
            ["LIGHTGBM"]       = mlDir,
        };

        // RECORD (Fire and forget) ONLY IF CONFIDENCE IS HIGH ENOUGH
        int targetHorizon = timeout.TimeoutCandles;
        if (isRiskBlocked)
        {
             consensus = consensus with { 
                 Probability = 0, 
                 FinalDirection = "NEUTRAL",
                 CombinedReasoningText = consensus.CombinedReasoningText + $"\n⚠️ Предупреждение риск-контроля: {gatekeeper.Reason}" 
             };
        }

        string? taTelemetry = null;
        string? mlTelemetry = null;
        string? smcTelemetry = null;

        string effectiveMarketRegime = !string.IsNullOrEmpty(taDetail.Regime) && taDetail.Regime != "UNKNOWN"
            ? taDetail.Regime
            : (state.VelocityRegime ?? ExtractMlRegime(mlPrediction?.ModelVersion));

        var mlFeatures = new {
            Candles = closedCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
            MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
            Smc = smcResult,
            Ta = taDetail,
            MacroContext = new {
                MinutesToNews = minutesToNews ?? -1,
                MarketRegime = effectiveMarketRegime,
                VelocityRegime = state.VelocityRegime ?? "UNKNOWN",
                Atr = mainAtr,
                Adx = mainAdx,
                Rsi = taDetail.RsiVal,
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
            NeuralBrain = new {
                Prob = consensus.MlProb,
                Score = consensus.MlScoreRaw,
                FinalProb = consensus.Probability
            }
        };
        string featuresJson = System.Text.Json.JsonSerializer.Serialize(mlFeatures);

        // Compact JSON for SHADOW/HOLD background records: omits heavy 160-candle arrays (which are already stored in subminute_candles)
        // Reduces row size from 18 KB to 0.8 KB (95.5% reduction), preventing PostgreSQL volume saturation.
        var shadowMlFeatures = new {
            Smc = smcResult,
            Ta = taDetail,
            MacroContext = mlFeatures.MacroContext,
            NeuralBrain = mlFeatures.NeuralBrain
        };
        string shadowFeaturesJson = System.Text.Json.JsonSerializer.Serialize(shadowMlFeatures);

        bool isS5 = timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase);
        bool isDirectional = consensus.FinalDirection is "BUY" or "PUT";
        bool isGlobalCbHalted = TradeOutcomeTracker.CircuitBreaker?.IsHalted() == true;
        bool isSymbolQuarantined = TradeOutcomeTracker.CircuitBreaker != null
            && TradeOutcomeTracker.CircuitBreaker.IsSymbolQuarantined(cleanAsset, timeframe, out _);
        bool isCbHalted = isGlobalCbHalted || isSymbolQuarantined;
        string sampleKey = $"{cleanAsset}_{timeframe}";

        int requiredProb = 58;
        bool htfAligned = mtfResult.DominantDirection == consensus.FinalDirection && isDirectional;
        bool isGoldenSetup = mtfResult.IsGoldenSetup && (!isSubminute || noiseAudit.IsTradeableImpulse);

        bool isCooldownActive = false;
        if (_lastRealTradeTimeByAsset.TryGetValue(cleanAsset, out var lastRealTime))
        {
            int tradeDurationSecs = Math.Max(targetHorizon * intervalSecs, intervalSecs);
            double requiredCooldownSecs = isS5 ? 20.0 : Math.Max(tradeDurationSecs + 2.0, 15.0);
            isCooldownActive = (DateTime.UtcNow - lastRealTime).TotalSeconds < requiredCooldownSecs;
        }

        bool isExhaustedOnS5 = false;
        if (isS5 && closedCandles != null && closedCandles.Length >= 3)
        {
            var last3 = closedCandles.TakeLast(3).ToArray();
            if (consensus.FinalDirection == "BUY" && last3.All(c => c.Close > c.Open) && taResult.rsiVal >= 64.0)
                isExhaustedOnS5 = true;
            else if (consensus.FinalDirection == "PUT" && last3.All(c => c.Close < c.Open) && taResult.rsiVal <= 36.0)
                isExhaustedOnS5 = true;
        }

        bool hasLiveNeuralSignal = !_settings.EnableMachineLearning || (mlPrediction != null && mlDir is "BUY" or "PUT");
        bool canExecuteReal = isDirectional 
            && hasLiveNeuralSignal
            && consensus.Probability >= requiredProb 
            && !isCbHalted
            && !isCooldownActive
            && (!isSubminute || noiseAudit.IsTradeableImpulse)
            && (!isS5 || (htfAligned && !isExhaustedOnS5));

        // Throttled shadow logging: only when DatasetCollectionMode is true or Circuit Breaker is actively halting,
        // and throttled to at most 1 write per minute per asset/tf to avoid 140,000+ row/day DB saturation.
        bool allowShadowRecord = isDirectional
            && (_settings.DatasetCollectionMode || isCbHalted)
            && (!_lastSampleTimeByAssetTf.TryGetValue(sampleKey, out var lastShadowTime) || (DateTime.UtcNow - lastShadowTime).TotalMinutes >= 1.0);

        if (canExecuteReal)
        {
            if (PriceFeed.UsePocketOption)
            {
                if (PocketOptionWebSocketStream.TryGetLivePrice(cleanAsset, out double freshRealPrice, maxAgeSeconds: 1.5) && freshRealPrice > 0)
                {
                    // Asymmetric Slippage Gate: only block adverse slippage
                    // BUY: freshRealPrice > currentLivePrice means buying at a higher/worse price.
                    // PUT: freshRealPrice < currentLivePrice means selling at a lower/worse price.
                    bool isAdverse = (consensus.FinalDirection == "BUY" && freshRealPrice > currentLivePrice) ||
                                     (consensus.FinalDirection == "PUT" && freshRealPrice < currentLivePrice);
                    double slippageBps = currentLivePrice > 0 ? (Math.Abs(freshRealPrice - currentLivePrice) / currentLivePrice * 10000.0) : double.PositiveInfinity;

                    if (isAdverse && (double.IsNaN(slippageBps) || double.IsInfinity(slippageBps) || slippageBps > 1.5))
                    {
                        _logger.LogWarning("[SlippageGate] Blocked {Asset} {Timeframe}: adverse slippage {SlippageBps:F2} bps exceeds 1.5 bps threshold (signal: {CurrentPrice}, fresh: {FreshPrice}, dir: {Dir})",
                            cleanAsset, timeframe, slippageBps, currentLivePrice, freshRealPrice, consensus.FinalDirection);
                        traceLines.Add($"[8. SlippageGate]    [ОТКЛОНЕНО] Неблагоприятное проскальзывание {slippageBps:F2} bps превышает лимит 1.5 bps -> 0ms");
                        canExecuteReal = false;
                    }
                    else
                    {
                        currentLivePrice = freshRealPrice;
                    }
                }
                else
                {
                    // Fail-Closed: If broker live price is stale (>1.5s) or unavailable, abort trade for capital preservation
                    _logger.LogWarning("[SlippageGate] Blocked {Asset} {Timeframe}: live price feed is stale (>1.5s) or unavailable. Trade aborted.",
                        cleanAsset, timeframe);
                    traceLines.Add("[8. SlippageGate]    [ОТКЛОНЕНО] Поток котировок брокера устарел (>1.5с) или недоступен -> 0ms");
                    canExecuteReal = false;
                }
            }

            if (canExecuteReal)
            {
                var nowUtc = DateTime.UtcNow;
                int tradeDurSecs = Math.Max(targetHorizon * intervalSecs, intervalSecs);
                double reqCooldown = isS5 ? 20.0 : Math.Max(tradeDurSecs + 2.0, 15.0);

                if (!_lastRealTradeTimeByAsset.TryAdd(cleanAsset, nowUtc))
                {
                    if (_lastRealTradeTimeByAsset.TryGetValue(cleanAsset, out var prevTime) &&
                        (nowUtc - prevTime).TotalSeconds < reqCooldown)
                    {
                        _logger.LogWarning("[Orchestrator] Concurrency guard: Asset {Asset} trade claimed by concurrent worker. Aborting live execution.", cleanAsset);
                        canExecuteReal = false;
                    }
                    else
                    {
                        _lastRealTradeTimeByAsset[cleanAsset] = nowUtc;
                    }
                }
                if (canExecuteReal)
                {
                    _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
                _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson,
                    smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", 1.0, "NEUTRAL",
                    effectiveMarketRegime,
                    state.VelocityRegime ?? "UNKNOWN",
                    mainAtr, mainAdx, taResult.rsiVal,
                    htfAligned,
                    minutesToNews ?? -1,
                    consensus.CombinedReasoningText ?? "",
                    mlPrediction?.ModelVersion ?? "",
                    mlPrediction?.Accuracy ?? 0.0,
                    priceEntropy, trendMaturity, pricePositionPct, bbSqueeze, taTelemetry, mlTelemetry, smcTelemetry);
                dbSw.Stop();
                traceLines.Add($"[8. База данных]     Записан Entry Price: {currentLivePrice} (Уверенность: {consensus.Probability}%, Ожидание: {targetHorizon} свечей) -> {dbSw.ElapsedMilliseconds}ms");
                }
                else
                {
                    dbSw.Stop();
                    traceLines.Add($"[8. Cooldown Guard]  Asset {cleanAsset} is claimed by concurrent worker -> {dbSw.ElapsedMilliseconds}ms");
                }
            }
        }
        else if (allowShadowRecord)
        {
            _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
            if (PriceFeed.UsePocketOption && PocketOptionWebSocketStream.TryGetLivePrice(cleanAsset, out double freshShadowPrice) && freshShadowPrice > 0)
                currentLivePrice = freshShadowPrice;
            _ = SignalTracker.RecordPredictionAsync("SHADOW_" + consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, shadowFeaturesJson,
                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", 1.0, "NEUTRAL",
                effectiveMarketRegime,
                state.VelocityRegime ?? "UNKNOWN",
                mainAtr, mainAdx, taResult.rsiVal,
                htfAligned,
                minutesToNews ?? -1,
                consensus.CombinedReasoningText ?? "",
                mlPrediction?.ModelVersion ?? "",
                mlPrediction?.Accuracy ?? 0.0,
                priceEntropy, trendMaturity, pricePositionPct, bbSqueeze, taTelemetry, mlTelemetry, smcTelemetry);
            dbSw.Stop();
            string shadowReason = isGlobalCbHalted ? ", Global CB halt guard" : (isSymbolQuarantined ? ", Per-symbol quarantine guard" 
                : (isS5 && isExhaustedOnS5 ? ", S5 peak exhaustion guard" 
                : (isS5 && !htfAligned ? ", S5 HTF divergence guard" 
                : (isS5 && isCooldownActive ? ", S5 cooldown guard" 
                : (isS5 && consensus.Probability < 58 ? ", S5 prob threshold guard" : "")))));
            traceLines.Add($"[8. DB Write:]     SHADOW TRADE: {currentLivePrice} (Prob: {consensus.Probability}%, Horizon: {targetHorizon}{shadowReason}) -> {dbSw.ElapsedMilliseconds}ms");
        }
        else
        {
            dbSw.Stop();
            
            // Background HOLD sampling: ONLY when DatasetCollectionMode is explicitly active
            bool isPeriodicSampleDue = _settings.DatasetCollectionMode && (!_lastSampleTimeByAssetTf.TryGetValue(sampleKey, out var lastTime) 
                                       || (DateTime.UtcNow - lastTime).TotalMinutes >= 5.0);

            if (isPeriodicSampleDue)
            {
                    _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
                _ = SignalTracker.RecordPredictionAsync("HOLD", cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, shadowFeaturesJson,
                    "NONE", false, false, 1.0, "NEUTRAL", effectiveMarketRegime, state.VelocityRegime ?? "UNKNOWN", mainAtr, mainAdx, taResult.rsiVal, false, minutesToNews ?? -1, "", "", 0.0, priceEntropy, trendMaturity, pricePositionPct, bbSqueeze, taTelemetry, mlTelemetry, smcTelemetry);
                traceLines.Add($"[8. База данных]     ФОНОВЫЙ СРЕЗ (HOLD): Сохранен для обучения ({consensus.Probability}%) -> {dbSw.ElapsedMilliseconds}ms");
            }
            else
            {
                traceLines.Add($"[8. База данных]     ПРОПУСК: Сигнал не прошел критерии входа ({consensus.Probability}%). Ожидаем >= {requiredProb}% -> {dbSw.ElapsedMilliseconds}ms");
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
        if (isCbHalted)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CIRCUIT_BREAKER",
                Title = "🛡️ Аварийный тормоз (Circuit Breaker)",
                Message = $"Зафиксирована серия убытков на повышенной волатильности. Вход временно заблокирован для защиты депозита, нейросеть продолжает фоновое обучение.",
                QualityScore = 35
            };
        }
        else if (isGoldenSetup && isDirectional && consensus.Probability >= 57)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "GOLDEN",
                Title = "⭐ Снайперский импульс (Golden Setup)",
                Message = $"Все 3 уровня анализа синхронны: нейросеть ({mlPrediction?.ModelVersion ?? "Neural Brain"}), контекст старшего ТФ и импульс волатильности ({noiseAudit.LocalAtrBps:F1} bps) подтверждают {consensus.FinalDirection}. Порог шума преодолен.",
                QualityScore = 96
            };
        }
        else if (isSubminute && !noiseAudit.IsTradeableImpulse)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CAUTION",
                Title = $"⚡ Вход в консолидации ({consensus.FinalDirection})",
                Message = $"Амплитуда свечей ({noiseAudit.LocalAtrBps:F2} bps) ниже порога шума брокера ({noiseAudit.NoiseFloorBps:F1} bps). Направление — {consensus.FinalDirection} ({consensus.Probability}%). Ожидайте смену свечи для входа.",
                RecommendedAsset = cleanAsset,
                RecommendedTf = timeframe.ToUpper(),
                QualityScore = Math.Min(65, consensus.Probability)
            };
        }
        else if (conflictPenalty < 1.0)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CONFLICT",
                Title = "⚠️ Конфликт таймфреймов",
                Message = $"На {timeframe} локальный импульс спорит со старшим трендом. Опасность ложного пробоя повышена.",
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
        else if (!isDirectional || consensus.Probability <= 52)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CAUTION",
                Title = $"⚡ Вход в консолидации ({consensus.FinalDirection})",
                Message = $"Рынок в стадии накопления ({uiMarketPhase}). Ожидайте смену свечи для входа {consensus.FinalDirection} ({consensus.Probability}%).",
                RecommendedAsset = cleanAsset,
                RecommendedTf = timeframe.ToUpper(),
                QualityScore = 65
            };
        }
        else
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "NORMAL",
                Title = $"🎯 Системный тренд ({consensus.FinalDirection})",
                Message = $"Структура рынка устойчивая ({uiMarketPhase}). Математическое ожидание подтверждено нейросетью ({consensus.Probability}%).",
                QualityScore = (int)Math.Clamp(consensus.Probability + 25, 75, 92)
            };
        }

        int tfSec = _fetcher.TimeframeSeconds(timeframe);
        if (tfSec > 0 && isSubminute && copilotAdvice != null && isDirectional)
        {
            long nowUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long nextCandleBoundary = (nowUnixSec - (nowUnixSec % tfSec)) + tfSec;
            double secRemaining = Math.Max(0.1, nextCandleBoundary - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
            string timingAlert = secRemaining > 1.2
                ? $"⏱ ВХОД НА СМЕНЕ СВЕЧИ: через {secRemaining:F1} сек (в {DateTimeOffset.FromUnixTimeSeconds(nextCandleBoundary).UtcDateTime:HH:mm:ss} UTC). Приготовьтесь нажать {consensus.FinalDirection}!"
                : $"⚡ ВХОД СЕЙЧАС: новая свеча открыта! Нажимайте {consensus.FinalDirection} немедленно!";
            copilotAdvice = copilotAdvice with { Message = timingAlert + "\n\n" + copilotAdvice.Message };
        }

        ValutaMetrics.PredictionsTotal.WithLabels(
            cleanAsset,
            timeframe,
            consensus.FinalDirection ?? "UNKNOWN",
            mlPrediction?.ModelVersion ?? "NONE"
        ).Inc();

        return new AnalysisResponseDto
        {
            tfConflict = conflictPenalty < 1.0,
            uiMarketSession = uiMarketSession,
            uiMarketPhase = uiMarketPhase,
            uiMarketEntropy = uiMarketEntropy,
            direction = isRiskBlocked ? "NEUTRAL" : (consensus.FinalDirection is "BUY" or "PUT" ? consensus.FinalDirection : "NEUTRAL"),
            probability = isRiskBlocked ? 0 : Math.Clamp(consensus.Probability, 50, 92),
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
            lgbmConfidence = (int)Math.Clamp(Math.Round(mlSignal.Confidence * 100), 50, 99),
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
            goldenSetup = isGoldenSetup,
            confluenceLabel = mtfResult.ConfluenceLabel,
            confluenceRatio = mtfResult.ConfluenceRatio,
            lgbmModelVersion = mlPrediction?.ModelVersion ?? "offline",
            copilotAdvice = copilotAdvice
        };
    }
}



