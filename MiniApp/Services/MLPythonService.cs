using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// Calls the Python LightGBM ML microservice via HTTP.
/// Static service using the shared HttpClient from MiniAppController.
/// Falls back gracefully using Polly Resilience Pipeline if the service is unavailable.
/// </summary>
public static class MLPythonService
{
    private static string _baseUrl = string.Empty;
    private static IHttpClientFactory? _httpFactory;

    public static void SetFactory(IHttpClientFactory factory) => _httpFactory = factory;
    private static Process? _mlProcess; // Track to prevent zombie leaks


    private static readonly JsonSerializerOptions _jsonOptions = new() { 
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower 
    };

    static MLPythonService()
    {
        // Note: HttpClient instances are obtained via IHttpClientFactory to benefit from Polly policies.
    }

    public class MarketDataColumnar
    {
        public long[] openTime { get; set; } = Array.Empty<long>();
        public double[] open { get; set; } = Array.Empty<double>();
        public double[] high { get; set; } = Array.Empty<double>();
        public double[] low { get; set; } = Array.Empty<double>();
        public double[] close { get; set; } = Array.Empty<double>();
        public double[] volume { get; set; } = Array.Empty<double>();
    }

    private static MarketDataColumnar ToColumnar(System.Collections.Generic.IList<MiniAppController.OhlcCandle> candles)
    {
        int count = candles.Count;
        var columnar = new MarketDataColumnar
        {
            openTime = new long[count],
            open = new double[count],
            high = new double[count],
            low = new double[count],
            close = new double[count],
            volume = new double[count]
        };

        for (int i = 0; i < count; i++)
        {
            var c = candles[i];
            columnar.openTime[i] = c.Timestamp == default ? 0 : new DateTimeOffset(c.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc) : c.Timestamp).ToUnixTimeSeconds();
            columnar.open[i] = c.Open;
            columnar.high[i] = c.High;
            columnar.low[i] = c.Low;
            columnar.close[i] = c.Close;
            columnar.volume[i] = c.Volume;
        }
        
