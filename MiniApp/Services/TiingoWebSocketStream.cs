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
/// Connects to wss://api.tiingo.com/fx to stream real-time forex ticks for all pairs.
/// Feeds ticks into RealtimeTickCollector for mathematically pure subminute candle generation.
///
/// Lifecycle invariant: at most ONE connection loop exists per process. All reconnects
/// happen by iterating that single loop (with backoff) — never by spawning another loop.
/// </summary>
public static class TiingoWebSocketStream
{
    private static ClientWebSocket? _webSocket;
    private static CancellationTokenSource _cts = new CancellationTokenSource();
    private static string[] _subscribedSymbols = Array.Empty<string>();
    private static DateTime _lastMessageTime = DateTime.UtcNow;

    // 0 = no loop running, 1 = loop running. Guarantees a single connection loop.
    private static int _loopRunning = 0;

    // Cancelled by the watchdog to drop the CURRENT connection; the single loop then reconnects.
    private static CancellationTokenSource? _connectionCts;

    private const int MinBackoffMs = 5_000;
    private const int MaxBackoffMs = 60_000;

    // Live price store: last tick per symbol
    private static readonly ConcurrentDictionary<string, double> _livePrices = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetLivePrice(string symbol, out double price)
    {
        return _livePrices.TryGetValue(AssetSanitizer.Sanitize(symbol), out price);
    }

    public static void StartStream(string[] symbols)
    {
        // Single-loop guard: a second StartStream call must not create a second connection.
        if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) != 0)
        {
            BotLogger.Warn("[Tiingo WS] StartStream called while a stream loop is already running. Ignored.");
            return;
        }

        _subscribedSymbols = symbols.Select(s => AssetSanitizer.Sanitize(s).ToLower()).ToArray();
        _cts = new CancellationTokenSource();
        _ = RunConnectionLoopAsync(_cts.Token);
        _ = WatchdogLoopAsync(_cts.Token);
    }

    public static void StopStream()
    {
        _cts.Cancel();
        try { _connectionCts?.Cancel(); } catch { }
        var ws = _webSocket;
        if (ws != null && ws.State == WebSocketState.Open)
        {
            _ = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None);
        }
    }

    private static async Task WatchdogLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(15000, token); // Check every 15s

                bool isDead = (DateTime.UtcNow - _lastMessageTime).TotalSeconds > 60;
                if (isDead && _webSocket?.State == WebSocketState.Open)
                {
                    BotLogger.Warn("[Tiingo WS] Watchdog detected silent drop (no ticks for >60s). Dropping connection to force reconnect...");
                    // Do NOT start another loop: cancel the current connection and let the single loop reconnect.
                    try { _connectionCts?.Cancel(); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <summary>
    /// The one and only connection loop. Each iteration owns exactly one socket (local variable),
    /// so concurrent receivers on a shared socket are impossible.
    /// </summary>
    private static async Task RunConnectionLoopAsync(CancellationToken token)
    {
        string apiKey = TiingoService.GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            BotLogger.Error("[Tiingo WS] TIINGO_API_KEY is missing. WS Aborted.");
            Interlocked.Exchange(ref _loopRunning, 0);
            return;
        }

        int backoffMs = MinBackoffMs;

        try
        {
            while (!token.IsCancellationRequested)
            {
                bool wasConnected = false;
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _connectionCts = connCts;
                var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _webSocket = ws;

                try
                {
                    await ws.ConnectAsync(new Uri("wss://api.tiingo.com/fx"), connCts.Token);
                    BotLogger.Info("[Tiingo WS] Connected!");
                    wasConnected = true;
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
                    await ws.SendAsync(Encoding.UTF8.GetBytes(jsonSub), WebSocketMessageType.Text, true, connCts.Token);

                    await ReceiveLoopAsync(ws, connCts.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break; // shutdown
                }
                catch (Exception ex)
                {
                    BotLogger.Error($"[Tiingo WS] Connection error: {ex.Message}. Retrying in {backoffMs / 1000}s...");
                }
                finally
                {
                    try { ws.Dispose(); } catch { }
                    if (ReferenceEquals(_webSocket, ws)) _webSocket = null;
                }

                // A connection that was established resets the backoff; repeated failures back off exponentially.
                backoffMs = wasConnected ? MinBackoffMs : Math.Min(backoffMs * 2, MaxBackoffMs);
                await Task.Delay(backoffMs, token);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            Interlocked.Exchange(ref _loopRunning, 0);
        }
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken connectionToken)
    {
        var buffer = new byte[8192];

        try
        {
            while (ws.State == WebSocketState.Open && !connectionToken.IsCancellationRequested)
            {
                using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
                ctsTimeout.CancelAfter(TimeSpan.FromMinutes(2));

                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ctsTimeout.Token);

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
                            _ = RealtimeTickCollector.OnPriceUpdateAsync(ticker, midPrice);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Either shutdown, a watchdog-forced drop, or a 2-minute receive timeout.
            // The single connection loop decides whether to reconnect.
            if (!connectionToken.IsCancellationRequested)
                BotLogger.Warn("[Tiingo WS] Receive loop timed out. Dropping connection to force reconnect.");
        }
        catch (Exception ex)
        {
            BotLogger.Error($"[Tiingo WS] Receive error: {ex.Message}");
        }
        // No reconnect here. Reconnecting is the sole responsibility of RunConnectionLoopAsync.
    }
}
