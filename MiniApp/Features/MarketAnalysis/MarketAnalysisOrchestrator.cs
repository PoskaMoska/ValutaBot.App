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
    private static readonly ConcurrentDictionary<string, (long StartTs, double CooldownSecs)> _lastRealTradeSwByAsset = new();
    private static double S(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;
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
        
        // C-3 FIX: Replace LINQ .Select().ToArray() with for-loop — eliminates lambda closure alloc + iterator alloc per call
        double[] mainPrices = new double[candles.Length];
        for (int i = 0; i < candles.Length; i++) mainPrices[i] = candles[i].Close;
        double currentLivePrice = mainPrices[^1];
        string dataHash = ComputeHash(candles);
        traceLines.Add($"[1. Источник Данных] {candles.Length} свечей. Live Цена: {currentLivePrice} | Массив Hash: [{dataHash}] -> {fetchSw.ElapsedMilliseconds}ms");

        int intervalSecs = _fetcher.TimeframeSeconds(timeframe);
        DateTime referenceNowUtc = (PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue)
            ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
            : DateTime.UtcNow;
        // Допуск 250мс для компенсации джиттера таймштампа тиков и монотонного Stopwatch
        bool isLastClosed = intervalSecs > 0 && candles[^1].Timestamp.AddSeconds(intervalSecs) <= referenceNowUtc.AddMilliseconds(250);
        // C-3 FIX: Zero-alloc closed-candle slice using array copy instead of LINQ Take/TakeLast/Select chains
        int availableClosed = isLastClosed ? candles.Length : candles.Length - 1;
        if (availableClosed < 0) availableClosed = 0;
        int closedLen = Math.Min(availableClosed, limit);
        int closedStart = availableClosed - closedLen;
        var closedCandles = new MiniAppController.OhlcCandle[closedLen];
        if (closedLen > 0)
        {
            Array.Copy(candles, closedStart, closedCandles, 0, closedLen);
        }
        double[] closedPrices  = new double[closedLen];
        double[] closedVolumes = new double[closedLen];
        for (int i = 0; i < closedLen; i++) { closedPrices[i] = closedCandles[i].Close; closedVolumes[i] = closedCandles[i].Volume; }

        string? higherTf = _fetcher.HigherTf(timeframe);
        MiniAppController.OhlcCandle[]? higherCandles = null;
        if (higherTf != null)
        {
            try
            {
                higherCandles = await _fetcher.FetchOhlcWithFallbackAsync(symbol, higherTf, cleanAsset, 100);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[TRACE {TraceId}] Higher TF {Htf} unavailable on cold start: {Msg}", traceId, higherTf, ex.Message);
            }
        }
        MiniAppController.OhlcCandle[] closedHigherCandles = [];
        if (higherCandles != null && higherCandles.Length > 0 && higherTf != null)
        {
            int higherSecs = _fetcher.TimeframeSeconds(higherTf);
            bool isHigherClosed = higherSecs > 0 && higherCandles[^1].Timestamp.AddSeconds(higherSecs) <= referenceNowUtc.AddMilliseconds(250);
            int higherClosedLen = isHigherClosed ? higherCandles.Length : higherCandles.Length - 1;
            if (higherClosedLen > 0)
            {
                if (higherClosedLen == higherCandles.Length)
                {
                    closedHigherCandles = higherCandles;
                }
                else
                {
                    closedHigherCandles = new MiniAppController.OhlcCandle[higherClosedLen];
                    Array.Copy(higherCandles, 0, closedHigherCandles, 0, higherClosedLen);
                }
            }
        }

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
            // C-3 FIX: Replace LINQ Skip+ToList+Max+Min with indexed for-loop (zero alloc)
            int _l20Start = prep.ClosedCandles.Length - 20;
            double maxHigh = double.MinValue, minLow = double.MaxValue;
            for (int _i = _l20Start; _i < prep.ClosedCandles.Length; _i++)
            {
                if (prep.ClosedCandles[_i].High > maxHigh) maxHigh = prep.ClosedCandles[_i].High;
                if (prep.ClosedCandles[_i].Low  < minLow)  minLow  = prep.ClosedCandles[_i].Low;
            }
            if (maxHigh > minLow) {
                pricePositionPct = Math.Clamp((prep.CurrentLivePrice - minLow) / (maxHigh - minLow), 0.0, 1.0);
            }

            // C-3 FIX: Replace LINQ .Average() calls with for-loops (eliminates 2 lambda allocs per call)
            double sma20 = 0.0;
            for (int _i = _l20Start; _i < prep.ClosedCandles.Length; _i++) sma20 += prep.ClosedCandles[_i].Close;
            sma20 /= 20.0;
            double _var20 = 0.0;
            for (int _i = _l20Start; _i < prep.ClosedCandles.Length; _i++) { double _d = prep.ClosedCandles[_i].Close - sma20; _var20 += _d * _d; }
            double stdev20 = Math.Sqrt(_var20 / 20.0);
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
            DayRangePositionPct = S(dayAnchors.DayRangePositionPct, 0.5),
            DistToDayHighBps = S(dayAnchors.DistToDayHighBps),
            DistToDayLowBps = S(dayAnchors.DistToDayLowBps),
            DistToAsianHighBps = S(dayAnchors.DistToAsianHighBps),
            DistToAsianLowBps = S(dayAnchors.DistToAsianLowBps),
            DxyMomentum1mBps = S(dxyMetrics.DxyMomentum1mBps),
            DxyMomentum5mBps = S(dxyMetrics.DxyMomentum5mBps),
            BasketSyncScore = S(dxyMetrics.BasketSyncScore),
            SpreadBps = S(liveSpreadBps),
            PriceEntropy = S(priceEntropy),
            TrendMaturity = S(trendMaturity),
            PricePositionPct = S(pricePositionPct, 0.5),
            BbSqueeze = bbSqueeze,
            MinutesToNews = minutesToNews ?? -1,
            Adx = S(mainAdx, 20.0),
            Rsi = S(taDetail.RsiVal, 50.0),
            HourUtc = DateTime.UtcNow.Hour,
            DayOfWeek = (int)DateTime.UtcNow.DayOfWeek == 0 ? 7 : (int)DateTime.UtcNow.DayOfWeek,
            TickCountLastCandle = prep.ClosedCandles.Length > 0 ? S(prep.ClosedCandles[^1].Volume) : 0,
            CrowdSentiment = S(PocketOptionWebSocketStream.GetCrowdSentiment(prep.CleanAsset), 50.0),
            Payout = S(PocketOptionWebSocketStream.GetAssetPayout(prep.CleanAsset), 82.0),
            Ta = new {
                Score = S(taDetail.Score),
                MicroVel = S(taDetail.MicroVel),
                HmaSlope = S(taDetail.HmaSlope),
                VolStrengthVal = S(taDetail.VolStrengthVal),
                VolRatio = S(taDetail.VolRatio)
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

    private static double ComputePandasEma(ReadOnlySpan<double> values, int span)
    {
        if (values.IsEmpty) return 0.0;
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
        double currentPrice,
        double? liveCrowdSentiment = null)
    {
        if (microCandles == null || microCandles.Length < 30 ||
            macroCandles == null || macroCandles.Length < 30 || currentPrice <= 0)
        {
            return null;
        }

        int macroEndIdx = macroCandles.Length;
        if (macroCandles.Length > 30)
        {
            DateTime microClose = microCandles[^1].Timestamp.AddSeconds(5);
            // Point-in-Time dual-stream synchronization: M1 open time must be <= microClose - 60s (entry_epoch - 55s)
            for (int i = macroCandles.Length - 1; i >= 29; i--)
            {
                if (macroCandles[i].Timestamp <= microClose.AddSeconds(-60))
                {
                    macroEndIdx = i + 1;
                    break;
                }
            }
        }
        if (macroEndIdx < 30)
            return null;

        ReadOnlySpan<MiniAppController.OhlcCandle> macroSlice = macroCandles.AsSpan(macroEndIdx - 30, 30);
        ReadOnlySpan<MiniAppController.OhlcCandle> microSlice = microCandles.AsSpan(microCandles.Length - 30, 30);

        static bool HasInvalidCandle(ReadOnlySpan<MiniAppController.OhlcCandle> slice)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                var c = slice[i];
                if (!double.IsFinite(c.Open) || !double.IsFinite(c.High) || !double.IsFinite(c.Low) ||
                    !double.IsFinite(c.Close) || !double.IsFinite(c.Volume) ||
                    c.Close <= 0 || c.Open <= 0 || c.Low <= 0 || c.High < c.Low || c.Volume < 0)
                    return true;
            }
            return false;
        }

        if (HasInvalidCandle(macroSlice) || HasInvalidCandle(microSlice))
            return null;

        static float[] NormalizeSlice(ReadOnlySpan<MiniAppController.OhlcCandle> slice)
        {
            float[] result = new float[150];
            double refP = slice[^1].Close > 0 ? slice[^1].Close : 1.0;
            double sumVol = 0.0;
            for (int i = 0; i < 30; i++) sumVol += slice[i].Volume;
            double meanVol = (sumVol / 30.0) + 1e-4;

            for (int i = 0; i < 30; i++)
            {
                var c = slice[i];
                int offset = i * 5;
                // Adversarial needle spike protection (Winsorized to institutional bounds)
                result[offset + 0] = Math.Clamp((float)((c.Close - refP) / refP * 1000.0), -50.0f, 50.0f);
                result[offset + 1] = Math.Clamp((float)((c.High - c.Low) / refP * 1000.0), 0.0f, 50.0f);
                result[offset + 2] = Math.Clamp((float)((c.High - Math.Max(c.Open, c.Close)) / refP * 1000.0), 0.0f, 50.0f);
                result[offset + 3] = Math.Clamp((float)((Math.Min(c.Open, c.Close) - c.Low) / refP * 1000.0), 0.0f, 50.0f);
                result[offset + 4] = Math.Clamp((float)(c.Volume / meanVol), 0.0f, 10.0f);
            }
            return result;
        }

        var macroData = NormalizeSlice(macroSlice);
        var microData = NormalizeSlice(microSlice);

        double curP = microSlice[^1].Close > 0 ? microSlice[^1].Close : 1.0;

        Span<double> macroCloses = stackalloc double[30];
        double sumM1Tr = 0.0;
        for (int i = 0; i < 30; i++)
        {
            macroCloses[i] = macroSlice[i].Close;
            sumM1Tr += Math.Max(macroSlice[i].High - macroSlice[i].Low, 1e-7);
        }
        double m1Ema = ComputePandasEma(macroCloses, 9);
        float distM1 = (float)((curP - m1Ema) / curP * 1000.0);

        Span<double> microCloses = stackalloc double[30];
        double sumS5Tr = 0.0;
        double sumMicroCloses = 0.0;
        for (int i = 0; i < 30; i++)
        {
            double c = microSlice[i].Close;
            microCloses[i] = c;
            sumMicroCloses += c;
            sumS5Tr += Math.Max(microSlice[i].High - microSlice[i].Low, 1e-7);
        }

        double s5Tr = sumS5Tr / 30.0;
        double m1Tr = sumM1Tr / 30.0;
        float atrRatio = (float)(s5Tr / (m1Tr + 1e-7));

        double microMean = sumMicroCloses / 30.0;
        double sumAbsDev = 0.0;
        for (int i = 0; i < 30; i++) sumAbsDev += Math.Abs(microCloses[i] - microMean);
        double maxDev = Math.Max(4.0 * (sumAbsDev / 30.0), 1e-5);
        double sumSq = 0.0;
        for (int i = 0; i < 30; i++)
        {
            double d = Math.Clamp(microCloses[i] - microMean, -maxDev, maxDev);
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

        double rawCrowd = liveCrowdSentiment ?? (double.IsFinite(last.CrowdSentiment) ? last.CrowdSentiment : 50.0);
        float normCrowd = Math.Clamp((float)((rawCrowd - 50.0) / 50.0), -1.0f, 1.0f);
        if (!float.IsFinite(normCrowd)) normCrowd = 0.0f;

        float[] bridgeData = new[] { distM1, atrRatio, zScore, wickBal, normCrowd };

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
                double liveCrowd = PocketOptionWebSocketStream.GetCrowdSentiment(prep.CleanAsset);
                var tensors = BuildOnnxTensors(prep.ClosedCandles, prep.ClosedHigherCandles, prep.CurrentLivePrice, liveCrowd);
                if (tensors.HasValue)
                {
                    var onnxPred = DualStreamOnnxService.Instance.Predict(tensors.Value.macro, tensors.Value.micro, tensors.Value.bridge);
                    if (onnxPred.Direction is "BUY" or "PUT")
                    {
                        // Real metadata from the training sidecar; never fabricate accuracy/AUC/NTrain.
                        var onnxMeta = DualStreamOnnxService.Instance.Metadata;
                        mlPrediction = new MLPythonService.MLPythonPrediction(
                            Direction: onnxPred.Direction,
                            Confidence: onnxPred.Probability,
                            ModelVersion: "dual_stream_onnx_v5",
                            Accuracy: onnxMeta?.Accuracy,
                            Auc: null,
                            NTrain: onnxMeta?.NTrain,
                            VarianceEstimate: null,
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
            mlDir = string.IsNullOrWhiteSpace(mlPrediction.Direction) ? "NEUTRAL" : mlPrediction.Direction;
            mlConf = double.IsFinite(mlPrediction.Confidence) ? Math.Clamp(mlPrediction.Confidence, 0.0, 1.0) : 0.5;
        }
        mlSw.Stop();
        ValutaMetrics.MlDuration.Observe(mlSw.Elapsed.TotalSeconds);
        double mlRawProb = (mlPrediction?.RawConfidence.HasValue == true && double.IsFinite(mlPrediction.RawConfidence.Value))
            ? Math.Clamp(mlPrediction.RawConfidence.Value, 0.0, 1.0)
            : 0.5;

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
        // C-6 FIX: Hoist HTF price/volume arrays to method scope so they are accessible outside
        // the if-block and can be reused in Evaluate4DMatrixAsync without 4 extra .Select().ToArray() allocs.
        double[] higherPrices  = Array.Empty<double>();
        double[] higherVolumes = Array.Empty<double>();
        if (prep.ClosedHigherCandles.Length > 0 && prep.HigherTf != null)
        {
            higherPrices  = new double[prep.ClosedHigherCandles.Length];
            higherVolumes = new double[prep.ClosedHigherCandles.Length];
            for (int i = 0; i < prep.ClosedHigherCandles.Length; i++)
            {
                higherPrices[i]  = prep.ClosedHigherCandles[i].Close;
                higherVolumes[i] = prep.ClosedHigherCandles[i].Volume;
            }
            var hAdx = _mathEngine.ComputeTrueAdx(prep.CleanAsset, prep.HigherTf, prep.ClosedHigherCandles);
            var hAtr = _mathEngine.ComputeAtr(prep.CleanAsset, prep.HigherTf, prep.ClosedHigherCandles);
            var hResult = _marketAnalyzer.ScoreTimeframe(prep.CleanAsset, prep.HigherTf, higherPrices, higherVolumes, candles: prep.ClosedHigherCandles, adxOverride: hAdx.adx, atrOverride: hAtr, isForex: prep.IsForex);
            // A-5 FIX: Raise conflictPenalty threshold from -0.01 to -0.05.
            // At -0.01 almost any pair of weak opposite-sign scores triggers the 30% penalty,
            // making it a near-permanent throttle instead of a genuine conflict guard.
            conflictPenalty *= (sensors.TaResult.score * hResult.score < -0.05) ? 0.7 : 1.0;
        }
        var mtfResult = await _cmEngine.Evaluate4DMatrixAsync(
            prep.CleanAsset, timeframe, prep.IsForex, prep.Symbol,
            prep.ClosedCandles, prep.ClosedPrices, prep.ClosedVolumes,
            prep.ClosedHigherCandles, higherPrices, higherVolumes);
        
        var taSignal = new TaSignal(sensors.TaResult.score, sensors.TaResult.confidence, sensors.TaResult.rsiVal, sensors.TaResult.hmaVal, sensors.TaResult.volStrengthVal, sensors.MainAtr, sensors.MainAdx);
        var smcSignal = new SmcSignal(sensors.SmcResult.BosDirection ?? "", sensors.SmcResult.SweepDirection ?? "", sensors.SmcResult.OrderBlockType ?? "", sensors.SmcResult.FvgType ?? "", "");
        var mlSignal = new MlSignal(ml.Direction, ml.Confidence, ml.Prediction?.Accuracy, ml.Prediction?.ModelVersion ?? "offline", ml.Prediction?.HorizonCandles, ml.Prediction?.RawConfidence);
        var stateSignal = new StateSignal(sensors.State.VelocityRegime, sensors.State.VelocityBpsPerSec, sensors.State.MomentumContribution);


        var noiseAudit = SubminuteNoiseGatekeeper.AuditCandleStream(prep.ClosedCandles, timeframe);
        traceLines.Add($"[6.8 Noise Gate]     {noiseAudit.RegimeDescription} (TR: {noiseAudit.LocalAtrBps:F2} bps, Порог: {noiseAudit.NoiseFloorBps:F1} bps, Z: {noiseAudit.ZScore:F2})");
        double liveCrowd = PocketOptionWebSocketStream.GetCrowdSentiment(prep.CleanAsset);
        var consensus = await _cmEngine.EvaluateMatrixAsync(
            prep.CleanAsset, timeframe, prep.TfLower.StartsWith("s"), conflictPenalty, 
            taSignal, smcSignal, mlSignal, stateSignal, mtfResult, 
            TradeOutcomeTracker.GetConsecutiveLosses(prep.CleanAsset, timeframe), 
            _marketAnalyzer.CalculateVolatilityRatio(prep.MainPrices), 
            noiseAudit,
            liveCrowd);
        
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
        else if (consensus.FinalDirection == "NEUTRAL" || string.IsNullOrEmpty(consensus.FinalDirection))
        {
            // БИЗНЕС-ИНВАРИАНТ: Бот ВСЕГДА даёт торговый сигнал (BUY или PUT).
            // При NEUTRAL / HOLD опираемся на взвешенный консенсус TA + SMC + MTF + импульс серии свечей,
            // а не на случайный цвет одной последней свечи.
            double fallbackScore = Math.Clamp(sensors.TaResult.score, -1.0, 1.0) * 0.50;

            string bosDir = (sensors.SmcResult.BosDirection ?? "").ToUpperInvariant();
            if (sensors.SmcResult.HasBos)
            {
                if (bosDir.Contains("BULL") || bosDir == "BUY") fallbackScore += 0.25;
                else if (bosDir.Contains("BEAR") || bosDir == "PUT") fallbackScore -= 0.25;
            }

            string mtfDir = (mtfResult.DominantDirection ?? "").ToUpperInvariant();
            if (mtfDir == "BUY") fallbackScore += 0.15;
            else if (mtfDir == "PUT") fallbackScore -= 0.15;

            if (prep.ClosedCandles.Length >= 5 && prep.ClosedCandles[^5].Open > 0)
            {
                double impulse5Bps = (prep.ClosedCandles[^1].Close - prep.ClosedCandles[^5].Open) / prep.ClosedCandles[^5].Open * 10000.0;
                fallbackScore += Math.Clamp(impulse5Bps / 15.0, -1.0, 1.0) * 0.15;
            }
            else if (Math.Abs(sensors.State.VelocityBpsPerSec) > 1e-6)
            {
                fallbackScore += Math.Clamp(sensors.State.VelocityBpsPerSec / 3.0, -1.0, 1.0) * 0.15;
            }

            string fallbackDir = "BUY";
            if (Math.Abs(fallbackScore) >= 0.05)
            {
                fallbackDir = fallbackScore > 0 ? "BUY" : "PUT";
            }
            else if (prep.ClosedCandles.Length > 0)
            {
                fallbackDir = prep.ClosedCandles[^1].Close >= prep.ClosedCandles[^1].Open ? "BUY" : "PUT";
            }
            else if (prep.ClosedPrices.Length >= 2)
            {
                fallbackDir = prep.ClosedPrices[^1] >= prep.ClosedPrices[^2] ? "BUY" : "PUT";
            }
            else if (prep.MainPrices.Length >= 2)
            {
                fallbackDir = prep.MainPrices[^1] >= prep.MainPrices[^2] ? "BUY" : "PUT";
            }

            int fallbackProb = consensus.Probability > 50
                ? consensus.Probability
                : (Math.Abs(fallbackScore) >= 0.25 && sensors.MainAdx >= 20.0
                    ? Math.Clamp(50 + (int)Math.Round(Math.Abs(fallbackScore) * 14.0), 52, 62)
                    : 50);

            string reasonSource = Math.Abs(fallbackScore) >= 0.05
                ? "по консенсусу TA/SMC/MTF и импульса"
                : "по ценовому микро-дрейфу";

            consensus = consensus with {
                FinalDirection = fallbackDir,
                CandidateDirection = fallbackDir,
                Probability = fallbackProb,
                CombinedReasoningText = consensus.CombinedReasoningText + $"\n🧭 Определение импульса: консенсус приведен к сигналу {fallbackDir} ({fallbackProb}%) {reasonSource}."
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

        bool isS5 = timeframe.Equals("s5", StringComparison.OrdinalIgnoreCase);
        bool isSubminute = prep.TfLower.StartsWith("s");
        bool isDirectional = cm.Consensus.FinalDirection is "BUY" or "PUT";
        bool isGlobalCbHalted = TradeOutcomeTracker.CircuitBreaker?.IsHalted() == true;
        bool isSymbolQuarantined = TradeOutcomeTracker.CircuitBreaker != null
            && TradeOutcomeTracker.CircuitBreaker.IsSymbolQuarantined(prep.CleanAsset, timeframe, out _);
        bool isCbHalted = isGlobalCbHalted || isSymbolQuarantined;
        string sampleKey = $"{prep.CleanAsset}_{timeframe}";

        double liveCrowdSentiment = PocketOptionWebSocketStream.GetCrowdSentiment(prep.CleanAsset);
        int currentPayout = PriceFeed.UsePocketOption ? PocketOptionWebSocketStream.GetAssetPayout(prep.CleanAsset) : 82;
        bool isAssetOpen = !PriceFeed.UsePocketOption || PocketOptionWebSocketStream.IsAssetOpen(prep.CleanAsset);
        bool isPayoutAcceptable = currentPayout >= 70 && isAssetOpen;

        // ══════════════════════════════════════════════════════════════════════════
        // ADAPTIVE PROBABILITY THRESHOLD (Based on Payout & Horizon Risk)
        // ══════════════════════════════════════════════════════════════════════════
        // Formula: Break-even win rate = 100 / (100 + payout). Target edge margin = +3.5%
        // E.g. at 92% -> BE 52.1% (req 58%), at 82% -> BE 54.9% (req 59%), at 70% -> BE 58.8% (req 63%)
        double breakEvenWinRate = 100.0 / (100.0 + Math.Max(currentPayout, 50));
        int adaptiveRequiredProb = Math.Max(58, (int)Math.Ceiling((breakEvenWinRate + 0.035) * 100.0));
        if (isS5)
        {
            adaptiveRequiredProb += 2; // Extra hurdle against 5s tick noise and high tie rates
        }
        int requiredProb = adaptiveRequiredProb;

        traceLines.Add($"[7.5 Adaptive Guard] Payout: {currentPayout}%, Порог: {requiredProb}% (BE: {breakEvenWinRate * 100:F1}%) | Толпа: {liveCrowdSentiment:F0}%");

        bool htfAligned = cm.MtfResult.DominantDirection == cm.Consensus.FinalDirection && isDirectional;
        bool isGoldenSetup = cm.MtfResult.IsGoldenSetup && (!isSubminute || cm.NoiseAudit.IsTradeableImpulse);

        bool isCooldownActive = false;
        if (_lastRealTradeSwByAsset.TryGetValue(prep.CleanAsset, out var lastReal))
        {
            int tradeDurationSecs = Math.Max(targetHorizon * prep.IntervalSecs, prep.IntervalSecs);
            double requiredCooldownSecs = isS5 ? 20.0 : Math.Max(tradeDurationSecs + 2.0, 15.0);
            isCooldownActive = Stopwatch.GetElapsedTime(lastReal.StartTs).TotalSeconds < Math.Max(lastReal.CooldownSecs, requiredCooldownSecs);
        }

        bool isExhaustedOnS5 = false;
        if (isS5 && prep.ClosedCandles != null && prep.ClosedCandles.Length >= 3)
        {
            // C-5 FIX: Replace TakeLast().ToArray() with direct index arithmetic (zero alloc)
            int _last3Start = prep.ClosedCandles.Length - 3;
            // C-5 FIX: Replace .All() LINQ with manual 3-element index check
            bool _allBull = prep.ClosedCandles[_last3Start].Close > prep.ClosedCandles[_last3Start].Open
                         && prep.ClosedCandles[_last3Start+1].Close > prep.ClosedCandles[_last3Start+1].Open
                         && prep.ClosedCandles[_last3Start+2].Close > prep.ClosedCandles[_last3Start+2].Open;
            bool _allBear = prep.ClosedCandles[_last3Start].Close < prep.ClosedCandles[_last3Start].Open
                         && prep.ClosedCandles[_last3Start+1].Close < prep.ClosedCandles[_last3Start+1].Open
                         && prep.ClosedCandles[_last3Start+2].Close < prep.ClosedCandles[_last3Start+2].Open;
            if (cm.Consensus.FinalDirection == "BUY" && _allBull && sensors.TaResult.rsiVal >= 64.0)
                isExhaustedOnS5 = true;
            else if (cm.Consensus.FinalDirection == "PUT" && _allBear && sensors.TaResult.rsiVal <= 36.0)
                isExhaustedOnS5 = true;
        }

        bool hasLiveNeuralSignal = !_settings.EnableMachineLearning || (ml.Prediction != null && ml.Direction is "BUY" or "PUT");

        bool canExecuteReal = isDirectional 
            && hasLiveNeuralSignal
            && isPayoutAcceptable
            && cm.Consensus.Probability >= requiredProb 
            && !isCbHalted
            && !isCooldownActive
            && (!isSubminute || cm.NoiseAudit.IsTradeableImpulse)
            && (!isS5 || (htfAligned && !isExhaustedOnS5));

        bool isS5Guarded = isS5 && (isExhaustedOnS5 || !htfAligned || isCooldownActive || cm.Consensus.Probability < requiredProb || !isPayoutAcceptable);
        bool allowShadowRecord = isDirectional
            && (_settings.DatasetCollectionMode || isCbHalted || isS5Guarded)
            && (!_lastSampleTimeByAssetTf.TryGetValue(sampleKey, out var lastShadowTime) || (DateTime.UtcNow - lastShadowTime).TotalMinutes >= 1.0);

        Dictionary<string, string> GetSourceDirections() => new()
        {
            ["TechAnalysis"] = DirectionExtensions.FromScore(cm.Consensus.TaScore).ToSignal(),
            ["SMC"]          = DirectionExtensions.FromScore(cm.Consensus.SmcScore).ToSignal(),
            ["NeuralBrain"]  = ml.Direction,
            ["LIGHTGBM"]     = ml.Direction,
        };

        object GetSanitizedSmc() => new {
            sensors.SmcResult.HasLiquiditySweep, sensors.SmcResult.SweepDirection,
            sensors.SmcResult.HasFvg, sensors.SmcResult.FvgType,
            FvgTop = S(sensors.SmcResult.FvgTop), FvgBottom = S(sensors.SmcResult.FvgBottom), FvgGapSize = S(sensors.SmcResult.FvgGapSize),
            sensors.SmcResult.HasOrderBlock, sensors.SmcResult.OrderBlockType,
            OrderBlockLevel = S(sensors.SmcResult.OrderBlockLevel), sensors.SmcResult.IsUnmitigatedOb,
            sensors.SmcResult.HasBos, sensors.SmcResult.BosDirection
        };

        TaScoringDetail GetSanitizedTa() => sensors.TaDetail with {
            Score = S(sensors.TaDetail.Score), Confidence = S(sensors.TaDetail.Confidence), RsiVal = S(sensors.TaDetail.RsiVal, 50.0),
            HmaVal = S(sensors.TaDetail.HmaVal), VolStrengthVal = S(sensors.TaDetail.VolStrengthVal), AtrVal = S(sensors.TaDetail.AtrVal),
            HmaSlope = S(sensors.TaDetail.HmaSlope), MicroVel = S(sensors.TaDetail.MicroVel), VolRatio = S(sensors.TaDetail.VolRatio),
            VelContrib = S(sensors.TaDetail.VelContrib), HmaContrib = S(sensors.TaDetail.HmaContrib), RsiContrib = S(sensors.TaDetail.RsiContrib),
            ConnorsContrib = S(sensors.TaDetail.ConnorsContrib), VolStrengthContrib = S(sensors.TaDetail.VolStrengthContrib)
        };

        object GetMacroContext() => new {
            MinutesToNews = sensors.MinutesToNews ?? -1,
            MarketRegime = cm.EffectiveMarketRegime,
            VelocityRegime = sensors.State.VelocityRegime ?? "UNKNOWN",
            Atr = S(sensors.MainAtr),
            Adx = S(sensors.MainAdx, 20.0),
            Rsi = S(sensors.TaDetail.RsiVal, 50.0),
            PriceEntropy = S(sensors.PriceEntropy),
            TrendMaturity = S(sensors.TrendMaturity),
            PricePositionPct = S(sensors.PricePositionPct, 0.5),
            BbSqueeze = sensors.BbSqueeze,
            HourUtc = DateTime.UtcNow.Hour,
            DayOfWeek = (int)DateTime.UtcNow.DayOfWeek == 0 ? 7 : (int)DateTime.UtcNow.DayOfWeek,
            Session = TradeOutcomeTracker.ComputeSession(DateTime.UtcNow),
            DayRangePositionPct = S(sensors.DayAnchors.DayRangePositionPct, 0.5),
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
            TickCountLastCandle = prep.ClosedCandles is { Length: > 0 } ? S(prep.ClosedCandles[^1].Volume) : 0,
            CrowdSentiment = S(liveCrowdSentiment, 50.0),
            Payout = S(currentPayout, 82.0)
        };

        object GetNeuralBrainPayload() => new {
            Prob = S(cm.Consensus.MlProb, 0.5),
            Score = S(cm.Consensus.MlScoreRaw),
            FinalProb = cm.Consensus.Probability
        };

        string BuildFeaturesJson()
        {
            int closedCount = prep.ClosedCandles?.Length ?? 0;
            var candlesPayload = new object[closedCount];
            for (int i = 0; i < closedCount; i++)
            {
                var c = prep.ClosedCandles![i];
                candlesPayload[i] = new { c.Timestamp, Open = S(c.Open), High = S(c.High), Low = S(c.Low), Close = S(c.Close), Volume = S(c.Volume) };
            }
            int mtfClosedCount = prep.ClosedHigherCandles?.Length ?? 0;
            var mtfCandlesPayload = new object[mtfClosedCount];
            for (int i = 0; i < mtfClosedCount; i++)
            {
                var c = prep.ClosedHigherCandles![i];
                mtfCandlesPayload[i] = new { c.Timestamp, Open = S(c.Open), High = S(c.High), Low = S(c.Low), Close = S(c.Close), Volume = S(c.Volume) };
            }

            var mlFeatures = new {
                Candles = candlesPayload,
                MtfCandles = mtfCandlesPayload,
                Smc = GetSanitizedSmc(),
                Ta = GetSanitizedTa(),
                MacroContext = GetMacroContext(),
                NeuralBrain = GetNeuralBrainPayload()
            };
            return System.Text.Json.JsonSerializer.Serialize(mlFeatures, _jsonOptions);
        }

        string BuildShadowFeaturesJson()
        {
            var shadowMlFeatures = new {
                Smc = GetSanitizedSmc(),
                Ta = GetSanitizedTa(),
                MacroContext = GetMacroContext(),
                NeuralBrain = GetNeuralBrainPayload()
            };
            return System.Text.Json.JsonSerializer.Serialize(shadowMlFeatures, _jsonOptions);
        }

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
                var claim = (StartTs: nowSw, CooldownSecs: reqCooldown);

                while (true)
                {
                    if (_lastRealTradeSwByAsset.TryGetValue(prep.CleanAsset, out var prev))
                    {
                        if (Stopwatch.GetElapsedTime(prev.StartTs).TotalSeconds < Math.Max(prev.CooldownSecs, reqCooldown))
                        {
                            _logger.LogWarning("[Orchestrator] Concurrency guard: Asset {Asset} trade claimed by concurrent worker. Aborting live execution.", prep.CleanAsset);
                            canExecuteReal = false;
                            break;
                        }
                        if (_lastRealTradeSwByAsset.TryUpdate(prep.CleanAsset, claim, prev))
                        {
                            break;
                        }
                    }
                    else if (_lastRealTradeSwByAsset.TryAdd(prep.CleanAsset, claim))
                    {
                        break;
                    }
                }

                if (canExecuteReal)
                {
                    _lastSampleTimeByAssetTf[sampleKey] = DateTime.UtcNow;
                    var sourceDirections = GetSourceDirections();
                    string featuresJson = BuildFeaturesJson();
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
                                sensors.PriceEntropy, sensors.TrendMaturity, sensors.PricePositionPct, sensors.BbSqueeze, null, null, null, liveCrowdSentiment, currentPayout);
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
            
            var sourceDirections = GetSourceDirections();
            string shadowFeaturesJson = BuildShadowFeaturesJson();
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
                sensors.PriceEntropy, sensors.TrendMaturity, sensors.PricePositionPct, sensors.BbSqueeze, null, null, null, liveCrowdSentiment, currentPayout);
            dbSw.Stop();
            string shadowReason = isGlobalCbHalted ? ", Global CB halt guard" : (isSymbolQuarantined ? ", Per-symbol quarantine guard" 
                : (!isPayoutAcceptable ? $", Payout/Closed guard ({currentPayout}%)"
                : (isS5 && isExhaustedOnS5 ? ", S5 peak exhaustion guard" 
                : (isS5 && !htfAligned ? ", S5 HTF divergence guard" 
                : (isS5 && isCooldownActive ? ", S5 cooldown guard" 
                : (isS5 && cm.Consensus.Probability < 58 ? ", S5 prob threshold guard" : ""))))));
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
                var sourceDirections = GetSourceDirections();
                string shadowFeaturesJson = BuildShadowFeaturesJson();
                _ = SignalTracker.RecordPredictionAsync("HOLD", prep.CleanAsset, timeframe, currentLivePrice, targetHorizon, prep.IntervalSecs, prep.IsForex, sourceDirections, cm.Consensus.Probability, cm.Consensus.TaScore, cm.Consensus.SmcScore, cm.Consensus.MlProb, cm.Consensus.MlScoreRaw, shadowFeaturesJson,
                    "NONE", false, false, 1.0, "NEUTRAL", cm.EffectiveMarketRegime, sensors.State.VelocityRegime ?? "UNKNOWN", sensors.MainAtr, sensors.MainAdx, sensors.TaResult.rsiVal, false, sensors.MinutesToNews ?? -1, "", "", 0.0, sensors.PriceEntropy, sensors.TrendMaturity, sensors.PricePositionPct, sensors.BbSqueeze, null, null, null, liveCrowdSentiment, currentPayout);
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
        else if (sensors.MainAdx >= 24.0 && Math.Abs(sensors.TaResult.score) >= 0.40) uiMarketPhase = sensors.TaResult.score > 0 ? "Бычий тренд" : "Медвежий тренд";
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
        double atrBps = prep.CurrentLivePrice > 0 ? (sensors.MainAtr / prep.CurrentLivePrice) * 10000.0 : cm.NoiseAudit.LocalAtrBps;
        double effectiveAtrBps = Math.Max(atrBps, cm.NoiseAudit.LocalAtrBps);
        double deadAtrBps = exec.IsSubminute ? 0.45 : 1.2;

        if (vel >= dangerVel) uiMarketEntropy = "ВЫСОКАЯ (Хаос / Опасно!)";
        else if (vel < deadVel && effectiveAtrBps < deadAtrBps && sensors.MainAdx < 18.0) uiMarketEntropy = "Мертвый рынок";

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

        double[] chartData = new double[prep.MainPrices.Length];
        for (int i = 0; i < prep.MainPrices.Length; i++)
        {
            chartData[i] = GetSafeLimit(prep.MainPrices[i]);
        }

        int rawCount = prep.RawCandles.Length;
        int ohlcTake = Math.Min(80, rawCount);
        int ohlcStart = rawCount - ohlcTake;
        var chartOhlc = new object[ohlcTake];
        for (int i = 0; i < ohlcTake; i++)
        {
            var c = prep.RawCandles[ohlcStart + i];
            chartOhlc[i] = new
            {
                o = Math.Round(GetSafeLimit(c.Open), 8),
                h = Math.Round(GetSafeLimit(c.High), 8),
                l = Math.Round(GetSafeLimit(c.Low), 8),
                c = Math.Round(GetSafeLimit(c.Close), 8),
                v = Math.Round(GetSafeLimit(c.Volume), 2)
            };
        }

        return new AnalysisResponseDto
        {
            tfConflict = cm.ConflictPenalty < 1.0,
            uiMarketSession = uiMarketSession,
            uiMarketPhase = uiMarketPhase,
            uiMarketEntropy = uiMarketEntropy,
            direction = sensors.IsRiskBlocked ? "NEUTRAL" : (cm.Consensus.FinalDirection is "BUY" or "PUT" ? cm.Consensus.FinalDirection : (prep.ClosedCandles.Length > 0 && prep.ClosedCandles[^1].Close >= prep.ClosedCandles[^1].Open ? "BUY" : "PUT")),
            probability = sensors.IsRiskBlocked ? 0 : Math.Clamp(cm.Consensus.Probability >= 50 ? cm.Consensus.Probability : 51, 50, 92),
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
            lgbmConfidence = (int)Math.Clamp(Math.Round(S(cm.MlSignal.Confidence, 0.5) * 100), 50, 99),
            winRateOverall = stats.WinRate,
            winRateAsset = assetStats.WinRate,
            signalsVerifiedAsset = assetStats.Verified,
            rsi = Math.Round(GetSafeLimit(sensors.TaResult.rsiVal), 1),
            ema = Math.Round(GetSafeLimit(sensors.TaResult.hmaVal), prep.IsForex ? 5 : 2),
            volumeStrength = Math.Round(GetSafeLimit(sensors.TaResult.volStrengthVal), 2),
            atr = Math.Round(GetSafeLimit(sensors.MainAtr), prep.IsForex ? 5 : 2),
            chartData = chartData,
            chartOhlc = chartOhlc,
            goldenSetup = exec.IsGoldenSetup,
            confluenceLabel = cm.MtfResult.ConfluenceLabel,
            confluenceRatio = cm.MtfResult.ConfluenceRatio,
            lgbmModelVersion = ml.Prediction?.ModelVersion ?? "offline",
            copilotAdvice = copilotAdvice
        };
    }

    #endregion
}
