using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ValutaBot.MiniApp;

/// <summary>
/// A zero-allocation (on the hot path) Ring Buffer for storing candlestick data.
/// Avoids O(N) array cloning on every WebSocket tick.
/// </summary>
public class CandleSeriesBuffer
{
    private readonly double[] _opens;
    private readonly double[] _highs;
    private readonly double[] _lows;
    private readonly double[] _prices;
    private readonly double[] _volumes;
    private int _head = 0;
    private int _count = 0;
    private readonly int _capacity;
    private readonly object _lock = new();

    public long LastCandleTime { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public CandleSeriesBuffer(int capacity = 100)
    {
        _capacity = capacity;
        _opens = new double[capacity];
        _highs = new double[capacity];
        _lows = new double[capacity];
        _prices = new double[capacity];
        _volumes = new double[capacity];
    }

    public void Update(double open, double high, double low, double close, double volume, long candleTime)
    {
        lock (_lock)
        {
            if (LastCandleTime == candleTime && _count > 0)
            {
                // Update latest tick (same candle)
                int idx = (_head - 1 + _capacity) % _capacity;
                _highs[idx] = Math.Max(_highs[idx], high);
                _lows[idx] = Math.Min(_lows[idx], low);
                _prices[idx] = close;
                _volumes[idx] = volume;
            }
            else
            {
                // Push new candle
                _opens[_head] = open;
                _highs[_head] = high;
                _lows[_head] = low;
                _prices[_head] = close;
                _volumes[_head] = volume;
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
            }
            LastCandleTime = candleTime;
            UpdatedAt = DateTime.UtcNow;
        }
    }

    public (double[] opens, double[] highs, double[] lows, double[] closes, double[] volumes, int count) GetOrderedSnapshotRented()
    {
        lock (_lock)
        {
            if (_count == 0) return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), 0);

            double[] outOpens = ArrayPool<double>.Shared.Rent(_count);
            double[] outHighs = ArrayPool<double>.Shared.Rent(_count);
            double[] outLows = ArrayPool<double>.Shared.Rent(_count);
            double[] outPrices = ArrayPool<double>.Shared.Rent(_count);
            double[] outVolumes = ArrayPool<double>.Shared.Rent(_count);

            int startIdx = (_head - _count + _capacity) % _capacity;
            for (int i = 0; i < _count; i++)
            {
                int srcIdx = (startIdx + i) % _capacity;
                outOpens[i] = _opens[srcIdx];
                outHighs[i] = _highs[srcIdx];
                outLows[i] = _lows[srcIdx];
                outPrices[i] = _prices[srcIdx];
                outVolumes[i] = _volumes[srcIdx];
            }
            return (outOpens, outHighs, outLows, outPrices, outVolumes, _count);
        }
    }
}

public static class BinanceWebSocketStream
{
    private static readonly ConcurrentDictionary<string, CandleSeriesBuffer> _liveCandles = new();

    // Payload consists of rented ArrayPool array and the valid length.
    private record struct SocketPayload(byte[] Buffer, int Length);

    private static Channel<SocketPayload> _jsonChannel = CreateChannel();

