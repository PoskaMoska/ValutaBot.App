using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ValutaBot.MiniApp.CQRS.Handlers;
using ValutaBot.App.MiniApp.Services;

namespace ValutaBot.MiniApp.Features.MarketAnalysis;

public class MarketAnalysisOrchestrator : IMarketAnalysisOrchestrator
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastSeenModelVersions = new();
    
    private readonly MarketDataFetcher _fetcher;
    private readonly IRiskGatekeeper _riskGatekeeper;
    private readonly IMathEngine _mathEngine;
    private readonly IMarketAnalyzer _marketAnalyzer;
    private readonly IConfluenceMatrixEngine _cmEngine;
    private readonly IWalkForwardValidationEngine _wfEngine;
    private readonly ITradeTimeoutEngine _timeoutEngine;
    private readonly IMonteCarloEngine _mcEngine;
    private readonly TradingBotSettings _settings;

    private string _asset = "";
    private string _timeframe = "";
    
    // Properties to mimic local variables
    private string _clean = "";
    private string? _symbol;
    private bool _isForex;
    private bool _isMajor;
    private int _limit;
    private string _tfLower = "";
    private bool _useMultiTf;
    private string _mainInterval = "";
    private string? _higherTf;
    private string? _lowerTf;
    private double[] _mainPrices = Array.Empty<double>();
    private double[] _mainVolumes = Array.Empty<double>();
    private string _mainOhlcKey = "";
    private MiniAppController.OhlcCandle[]? _ohlcCandles;
    private (double[] prices, double[] volumes)? _higherResultData;
    private (double[] prices, double[] volumes)? _lowerResultData;
    
    private double _conflictPenalty = 1.0;

    private SmcEngine.SmcAnalysisResult _smcResult;
    private OrderFlowEngine.OrderFlowResult _orderFlowResult;
    private WalkForwardValidationEngine.WalkForwardResult _wfResult;

    private string _lgbmDirection = "NEUTRAL";
    private double _lgbmConfidence = 0.5;
    private string _lgbmModelVersion = "disabled";
    private double? _lgbmAccuracy = null;
    private MLPythonService.MLPythonPrediction? _prediction;
    private ContinuousStateResult? _continuousState;
    // llmReport Р±РѕР»СЊС€Рµ РЅРµ С…СЂР°РЅРёС‚СЃСЏ РєР°Рє РїРѕР»Рµ вЂ” РіРµРЅРµСЂРёСЂСѓРµС‚СЃСЏ inline РІ BuildFinalConsensusAsync.
    
    private double _mainAdx, _mainPdi, _mainMdi, _mainAtr;
    private (double score, double confidence, double rsiVal, double emaVal, double volStrengthVal, double atrVal) _mainResult;

    public MarketAnalysisOrchestrator(
        MarketDataFetcher fetcher,
        IRiskGatekeeper riskGatekeeper,
        IMathEngine mathEngine,
        IMarketAnalyzer marketAnalyzer,
        IConfluenceMatrixEngine cmEngine,
        IWalkForwardValidationEngine wfEngine,
        ITradeTimeoutEngine timeoutEngine,
        IMonteCarloEngine mcEngine,
        Microsoft.Extensions.Options.IOptions<TradingBotSettings> settings
    )
    {
        _fetcher = fetcher;
        _riskGatekeeper = riskGatekeeper;
        _mathEngine = mathEngine;
        _marketAnalyzer = marketAnalyzer;
        _cmEngine = cmEngine;
        _wfEngine = wfEngine;
        _timeoutEngine = timeoutEngine;
        _mcEngine = mcEngine;
        _settings = settings.Value;
    }

    internal static double MfConflictPenalty((double score, double conf, double rsi, double ema, double vol, double atr) main,
                                             (double score, double conf, double rsi, double ema, double vol, double atr) higher)
    {
        int mainDir = main.score > 0.05 ? 1 : main.score < -0.05 ? -1 : 0;
        int higherDir = higher.score > 0.05 ? 1 : higher.score < -0.05 ? -1 : 0;
        if (mainDir != 0 && higherDir != 0 && mainDir != higherDir)
            return 0.7; // 30% penalty for active opposing trends
        return 1.0;
    }

    private ValutaBot.App.MiniApp.Data.Repositories.UserSettings? _userSettings;

    private bool IsSettingEnabled(bool globalSetting, bool? userSetting)
    {
        if (userSetting.HasValue) return userSetting.Value;
        return globalSetting;
    }

    private double GetSafeLimit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
        if (value > 1e6) return 1e6;
        if (value < -1e6) return -1e6;
        return value;
    }

    private double GetSafePenalty(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return 1.0;
        return value;
    }

    public async Task<object> ExecuteAnalysisAsync(string asset, string timeframe, ValutaBot.App.MiniApp.Data.Repositories.UserSettings? userSettings = null)
    {
        _asset = asset;
        _timeframe = timeframe;
        _userSettings = userSettings;

        // в”Ђв”Ђ Profiling: Р·Р°РјРµСЂ РІСЂРµРјРµРЅРё РєР°Р¶РґРѕРіРѕ СЌС‚Р°РїР° РїР°Р№РїР»Р°Р№РЅР° в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var swStage = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // T0 в†’ T1: РїРѕР»СѓС‡РµРЅРёРµ СЂС‹РЅРѕС‡РЅС‹С… РґР°РЅРЅС‹С…
            await InitializeDataAsync();
            BotLogger.Info($"[Timing] {_asset}/{_timeframe} | T1 DataFetch: {swStage.ElapsedMilliseconds}ms");
            swStage.Restart();

            if (_mainPrices == null || _mainPrices.Length == 0)
            {
                throw new Exception("РќРµРґРѕСЃС‚Р°С‚РѕС‡РЅРѕ РґР°РЅРЅС‹С… РґР»СЏ Р°РЅР°Р»РёР·Р°. Р‘РёСЂР¶Р° РёР»Рё РїСЂРѕРІР°Р№РґРµСЂ РІРµСЂРЅСѓР»Рё РїСѓСЃС‚РѕР№ СЂРµР·СѓР»СЊС‚Р°С‚.");
            }

            // T1 в†’ T2: Gatekeeper + ContinuousState
            var gatekeeper = _riskGatekeeper.ValidateMarketGatekeeper(_asset, _timeframe, _mainPrices, _ohlcCandles);
            if (!gatekeeper.IsTradeable)
            {
                BotLogger.Warn($"[Analysis] Gatekeeper aborted trade for {_asset} ({_timeframe}): {gatekeeper.Reason}");
                throw new Exception(gatekeeper.Reason);
            }

            _continuousState = ContinuousStateEngine.EvaluateContinuousState(_mainPrices, _asset, _timeframe);
            BotLogger.Info($"[Timing] {_asset}/{_timeframe} | T2 Gatekeeper+State: {swStage.ElapsedMilliseconds}ms");
            swStage.Restart();

            // T2 в†’ T3: РїР°СЂР°Р»Р»РµР»СЊРЅС‹Р№ Р±Р»РѕРє (Mechanics + TA + ML)
            var mechanicsTask = AnalyzeCoreMechanicsAsync();
            var techTask = EvaluateTechnicalIndicatorsAsync();
            var mlTask = FetchMachineLearningAsync();

            await Task.WhenAll(mechanicsTask, techTask, mlTask);
            BotLogger.Info($"[Timing] {_asset}/{_timeframe} | T3 Parallel(Mechanics+TA+ML): {swStage.ElapsedMilliseconds}ms");
            swStage.Restart();

            // T3 в†’ T4: С„РёРЅР°Р»СЊРЅС‹Р№ РєРѕРЅСЃРµРЅСЃСѓСЃ + DB (GenerateLlmReport СѓР±СЂР°РЅ РєР°Рє РѕС‚РґРµР»СЊРЅС‹Р№ СЌС‚Р°Рї вЂ”
            // СЌС‚Рѕ Р±С‹Р»Р° С‡РёСЃС‚Р°СЏ СЃС‚СЂРѕРєРѕРІР°СЏ РєРѕРЅРєР°С‚РµРЅР°С†РёСЏ, РЅРµ LLM-РІС‹Р·РѕРІ, РІСЃС‚СЂРѕРµРЅР° РІ BuildFinalConsensusAsync)
            var result = await BuildFinalConsensusAsync();
            BotLogger.Info($"[Timing] {_asset}/{_timeframe} | T4 Consensus+DB: {swStage.ElapsedMilliseconds}ms");
            BotLogger.Info($"[Timing] {_asset}/{_timeframe} | TOTAL: {swTotal.ElapsedMilliseconds}ms");

            return result;
        }
        catch (ExchangeUnavailableException exEx)
        {
            BotLogger.Warn($"[Timing] {_asset}/{_timeframe} | FAILED at {swTotal.ElapsedMilliseconds}ms вЂ” ExchangeUnavailable");
            MiniAppController.LastExceptionMessage = exEx.ToString();
            BotLogger.Warn($"[Analysis] Exchange unavailable for asset {_asset}: {exEx.Message}");
            throw;
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[Timing] {_asset}/{_timeframe} | FAILED at {swTotal.ElapsedMilliseconds}ms вЂ” {ex.GetType().Name}");
            MiniAppController.LastExceptionMessage = ex.ToString();
            BotLogger.Error($"[Analysis] Analysis failed for asset {_asset} on {_timeframe}", ex);
            throw;
        }
    }


    private async Task InitializeDataAsync()
    {
        _clean = AssetSanitizer.Sanitize(_asset);
        DayOfWeek day = DateTime.UtcNow.DayOfWeek;
        _symbol = AssetSanitizer.MapSymbolByDayOfWeek(_clean, day);

        _isForex = _symbol == null || _symbol == "EURUSDT" || _symbol == "GBPUSDT" || _symbol == "AUDUSDT";
        _isMajor = _symbol == "BTCUSDT" || _symbol == "ETHUSDT" || _symbol == "SOLUSDT";
        _limit = 100;
        _tfLower = _timeframe.ToLower().Trim();
        if (_tfLower == "s10" || _tfLower == "s15" || _tfLower == "s30") _limit = 130;
        else if (_tfLower == "m1" || _tfLower == "m2" || _tfLower == "m3" || _tfLower == "m5") _limit = 150;
        else if (_tfLower == "m15" || _tfLower == "m30" || _tfLower == "h1") _limit = 200;

        _useMultiTf = true;
        _mainInterval = _fetcher.IntervalMap(_timeframe);
        _higherTf = _useMultiTf ? _fetcher.HigherTf(_timeframe) : null;
        _lowerTf = _useMultiTf ? _fetcher.LowerTf(_timeframe) : null;

        _mainOhlcKey = _symbol != null ? $"{_symbol}_{_mainInterval}" : $"{_clean}_{_mainInterval}";

        var mainResultTuple = await _fetcher.FetchBinanceWithFallback(_symbol, _mainInterval, _clean, _limit);
        _mainPrices = mainResultTuple.prices;
        _mainVolumes = mainResultTuple.volumes;

        _ohlcCandles = await _fetcher.FetchOhlcWithFallbackAsync(_symbol, _timeframe, _asset, _limit);
        if (_ohlcCandles == null || _ohlcCandles.Length == 0)
        {
            // OTC candles not yet accumulated вЂ” use main prices as synthetic OHLC
            BotLogger.Warn($"[Orchestrator] No OTC candles for {_asset} ({_timeframe}) вЂ” using synthetic OHLC from main prices.");
            _ohlcCandles = _mainPrices.Select(p => new MiniAppController.OhlcCandle(p, p, p, p, 0)).ToArray();
        }
        else if (_ohlcCandles.Length < 2)
        {
            BotLogger.Warn($"[Orchestrator] Only {_ohlcCandles.Length} candle(s) for {_asset} ({_timeframe}) вЂ” analysis may be limited.");
        }

        var higherTask = _higherTf != null ? SafeFetch(_higherTf) : Task.FromResult<(double[] prices, double[] volumes)?>(null);
        var lowerTask = _lowerTf != null ? SafeFetch(_lowerTf) : Task.FromResult<(double[] prices, double[] volumes)?>(null);

        var extraTasks = new List<Task<(double[] prices, double[] volumes)?>>();
        if (_isMajor)
        {
            string[] checkTfs = { "m1", "m5", "m15", "h1" };
            foreach (var cTf in checkTfs)
            {
                if (cTf != _timeframe && cTf != _higherTf && cTf != _lowerTf)
                {
                    extraTasks.Add(SafeFetch(cTf));
                }
            }
        }

        await Task.WhenAll(higherTask, lowerTask);
        if (extraTasks.Count > 0) await Task.WhenAll(extraTasks);

        _higherResultData = await higherTask;
        _lowerResultData = await lowerTask;
    }

    private async Task<(double[] prices, double[] volumes)?> SafeFetch(string tf)
    {
        try { return await _fetcher.FetchBinanceWithFallback(_symbol, tf, _asset, _limit); }
        catch (Exception ex) { Console.WriteLine($"[Fetch Warning] TF {tf} failed: {ex.Message}"); return null; }
    }

    private async Task AnalyzeCoreMechanicsAsync()
    {
        if (IsSettingEnabled(_settings.EnableSmc, _userSettings?.EnableSmc))
        {
            _smcResult = SmcEngine.AnalyzeSmcStructure(_asset, _mainInterval, _ohlcCandles ?? Array.Empty<MiniAppController.OhlcCandle>(), _mainPrices[^1]);
            BotLogger.Info($"[SMC Engine] Asset {_asset} ({_timeframe}): SMC Zones updated.");
        }
        else
        {
            _smcResult = new SmcEngine.SmcAnalysisResult();
        }

        if (IsSettingEnabled(_settings.EnableOrderFlow, _userSettings?.EnableOf))
        {
            _orderFlowResult = OrderFlowEngine.AnalyzeOrderFlow(_asset, _mainInterval, _ohlcCandles ?? Array.Empty<MiniAppController.OhlcCandle>(), _mainPrices[^1]);
            BotLogger.Info($"[Order Flow] Asset {_asset} ({_timeframe}): {_orderFlowResult.Description}");
        }
        else
        {
            _orderFlowResult = new OrderFlowEngine.OrderFlowResult();
        }

        // FIX Race Condition: MTF SMC выравнивание перенесено сюда из EvaluateTechnicalIndicatorsAsync.
        // Ранее ValidateMtfSmcAlignment читал _smcResult из параллельного Task (Task.WhenAll),
        // без гарантии порядка — _smcResult мог быть ещё не записан → гонка данных.
        // Теперь выравнивание выполняется строго ПОСЛЕ записи _smcResult в этом же методе.
        if (_higherResultData != null && _higherTf != null)
        {
            try
            {
                var higherOhlcForSmc = await _fetcher.FetchOhlcWithFallbackAsync(_symbol, _higherTf, _asset);
                if (higherOhlcForSmc != null && _higherResultData.Value.prices.Length > 0)
                {
                    var htfSmcResult = SmcEngine.AnalyzeSmcStructure(_asset, _higherTf, higherOhlcForSmc, _higherResultData.Value.prices[^1]);
                    var mtfValidation = SmcEngine.ValidateMtfSmcAlignment(_smcResult, htfSmcResult);
                    _conflictPenalty *= mtfValidation.ConfluenceMultiplier;
                    BotLogger.Info($"[MTF SMC Validation] Alignment: {mtfValidation.AlignmentStatus} | Multiplier={mtfValidation.ConfluenceMultiplier:F2}x");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[MTF SMC] Failed to fetch higher TF OHLC for SMC alignment: {ex.Message}");
            }
        }
    }

    private async Task FetchMachineLearningAsync()
    {
        _wfResult = _wfEngine.ValidateWalkForward(_asset, _timeframe);
        if (_wfResult.IsOverfitted || _wfResult.IsCooloffActive)
        {
            BotLogger.Warn($"[Anti-Overfitting] {_asset} ({_timeframe}): {_wfResult.StatusReasoning} ML weight multiplier set to {_wfResult.WeightMultiplier}x.");
        }

        if (!IsSettingEnabled(_settings.EnableMachineLearning, _userSettings?.EnableMl))
        {
            _lgbmDirection = "NEUTRAL";
            _lgbmConfidence = 0.5;
            _lgbmModelVersion = "disabled";
            BotLogger.Info($"[ML Engine] ML is disabled in settings for {_asset} ({_timeframe}).");
            return;
        }

        if (_ohlcCandles != null && _ohlcCandles.Length >= 60)
        {
            try
            {
                _prediction = await MLPythonService.PredictAsync(_asset, _timeframe, _ohlcCandles, _isForex);
                if (_prediction != null)
                {
                    _lgbmModelVersion = string.IsNullOrEmpty(_prediction.ModelVersion) ? "unknown" : _prediction.ModelVersion;
                    _lgbmAccuracy = _prediction.Accuracy;

                    if (_prediction.Direction != "NEUTRAL")
                    {
                        _lgbmDirection = _prediction.Direction;
                        _lgbmConfidence = (float)(_prediction.Confidence * _wfResult.WeightMultiplier);
                        _lgbmConfidence = Math.Clamp(_lgbmConfidence, 0f, 1f);

                        if (_lgbmConfidence < 0.51f)
                        {
                            BotLogger.Info($"[ML Override] WalkForward suppressed ML confidence to {_lgbmConfidence:F2}. Reverting to pure Math.");
                            _lgbmDirection = "NEUTRAL";
                        }
                        else
                        {
                            BotLogger.Info($"[ML Override] ML confident ({_lgbmConfidence:F2}). Passing vector to Confluence Matrix.");
                        }
                    }
                    else
                    {
                        _lgbmDirection = "NEUTRAL";
                        _lgbmConfidence = 0.5;
                    }

                    // ✨ ML Telemetry: Global Retraining ✨
                    if (!string.IsNullOrEmpty(_prediction.ModelVersion))
                    {
                        string cacheKey = $"{_asset}_{_timeframe}";
                        string currentVer = _prediction.ModelVersion;
                        string oldVer = "";
                        bool versionChanged = false;
                        
                        if (_lastSeenModelVersions.TryGetValue(cacheKey, out oldVer))
                        {
                            if (oldVer != currentVer)
                            {
                                versionChanged = true;
                            }
                        }
                        
                        _lastSeenModelVersions[cacheKey] = currentVer;

                        if (versionChanged && !string.IsNullOrEmpty(oldVer))
                        {
                            // Skip the very first startup assignment spam, only alert on actual changes during runtime
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        string accStr    = _prediction.Accuracy.HasValue ? $"{_prediction.Accuracy.Value * 100:F1}%" : "N/A";
                                        string aucStr    = _prediction.Auc.HasValue ? $"{_prediction.Auc.Value:F3}" : "N/A";
                                        string nTrainStr = _prediction.NTrain.HasValue ? $"{_prediction.NTrain.Value:N0}" : "N/A";
                                        string icon      = _prediction.Accuracy.HasValue
                                            ? (_prediction.Accuracy.Value >= 0.57 ? "🟢" : _prediction.Accuracy.Value >= 0.54 ? "🟡" : "🔴")
                                            : "⚪";

                                        string report = $"🔄 <b>Переобучение модели</b>\n" +
                                                        $"{icon} <b>{_asset}</b> ({_timeframe}): Точность <b>{accStr}</b> | AUC <b>{aucStr}</b> | {nTrainStr} свечей";

                                        // await TelegramBotService.SendMessageToAdmins(report); // Disabled per user request

                                        string logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                                        System.IO.Directory.CreateDirectory(logDir);
                                        string logFile = System.IO.Path.Combine(logDir, "ml_global_retrain.csv");
                                        bool writeHeader = !System.IO.File.Exists(logFile);
                                        using var writer = new System.IO.StreamWriter(logFile, append: true);
                                        if (writeHeader) await writer.WriteLineAsync("Timestamp,Asset,OldVersion,NewVersion,Accuracy,Auc,NTrain");
                                        await writer.WriteLineAsync($"{DateTime.UtcNow:O},{_asset},{oldVer},{_prediction.ModelVersion},{_prediction.Accuracy},{_prediction.Auc},{_prediction.NTrain}");
                                    }
                                    catch (Exception tEx)
                                    {
                                        BotLogger.Error("[MarketAnalysis] Error sending ML global telemetry", tEx);
                                    }
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) 
            { 
                Console.WriteLine($"[Python ML Warning] {ex.GetType().Name}: {ex.Message}");
                // _llmReport СѓРґР°Р»С‘РЅ вЂ” РѕС‚С‡С‘С‚ РіРµРЅРµСЂРёСЂСѓРµС‚СЃСЏ inline РІ BuildLlmSummary
            }
        }
    }

    // GenerateLlmReport СѓРґР°Р»С‘РЅ РєР°Рє РѕС‚РґРµР»СЊРЅС‹Р№ pipeline-СЌС‚Р°Рї T4.
    // Р‘С‹Р» РїРµСЂРµРёРјРµРЅРѕРІР°РЅ РІ BuildLlmSummary Рё РІСЃС‚СЂРѕРµРЅ РІ BuildFinalConsensusAsync.
    // LlmReportingService вЂ” С‡РёСЃС‚Р°СЏ СЃС‚СЂРѕРєРѕРІР°СЏ РєРѕРЅРєР°С‚РµРЅР°С†РёСЏ, РЅРµ LLM-РІС‹Р·РѕРІ.
    private string BuildLlmSummary()
    {
        if (_ohlcCandles == null || _ohlcCandles.Length < 60)
            return "вљ пёЏ РќРµРґРѕСЃС‚Р°С‚РѕС‡РЅРѕ РґР°РЅРЅС‹С… РґР»СЏ РѕС‚С‡С‘С‚Р°.";
        try
        {
            var llmService = new ValutaBot.App.MiniApp.Services.LlmReportingService();
            var regime = _continuousState?.VelocityRegime ?? "UNKNOWN";
            bool isUp   = _lgbmDirection == "BUY";
            bool isTaUp = _mainResult.score > 0;
            bool isOfUp = _orderFlowResult.ScoreContribution > 0;
            return llmService.GenerateMarketSummary(_asset, regime, _prediction, isUp, isTaUp, isOfUp);
        }
        catch (Exception ex)
        {
            return $"вљ пёЏ РћС€РёР±РєР° РіРµРЅРµСЂР°С†РёРё РѕС‚С‡С‘С‚Р°: {ex.Message}";
        }
    }


    private async Task EvaluateTechnicalIndicatorsAsync()
    {
        (_mainAdx, _mainPdi, _mainMdi) = _ohlcCandles != null ? _mathEngine.ComputeTrueAdx(_asset, _timeframe, _ohlcCandles) : (20.0, 0.0, 0.0);
        _mainAtr = _ohlcCandles != null ? _mathEngine.ComputeAtr(_asset, _timeframe, _ohlcCandles) : 0;

        _mainResult = _marketAnalyzer.ScoreTimeframe(_asset, _timeframe, _mainPrices, _mainVolumes ?? Array.Empty<double>(), candles: _ohlcCandles, adxOverride: _mainAdx, atrOverride: _mainAtr, isForex: _isForex);

        if (_higherResultData != null)
        {
            MiniAppController.OhlcCandle[]? higherOhlc = null;
            if (_higherTf != null)
            {
                try
                {
                    higherOhlc = await _fetcher.FetchOhlcWithFallbackAsync(_symbol, _higherTf, _asset);
                    if (higherOhlc == null || higherOhlc.Length == 0)
                    {
                        BotLogger.Warn($"[Orchestrator] No OTC candles for {_asset} ({_higherTf}) — using synthetic OHLC from higher prices.");
                        higherOhlc = _higherResultData.Value.prices.Select(p => new MiniAppController.OhlcCandle(p, p, p, p, 0)).ToArray();
                    }
                }
                catch (Exception ex)
                {
                    BotLogger.Warn($"[Analysis] Failed to fetch higher TF OHLC candles: {ex.Message}");
                    higherOhlc = _higherResultData.Value.prices.Select(p => new MiniAppController.OhlcCandle(p, p, p, p, 0)).ToArray();
                }
            }

            // NOTE: MTF SMC ValidateMtfSmcAlignment был перенесён в AnalyzeCoreMechanicsAsync
            // чтобы устранить гонку данных по _smcResult (Task.WhenAll race condition fix).

            var (hAdx, hPdi, hMdi) = higherOhlc != null ? _mathEngine.ComputeTrueAdx(_asset, _higherTf ?? "", higherOhlc) : (20.0, 0.0, 0.0);
            double hAtr = higherOhlc != null ? _mathEngine.ComputeAtr(_asset, _higherTf ?? "", higherOhlc) : 0;
            var higherResult = _marketAnalyzer.ScoreTimeframe(_asset, _higherTf ?? "", _higherResultData.Value.prices, _higherResultData.Value.volumes ?? Array.Empty<double>(), candles: higherOhlc, adxOverride: hAdx, atrOverride: hAtr, isForex: _isForex);

            _conflictPenalty *= MfConflictPenalty(_mainResult, higherResult);
        }
    }

    private async Task<object> BuildFinalConsensusAsync()
    {
        bool isSubMinute = _timeframe.ToLower().StartsWith("s");
        
        // Construct Signals for the Confluence Matrix
        var taSignal = new TaSignal(_mainResult.score, _mainResult.confidence, _mainResult.rsiVal, _mainResult.emaVal, _mainResult.volStrengthVal, _mainAtr, _mainAdx);
        
        var smcParts = new List<string>();
        if (_smcResult.HasLiquiditySweep && !string.IsNullOrEmpty(_smcResult.SweepDirection)) smcParts.Add($"Sweep: {_smcResult.SweepDirection}");
        if (_smcResult.HasBos && !string.IsNullOrEmpty(_smcResult.BosDirection)) smcParts.Add($"BOS: {_smcResult.BosDirection}");
        if (_smcResult.HasFvg && !string.IsNullOrEmpty(_smcResult.FvgType)) smcParts.Add($"FVG: {_smcResult.FvgType}");
        if (_smcResult.HasOrderBlock && !string.IsNullOrEmpty(_smcResult.OrderBlockType)) smcParts.Add($"OB: {_smcResult.OrderBlockType}");
        string smcReasoning = smcParts.Count > 0 ? string.Join(", ", smcParts) : "No clear structure";

        var smcSignal = new SmcSignal(_smcResult.BosDirection, _smcResult.SweepDirection, _smcResult.OrderBlockType, _smcResult.FvgType, smcReasoning);
        var ofSignal = new OrderflowSignal(_orderFlowResult.ScoreContribution, _orderFlowResult.Description);
        var mlSignal = new MlSignal(_lgbmDirection, _lgbmConfidence, _lgbmAccuracy, _lgbmModelVersion);
        
        var stateSignal = new StateSignal(_continuousState?.VelocityRegime ?? "UNKNOWN", _continuousState?.VelocityBpsPerSec ?? 0, _continuousState?.MomentumContribution ?? 0);

        var mtfResult = await _cmEngine.Evaluate4DMatrixAsync(_asset, _timeframe, _isForex, _symbol);

                int consecutiveLosses = TradeOutcomeTracker.GetConsecutiveLosses(_asset, _timeframe);
        double volRatio = _marketAnalyzer.CalculateVolatilityRatio(_mainPrices);
        var consensus = await _cmEngine.EvaluateMatrixAsync(
            _asset, _timeframe, isSubMinute, _conflictPenalty, 
            taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, consecutiveLosses, volRatio);

        string finalDirection = consensus.FinalDirection;
        int finalProbability = consensus.Probability;
        
        int timeframeSec = _fetcher.TimeframeSeconds(_timeframe);
        var timeoutResult = _timeoutEngine.CalculateTimeout(_asset, _timeframe, _mainAtr, volRatio, _smcResult, _mainPrices[^1]);
        
        // --- PRODUCTION KILL SWITCH (Pre-Simulation) ---
        if (_wfResult.IsCooloffActive)
        {
            var remaining = _wfResult.CooloffUntil - DateTime.UtcNow;
            int mins = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            BotLogger.Warn($"[KillSwitch] Blocked trade for {_asset} {_timeframe} due to WFE Cooloff. Resumes in {mins} min.");
            throw new Exception(
                $"\u26a0\ufe0f \u0410\u043d\u0430\u043b\u0438\u0437\u0430\u0442\u043e\u0440 \u0437\u0430\u0431\u043b\u043e\u043a\u0438\u0440\u043e\u0432\u0430\u043d: 3 \u043f\u043e\u0441\u043b\u0435\u0434\u043e\u0432\u0430\u0442\u0435\u043b\u044c\u043d\u044b\u0445 \u0443\u0431\u044b\u0442\u043a\u0430 (\u0414\u0440\u043e\u0443\u0434\u0430\u0443\u043d \u043f\u0440\u043e\u0442\u0435\u043a\u0446\u0438\u044f). " +
                $"\u23f3 \u0420\u0430\u0437\u0431\u043b\u043e\u043a\u0438\u0440\u043e\u0432\u043a\u0430 \u0447\u0435\u0440\u0435\u0437: {mins} \u043c\u0438\u043d. (\u0432 {_wfResult.CooloffUntil:HH:mm} UTC). " +
                "\u041f\u043e\u0434\u043e\u0436\u0434\u0438\u0442\u0435 \u0438\u043b\u0438 \u0441\u043c\u0435\u043d\u0438\u0442\u0435 \u0430\u043a\u0442\u0438\u0432, \u0447\u0442\u043e\u0431\u044b \u043f\u0440\u043e\u0434\u043e\u043b\u0436\u0438\u0442\u044c \u0430\u043d\u0430\u043b\u0438\u0437.");
        }

        // Р—Р°РјРµРЅР° Monte Carlo (O(1000)) РЅР° С‚СЂРё Р·Р°РєСЂС‹С‚С‹Рµ С„РѕСЂРјСѓР»С‹ (O(1)).
        // Р РµР·СѓР»СЊС‚Р°С‚ РјР°С‚РµРјР°С‚РёС‡РµСЃРєРё РёРґРµРЅС‚РёС‡РµРЅ РїСЂРё Р±РёРЅР°СЂРЅРѕР№ СЃС‚СЂСѓРєС‚СѓСЂРµ РІС‹РїР»Р°С‚.
        MonteCarloResult mcResult;
        if (finalDirection == "NEUTRAL")
        {
            mcResult = new MonteCarloResult(0, 0, 0, 0, "Blocked", "Blocked", "Trade blocked before simulation");
        }
        else
        {
            const double Payout = 0.80; // РЎС‚Р°РЅРґР°СЂС‚РЅС‹Р№ РєРѕСЌС„С„РёС†РёРµРЅС‚ РІС‹РїР»Р°С‚С‹ Pocket Option (80%)
            double p = Math.Clamp(finalProbability / 100.0, 0.35, 0.95);
            double q = 1.0 - p;

            // 1. Expected Value: EV = p Г— Payout в€’ q Г— 1.0
            double evRatio   = (p * Payout) - (q * 1.0);
            double evPct     = Math.Round(evRatio * 100.0, 1);

            // 2. Fractional Kelly Criterion (25% Kelly РґР»СЏ РєРѕРЅСЃРµСЂРІР°С‚РёРІРЅРѕРіРѕ СѓРїСЂР°РІР»РµРЅРёСЏ РєР°РїРёС‚Р°Р»РѕРј)
            double fullKelly      = (p * Payout - q) / Payout;
            double fractionalKelly = Math.Clamp(fullKelly * 0.25, 0.0, 0.05);
            double kellyRiskPct   = Math.Round(fractionalKelly * 100.0, 1);

            // 3. Success rate = РЅР°РїСЂСЏРјСѓСЋ РёР· РІРµСЂРѕСЏС‚РЅРѕСЃС‚Рё (Р±РµР· СЃРёРјСѓР»СЏС†РёРё)
            int syntheticIterations  = 1000;
            int syntheticSuccessCount = (int)Math.Round(p * syntheticIterations);

            string evLabel     = evPct > 0
                ? $"+{evPct:F1}% EV (Positive Expectancy)"
                : $" {evPct:F1}% EV (Negative Expectancy)";

            string kellyLabel  = kellyRiskPct > 0
                ? $"{kellyRiskPct:F1}% - {Math.Min(kellyRiskPct + 0.5, 5.0):F1}% of Capital"
                : "0% (Do not trade, low edge)";

            string summary = $"Direct Formula (O(1)): {syntheticSuccessCount}/{syntheticIterations} est. | EV: {(evPct > 0 ? "+" : " ")}{evPct:F1}% | Kelly Risk: {kellyRiskPct:F1}%";

            mcResult = new MonteCarloResult(
                syntheticIterations,
                syntheticSuccessCount,
                evPct,
                kellyRiskPct,
                evLabel,
                kellyLabel,
                summary
            );
        }

        string orderFlowDir = _orderFlowResult.ScoreContribution > 0 ? "BUY" : _orderFlowResult.ScoreContribution < 0 ? "PUT" : "NEUTRAL";
        
        await SignalTracker.RecordPredictionAsync(
            finalDirection, _asset, _timeframe, _mainPrices[^1],
            expiryCandles: timeoutResult.TimeoutCandles,
            timeframeSecs: timeframeSec, isForex: _isForex, binanceSymbol: _symbol,
            sourceDirections: new Dictionary<string, string> {
                ["LIGHTGBM"] = _lgbmDirection, ["SKENDER_MATH"] = consensus.FinalTotalScore > 0.02 ? "BUY" : consensus.FinalTotalScore < -0.02 ? "PUT" : "NEUTRAL",
                ["SMC"] = (smcSignal.SweepDirection ?? "").Contains("BULLISH") ? "BUY" : (smcSignal.SweepDirection ?? "").Contains("BEARISH") ? "PUT" : "NEUTRAL", ["ORDERFLOW"] = orderFlowDir,
                ["NATIVE_ML"] = "NEUTRAL"
            }
        );

        // РџР°СЂР°Р»Р»РµР»СЊРЅС‹Р№ Р·Р°РїСѓСЃРє С‚СЂС‘С… РЅРµР·Р°РІРёСЃРёРјС‹С… DB-Р·Р°РїСЂРѕСЃРѕРІ РІРјРµСЃС‚Рѕ РїРѕСЃР»РµРґРѕРІР°С‚РµР»СЊРЅРѕРіРѕ.
        // Р­РєРѕРЅРѕРјРёСЏ: ~2вЂ“3x latency РїСЂРё РєР°Р¶РґРѕРј РІС‹Р·РѕРІРµ (СѓСЃС‚СЂР°РЅСЏРµС‚ sequential await chain).
        var overallStatsTask    = SignalTracker.GetOverallStatsAsync();
        var assetStatsTask      = SignalTracker.GetStatsAsync(_asset, _timeframe);
        var pendingCountTask    = SignalTracker.GetPendingCountAsync();

        await Task.WhenAll(overallStatsTask, assetStatsTask, pendingCountTask);

        var overallStats = await overallStatsTask;
        var assetStats   = await assetStatsTask;
        int pendingCount = await pendingCountTask;

        return new
        {
            direction = finalDirection,
            probability = finalProbability,
            duration = timeoutResult.TimeoutText,
            adaptiveReasoning = $"{timeoutResult.Reasoning} | {mtfResult.SummaryReasoning}",
            goldenSetup = mtfResult.IsGoldenSetup,
            confluenceLabel = mtfResult.ConfluenceLabel,
            confluenceRatio = mtfResult.ConfluenceRatio,
            expiryCandles = timeoutResult.TimeoutCandles,
            chartData = _mainPrices,
            rsi = Math.Round(_mainResult.rsiVal, 1),
            ema = Math.Round(_mainResult.emaVal, 2),
            volumeStrength = Math.Round(_mainResult.volStrengthVal, 2),
            tfConflict = _conflictPenalty < 1.0,
            lgbmDirection = _lgbmDirection,
            lgbmConfidence = Math.Round(_lgbmConfidence * 100, 0),
            lgbmAccuracy = _lgbmAccuracy.HasValue ? Math.Round(_lgbmAccuracy.Value * 100, 1) : (double?)null,
            lgbmModelVersion = _lgbmModelVersion,
            newsSentiment = "Neutral", // Removed old logic
            newsScore = 0.0,
            newsSummary = "",
            newsHeadlines = Array.Empty<string>(),
            claudeReasoning = consensus.CombinedReasoningText,
            winRateOverall = overallStats.HasData ? overallStats.WinRate : (double?)null,
            winRateAsset = assetStats.HasData ? assetStats.WinRate : (double?)null,
            signalsVerified = overallStats.Verified,
            signalsPending = pendingCount,
            monteCarloIterations = mcResult.Iterations,
            monteCarloSuccess = mcResult.SuccessCount,
            evPct = mcResult.ExpectedValuePct,
            evLabel = mcResult.EvLabel,
            kellyRiskPct = mcResult.KellyRiskPct,
            kellyLabel = mcResult.KellyLabel,
            monteCarloSummary = mcResult.SummaryReasoning,
            wfIsCooloffActive = _wfResult.IsCooloffActive,
            llmReport = BuildLlmSummary()  // Inline: Р±РѕР»СЊС€Рµ РЅРµ РѕС‚РґРµР»СЊРЅС‹Р№ T4 СЌС‚Р°Рї РїР°Р№РїР»Р°Р№РЅР°
        };
    }
}