        return columnar;
    }

    public record MLPythonPrediction(
        string Direction,
        double Confidence,
        string ModelVersion,
        double? Accuracy,
        double? Auc,
        int? NTrain,
        double? VarianceEstimate = null,
        double? RawConfidence = null,
        int? HorizonCandles = null
    );

    public static void Init(string? baseUrl)
    {
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(_baseUrl))
        {
            BotLogger.Info($"[MLPython] Service URL: {_baseUrl}");
            if (_baseUrl.Contains("localhost") || _baseUrl.Contains("127.0.0.1"))
            {
                EnsureLocalPythonServiceRunning();
            }
        }
        else
        {
            BotLogger.Info("[MLPython] No ML_SERVICE_URL configured — Python ML disabled.");
        }
    }

    private static CancellationTokenSource? _watchdogCts;
    private static int _isLaunching = 0; // Lock to prevent multiple spawns

    private static void EnsureLocalPythonServiceRunning()
    {
        // On Railway (and any container orchestrator) the watchdog must NOT run:
        // the orchestrator already handles process restarts via health checks.
        // A competing C# watchdog would cause kill/restart races and prevent recovery.
        if (IsRunningOnRailway())
        {
            BotLogger.Info("[MLPython] Running on Railway — skipping local process watchdog (container orchestrator handles restarts).");
            return;
        }

        if (Interlocked.CompareExchange(ref _isLaunching, 1, 0) != 0)
            return;

        Task.Run(async () =>
        {
            try
            {
                try
                {
                    var testClient = _httpFactory?.CreateClient("MLPythonService");
                    if (testClient != null) 
                    {
                        testClient.Timeout = TimeSpan.FromSeconds(3);
                        var res = await testClient.GetAsync(new Uri($"{_baseUrl}/health"));
                        if (res.IsSuccessStatusCode)
                        {
                            BotLogger.Info("[MLPython] Local LightGBM service is active.");
                            StartPythonWatchdog();
                            return;
                        }
                    }
                }
                catch
                {
                    // Not running yet -> try launching
                }

                LaunchPythonProcess();
                StartPythonWatchdog();
            }
            finally
            {
                Interlocked.Exchange(ref _isLaunching, 0);
            }
        });
    }

    /// <summary>
    /// Returns true when running inside Railway (or any platform that sets RAILWAY_ENVIRONMENT).
    /// Used to disable the local Python watchdog which conflicts with container-level restarts.
    /// </summary>
    private static bool IsRunningOnRailway() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_SERVICE_NAME")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAILWAY_PROJECT_ID"));

    private static void LaunchPythonProcess()
    {
        try
        {
            string mlDir = Path.Combine(Directory.GetCurrentDirectory(), "ml_service");
            string mainScript = Path.Combine(mlDir, "main.py");

            if (!File.Exists(mainScript))
            {
                mlDir = Path.Combine(AppContext.BaseDirectory, "ml_service");
                mainScript = Path.Combine(mlDir, "main.py");
            }

            if (File.Exists(mainScript))
            {
                BotLogger.Info("[MLPython] Auto-starting Python LightGBM microservice...");
                bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows);
                var psi = new ProcessStartInfo
                {
                    FileName         = isWindows ? "py" : "python3",
                    Arguments        = $"\"{mainScript}\"",
                    WorkingDirectory = mlDir,
                    UseShellExecute  = false,
                    CreateNoWindow   = true
                };

                _mlProcess = Process.Start(psi);
                if (_mlProcess != null)
                {
                    BotLogger.Info($"[MLPython] Python service started (PID: {_mlProcess.Id}).");

                    // FIX: Ensure Python process is killed when C# app exits.
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        try
                        {
                            if (_mlProcess != null && !_mlProcess.HasExited)
                            {
                                BotLogger.Info($"[MLPython] Terminating Python service (PID: {_mlProcess.Id})...");
                                _mlProcess.Kill();
                            }
                        }
                        catch { /* Ignore kill errors during shutdown */ }
                    };
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MLPython] Local auto-start notice: {ex.Message}");
        }
    }

    /// <summary>
    /// FIX 3 (2026-09-13): Background watchdog that pings /health every 60s.
    /// After 3 consecutive failures, kills the stale Python process and relaunches it.
    /// Prevents silent ML degradation when Python crashes mid-session.
    /// Only runs for localhost — on Railway, the container orchestrator handles restarts.
    /// </summary>
    private static void StartPythonWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = new CancellationTokenSource();
        var token = _watchdogCts.Token;

        _ = Task.Run(async () =>
        {
            const int CheckIntervalSeconds  = 60;
            const int HealthTimeoutSeconds  = 5;
            const int MaxConsecutiveFails   = 3;
            const int RestartCooldownMs     = 8_000; // Wait for Python to bind port

            int consecutiveFails = 0;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CheckIntervalSeconds));
            while (!token.IsCancellationRequested)
            {
                try { await timer.WaitForNextTickAsync(token); }
                catch (OperationCanceledException) { break; }

                try
                {
                    var hc = _httpFactory?.CreateClient("MLPythonService");
                    if (hc != null) 
                    {
                        hc.Timeout = TimeSpan.FromSeconds(HealthTimeoutSeconds);
                        var resp = await hc.GetAsync(new Uri($"{_baseUrl}/health"), token);

                        if (resp.IsSuccessStatusCode)
                        {
                            if (consecutiveFails > 0)
                                BotLogger.Info($"[MLPython Watchdog] Service recovered after {consecutiveFails} failed check(s).");
                            consecutiveFails = 0;
                        }
                        else
                        {
                            consecutiveFails++;
                            BotLogger.Warn($"[MLPython Watchdog] Health check returned {(int)resp.StatusCode} ({consecutiveFails}/{MaxConsecutiveFails}).");
                        }
                    }
                    else
                    {
                        BotLogger.Warn("[MLPython Watchdog] IHttpClientFactory not initialized yet.");
                        consecutiveFails++;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    consecutiveFails++;
                    BotLogger.Warn($"[MLPython Watchdog] Health check failed ({consecutiveFails}/{MaxConsecutiveFails}).");
                }

                if (consecutiveFails >= MaxConsecutiveFails)
                {
                    BotLogger.Warn($"[MLPython Watchdog] {MaxConsecutiveFails} consecutive failures — restarting Python service.");
                    consecutiveFails = 0;

                    // Kill stale process
                    bool killSuccess = true;
                    try
                    {
                        if (_mlProcess != null && !_mlProcess.HasExited)
                        {
                            BotLogger.Warn($"[MLPython] Watchdog killing stale Python service (PID: {_mlProcess.Id})...");
                            _mlProcess.Kill();
                        }
                    }
                    catch (Exception killEx) 
                    { 
                        BotLogger.Error($"[MLPython] Watchdog failed to kill stale Python service. Aborting relaunch to prevent zombie leak.", killEx);
                        killSuccess = false;
                    }

                    if (killSuccess)
                    {
                        await Task.Delay(1000, token); // brief pause before relaunch
                        LaunchPythonProcess();
                    }
                    await Task.Delay(RestartCooldownMs, token); // wait for port binding
                }
            }
        }, token);
    }


    private static string MapSymbol(string symbol, bool isForex)
    {
        string baseSym = symbol.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "").Replace(" OTC", "");
        if (!isForex)
            return baseSym;
        
        if (baseSym.Length == 6 && !baseSym.EndsWith("USDT"))
        {
            return baseSym;
        }
        return baseSym;
    }

    public static async Task<MLPythonPrediction?> PredictAsync(
        string symbol,
        string interval,
        MiniAppController.OhlcCandle[] candles,
        bool isForex = false,
        MiniAppController.OhlcCandle[]? mtfCandles = null,
        ValutaBot.MiniApp.SmcEngine.SmcAnalysisResult? smcResult = null,
        ValutaBot.MiniApp.OrderFlowEngine.OrderFlowResult? ofResult = null)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;

        try
        {
            var binanceSymbol = MapSymbol(symbol, isForex);
            
            var mappedCandles = candles.Select(c => new {
                openTime = new DateTimeOffset(DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = c.Volume
            }).ToArray();

            object? mappedMtf = null;
            if (mtfCandles != null && mtfCandles.Length > 0)
            {
                mappedMtf = mtfCandles.Select(c => new {
                    openTime = new DateTimeOffset(DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    open = c.Open,
                    high = c.High,
                    low = c.Low,
                    close = c.Close,
                    volume = c.Volume
                }).ToArray();
            }

            string smcBosDir = smcResult?.BosDirection ?? "NONE";
            bool smcHasOb = smcResult?.HasOrderBlock ?? false;
            bool smcHasFvg = smcResult?.HasFvg ?? false;
            double ofDeltaRatio = ofResult?.DeltaRatio ?? 1.0;
            string ofState = ofResult?.OrderFlowState ?? "NEUTRAL";

            object payload = mappedMtf != null 
                ? new { symbol = binanceSymbol, interval = interval, candles = mappedCandles, is_forex = isForex, mtf_candles = mappedMtf,
                        smc_bos_dir = smcBosDir, smc_has_ob = smcHasOb, smc_has_fvg = smcHasFvg, of_delta_ratio = ofDeltaRatio, of_state = ofState }
                : new { symbol = binanceSymbol, interval = interval, candles = mappedCandles, is_forex = isForex,
                        smc_bos_dir = smcBosDir, smc_has_ob = smcHasOb, smc_has_fvg = smcHasFvg, of_delta_ratio = ofDeltaRatio, of_state = ofState };

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            using var content = new ByteArrayContent(jsonBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var client = _httpFactory?.CreateClient("MLPythonService") ?? throw new InvalidOperationException("HttpFactory not set");
            var response = await client.PostAsync(new Uri($"{_baseUrl}/predict"), content);
            
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            
            var responseBody = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<MLPythonPrediction>(responseBody, _jsonOptions);
            
            if (result != null && result.Direction != "NEUTRAL")
            {
                string smcBos = smcResult?.BosDirection ?? "NONE";
                string smcOb = (smcResult?.OrderBlockType != null) ? "OB_PRESENT" : "NO_OB";
                double ofRat = ofResult?.ScoreContribution ?? 0.0;
                
                BotLogger.Info($"[ML Insights] {binanceSymbol}/{interval} | СЦЕНАРИЙ: Слом={smcBos}, Блок={smcOb}, ОФ={ofRat:F1} | ВЕРДИКТ: {result.Direction} (Уверенность: {result.Confidence*100:F1}%) [v:{result.ModelVersion}]");
                return new MLPythonPrediction(
                    Direction:        result.Direction,
                    Confidence:       result.Confidence,
                    ModelVersion:     result.ModelVersion,
                    Accuracy:         result.Accuracy,
                    Auc:              result.Auc,
                    NTrain:           result.NTrain,
                    VarianceEstimate: result.VarianceEstimate,
                    RawConfidence:    result.RawConfidence,
                    HorizonCandles:   result.HorizonCandles
                );
            }
            return null;
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            return null; 
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MLPython] Pipeline execution failed: {ex.Message}");
            return null;
        }
    }

    public static async Task SendFeedbackAsync(
        string asset,
        string timeframe,
        bool wasWin,
        double entryPrice,
        double exitPrice,
        string direction,
        DateTime? entryTime = null,
        bool isForex = false)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return;
        
        try
        {
            var binanceSymbol = MapSymbol(asset, isForex);
            var payload = new
            {
                asset = binanceSymbol,
                timeframe = timeframe,
                entry_price = entryPrice,
                exit_price = exitPrice,
                direction = direction,
                was_win = wasWin,
                is_forex = isForex,
                timestamp = (entryTime ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            if (_httpFactory == null)
            {
                BotLogger.Warn("[MLPython] IHttpClientFactory not initialized yet. Skipping feedback.");
                return;
            }
            var response = await _httpFactory.CreateClient("MLPythonService").PostAsync(new Uri($"{_baseUrl}/feedback"), content);
            
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                string winStr = wasWin ? "WIN" : "LOSS";
                BotLogger.Info($"[AI Feedback Detector] Feedback sent for {asset}/{timeframe} -> {winStr}. Python Response: {responseBody}");
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                BotLogger.Warn($"[MLPython] Feedback rejected: {(int)response.StatusCode}. Details: {errorBody}");
            }
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException) { }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MLPython] Online RL feedback notice: {ex.Message}");
        }
    }

    public static async Task<bool> ForceTrainGlobalAsync(string asset, string timeframe, bool isForex = false, int limit = 2000)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return false;
        try
        {
            var binanceSymbol = MapSymbol(asset, isForex);
            var payload = new
            {
                symbol = binanceSymbol,   // FIXED: was 'asset'
                interval = timeframe,     // FIXED: was 'timeframe'
                is_forex = isForex,
                limit = limit
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            if (_httpFactory == null)
            {
                BotLogger.Warn("[MLPython] IHttpClientFactory not initialized yet. Skipping force train.");
                return false;
            }
            // Use long-running client bypassing Polly short timeouts
            var response = await _httpFactory.CreateClient("MLPythonLongRunning")
                                        .PostAsync(new Uri($"{_baseUrl}/train/sync"), content);

            if (response.IsSuccessStatusCode)
            {
                BotLogger.Info($"[MLPython] Global Batch Retraining SUCCESS for {asset}/{timeframe}.");
                return true;
            }
            else
            {
                BotLogger.Warn($"[MLPython] Global Batch Retraining failed: {(int)response.StatusCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MLPython] Global Batch Retraining error: {ex.Message}");
            return false;
        }
    }
}