    private static Channel<SocketPayload> CreateChannel() => Channel.CreateBounded<SocketPayload>(new BoundedChannelOptions(2000)
    {
        SingleWriter = true,
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private static CancellationTokenSource? _cts;
    private static bool _isRunning = false;

    public static bool TryGetLiveCandles(string symbol, string interval, out double[] opens, out double[] highs, out double[] lows, out double[] prices, out double[] volumes, out int count)
    {
        string key = $"{symbol.ToUpper()}_{interval.ToLower()}";
        if (_liveCandles.TryGetValue(key, out var buffer))
        {
            if ((DateTime.UtcNow - buffer.UpdatedAt).TotalSeconds < 5)
            {
                (opens, highs, lows, prices, volumes, count) = buffer.GetOrderedSnapshotRented();
                return true;
            }
        }

        opens = Array.Empty<double>();
highs = Array.Empty<double>();
lows = Array.Empty<double>();
prices = Array.Empty<double>();
        volumes = Array.Empty<double>();
        count = 0;
        return false;
    }

    public static void StartStream(IEnumerable<string> symbols, string interval = "1m")
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();

        var streams = new List<string>();
        foreach (var s in symbols)
        {
            string cleanSym = AssetSanitizer.Sanitize(s).ToLower();
            streams.Add($"{cleanSym}@kline_{interval}");
        }
        string streamNames = string.Join("/", streams);
        string wsUrl = $"wss://stream.binance.com:9443/stream?streams={streamNames}";

        _jsonChannel = CreateChannel();

        _ = Task.Run(() => BackgroundConsumerLoopAsync(interval, _cts.Token));
        _ = Task.Run(() => ProducerNetworkLoopAsync(wsUrl, _cts.Token));
    }

    public static void StopStream()
    {
        if (!_isRunning) return;
        _cts?.Cancel();
        _jsonChannel.Writer.TryComplete();
        _isRunning = false;
        BotLogger.Info("[WebSocket Producer] WebSocket stream stopped and disconnected.");
    }
    
    public static void Stop() => StopStream(); // Forward to StopStream for backwards compatibility

    private static async Task ProducerNetworkLoopAsync(string url, CancellationToken token)
    {
        int reconnectAttempts = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new ClientWebSocket();
                client.Options.SetRequestHeader("User-Agent", "ValutaBot/2.0-ZeroAlloc");
                client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                BotLogger.Info($"[WebSocket Producer] Connecting to Binance real-time stream: {url}");
                await client.ConnectAsync(new Uri(url), token);
                reconnectAttempts = 0;
                BotLogger.Info("[WebSocket Producer] Connected successfully to Binance WebSocket stream!");

                byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(65536);

                while (client.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    ValueWebSocketReceiveResult result = default;
                    int offset = 0;

                    try
                    {
                        do
                        {
                            if (offset >= receiveBuffer.Length)
                            {
                                var newBuffer = ArrayPool<byte>.Shared.Rent(receiveBuffer.Length * 2);
                                Array.Copy(receiveBuffer, newBuffer, offset);
                                ArrayPool<byte>.Shared.Return(receiveBuffer);
                                receiveBuffer = newBuffer;
                            }

                            result = await client.ReceiveAsync(receiveBuffer.AsMemory(offset, receiveBuffer.Length - offset), token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            offset += result.Count;
                        }
                        while (!result.EndOfMessage && !token.IsCancellationRequested);
                    }
                    catch (WebSocketException wsEx)
                    {
                        BotLogger.Warn($"[WebSocket Producer] Network frame receive error: {wsEx.Message}. Reconnecting.");
                        break;
                    }

                    if (token.IsCancellationRequested || client.State != WebSocketState.Open)
                    {
                        BotLogger.Warn($"[WebSocket Producer] Socket state changed to {client.State}. Reconnecting...");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        BotLogger.Warn("[WebSocket Producer] Received close frame from Binance. Reconnecting...");
                        try
                        {
                            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", token);
                        }
                        catch { /* Ignore close handshake error */ }
                        break;
                    }

                    if (offset > 0)
                    {
                        byte[] channelBuffer = ArrayPool<byte>.Shared.Rent(offset);
                        Array.Copy(receiveBuffer, channelBuffer, offset);

                        if (!_jsonChannel.Writer.TryWrite(new SocketPayload(channelBuffer, offset)))
                        {
                            ArrayPool<byte>.Shared.Return(channelBuffer); // Return if drop occurs
                        }
                    }
                }
                ArrayPool<byte>.Shared.Return(receiveBuffer);
            }
            catch (WebSocketException wsEx)
            {
                reconnectAttempts++;
                BotLogger.Warn($"[WebSocket Producer] Connection exception (Attempt #{reconnectAttempts}): {wsEx.Message}");
            }
            catch (Exception ex)
            {
                reconnectAttempts++;
                BotLogger.Error($"[WebSocket Producer] Unexpected error (Attempt #{reconnectAttempts}): {ex.Message}", ex);
            }


            if (!token.IsCancellationRequested)
            {
                int delayMs = Math.Min(10000, 2000 + (reconnectAttempts * 1000));
                BotLogger.Info($"[WebSocket Producer] Waiting {delayMs}ms before instantiating new ClientWebSocket...");
                await Task.Delay(delayMs, token);
            }
        }
    }

