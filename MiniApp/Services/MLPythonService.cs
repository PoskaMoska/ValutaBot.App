using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace ValutaBot.MiniApp;

/// <summary>
/// Calls the Python LightGBM ML microservice via HTTP.
/// Static service using the shared HttpClient from MiniAppController.
/// Falls back gracefully using Polly Resilience Pipeline if the service is unavailable.
/// </summary>
public static class MLPythonService
{
     // Timeout managed by Polly
    private static string _baseUrl = string.Empty;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
// ── Result type ────────────────────────────────────────────────────────

    public record MLPythonPrediction(
        string Direction,       // "BUY" | "PUT" | "NEUTRAL"
        double Confidence,      // 0.0 – 1.0
        string ModelVersion,    // e.g. "lgbm-v1-BTCUSDT_1m-1720000000"
        double? Accuracy,       // CV accuracy (null if model not yet trained)
        double? Auc             // CV AUC-ROC
    );

    // ── Init ───────────────────────────────────────────────────────────────

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
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = isWindows ? "py" : "python3",
                        Arguments = $"\"{mainScript}\"",
                        WorkingDirectory = mlDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(psi);
                    BotLogger.Info("[MLPython] Python LightGBM service started in background!");
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[MLPython] Local auto-start notice: {ex.Message}");
            }
        });
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Send candles to the Python ML service and receive a direction prediction.
    /// Returns null if the service is unavailable or disabled.
    /// </summary>
    public static async Task<MLPythonPrediction?> PredictAsync(
        string symbol,
        string interval,
        MiniAppController.OhlcCandle[] candles,
        bool isForex = false)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
            return null;

        try
        {
            // Map ValutaBot asset name to Binance-style symbol
            var binanceSymbol = MapSymbol(symbol, isForex);

            // Build request payload
            var candleList = candles.Select(c => new
            {
                openTime = c.Timestamp == default ? 0 : new DateTimeOffset(c.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc) : c.Timestamp).ToUnixTimeSeconds(),
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = c.Volume
            }).ToArray();

            var payload = new
            {
                symbol = binanceSymbol,
                interval = interval,
                candles = candleList,
                is_forex = isForex
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await MiniAppController.HttpFactory!.CreateClient("MLPythonService").PostAsync(new Uri($"{_baseUrl}/predict"), content);

            if (!response.IsSuccessStatusCode)
            {
                BotLogger.Warn($"[MLPython] Non-OK response: {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PredictResponseDto>(body, _jsonOptions);

            if (result == null)
                return null;

            BotLogger.Info($"[MLPython] {binanceSymbol}/{interval} → {result.Direction} " +
                           $"conf={result.Confidence:F2} model={result.ModelVersion}");

            return new MLPythonPrediction(
                Direction:    result.Direction ?? "NEUTRAL",
                Confidence:   result.Confidence,
                ModelVersion: result.ModelVersion ?? "unknown",
                Accuracy:     result.Accuracy,
                Auc:          result.Auc
            );
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            // Circuit is open, requests are failing fast. Suppress to avoid log spam on every tick.
            return null; 
        }
        catch (Exception ex)
        {
            // Fallback for any other pipeline failure (like timeout when circuit is half-open or closed)
            BotLogger.Warn($"[MLPython] Pipeline execution failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Sends real verified trade outcome (Win/Loss) to Python ML service for online reinforcement learning.
    /// </summary>
    public static async Task RecordOnlineTradeOutcomeAsync(
        string asset,
        string timeframe,
        double entryPrice,
        double exitPrice,
        string direction,
        bool wasWin,
        bool isForex = false)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return;

        try
        {
            var binanceSymbol = MapSymbol(asset, isForex);
            var payload = new
            {
                asset = binanceSymbol, // FIX: Pass the mapped symbol so Python updates the correct model
                timeframe,
                entry_price = entryPrice,
                exit_price = exitPrice,
                direction,
                was_win = wasWin,
                timestamp = DateTime.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await MiniAppController.HttpFactory!.CreateClient("MLPythonService").PostAsync(new Uri($"{_baseUrl}/feedback"), content);
            
            if (response.IsSuccessStatusCode)
            {
                BotLogger.Info($"[MLPython] Online RL feedback registered for {asset}/{timeframe} → {(wasWin ? "WIN" : "LOSS")}");
            }
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            // Circuit is open, suppress log
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[MLPython] Online RL feedback notice: {ex.Message}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Forces global batch training on the provided historical candles via /train/sync.
    /// </summary>
    public static async Task<bool> ForceTrainGlobalAsync(
        string asset,
        string interval,
        MiniAppController.OhlcCandle[] history,
        bool isForex = false)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return false;

        try
        {
            var binanceSymbol = MapSymbol(asset, isForex);
            var candleList = history.Select(c => new
            {
                openTime = c.Timestamp == default ? 0 : new DateTimeOffset(c.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc) : c.Timestamp).ToUnixTimeSeconds(),
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = c.Volume
            }).ToArray();

            var payload = new
            {
                symbol = binanceSymbol,
                interval = interval,
                candles = candleList,
                is_forex = isForex
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Increase timeout for massive global training
            var client = MiniAppController.HttpFactory!.CreateClient("MLPythonService");
            client.Timeout = TimeSpan.FromMinutes(10);
            
            var response = await client.PostAsync(new Uri($"{_baseUrl}/train/sync"), content);
            
            if (response.IsSuccessStatusCode)
            {
                BotLogger.Info($"[MLPython] Global Batch Retraining completed for {asset}/{interval} on {history.Length} candles.");
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

    /// <summary>
    /// Map ValutaBot internal symbol to Binance-style uppercase symbol.
    /// Forex pairs are returned as-is (service handles them gracefully).
    /// </summary>
    private static string MapSymbol(string asset, bool isForex)
    {
        if (isForex)
        {
            // For forex, use EURUSD-style (Python service uses it as a key for model storage)
            return asset.Replace("/", "").Replace(" ", "").Replace("OTC", "")
                        .Replace("otc", "").Trim().ToUpperInvariant();
        }

        return asset.ToUpperInvariant() switch
        {
            "BTC" or "BITCOIN"  => "BTCUSDT",
            "ETH" or "ETHEREUM" => "ETHUSDT",
            "SOL" or "SOLANA"   => "SOLUSDT",
            "BNB"               => "BNBUSDT",
            "XRP"               => "XRPUSDT",
            "ADA"               => "ADAUSDT",
            "DOGE"              => "DOGEUSDT",
            _                   => asset.ToUpperInvariant().EndsWith("USDT")
                                       ? asset.ToUpperInvariant()
                                       : asset.ToUpperInvariant() + "USDT"
        };
    }

    // ── DTO ────────────────────────────────────────────────────────────────

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json deserialization")]
    internal class PredictResponseDto
    {
        [JsonPropertyName("direction")]    public string?  Direction    { get; set; }
        [JsonPropertyName("confidence")]   public double   Confidence   { get; set; }
        [JsonPropertyName("model_version")]public string?  ModelVersion { get; set; }
        [JsonPropertyName("accuracy")]     public double?  Accuracy     { get; set; }
        [JsonPropertyName("auc")]          public double?  Auc          { get; set; }
    }
}

