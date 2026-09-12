using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static Process? _mlProcess; // Track to prevent zombie leaks

    public record MLPythonPrediction(
        string Direction,
        double Confidence,
        string ModelVersion,
        double? Accuracy,
        double? Auc,
        int? NTrain
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

    private static void EnsureLocalPythonServiceRunning()
    {
        Task.Run(async () =>
        {
            try
            {
                using var testClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var res = await testClient.GetAsync(new Uri($"{_baseUrl}/health"));
                if (res.IsSuccessStatusCode)
                {
                    BotLogger.Info("[MLPython] Local LightGBM service is active.");
                    return;
                }
            }
            catch
            {
                // Not running yet -> try launching
            }

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
                    bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
                    var psi = new ProcessStartInfo
                    {
                        FileName = isWindows ? "py" : "python3",
                        Arguments = $"\"{mainScript}\""",
                        WorkingDirectory = mlDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    _mlProcess = Process.Start(psi);
                    if (_mlProcess != null)
                    {
                        BotLogger.Info($"[MLPython] Python LightGBM service started in background (PID: {_mlProcess.Id})!");
                        
                        // FIX: Ensure Python process is killed when C# app exits to prevent OOM / Zombie leaks
                        AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
                        {
                            try
                            {
                                if (_mlProcess != null && !_mlProcess.HasExited)
                                {
                                    BotLogger.Info($"[MLPython] Terminating background Python service (PID: {_mlProcess.Id})...");
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
        });
    }

    private static string MapSymbol(string symbol, bool isForex)
    {
        if (!isForex)
            return symbol.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
        
        string baseSym = symbol.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
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
        MiniAppController.OhlcCandle[]? mtfCandles = null)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;

        try
        {
            var binanceSymbol = MapSymbol(symbol, isForex);
            var candleList = candles.Select(c => new
            {
                openTime = c.Timestamp == default ? 0 : new DateTimeOffset(c.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc) : c.Timestamp).ToUnixTimeSeconds(),
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = c.Volume
            }).ToList();

            object payload;
            if (mtfCandles != null && mtfCandles.Length > 0)
            {
                var mtfList = mtfCandles.Select(c => new
                {
                    openTime = c.Timestamp == default ? 0 : new DateTimeOffset(c.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc) : c.Timestamp).ToUnixTimeSeconds(),
                    open = c.Open,
                    high = c.High,
                    low = c.Low,
                    close = c.Close,
                    volume = c.Volume
                }).ToList();

                payload = new { symbol = binanceSymbol, interval = interval, candles = candleList, is_forex = isForex, mtf_candles = mtfList };
            }
            else
            {
                payload = new { symbol = binanceSymbol, interval = interval, candles = candleList, is_forex = isForex };
            }

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await MiniAppController.HttpFactory!.CreateClient("MLPythonService").PostAsync(new Uri($"{_baseUrl}/predict"), content);
            
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
                BotLogger.Info($"[MLPython] {binanceSymbol}/{interval} -> {result.Direction} (Conf: {result.Confidence:F2}) [v:{result.ModelVersion}]");
                return new MLPythonPrediction(
                    Direction:    result.Direction,
                    Confidence:   result.Confidence,
                    ModelVersion: result.ModelVersion,
                    Accuracy:     result.Accuracy,
                    Auc:          result.Auc,
                    NTrain:       result.NTrain
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
                was_win = wasWin,
                entry_price = entryPrice,
                is_forex = isForex,
                timestamp = (entryTime ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await MiniAppController.HttpFactory!.CreateClient("MLPythonService").PostAsync(new Uri($"{_baseUrl}/feedback"), content);
            
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                string winStr = wasWin ? "WIN" : "LOSS";
                BotLogger.Info($"[AI Feedback Detector] Feedback sent for {asset}/{timeframe} -> {winStr}. Python Response: {responseBody}");
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
                asset = binanceSymbol,
                timeframe = timeframe,
                is_forex = isForex,
                limit = limit
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Use long-running client bypassing Polly short timeouts
            var response = await MiniAppController.HttpFactory!.CreateClient("MLPythonLongRunning")
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