    private static async Task BackgroundConsumerLoopAsync(string interval, CancellationToken token)
    {
        BotLogger.Info("[WebSocket Consumer] Started background zero-allocation processing loop.");

        try
        {
            await foreach (var payload in _jsonChannel.Reader.ReadAllAsync(token))
            {
                try
                {
                    // Zero allocation raw byte parse
                    ProcessKlineMessage(payload.Buffer.AsSpan(0, payload.Length), interval);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            BotLogger.Info("[WebSocket Consumer] Channel reader loop cancelled.");
        }
        catch (Exception ex)
        {
            BotLogger.Error("[WebSocket Consumer] Error processing frame in consumer loop", ex);
        }
        finally
        {
            while (_jsonChannel.Reader.TryRead(out var leftover))
            {
                ArrayPool<byte>.Shared.Return(leftover.Buffer);
            }
        }
    }

    private static CandleSeriesBuffer CreateBuffer(string key) => new CandleSeriesBuffer(100);

    private static void ProcessKlineMessage(ReadOnlySpan<byte> jsonData, string interval)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(jsonData);
            string? stream = null;
            string? symbol = null;
            double openPrice = 0;
double highPrice = 0;
double lowPrice = 0;
double closePrice = 0;
            double volume = 0;
            long startTime = 0;
            
            while (reader.Read())
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("stream"u8))
                    {
                        reader.Read();
                        stream = reader.GetString();
                    }
                    else if (reader.ValueTextEquals("s"u8))
                    {
                        reader.Read();
                        symbol = reader.GetString();
                    }
                    else if (reader.ValueTextEquals("o"u8))
{
    reader.Read();
    if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        _ = System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out openPrice, out _);
    else
        openPrice = reader.GetDouble();
}
else if (reader.ValueTextEquals("h"u8))
{
    reader.Read();
    if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        _ = System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out highPrice, out _);
    else
        highPrice = reader.GetDouble();
}
else if (reader.ValueTextEquals("l"u8))
{
    reader.Read();
    if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        _ = System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out lowPrice, out _);
    else
        lowPrice = reader.GetDouble();
}
else if (reader.ValueTextEquals("c"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
                            _ = System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out closePrice, out _);
                        else
                            closePrice = reader.GetDouble();
                    }
                    else if (reader.ValueTextEquals("v"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
                            _ = System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out volume, out _);
                        else
                            volume = reader.GetDouble();
                    }
                    else if (reader.ValueTextEquals("t"u8))
                    {
                        reader.Read();
                        startTime = reader.GetInt64();
                    }
                }
            }

            if (stream != null && symbol != null)
            {
                if (stream.Contains("kline") || (closePrice > 0 && volume > 0))
                {
                    if (!string.IsNullOrEmpty(symbol) && closePrice > 0)
                    {
                        string key = $"{ValutaBot.MiniApp.AssetSanitizer.Sanitize(symbol)}_{interval.ToLower()}";

                        var buffer = _liveCandles.GetOrAdd(key, CreateBuffer);
                        buffer.Update(openPrice, highPrice, lowPrice, closePrice, volume, startTime);
var cleanSymbol = ValutaBot.MiniApp.AssetSanitizer.Sanitize(symbol);

                    }
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn("[WebSocket Consumer] Error parsing zero-allocation JSON frame", ex);
        }
    }

    
}







