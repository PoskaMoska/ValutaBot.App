using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// WebSocket client for Tiingo.
/// Connects to wss://api.tiingo.com/fx to stream real-time forex ticks.
/// Overcomes TwelveData's limitation by supporting all pairs on the free tier.
/// Feeds ticks into RealtimeTickCollector for mathematically pure subminute candle generation.
/// </summary>
public static class TiingoWebSocketStream
{
    private static ClientWebSocket? _webSocket;
    private static CancellationTokenSource _cts = new CancellationTokenSource();
    private static string[] _subscribedSymbols = Array.Empty<string>();
    private static bool _isConnecting = false;
    private static DateTime _lastMessageTime = DateTime.UtcNow;

    // We store the last live price here exactly like TwelveDataWS does
    private static readonly ConcurrentDictionary<string, double> _livePrices = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetLivePrice(string symbol, out double price)
    {
        return _livePrices.TryGetValue(AssetSanitizer.Sanitize(symbol), out price);
    }

    public static void StartStream(string[] symbols)
    {
        _subscribedSymbols = symbols.Select(s => AssetSanitizer.Sanitize(s).ToLower()).ToArray();
        _cts = new CancellationTokenSource();
        _ = ConnectAndListenAsync();
        _ = WatchdogLoopAsync();
    }

    public static void StopStream()
    {
        _cts.Cancel();
        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            _ = _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None);
        }
    }

    private static async Task WatchdogLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await Task.Delay(15000); // Check every 15s

            bool isDead = (DateTime.UtcNow - _lastMessageTime).TotalSeconds > 60;
            if (isDead && _webSocket?.State == WebSocketState.Open)
            {
                BotLogger.Warn("[Tiingo WS] Watchdog detected silent drop (no ticks for >60s). Reconnecting...");
                try { _webSocket.Dispose(); } catch { }
                _webSocket = null;
                _isConnecting = false;
                _ = ConnectAndListenAsync();
            }
        }
    }

    private static async Task ConnectAndListenAsync()
    {
        if (_isConnecting) return;
        _isConnecting = true;

        string apiKey = TiingoService.GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            BotLogger.Error("[Tiingo WS] TIINGO_API_KEY is missing. WS Aborted.");
            _isConnecting = false;
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _webSocket = new ClientWebSocket();
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                
                await _webSocket.ConnectAsync(new Uri("wss://api.tiingo.com/fx"), _cts.Token);
                BotLogger.Info("[Tiingo WS] Connected!");
                _lastMessageTime = DateTime.UtcNow;

                var subscribeMessage = new
                {
                    eventName = "subscribe",
                    authorization = apiKey,
                    eventData = new
                    {
                        thresholdLevel = 5,
                        tickers = _subscribedSymbols
                    }
                };

                string jsonSub = JsonSerializer.Serialize(subscribeMessage);
                await _webSocket.SendAsync(Encoding.UTF8.GetBytes(jsonSub), WebSocketMessageType.Text, true, _cts.Token);

                _isConnecting = false;
                await ReceiveLoopAsync();
            }
            catch (Exception ex)
            {
                BotLogger.Error($"[Tiingo WS] Connection error: {ex.Message}. Retrying in 5s...");
                _isConnecting = false;
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private static async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                ctsTimeout.CancelAfter(TimeSpan.FromMinutes(2));

                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ctsTimeout.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    BotLogger.Warn($"[Tiingo WS] Closed by server: {result.CloseStatusDescription}");
                    break;
                }

                _lastMessageTime = DateTime.UtcNow;
                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                
                // Fast path parsing
                if (!message.Contains("\"messageType\":\"A\"")) continue;

                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    // Tiingo format: [messageType (0), ticker (1), date (2), bidSize (3), bidPrice (4), midPrice (5), askSize (6), askPrice (7)]
                    if (dataArr.GetArrayLength() >= 6)
                    {
                        string ticker = dataArr[1].GetString()?.ToUpper() ?? "";
                        double midPrice = dataArr[5].GetDouble();
                        
                        if (midPrice > 0)
                        {
                            _livePrices[ticker] = midPrice;
                            RealtimeTickCollector.AddTick(ticker, midPrice);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            BotLogger.Warn("[Tiingo WS] Receive loop timed out or canceled. Dropping connection to force reconnect.");
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[Tiingo WS] Receive error: {ex.Message}");
        }
        finally
        {
            if (_webSocket != null)
            {
                try { _webSocket.Dispose(); } catch { }
                _webSocket = null;
            }
            _ = ConnectAndListenAsync();
        }
    }
}
