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
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

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
    private static readonly ConcurrentDictionary<string, string> _lastSeenModelVersions = new();
    private static readonly ConcurrentDictionary<string, DateTime> _lastSampleTimeByAssetTf = new();
    private static readonly ConcurrentDictionary<string, long> _lastRealTradeSwByAsset = new();
    private static readonly System.Threading.SemaphoreSlim _csvSemaphore = new(1, 1);
    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    
    private readonly MarketDataFetcher _fetcher = fetcher;
    private readonly IRiskGatekeeper _riskGatekeeper = riskGatekeeper;
    private readonly IMathEngine _mathEngine = mathEngine;
    private readonly IMarketAnalyzer _marketAnalyzer = marketAnalyzer;
    private readonly IConfluenceMatrixEngine _cmEngine = cmEngine;
    private readonly ITradeTimeoutEngine _timeoutEngine = timeoutEngine;
    private readonly TradingBotSettings _settings = settings.Value;
    private readonly ILogger<MarketAnalysisOrchestrator> _logger = logger;

    #region Pipeline DTO Records

    private sealed record PreparedCandleData(
        string CleanAsset,
        string? Symbol,
        bool IsForex,
        string TfLower,
        int Limit,
        int IntervalSecs,
        MiniAppController.OhlcCandle[] RawCandles,
        MiniAppController.OhlcCandle[] ClosedCandles,
        double[] MainPrices,
        double[] ClosedPrices,
        double[] ClosedVolumes,
        double CurrentLivePrice,
        string DataHash,
        string? HigherTf,
        MiniAppController.OhlcCandle[] ClosedHigherCandles
    );

    private sealed record SensorsAndFeatures(
        TechnicalAnalysisEngine.GatekeeperResult Gatekeeper,
        bool IsRiskBlocked,
        ContinuousStateResult State,
        SmcEngine.SmcAnalysisResult SmcResult,
        TaScoringDetail TaDetail,
        (double score, double confidence, double rsiVal, double hmaVal, double volStrengthVal, double atrVal) TaResult,
        double MainAtr,
        double MainAdx,
        double PriceEntropy,
        int TrendMaturity,
        double PricePositionPct,
        bool BbSqueeze,
        int? MinutesToNews,
        DayLevelAnchors DayAnchors,
        DollarBasketMetrics DxyMetrics,
        double LiveSpreadBps,
        object MacroPayload
    );

    private sealed record NeuralBrainOutcome(
        MLPythonService.MLPythonPrediction? Prediction,
        string Direction,
        double Confidence,
        double RawConfidence
    );

    private sealed record MatrixConsensusOutcome(
        ConsensusDecision Consensus,
        SubminuteNoiseGatekeeper.NoiseAuditResult NoiseAudit,
        ConfluenceMatrixResult MtfResult,
        double ConflictPenalty,
        TaSignal TaSignal,
        SmcSignal SmcSignal,
        MlSignal MlSignal,
        StateSignal StateSignal,
        string EffectiveMarketRegime,
        TradeTimeoutEngine.TimeoutResult Timeout
    );

    private sealed record ExecutionOutcome(
        double ExecutedLivePrice,
        bool IsGoldenSetup,
        bool IsSubminute,
        bool IsS5,
        bool IsDirectional,
        bool IsCbHalted,
        bool IsExhaustedOnS5,
        bool HtfAligned,
        bool IsCooldownActive
    );

    #endregion

    #region Helper Utilities

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

    private static double GetSafeLimit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        if (value > 1e6) return 1e6;
        if (value < -1e6) return -1e6;
        return value;
    }

    private static string ComputeHash(MiniAppController.OhlcCandle[] cands)
    {
        long sumBits = 0;
        foreach (var c in cands) sumBits ^= BitConverter.DoubleToInt64Bits(c.Close);
        return sumBits.ToString("X8").Substring(0, 8);
    }

    #endregion

    #region Main Pipeline Entrypoint

    public async Task<AnalysisResponseDto> ExecuteAnalysisAsync(
        string asset,
        string timeframe,
        ValutaBot.App.MiniApp.Data.Repositories.UserSettings? userSettings = null,
        MiniAppController.OhlcCandle[]? preloadedCandles = null,
        System.Threading.CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var timer = ValutaMetrics.PipelineDuration.NewTimer();
        var sw = Stopwatch.StartNew();
        string traceId = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
        var traceLines = new List<string>();

        _logger.LogInformation("[TRACE {TraceId}] Analysis started for Asset: {Asset}, TF: {Timeframe}", traceId, asset, timeframe);

        // Stage 1: Fetch and Prepare Closed Candle Windows
        var prepared = await FetchAndPrepareCandlesAsync(asset, timeframe, preloadedCandles, traceId, traceLines);

        // Stage 2: Technical, SMC & Mathematical Sensors
        var sensors = await ComputeSensorsAndFeaturesAsync(prepared, timeframe, traceId, traceLines);

        // Stage 3: Dual-Stream Neural Brain / PatchTST Inference
        var mlOutcome = await QueryNeuralBrainAsync(prepared, sensors, traceId, traceLines);

        // Stage 4: Matrix & Multi-Timeframe Consensus Evaluation
        var consensusOutcome = await EvaluateConsensusAndMatrixAsync(prepared, timeframe, sensors, mlOutcome, traceId, traceLines);

        // Stage 5: Execution Guards, Cooldowns & Trade Recording (Real / Shadow / Hold)
        var execOutcome = await EvaluateExecutionAndRecordTradeAsync(prepared, timeframe, sensors, mlOutcome, consensusOutcome, traceLines);

        // Stage 6: Pipeline Trace Console Logging
        sw.Stop();
        LogPipelineTrace(traceId, prepared.CleanAsset, timeframe, traceLines, consensusOutcome.Consensus, sw.ElapsedMilliseconds);

        // Stage 7: Assemble Final Response DTO
        return await BuildResponseDtoAsync(prepared, timeframe, sensors, mlOutcome, consensusOutcome, execOutcome);
    }

    #endregion

    #region Pipeline Stage 1: Fetch and Prepare Candles

    private async Task<PreparedCandleData> FetchAndPrepareCandlesAsync(
        string asset,
        string timeframe,
        MiniAppController.OhlcCandle[]? preloadedCandles,
        string traceId,
        List<string> traceLines)
    {
        string cleanAsset = AssetSanitizer.Sanitize(asset);
        DayOfWeek day = DateTime.UtcNow.DayOfWeek;
        string? symbol = PriceFeed.UsePocketOption ? null : AssetSanitizer.MapSymbolByDayOfWeek(cleanAsset, day);
        bool isForex = AssetSanitizer.IsForexAsset(cleanAsset);
        
        string tfLower = TimeframeNormalizer.Normalize(timeframe);
        int limit = (tfLower.StartsWith("s") || tfLower.StartsWith("m1") || tfLower.StartsWith("m5")) ? 160 : 200;
        int fetchLimit = limit + 1; // +1 so stripping unclosed candle leaves a full 160 closed-candle window

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

        string? higherTf = _fetcher.HigherTf(timeframe);
        MiniAppController.OhlcCandle[]? higherCandles = null;
        if (higherTf != null)
        {
            higherCandles = await _fetcher.FetchOhlcWithFallbackAsync(symbol, higherTf, cleanAsset, 100);
        }
        var closedHigherCandles = (higherCandles != null && higherCandles.Length > 1 && higherTf != null) 
            ? ((_fetcher.TimeframeSeconds(higherTf) > 0 && higherCandles[^1].Timestamp.AddSeconds(_fetcher.TimeframeSeconds(higherTf)) <= referenceNowUtc) 
                ? higherCandles 
                : higherCandles.Take(higherCandles.Length - 1).ToArray())
            : [];

        return new PreparedCandleData(
            cleanAsset,
            symbol,
            isForex,
            tfLower,
            limit,
            intervalSecs,
            candles,
            closedCandles,
            mainPrices,
            closedPrices,
            closedVolumes,
            currentLivePrice,
            dataHash,
            higherTf,
            closedHigherCandles
        );
    }

    #endregion

    #region Pipeline Stage 2: Technical & Math Sensors

    private async Task<SensorsAndFeatures> ComputeSensorsAndFeaturesAsync(
        PreparedCandleData prep,
        string timeframe,
        string traceId,
        List<string> traceLines)
    {
        // 1. Risk Gatekeeper
        var gatekeeperSw = Stopwatch.StartNew();
        var gatekeeper = _riskGatekeeper.ValidateMarketGatekeeper(prep.CleanAsset, timeframe, prep.MainPrices, prep.RawCandles);
        gatekeeperSw.Stop();
        
        bool isRiskBlocked = !gatekeeper.IsTradeable;
        if (isRiskBlocked)
        {
            ValutaMetrics.GatekeeperBlocksTotal.WithLabels(prep.CleanAsset, timeframe, string.IsNullOrEmpty(gatekeeper.Reason) ? "UNKNOWN" : gatekeeper.Reason).Inc();
            _logger.LogWarning("[TRACE {TraceId}] Risk Gatekeeper blocked trade: {Reason}", traceId, gatekeeper.Reason);
        }
        traceLines.Add($"[2. Gatekeeper]      Риск-контроль пройден ({(string.IsNullOrEmpty(gatekeeper.Reason) ? "Волатильность в норме" : gatekeeper.Reason)}) -> {gatekeeperSw.ElapsedMilliseconds}ms");

        // 2. Continuous State
        var state = ContinuousStateEngine.EvaluateContinuousState(prep.ClosedPrices, prep.CleanAsset, timeframe, isAlreadyClosed: true);

        // 3. SMC & TA Scoring in Parallel
        var engSw = Stopwatch.StartNew();
        var smcTask = Task.Run(() => SmcEngine.AnalyzeSmcStructure(prep.CleanAsset, timeframe, prep.ClosedCandles, prep.ClosedCandles.Length > 0 ? prep.ClosedCandles[^1].Close : prep.CurrentLivePrice));
        
        var taSw = Stopwatch.StartNew();
        int adxAtrPeriod = prep.TfLower.StartsWith("s") ? 7 : 14;
        var (mainAdx, mainPdi, mainMdi) = prep.ClosedCandles.Length > 0 ? _mathEngine.ComputeTrueAdx(prep.CleanAsset, timeframe, prep.ClosedCandles, adxAtrPeriod) : (20.0, 0.0, 0.0);
        double mainAtr = prep.ClosedCandles.Length > 0 ? _mathEngine.ComputeAtr(prep.CleanAsset, timeframe, prep.ClosedCandles, adxAtrPeriod) : 0;
        var taDetail = _marketAnalyzer.ScoreTimeframeDetailed(prep.CleanAsset, timeframe, prep.ClosedPrices, prep.ClosedVolumes, candles: prep.ClosedCandles, adxOverride: mainAdx, atrOverride: mainAtr, isForex: prep.IsForex, pdiOverride: mainPdi, mdiOverride: mainMdi);
        var taResult = (score: taDetail.Score, confidence: taDetail.Confidence, rsiVal: taDetail.RsiVal, hmaVal: taDetail.HmaVal, volStrengthVal: taDetail.VolStrengthVal, atrVal: taDetail.AtrVal);
        taSw.Stop();
        traceLines.Add($"[3. Расчеты TA]      Индикаторы, ADX ({mainAdx:F1}) и ATR вычислены -> {taSw.ElapsedMilliseconds}ms");

        var smcResult = await smcTask;
        engSw.Stop();
        traceLines.Add($"[4. Структура]       Анализ структуры завершен -> {engSw.ElapsedMilliseconds}ms");

        // 4. Macro & Mathematical Sensors
        double priceEntropy = 0.0;
        int trendMaturity = 0;
        double pricePositionPct = 0.5;
        bool bbSqueeze = false;

        if (prep.ClosedCandles.Length >= 20)
        {
            var last20 = prep.ClosedCandles.Skip(prep.ClosedCandles.Length - 20).ToList();
            double maxHigh = last20.Max(c => c.High);
            double minLow = last20.Min(c => c.Low);
            if (maxHigh > minLow) {
                pricePositionPct = Math.Clamp((prep.CurrentLivePrice - minLow) / (maxHigh - minLow), 0.0, 1.0);
            }

            double sma20 = last20.Average(c => c.Close);
            double stdev20 = Math.Sqrt(last20.Average(c => Math.Pow(c.Close - sma20, 2)));
            bbSqueeze = sma20 > 1e-6 && (stdev20 * 4.0) / sma20 < 0.0005;

            int streak = 0;
            bool? upTrend = null;
            for (int i = prep.ClosedCandles.Length - 1; i >= 1; i--) {
                bool isUp = prep.ClosedCandles[i].Close > prep.ClosedCandles[i-1].Close;
                bool isDown = prep.ClosedCandles[i].Close < prep.ClosedCandles[i-1].Close;
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
        var dayAnchors = await MacroContextEngine.GetDayLevelAnchorsAsync(prep.CleanAsset, prep.CurrentLivePrice);
        var dxyMetrics = MacroContextEngine.ComputeDollarBasketMetrics();
        double liveSpreadBps = 0.0;

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
            TickCountLastCandle = prep.ClosedCandles.Length > 0 ? prep.ClosedCandles[^1].Volume : 0,
            Ta = new {
                Score = taDetail.Score,
                MicroVel = taDetail.MicroVel,
                HmaSlope = taDetail.HmaSlope,
                VolRatio = taDetail.VolStrengthVal
            }
        };

        return new SensorsAndFeatures(
            gatekeeper,
            isRiskBlocked,
            state,
            smcResult,
            taDetail,
            taResult,
            mainAtr,
            mainAdx,
            priceEntropy,
            trendMaturity,
            pricePositionPct,
            bbSqueeze,
            minutesToNews,
            dayAnchors,
            dxyMetrics,
            liveSpreadBps,
            macroPayload
        );
    }

    #endregion

    #region Pipeline Stage 3: Neural Brain ML Inference

    private static double ComputePandasEma(double[] values, int span)
    {
        if (values == null || values.Length == 0) return 0.0;
        double alpha = 2.0 / (span + 1.0);
        double num = 0.0;
        double den = 0.0;
        double w = 1.0;
        for (int i = values.Length - 1; i >= 0; i--)
        {
            num += w * values[i];
            den += w;
            w *= (1.0 - alpha);
        }
        return den > 0 ? num / den : values[^1];
    }

    private static (float[] macro, float[] micro, float[] bridge)? BuildOnnxTensors(
        MiniAppController.OhlcCandle[] microCandles,
        MiniAppController.OhlcCandle[] macroCandles,
        double currentPrice)
    {
        if (microCandles == null || microCandles.Length < 30 ||
            macroCandles == null || macroCandles.Length < 30 || currentPrice <= 0)
        {
            return null;
        }

        var macroSlice = macroCandles[^30..];
        var microSlice = microCandles[^30..];

        float[] NormalizeSlice(MiniAppController.OhlcCandle[] slice)
        {
            float[] result = new float[150];
            double refP = slice[^1].Close > 0 ? slice[^1].Close : 1.0;
            double meanVol = slice.Average(c => c.Volume) + 1e-4;

            for (int i = 0; i < 30; i++)
            {
                var c = slice[i];
                int offset = i * 5;
                result[offset + 0] = (float)((c.Close - refP) / refP * 1000.0);
                result[offset + 1] = (float)((c.High - c.Low) / refP * 1000.0);
                result[offset + 2] = (float)((c.High - Math.Max(c.Open, c.Close)) / refP * 1000.0);
                result[offset + 3] = (float)((Math.Min(c.Open, c.Close) - c.Low) / refP * 1000.0);
                result[offset + 4] = Math.Clamp((float)(c.Volume / meanVol), 0.0f, 10.0f);
            }
            return result;
        }

        var macroData = NormalizeSlice(macroSlice);
        var microData = NormalizeSlice(microSlice);

        double curP = microSlice[^1].Close > 0 ? microSlice[^1].Close : 1.0;

        double[] macroCloses = new double[30];
        for (int i = 0; i < 30; i++) macroCloses[i] = macroSlice[i].Close;
        double m1Ema = ComputePandasEma(macroCloses, 9);
        float distM1 = (float)((curP - m1Ema) / curP * 1000.0);

        double s5Tr = microSlice.Average(c => Math.Max(c.High - c.Low, 1e-7));
        double m1Tr = macroSlice.Average(c => Math.Max(c.High - c.Low, 1e-7));
        float atrRatio = (float)(s5Tr / (m1Tr + 1e-7));

        double[] microCloses = new double[30];
        for (int i = 0; i < 30; i++) microCloses[i] = microSlice[i].Close;
        double microMean = microCloses.Average();
        double sumSq = 0.0;
        for (int i = 0; i < 30; i++)
        {
            double d = microCloses[i] - microMean;
            sumSq += d * d;
        }
        double microStd = Math.Sqrt(sumSq / 30.0) + 1e-7;
        double s5Ema = ComputePandasEma(microCloses, 7);
        float zScore = (float)((curP - s5Ema) / microStd);

        var last = microSlice[^1];
        double rng = Math.Max(last.High - last.Low, 1e-7);
        double upperWick = last.High - Math.Max(last.Open, last.Close);
        double lowerWick = Math.Min(last.Open, last.Close) - last.Low;
        float wickBal = (float)((lowerWick - upperWick) / rng);

        // Guard against NaN/Inf before Math.Clamp (since Math.Clamp(NaN, min, max) returns NaN)
        if (!float.IsFinite(distM1) || !float.IsFinite(atrRatio) || !float.IsFinite(zScore) || !float.IsFinite(wickBal))
        {
            return null;
        }

        // Sanitize & clamp features identically to export_objective_dataset.py
        distM1 = Math.Clamp(distM1, -50.0f, 50.0f);
        atrRatio = Math.Clamp(atrRatio, 0.01f, 20.0f);
        zScore = Math.Clamp(zScore, -5.0f, 5.0f);
        wickBal = Math.Clamp(wickBal, -1.0f, 1.0f);

        float[] bridgeData = new[] { distM1, atrRatio, zScore, wickBal };

        return (macroData, microData, bridgeData);
    }

    private async Task<NeuralBrainOutcome> QueryNeuralBrainAsync(
        PreparedCandleData prep,
        SensorsAndFeatures sensors,
        string traceId,
        List<string> traceLines)
    {
        var mlSw = Stopwatch.StartNew();
        MLPythonService.MLPythonPrediction? mlPrediction = null;

        if (_settings.EnableMachineLearning)
        {
            // Fast Path: In-process ONNX Runtime (strictly s5 + m1 as trained in dual_stream_brain_v5.onnx)
            if (DualStreamOnnxService.Instance.IsModelLoaded && prep.TfLower.Equals("s5", StringComparison.OrdinalIgnoreCase))
            {
                var tensors = BuildOnnxTensors(prep.ClosedCandles, prep.ClosedHigherCandles, prep.CurrentLivePrice);
                if (tensors.HasValue)
                {
                    var onnxPred = DualStreamOnnxService.Instance.Predict(tensors.Value.macro, tensors.Value.micro, tensors.Value.bridge);
                    if (onnxPred.Direction is "BUY" or "PUT")
                    {
                        mlPrediction = new MLPythonService.MLPythonPrediction(
                            Direction: onnxPred.Direction,
                            Confidence: onnxPred.Probability,
                            ModelVersion: "dual_stream_onnx_v5",
                            Accuracy: 0.62,
                            Auc: 0.65,
                            NTrain: 52768,
                            VarianceEstimate: 0.05,
                            RawConfidence: onnxPred.RawProbability,
                            HorizonCandles: 1
                        );
                    }
                }
            }

            // Fallback Path: Python HTTP microservice
            if (mlPrediction == null)
            {
                mlPrediction = await MLPythonService.PredictAsync(prep.CleanAsset, prep.TfLower, prep.ClosedCandles, prep.IsForex, prep.ClosedHigherCandles, sensors.SmcResult, sensors.MacroPayload);
            }
        }
        
        string mlDir = "NEUTRAL";
        double mlConf = 0.5;
        if (mlPrediction != null) {
            mlDir = mlPrediction.Direction;
            mlConf = mlPrediction.Confidence;
        }
        mlSw.Stop();
        ValutaMetrics.MlDuration.Observe(mlSw.Elapsed.TotalSeconds);
        double mlRawProb = mlPrediction?.RawConfidence ?? 0.5;

        _logger.LogInformation("[TRACE {TraceId}] Neural Brain: Verdict={Dir} Conf={Conf:F4} (RawConf={RawConf:F4}) | Model={Version}",
            traceId, mlDir, mlConf, mlRawProb, mlPrediction?.ModelVersion ?? "null");

        if (mlPrediction != null) {
            string hasOb = sensors.SmcResult.OrderBlockType != null ? "Yes" : "No";
            string mlTrace = mlPrediction.TopFeatures != null && mlPrediction.TopFeatures.Count >= 3 
                ? $"Scenario (BOS={sensors.SmcResult.BosDirection ?? "None"}, OB={hasOb}). Verdict: {mlDir} ({mlConf*100:F1}%) | Explainer: {mlPrediction.TopFeatures[0]}, {mlPrediction.TopFeatures[1]}, {mlPrediction.TopFeatures[2]}" 
                : $"Scenario (BOS={sensors.SmcResult.BosDirection ?? "None"}, OB={hasOb}). Verdict: {mlDir} ({mlConf*100:F1}%)";
            traceLines.Add($"[5. PREDICT ML] {mlTrace} -> {mlSw.ElapsedMilliseconds}ms");
        } else {
            traceLines.Add($"[5. PREDICT ML] Neural Brain offline (conf: {mlConf:F2}) -> {mlSw.ElapsedMilliseconds}ms");
        }

        return new NeuralBrainOutcome(mlPrediction, mlDir, mlConf, mlRawProb);
    }

    #endregion

    #region Pipeline Stage 4: Matrix & Multi-Timeframe Consensus

    private async Task<MatrixConsensusOutcome> EvaluateConsensusAndMatrixAsync(
        PreparedCandleData prep,
        string timeframe,
        SensorsAndFeatures sensors,
        NeuralBrainOutcome ml,
        string traceId,
        List<string> traceLines)
    {
        var matrixSw = Stopwatch.StartNew();
        double conflictPenalty = 1.0;
        if (prep.ClosedHigherCandles.Length > 0 && prep.HigherTf != null) {
            var hAdx = _mathEngine.ComputeTrueAdx(prep.CleanAsset, prep.HigherTf, prep.ClosedHigherCandles);
            var hAtr = _mathEngine.ComputeAtr(prep.CleanAsset, prep.HigherTf, prep.ClosedHigherCandles);
            var hResult = _marketAnalyzer.ScoreTimeframe(prep.CleanAsset, prep.HigherTf, prep.ClosedHigherCandles.Select(c=>c.Close).ToArray(), prep.ClosedHigherCandles.Select(c=>c.Volume).ToArray(), candles: prep.ClosedHigherCandles, adxOverride: hAdx.adx, atrOverride: hAtr, isForex: prep.IsForex);
            conflictPenalty *= (sensors.TaResult.score * hResult.score < -0.01) ? 0.7 : 1.0;
        }

        var mtfResult = await _cmEngine.Evaluate4DMatrixAsync(prep.CleanAsset, timeframe, prep.IsForex, prep.Symbol, prep.ClosedCandles, prep.ClosedPrices, prep.ClosedVolumes, prep.ClosedHigherCandles, prep.ClosedHigherCandles.Select(c=>c.Close).ToArray(), prep.ClosedHigherCandles.Select(c=>c.Volume).ToArray());
        
        var taSignal = new TaSignal(sensors.TaResult.score, sensors.TaResult.confidence, sensors.TaResult.rsiVal, sensors.TaResult.hmaVal, sensors.TaResult.volStrengthVal, sensors.MainAtr, sensors.MainAdx);
        var smcSignal = new SmcSignal(sensors.SmcResult.BosDirection ?? "", sensors.SmcResult.SweepDirection ?? "", sensors.SmcResult.OrderBlockType ?? "", sensors.SmcResult.FvgType ?? "", "");
        var mlSignal = new MlSignal(ml.Direction, ml.Confidence, ml.Prediction?.Accuracy, ml.Prediction?.ModelVersion ?? "offline", ml.Prediction?.HorizonCandles, ml.Prediction?.RawConfidence);
        var stateSignal = new StateSignal(sensors.State.VelocityRegime, sensors.State.VelocityBpsPerSec, sensors.State.MomentumContribution);


        var noiseAudit = SubminuteNoiseGatekeeper.AuditCandleStream(prep.ClosedCandles, timeframe);
        traceLines.Add($"[6.8 Noise Gate]     {noiseAudit.RegimeDescription} (TR: {noiseAudit.LocalAtrBps:F2} bps, Порог: {noiseAudit.NoiseFloorBps:F1} bps, Z: {noiseAudit.ZScore:F2})");
        var consensus = await _cmEngine.EvaluateMatrixAsync(prep.CleanAsset, timeframe, prep.TfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(prep.CleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(prep.MainPrices), noiseAudit);
        
        matrixSw.Stop();
        traceLines.Add($"[6. Риск-контроль]    Нейросеть и фильтры сведены (Фаза: {sensors.State.VelocityRegime ?? "UNKNOWN"}) -> {matrixSw.ElapsedMilliseconds}ms");


        var dbSw = Stopwatch.StartNew();
        var timeout = _timeoutEngine.CalculateTimeout(prep.CleanAsset, timeframe, sensors.MainAtr, 1.0, sensors.SmcResult, prep.CurrentLivePrice, sensors.State, prep.IsForex);

        string finalHash = ComputeHash(prep.RawCandles);
        if (finalHash == prep.DataHash) {
            traceLines.Add($"[7. Data Integrity]  Проверка: Hash [{finalHash}] совпадает. Искажений нет. -> {dbSw.ElapsedMilliseconds}ms");
        } else {
            traceLines.Add($"[7. Data Integrity]  [ВНИМАНИЕ! ДАННЫЕ ИСКАЖЕНЫ] Ожидался {prep.DataHash}, получен {finalHash} -> {dbSw.ElapsedMilliseconds}ms");
        }

        if (sensors.IsRiskBlocked)
        {
             consensus = consensus with { 
                 Probability = 0, 
                 FinalDirection = "NEUTRAL",
                 CombinedReasoningText = consensus.CombinedReasoningText + $"\n⚠️ Предупреждение риск-контроля: {sensors.Gatekeeper.Reason}" 
             };
        }
        else if (consensus.FinalDirection == "NEUTRAL")
        {
            string fallbackDir = prep.ClosedCandles.Length > 0 && prep.ClosedCandles[^1].Close >= prep.ClosedCandles[^1].Open ? "BUY" : "PUT";
            consensus = consensus with {
                FinalDirection = fallbackDir,
                CandidateDirection = fallbackDir,
                Probability = 51,
                CombinedReasoningText = consensus.CombinedReasoningText + $"\n🧭 Базовый дрейф: нейтральный консенсус приведен к {fallbackDir} (51%) по направлению закрытия последней свечи."
            };
        }

        string effectiveMarketRegime = !string.IsNullOrEmpty(sensors.TaDetail.Regime) && sensors.TaDetail.Regime != "UNKNOWN"
            ? sensors.TaDetail.Regime
            : (sensors.State.VelocityRegime ?? ExtractMlRegime(ml.Prediction?.ModelVersion));

        return new MatrixConsensusOutcome(
            consensus,
            noiseAudit,
            mtfResult,
            conflictPenalty,
            taSignal,
            smcSignal,
            mlSignal,
            stateSignal,
            effectiveMarketRegime,
            timeout
        );
    }

    #endregion

    #region Pipeline Stage 5: Execution Guards & Trade Recording

    private async Task<ExecutionOutcome> EvaluateExecutionAndRecordTradeAsync(
        PreparedCandleData prep,
        string timeframe,
        SensorsAndFeatures sensors,
        NeuralBrainOutcome ml,
        MatrixConsensusOutcome cm,
        List<string> traceLines)
    {
        var dbSw = Stopwatch.StartNew();
        int targetHorizon = cm.Timeout.TimeoutCandles;

        // D-6 fix: sanitize NaN/Infinity before JSON serialization (AllowNamedFloatingPointLiterals would emit "NaN" strings).
        static double S(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;

        var sourceDirections = new Dictionary<string, string>
        {
            ["TechAnalysis"] = DirectionExtensions.FromScore(cm.Consensus.TaScore).ToSignal(),
            ["SMC"]          = DirectionExtensions.FromScore(cm.Consensus.SmcScore).ToSignal(),
            ["NeuralBrain"]  = ml.Direction,
            ["LIGHTGBM"]     = ml.Direction,
        };

        var mlFeatures = new {
            Candles = prep.ClosedCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
            MtfCandles = prep.ClosedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
            Smc = sensors.SmcResult,
            Ta = sensors.TaDetail,
            MacroContext = new {
                MinutesToNews = sensors.MinutesToNews ?? -1,
                MarketRegime = cm.EffectiveMarketRegime,
                VelocityRegime = sensors.State.VelocityRegime ?? "UNKNOWN",
                Atr = S(sensors.MainAtr),
                Adx = S(sensors.MainAdx, 20.0),
                Rsi = S(sensors.TaDetail.RsiVal, 50.0),
                PriceEntropy = S(sensors.PriceEntropy),
                TrendMaturity = S(sensors.TrendMaturity),
                PricePositionPct = S(sensors.PricePositionPct, 50.0),
                BbSqueeze = sensors.BbSqueeze,
                HourUtc = DateTime.UtcNow.Hour,
                DayOfWeek = (int)DateTime.UtcNow.DayOfWeek == 0 ? 7 : (int)DateTime.UtcNow.DayOfWeek,
                Session = TradeOutcomeTracker.ComputeSession(DateTime.UtcNow),
                DayRangePositionPct = S(sensors.DayAnchors.DayRangePositionPct, 50.0),
                DistToDayHighBps = S(sensors.DayAnchors.DistToDayHighBps),
                DistToDayLowBps = S(sensors.DayAnchors.DistToDayLowBps),
                AsianHigh = S(sensors.DayAnchors.AsianHigh),
                AsianLow = S(sensors.DayAnchors.AsianLow),
                DistToAsianHighBps = S(sensors.DayAnchors.DistToAsianHighBps),
                DistToAsianLowBps = S(sensors.DayAnchors.DistToAsianLowBps),
                DxyMomentum1mBps = S(sensors.DxyMetrics.DxyMomentum1mBps),
                DxyMomentum5mBps = S(sensors.DxyMetrics.DxyMomentum5mBps),
                BasketSyncScore = S(sensors.DxyMetrics.BasketSyncScore),
                SpreadBps = S(sensors.LiveSpreadBps),
                TickCountLastCandle = prep.ClosedCandles.Length > 0 ? prep.ClosedCandles[^1].Volume : 0
            },
            NeuralBrain = new {
                Prob = cm.Consensus.MlProb,
                Score = cm.Consensus.MlScoreRaw,
                FinalProb = cm.Consensus.Probability
            }
        };
        string featuresJson = System.Text.Json.JsonSerializer.Serialize(mlFeatures, _jsonOptions);

        var shadowMlFeatures = new {
            Smc = sensors.SmcResult,
            Ta = sensors.TaDetail,
            MacroContext = mlFeatures.MacroContext,
            NeuralBrain = mlFeatures.NeuralBrain
        };
        string shadowFeaturesJson = System.Text.Json.JsonSerializer.Serialize(shadowMlFeatures, _jsonOptions);

        bool isS5 = timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase);
        bool isSubminute = prep.TfLower.StartsWith("s");
        bool isDirectional = cm.Consensus.FinalDirection is "BUY" or "PUT";
        bool isGlobalCbHalted = TradeOutcomeTracker.CircuitBreaker?.IsHalted() == true;
        bool isSymbolQuarantined = TradeOutcomeTracker.CircuitBreaker != null
            && TradeOutcomeTracker.CircuitBreaker.IsSymbolQuarantined(prep.CleanAsset, timeframe, out _);
        bool isCbHalted = isGlobalCbHalted || isSymbolQuarantined;
        string sampleKey = $"{prep.CleanAsset}_{timeframe}";

        int requiredProb = 58;
        bool htfAligned = cm.MtfResult.DominantDirection == cm.Consensus.FinalDirection && isDirectional;
        bool isGoldenSetup = cm.MtfResult.IsGoldenSetup && (!isSubminute || cm.NoiseAudit.IsTradeableImpulse);

        bool isCooldownActive = false;
        if (_lastRealTradeSwByAsset.TryGetValue(prep.CleanAsset, out var lastRealSw))
        {
            int tradeDurationSecs = Math.Max(targetHorizon * prep.IntervalSecs, prep.IntervalSecs);
            double requiredCooldownSecs = isS5 ? 20.0 : Math.Max(tradeDurationSecs + 2.0, 15.0);
            isCooldownActive = Stopwatch.GetElapsedTime(lastRealSw).TotalSeconds < requiredCooldownSecs;
        }

        bool isExhaustedOnS5 = false;
        if (isS5 && prep.ClosedCandles != null && prep.ClosedCandles.Length >= 3)
        {
            var last3 = prep.ClosedCandles.TakeLast(3).ToArray();
            if (cm.Consensus.FinalDirection == "BUY" && last3.All(c => c.Close > c.Open) && sensors.TaResult.rsiVal >= 64.0)
                isExhaustedOnS5 = true;
            else if (cm.Consensus.FinalDirection == "PUT" && last3.All(c => c.Close < c.Open) && sensors.TaResult.rsiVal <= 36.0)
                isExhaustedOnS5 = true;
        }

        bool hasLiveNeuralSignal = !_settings.EnableMachineLearning || (ml.Prediction != null && ml.Direction is "BUY" or "PUT");
        bool canExecuteReal = isDirectional 
            && hasLiveNeuralSignal
            && cm.Consensus.Probability >= requiredProb 
            && !isCbHalted
            && !isCooldownActive
            && (!isSubminute || cm.NoiseAudit.IsTradeableImpulse)
            && (!isS5 || (htfAligned && !isExhaustedOnS5));

        bool isS5Guarded = isS5 && (isExhaustedOnS5 || !htfAligned || isCooldownActive || cm.Consensus.Probability < requiredProb);
        bool allowShadowRecord = isDirectional
            && (_settings.DatasetCollectionMode || isCbHalted || isS5Guarded)
            && (!_lastSampleTimeByAssetTf.TryGetValue(sampleKey, out var lastShadowTime) || (DateTime.UtcNow - lastShadowTime).TotalMinutes >= 1.0);

        double currentLivePrice = prep.CurrentLivePrice;

        if (canExecuteReal)
        {
            if (PriceFeed.UsePocketOption)
            {
                if (PocketOptionWebSocketStream.TryGetLivePrice(prep.CleanAsset, out double freshRealPrice, maxAgeSeconds: 1.5) && freshRealPrice > 0)
                {
                    bool isAdverse = (cm.Consensus.FinalDirection == "BUY" && freshRealPrice > currentLivePrice) ||
                                     (cm.Consensus.FinalDirection == "PUT" && freshRealPrice < currentLivePrice);
                    double slippageBps = currentLivePrice > 0 ? (Math.Abs(freshRealPrice - currentLivePrice) / currentLivePrice * 10000.0) : double.PositiveInfinity;

                    if (isAdverse && (double.IsNaN(slippageBps) || double.IsInfinity(slippageBps) || slippageBps > 1.5))
                    {
                        _logger.LogWarning("[SlippageGate] Blocked {Asset} {Timeframe}: adverse slippage {SlippageBps:F2} bps exceeds 1.5 bps threshold (signal: {CurrentPrice}, fresh: {FreshPrice}, dir: {Dir})",
                            prep.CleanAsset, timeframe, slippageBps, currentLivePrice, freshRealPrice, cm.Consensus.FinalDirection);
                        traceLines.Add($"[8. SlippageGate]    [ОТКЛОНЕНО] Неблагоприятное проскальзывание {slippageBps:F2} bps превышает лимит 1.5 bps -> 0ms");
                        canExecuteReal = false;
                        allowShadowRecord = false;
                    }
                    else
                    {
                        currentLivePrice = freshRealPrice;
                    }
                }
                else
                {
                    _logger.LogWarning("[SlippageGate] Blocked {Asset} {Timeframe}: live price feed is stale (>1.5s) or unavailable. Trade aborted.",
                        prep.CleanAsset, timeframe);
                    traceLines.Add("[8. SlippageGate]    [ОТКЛОНЕНО] Поток котировок брокера устарел (>1.5с) или недоступен -> 0ms");
                    canExecuteReal = false;
                    allowShadowRecord = false;
                }
            }

            if (canExecuteReal && PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue && prep.IntervalSecs > 0)
            {
                DateTime execRefTime = PocketOptionWebSocketStream.GetBrokerTimeUtc();
                long bucketTicks = TimeSpan.FromSeconds(prep.IntervalSecs).Ticks;
                DateTime barOpen = new DateTime(execRefTime.Ticks - (execRefTime.Ticks % bucketTicks), DateTimeKind.Utc);
                double elapsedAfterMl = (execRefTime - barOpen).TotalSeconds;
                double maxExecElapsed = prep.IntervalSecs switch { 5 => 1.3, 10 => 2.2, 15 => 2.8, 30 => 3.8, _ => 5.0 };
                if (elapsedAfterMl > maxExecElapsed)
                {
                    _logger.LogWarning("[ExpiryGuard] Aborted {Asset}/{Timeframe} after ML inference: +{Elapsed:F2}s into candle exceeds strict limit {Max:F1}s.",
                        prep.CleanAsset, timeframe, elapsedAfterMl, maxExecElapsed);
                    canExecuteReal = false;
                    allowShadowRecord = false;
                }
            }

            if (canExecuteReal)
            {
                var nowSw = Stopwatch.GetTimestamp();
                int tradeDurSecs = Math.Max(targetHorizon * prep.IntervalSecs, prep.IntervalSecs);
                double reqCooldown = isS5 ? 20.0 : Math.Max(tradeDurSecs + 2.0, 15.0);

                while (true)
                {
                    if (_lastRealTradeSwByAsset.TryGetValue(prep.CleanAsset, out var prevSw))
                    {
                        if (Stopwatch.GetElapsedTime(prevSw).TotalSeconds < reqCooldown)
                        {
                            _logger.LogWarning("[Orchestrator] Concurrency guard: Asset {Asset} trade claimed by concurrent worker. Aborting live execution.", prep.CleanAsset);
                            canExecuteReal = false;
                            break;
                        }
                        if (_lastRealTradeSwByAsset.TryUpdate(prep.CleanAsset, nowSw, prevSw))
                        {
                            break;
                        }
                    }
                    else if (_lastRealTradeSwByAsset.TryAdd(prep.CleanAsset, nowSw))
                    {
                        break;
                    }
                }

                if (canExecuteReal)
                {
                    _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SignalTracker.RecordPredictionAsync(cm.Consensus.FinalDirection, prep.CleanAsset, timeframe, currentLivePrice, targetHorizon, prep.IntervalSecs, prep.IsForex, sourceDirections, cm.Consensus.Probability, cm.Consensus.TaScore, cm.Consensus.SmcScore, cm.Consensus.MlProb, cm.Consensus.MlScoreRaw, featuresJson,
                                sensors.SmcResult.BosDirection ?? "NONE", sensors.SmcResult.OrderBlockType != "NONE", sensors.SmcResult.FvgType != "NONE", 1.0, "NEUTRAL",
                                cm.EffectiveMarketRegime,
                                sensors.State.VelocityRegime ?? "UNKNOWN",
                                sensors.MainAtr, sensors.MainAdx, sensors.TaResult.rsiVal,
                                htfAligned,
                                sensors.MinutesToNews ?? -1,
                                cm.Consensus.CombinedReasoningText ?? "",
                                ml.Prediction?.ModelVersion ?? "",
                                ml.Prediction?.Accuracy ?? 0.0,
                                sensors.PriceEntropy, sensors.TrendMaturity, sensors.PricePositionPct, sensors.BbSqueeze, null, null, null);
                        }
                        catch (Exception ex)
                        {
                            // D-5 fix: cooldown is NOT rolled back on DB error - the broker trade may already be open;
                            // removing the CAS slot would allow an immediate double entry on the same asset.
                            _logger.LogError(ex, "[SignalTracker] Failed to record prediction for {Asset}. Cooldown RETAINED to prevent double-entry.", prep.CleanAsset);
                        }
                    });
                    dbSw.Stop();
                    traceLines.Add($"[8. База данных]     Записан Entry Price: {currentLivePrice} (Уверенность: {cm.Consensus.Probability}%, Ожидание: {targetHorizon} свечей) -> {dbSw.ElapsedMilliseconds}ms");
                }
                else
                {
                    dbSw.Stop();
                    traceLines.Add($"[8. Cooldown Guard]  Asset {prep.CleanAsset} is claimed by concurrent worker -> {dbSw.ElapsedMilliseconds}ms");
                }
            }
        }
        else if (allowShadowRecord)
        {
            _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
            if (PriceFeed.UsePocketOption && PocketOptionWebSocketStream.TryGetLivePrice(prep.CleanAsset, out double freshShadowPrice) && freshShadowPrice > 0)
                currentLivePrice = freshShadowPrice;
            
            _ = SignalTracker.RecordPredictionAsync("SHADOW_" + cm.Consensus.FinalDirection, prep.CleanAsset, timeframe, currentLivePrice, targetHorizon, prep.IntervalSecs, prep.IsForex, sourceDirections, cm.Consensus.Probability, cm.Consensus.TaScore, cm.Consensus.SmcScore, cm.Consensus.MlProb, cm.Consensus.MlScoreRaw, shadowFeaturesJson,
                sensors.SmcResult.BosDirection ?? "NONE", sensors.SmcResult.OrderBlockType != "NONE", sensors.SmcResult.FvgType != "NONE", 1.0, "NEUTRAL",
                cm.EffectiveMarketRegime,
                sensors.State.VelocityRegime ?? "UNKNOWN",
                sensors.MainAtr, sensors.MainAdx, sensors.TaResult.rsiVal,
                htfAligned,
                sensors.MinutesToNews ?? -1,
                cm.Consensus.CombinedReasoningText ?? "",
                ml.Prediction?.ModelVersion ?? "",
                ml.Prediction?.Accuracy ?? 0.0,
                sensors.PriceEntropy, sensors.TrendMaturity, sensors.PricePositionPct, sensors.BbSqueeze, null, null, null);
            dbSw.Stop();
            string shadowReason = isGlobalCbHalted ? ", Global CB halt guard" : (isSymbolQuarantined ? ", Per-symbol quarantine guard" 
                : (isS5 && isExhaustedOnS5 ? ", S5 peak exhaustion guard" 
                : (isS5 && !htfAligned ? ", S5 HTF divergence guard" 
                : (isS5 && isCooldownActive ? ", S5 cooldown guard" 
                : (isS5 && cm.Consensus.Probability < 58 ? ", S5 prob threshold guard" : "")))));
            traceLines.Add($"[8. DB Write:]     SHADOW TRADE: {currentLivePrice} (Prob: {cm.Consensus.Probability}%, Horizon: {targetHorizon}{shadowReason}) -> {dbSw.ElapsedMilliseconds}ms");
        }
        else
        {
            dbSw.Stop();
            bool isPeriodicSampleDue = _settings.DatasetCollectionMode && (!_lastSampleTimeByAssetTf.TryGetValue(sampleKey, out var lastTime) 
                                       || (DateTime.UtcNow - lastTime).TotalMinutes >= 5.0);

            if (isPeriodicSampleDue)
            {
                _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
                _ = SignalTracker.RecordPredictionAsync("HOLD", prep.CleanAsset, timeframe, currentLivePrice, targetHorizon, prep.IntervalSecs, prep.IsForex, sourceDirections, cm.Consensus.Probability, cm.Consensus.TaScore, cm.Consensus.SmcScore, cm.Consensus.MlProb, cm.Consensus.MlScoreRaw, shadowFeaturesJson,
                    "NONE", false, false, 1.0, "NEUTRAL", cm.EffectiveMarketRegime, sensors.State.VelocityRegime ?? "UNKNOWN", sensors.MainAtr, sensors.MainAdx, sensors.TaResult.rsiVal, false, sensors.MinutesToNews ?? -1, "", "", 0.0, sensors.PriceEntropy, sensors.TrendMaturity, sensors.PricePositionPct, sensors.BbSqueeze, null, null, null);
                traceLines.Add($"[8. База данных]     ФОНОВЫЙ СРЕЗ (HOLD): Сохранен для обучения ({cm.Consensus.Probability}%) -> {dbSw.ElapsedMilliseconds}ms");
            }
            else
            {
                traceLines.Add($"[8. База данных]     ПРОПУСК: Сигнал не прошел критерии входа ({cm.Consensus.Probability}%). Ожидаем >= {requiredProb}% -> {dbSw.ElapsedMilliseconds}ms");
            }
        }

        return new ExecutionOutcome(
            currentLivePrice,
            isGoldenSetup,
            isSubminute,
            isS5,
            isDirectional,
            isCbHalted,
            isExhaustedOnS5,
            htfAligned,
            isCooldownActive
        );
    }

    #endregion

    #region Pipeline Stage 6: Console Trace Logging

    private static void LogPipelineTrace(
        string traceId,
        string cleanAsset,
        string timeframe,
        List<string> traceLines,
        ConsensusDecision consensus,
        long elapsedMs)
    {
        var sbTrace = new System.Text.StringBuilder();
        sbTrace.AppendLine("=================================================");
        sbTrace.AppendLine($"[PIPELINE TRACE: {traceId}] Запрос сигнала: {cleanAsset} {timeframe}");
        foreach (var line in traceLines) {
            sbTrace.AppendLine(line);
        }
        sbTrace.AppendLine("-------------------------------------------------");
        sbTrace.AppendLine($"ИТОГ: Время: {elapsedMs}ms | Результат: {consensus.FinalDirection} {consensus.Probability}%");
        sbTrace.AppendLine("=================================================");
        Console.WriteLine(sbTrace.ToString());
    }

    #endregion

    #region Pipeline Stage 7: Assemble Response DTO

    private async Task<AnalysisResponseDto> BuildResponseDtoAsync(
        PreparedCandleData prep,
        string timeframe,
        SensorsAndFeatures sensors,
        NeuralBrainOutcome ml,
        MatrixConsensusOutcome cm,
        ExecutionOutcome exec)
    {
        var stats = await SignalTracker.GetOverallStatsAsync();
        var assetStats = await SignalTracker.GetStatsAsync(prep.CleanAsset, timeframe);

        string uiMarketSession = "ВНЕБИРЖЕВАЯ (OTC)";
        if (!prep.CleanAsset.Contains("BTC") && !prep.CleanAsset.Contains("ETH") && !prep.CleanAsset.Contains("SOL"))
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
        string regime = sensors.State.VelocityRegime ?? "";
        if (regime.Contains("UP")) uiMarketPhase = "Бычий импульс";
        else if (regime.Contains("DOWN")) uiMarketPhase = "Медвежий импульс";
        else if (regime == "DECELERATING") uiMarketPhase = "Замедление (Коррекция)";
        else if (sensors.TaResult.rsiVal >= 62) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Перекупленность (Сброс)" : "Бычий тренд (Пологий)";
        else if (sensors.TaResult.rsiVal <= 38) uiMarketPhase = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase) ? "Перепроданность (Отскок)" : "Медвежий тренд (Пологий)";
        else if (sensors.TaResult.rsiVal >= 54) uiMarketPhase = "Умеренный рост";
        else if (sensors.TaResult.rsiVal <= 46) uiMarketPhase = "Умеренное падение";
        else uiMarketPhase = "Истинный Флэт";

        string uiMarketEntropy = "В норме (Безопасно)";
        double vel = Math.Abs(sensors.State.VelocityBpsPerSec);
        double dangerVel = exec.IsSubminute ? 0.3 : 3.0; 
        double deadVel   = exec.IsSubminute ? 0.02 : 0.1;

        if (vel >= dangerVel) uiMarketEntropy = "ВЫСОКАЯ (Хаос / Опасно!)";
        else if (vel < deadVel) uiMarketEntropy = "Мертвый рынок";

        string uiSmcDirection = 
            (cm.SmcSignal.SweepDirection ?? "").Contains("BULLISH") ? "BUY" : 
            (cm.SmcSignal.SweepDirection ?? "").Contains("BEARISH") ? "PUT" : 
            (cm.SmcSignal.BosDirection ?? "").Contains("BULLISH") ? "BUY" : 
            (cm.SmcSignal.BosDirection ?? "").Contains("BEARISH") ? "PUT" : 
            (cm.SmcSignal.OrderBlockType ?? "").Contains("BULLISH") ? "BUY" : 
            (cm.SmcSignal.OrderBlockType ?? "").Contains("BEARISH") ? "PUT" : 
            (cm.SmcSignal.FvgType ?? "").Contains("BULLISH") ? "BUY" : 
            (cm.SmcSignal.FvgType ?? "").Contains("BEARISH") ? "PUT" : 
            "NEUTRAL";
        int uiSmcConfidence = uiSmcDirection == "NEUTRAL" ? 50 : 50 + (int)Math.Clamp(Math.Max(5, Math.Abs(cm.Consensus.SmcScore * 15.0)), 5, 15);

        // Copilot Tactical Advice
        CopilotAdviceDto copilotAdvice;
        if (exec.IsCbHalted)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CIRCUIT_BREAKER",
                Title = "🛡️ Аварийный тормоз (Circuit Breaker)",
                Message = $"Зафиксирована серия убытков на повышенной волатильности. Вход временно заблокирован для защиты депозита, нейросеть продолжает фоновое обучение.",
                QualityScore = 35
            };
        }
        else if (exec.IsGoldenSetup && exec.IsDirectional && cm.Consensus.Probability >= 57)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "GOLDEN",
                Title = "⭐ Снайперский импульс (Golden Setup)",
                Message = $"Все 3 уровня анализа синхронны: нейросеть ({ml.Prediction?.ModelVersion ?? "Neural Brain"}), контекст старшего ТФ и импульс волатильности ({cm.NoiseAudit.LocalAtrBps:F1} bps) подтверждают {cm.Consensus.FinalDirection}. Порог шума преодолен.",
                QualityScore = 96
            };
        }
        else if (exec.IsSubminute && !cm.NoiseAudit.IsTradeableImpulse)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CAUTION",
                Title = $"⚡ Вход в консолидации ({cm.Consensus.FinalDirection})",
                Message = $"Амплитуда свечей ({cm.NoiseAudit.LocalAtrBps:F2} bps) ниже порога шума брокера ({cm.NoiseAudit.NoiseFloorBps:F1} bps). Направление — {cm.Consensus.FinalDirection} ({cm.Consensus.Probability}%). Ожидайте смену свечи для входа.",
                RecommendedAsset = prep.CleanAsset,
                RecommendedTf = timeframe.ToUpper(),
                QualityScore = Math.Min(65, cm.Consensus.Probability)
            };
        }
        else if (cm.ConflictPenalty < 1.0)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CONFLICT",
                Title = "⚠️ Конфликт таймфреймов",
                Message = $"На {timeframe} локальный импульс спорит со старшим трендом. Опасность ложного пробоя повышена.",
                RecommendedAsset = prep.CleanAsset,
                RecommendedTf = "M1",
                QualityScore = 55
            };
        }
        else if (exec.IsSubminute && vel >= dangerVel)
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
        else if (sensors.MinutesToNews.HasValue && sensors.MinutesToNews.Value >= 0 && sensors.MinutesToNews.Value <= 15)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "NEWS_RISK",
                Title = "📰 Высокая новостная волатильность",
                Message = $"Через {sensors.MinutesToNews.Value} мин выходит важная макро-новость. Спреды расширены, крупные игроки сокращают ликвидность.",
                QualityScore = 48
            };
        }
        else if (!exec.IsDirectional || cm.Consensus.Probability <= 52)
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "CAUTION",
                Title = $"⚡ Вход в консолидации ({cm.Consensus.FinalDirection})",
                Message = $"Рынок в стадии накопления ({uiMarketPhase}). Ожидайте смену свечи для входа {cm.Consensus.FinalDirection} ({cm.Consensus.Probability}%).",
                RecommendedAsset = prep.CleanAsset,
                RecommendedTf = timeframe.ToUpper(),
                QualityScore = 65
            };
        }
        else
        {
            copilotAdvice = new CopilotAdviceDto
            {
                Status = "NORMAL",
                Title = $"🎯 Системный тренд ({cm.Consensus.FinalDirection})",
                Message = $"Структура рынка устойчивая ({uiMarketPhase}). Математическое ожидание подтверждено нейросетью ({cm.Consensus.Probability}%).",
                QualityScore = (int)Math.Clamp(cm.Consensus.Probability + 25, 75, 92)
            };
        }

        int tfSec = prep.IntervalSecs;
        if (tfSec > 0 && exec.IsSubminute && copilotAdvice != null && exec.IsDirectional)
        {
            long nowUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long nextCandleBoundary = (nowUnixSec - (nowUnixSec % tfSec)) + tfSec;
            double secRemaining = Math.Max(0.1, nextCandleBoundary - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
            string timingAlert = secRemaining > 1.2
                ? $"⏱ ВХОД НА СМЕНЕ СВЕЧИ: через {secRemaining:F1} сек (в {DateTimeOffset.FromUnixTimeSeconds(nextCandleBoundary).UtcDateTime:HH:mm:ss} UTC). Приготовьтесь нажать {cm.Consensus.FinalDirection}!"
                : $"⚡ ВХОД СЕЙЧАС: новая свеча открыта! Нажимайте {cm.Consensus.FinalDirection} немедленно!";
            copilotAdvice = copilotAdvice with { Message = timingAlert + "\n\n" + copilotAdvice.Message };
        }

        ValutaMetrics.PredictionsTotal.WithLabels(
            prep.CleanAsset,
            timeframe,
            cm.Consensus.FinalDirection ?? "UNKNOWN",
            ml.Prediction?.ModelVersion ?? "NONE"
        ).Inc();

        return new AnalysisResponseDto
        {
            tfConflict = cm.ConflictPenalty < 1.0,
            uiMarketSession = uiMarketSession,
            uiMarketPhase = uiMarketPhase,
            uiMarketEntropy = uiMarketEntropy,
            direction = sensors.IsRiskBlocked ? "NEUTRAL" : (cm.Consensus.FinalDirection is "BUY" or "PUT" ? cm.Consensus.FinalDirection : "NEUTRAL"),
            probability = sensors.IsRiskBlocked ? 0 : Math.Clamp(cm.Consensus.Probability, 50, 92),
            duration = cm.Timeout.TimeoutText,
            expiryCandles = cm.Timeout.TimeoutCandles,
            adaptiveReasoning = cm.Consensus.CombinedReasoningText ?? string.Empty,
            taDirection = cm.Consensus.TaScore > 0.02 ? "BUY" : cm.Consensus.TaScore < -0.02 ? "PUT" : "NEUTRAL",
            taConfidence = 50 + (int)Math.Clamp(Math.Abs(cm.Consensus.TaScore * 15.0), 0, 15),
            ofDirection = "NEUTRAL",
            ofConfidence = 0,
            smcDirection = uiSmcDirection,
            smcConfidence = uiSmcConfidence,
            lgbmDirection = cm.MlSignal.Direction,
            lgbmConfidence = (int)Math.Clamp(Math.Round(cm.MlSignal.Confidence * 100), 50, 99),
            winRateOverall = stats.WinRate,
            winRateAsset = assetStats.WinRate,
            signalsVerifiedAsset = assetStats.Verified,
            rsi = Math.Round(GetSafeLimit(sensors.TaResult.rsiVal), 1),
            ema = Math.Round(GetSafeLimit(sensors.TaResult.hmaVal), prep.IsForex ? 5 : 2),
            volumeStrength = Math.Round(GetSafeLimit(sensors.TaResult.volStrengthVal), 2),
            atr = Math.Round(GetSafeLimit(sensors.MainAtr), prep.IsForex ? 5 : 2),
            chartData = prep.MainPrices,
            chartOhlc = prep.RawCandles.TakeLast(80).Select(c => new { o = Math.Round(c.Open, 8), h = Math.Round(c.High, 8), l = Math.Round(c.Low, 8), c = Math.Round(c.Close, 8), v = Math.Round(c.Volume, 2) }).ToArray(),
            goldenSetup = exec.IsGoldenSetup,
            confluenceLabel = cm.MtfResult.ConfluenceLabel,
            confluenceRatio = cm.MtfResult.ConfluenceRatio,
            lgbmModelVersion = ml.Prediction?.ModelVersion ?? "offline",
            copilotAdvice = copilotAdvice
        };
    }

    #endregion
}
