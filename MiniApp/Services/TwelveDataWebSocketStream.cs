using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// WebSocket client for TwelveData.
/// Connects to wss://ws.twelvedata.com/v1/quotes/price to stream real-time forex ticks.
/// Feeds ticks into RealtimeTickCollector for mathematically pure 5s candle generation.
///
/// ROOT-CAUSE FIX (2026-09-04):
/// Previously ReceiveAsync had no timeout. When TwelveData server silently dropped the
/// TCP connection (no WebSocket Close frame), ReceiveAsync blocked indefinitely.
/// During this silent-dead period (typically 2-4 min until OS-level TCP keepalive fires),
/// _livePrices was frozen → flat candles were emitted → indicators degraded → BAD signals.
/// After OS timeout, exception was caught → reconnect → GOOD again. Cycle repeated.
///
/// Fix 1: Watchdog task aborts the WebSocket if no tick arrives for 30 seconds.
/// Fix 2: IsAlive flag exposed so RealtimeTickCollector skips flat-candle emission
///         when the connection is dead (better a gap than a stale price).
/// </summary>
public static class TwelveDataWebSocketStream
{
    private static ClientWebSocket? _ws;
    private static CancellationTokenSource? _cts;
    private static bool _isRunning = false;
    private static readonly ConcurrentDictionary<string, double> _livePrices = new();

    // ROOT-CAUSE FIX: track liveness for RealtimeTickCollector
    private static volatile bool _wsIsAlive = false;
    private static DateTime _lastTickTime = DateTime.MinValue;

    // Exposed so RealtimeTickCollector can skip flat candles when WS is dead.
    // A 45-second grace window covers short network blips without creating gaps.
    public static bool IsAlive =>
        _wsIsAlive && (DateTime.UtcNow - _lastTickTime).TotalSeconds < 45;

    public static void StartStream(string[] symbols)
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ConnectAndConsumeAsync(symbols, _cts.Token));
    }

    public static void StopStream()
    {
        if (!_isRunning) return;
        _cts?.Cancel();
        _isRunning = false;
        _wsIsAlive = false;
        try { _ws?.Dispose(); } catch { }
    }

    public static bool TryGetLivePrice(string symbol, out double price)
    {
        string cleanSym = AssetSanitizer.Sanitize(symbol).ToUpper();
        return _livePrices.TryGetValue(cleanSym, out price);
    }

    private static async Task ConnectAndConsumeAsync(string[] symbols, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _wsIsAlive = false;

            try
            {
                string apiKey = TwelveDataService.GetApiKey();
                if (string.IsNullOrEmpty(apiKey))
                {
                    BotLogger.Warn("[TwelveData WS] No API key found. Waiting 10s before retry...");
                    await Task.Delay(10000, ct);
                    continue;
                }

                using (_ws = new ClientWebSocket())
                {
                    string url = $"wss://ws.twelvedata.com/v1/quotes/price?apikey={apiKey}";
                    await _ws.ConnectAsync(new Uri(url), ct);
                    BotLogger.Info("[TwelveData WS] Connected successfully.");

                    // Mark alive — we're connected
                    _wsIsAlive = true;
                    _lastTickTime = DateTime.UtcNow;

                    // Subscribe to symbols
                    var formattedSymbols = string.Join(",", symbols.Select(s => {
                        string clean = AssetSanitizer.Sanitize(s).ToUpper();
                        if (clean.Length == 6) return $"{clean.Substring(0,3)}/{clean.Substring(3,3)}";
                        return clean;
                    }));

                    var subMsg = new { action = "subscribe", @params = new { symbols = formattedSymbols } };
                    var subBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(subMsg));
                    await _ws.SendAsync(new ArraySegment<byte>(subBytes), WebSocketMessageType.Text, true, ct);

                    // ROOT-CAUSE FIX: Watchdog — aborts WebSocket if no tick arrives for 30s.
                    // Covers the "silent TCP drop" case where _ws.State stays Open but no data flows.
                    using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = Task.Run(async () =>
                    {
                        const int SilenceThresholdSeconds = 30;
                        while (!watchdogCts.Token.IsCancellationRequested)
                        {
                            try { await Task.Delay(10_000, watchdogCts.Token); }
                            catch (OperationCanceledException) { break; }

                            double silenceSecs = (DateTime.UtcNow - _lastTickTime).TotalSeconds;
                            if (silenceSecs > SilenceThresholdSeconds)
                            {
                                BotLogger.Warn($"[WS Watchdog] No ticks for {silenceSecs:F0}s — forcing reconnect.");
                                _wsIsAlive = false;
                                try { _ws?.Abort(); } catch { }
                                break;
                            }
                        }
                    }, watchdogCts.Token);

                    var buffer = new byte[8192];
                    var messageBuffer = new System.IO.MemoryStream();
                    while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                BotLogger.Warn("[TwelveData WS] Server closed connection.");
                                goto exitLoop;
                            }
                            messageBuffer.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        string json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.SetLength(0);
                        ProcessMessage(json);
                    }
                    exitLoop:;
                    watchdogCts.Cancel(); // Stop watchdog when connection loop ends
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _wsIsAlive = false;
                BotLogger.Error("[TwelveData WS] Connection error", ex);
                await Task.Delay(5000, ct); // Backoff before reconnect
            }
        }

        _wsIsAlive = false;
    }

    private static void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("event", out var eventProp) && eventProp.GetString() == "price")
            {
                string rawSymbol = root.GetProperty("symbol").GetString() ?? "";
                double price = root.GetProperty("price").GetDouble();

                string cleanSym = rawSymbol.Replace("/", "").ToUpper();

                // Update local fast cache
                _livePrices[cleanSym] = price;
                SignalTracker._livePrices[cleanSym] = price;

                // ROOT-CAUSE FIX: Update liveness timestamp on every real tick
                _lastTickTime = DateTime.UtcNow;
                _wsIsAlive = true;

                // Push to accumulator for 5s candles
                RealtimeTickCollector.OnPriceUpdate(cleanSym, price);
            }
        }
        catch { /* Ignore parsing errors on ping/heartbeat messages */ }
    }
}