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
/// </summary>
public static class TwelveDataWebSocketStream
{
    private static ClientWebSocket? _ws;
    private static CancellationTokenSource? _cts;
    private static bool _isRunning = false;
    private static readonly ConcurrentDictionary<string, double> _livePrices = new();

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

                    // Subscribe to symbols
                    var formattedSymbols = string.Join(",", symbols.Select(s => {
                        string clean = AssetSanitizer.Sanitize(s).ToUpper();
                        if (clean.Length == 6) return $"{clean.Substring(0,3)}/{clean.Substring(3,3)}";
                        return clean;
                    }));

                    var subMsg = new { action = "subscribe", @params = new { symbols = formattedSymbols } };
                    var subBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(subMsg));
                    await _ws.SendAsync(new ArraySegment<byte>(subBytes), WebSocketMessageType.Text, true, ct);

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
                        messageBuffer.SetLength(0); // Reset for next message
                        ProcessMessage(json);
                    }
                    exitLoop:;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                BotLogger.Error("[TwelveData WS] Connection error", ex);
                await Task.Delay(5000, ct); // Backoff before reconnect
            }
        }
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

                // Push to accumulator for 5s candles
                RealtimeTickCollector.OnPriceUpdate(cleanSym, price);
            }
        }
        catch { /* Ignore parsing errors on ping/heartbeat messages */ }
    }
}