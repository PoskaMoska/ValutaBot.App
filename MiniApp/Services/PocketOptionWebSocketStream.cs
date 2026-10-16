using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp;

/// <summary>
/// Autonomous Pocket Option OTC WebSocket client.
/// Connects directly to Pocket Option Socket.IO gateway (wss://demo-api-eu.po.market/socket.io/?EIO=4&transport=websocket)
/// using the session credentials stored in po_collector_state.
/// Streams live 24/7 ticks for EURUSD_otc, GBPUSD_otc, USDJPY_otc, AUDUSD_otc, USDCAD_otc, USDCHF_otc.
/// Feeds ticks directly into RealtimeTickCollector for subminute candle generation (s5, s10, s15, s30, 1min)
/// and writes to po_ticks in PostgreSQL.
/// Runs completely headlessly in the cloud without requiring any browser or Tampermonkey script.
/// </summary>
public static class PocketOptionWebSocketStream
{
    private static ClientWebSocket? _webSocket;
    private static CancellationTokenSource _cts = new CancellationTokenSource();
    private static DateTime _lastMessageTime = DateTime.UtcNow;
    private static int _loopRunning = 0;
    private static CancellationTokenSource? _connectionCts;
    private static long _totalTicksIngested = 0;

    private static readonly string[] DefaultAssets = {
        "EURUSD_otc", "GBPUSD_otc", "USDJPY_otc", "AUDUSD_otc", "USDCAD_otc", "USDCHF_otc"
    };

    private static string[] _subscribedAssets = DefaultAssets;

    private const int ReconnectAfterConnectedMs = 1_000;
    private const int MinBackoffMs = 5_000;
    private const int MaxBackoffMs = 60_000;
    private const double ServerTimeOffsetSec = 7200.0; // Pocket Option server time is UTC+2

    private readonly record struct PoTickRow(string Asset, double Ts, double Price, long RecvMs);

    private static readonly SemaphoreSlim _sendLock = new(1, 1);
    private static DateTime _lastPsHeartbeat = DateTime.UtcNow;

    // Live price store: last tick per symbol
    private static readonly ConcurrentDictionary<string, double> _livePrices = new(StringComparer.OrdinalIgnoreCase);

    // Watermark per candle asset key (e.g. EURUSD_PO) to prevent duplicate volume while backfilling reconnect gaps
    private static readonly ConcurrentDictionary<string, double> _lastCandleSrvTs = new(StringComparer.OrdinalIgnoreCase);

    // Queue for batching tick inserts into PostgreSQL
    private static readonly BlockingCollection<PoTickRow> _tickDbQueue =
        new(new ConcurrentQueue<PoTickRow>(), 50_000);

    public static bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public static long TotalTicksIngested => Interlocked.Read(ref _totalTicksIngested);
    public static string Status { get; private set; } = "IDLE";

    private static async Task SendTextAsync(ClientWebSocket ws, string payload, CancellationToken token)
    {
        if (ws.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await _sendLock.WaitAsync(token);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public static bool TryGetLivePrice(string symbol, out double price)
    {
        return _livePrices.TryGetValue(AssetSanitizer.Sanitize(symbol), out price);
    }

    public static void StartStream(string[]? assets = null)
    {
        if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) != 0)
        {
            BotLogger.Warn("[PO WS] StartStream called while stream loop is already running. Ignored.");
            return;
        }

        if (assets != null && assets.Length > 0)
        {
            _subscribedAssets = assets;
        }

        _cts = new CancellationTokenSource();
        _ = RunConnectionLoopAsync(_cts.Token);
        _ = WatchdogLoopAsync(_cts.Token);
        _ = DbBatchFlusherAsync(_cts.Token);
        BotLogger.Info("[PO WS] Autonomous Pocket Option OTC stream started.");
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
        Status = "STOPPED";
    }

    private static async Task<(string? authPayloadJson, string? tradingUrl)> LoadSessionFromDbAsync()
    {
        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            var stateJson = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT state FROM po_collector_state WHERE id = 1;");

            if (!string.IsNullOrEmpty(stateJson))
            {
                using var doc = JsonDocument.Parse(stateJson);
                var root = doc.RootElement;
                string? tradingUrl = root.TryGetProperty("tradingUrl", out var u) ? u.GetString() : null;
                string? authPayloadJson = root.TryGetProperty("authPayload", out var a) ? a.GetRawText() : null;
                return (authPayloadJson, tradingUrl);
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Failed to read session from DB: {ex.Message}");
        }
        return (null, null);
    }

    private static async Task WatchdogLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(15000, token);
                var ws = _webSocket;
                if (ws == null || ws.State != WebSocketState.Open) continue;

                var now = DateTime.UtcNow;
                var idleSeconds = (now - _lastMessageTime).TotalSeconds;
                if (idleSeconds > 15)
                {
                    try { await SendTextAsync(ws, "2", token); } catch { }
                }

                // Send Socket.IO application-level status heartbeat 42["ps"] every ~45s
                // to prevent demo-api-eu.po.market from dropping the connection on its 3-minute timer.
                if ((now - _lastPsHeartbeat).TotalSeconds >= 45)
                {
                    try
                    {
                        await SendTextAsync(ws, "42[\"ps\"]", token);
                        _lastPsHeartbeat = now;
                    }
                    catch { }
                }

                bool isDead = idleSeconds > 40;
                if (isDead && ws.State == WebSocketState.Open)
                {
                    BotLogger.Warn("[PO WS] Watchdog detected silent drop (>40s no ticks). Dropping connection to reconnect...");
                    try { _connectionCts?.Cancel(); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task RunConnectionLoopAsync(CancellationToken token)
    {
        int backoffMs = MinBackoffMs;

        try
        {
            while (!token.IsCancellationRequested)
            {
                Status = "CONNECTING";
                var (authPayloadJson, tradingUrl) = await LoadSessionFromDbAsync();

                if (string.IsNullOrEmpty(authPayloadJson) || string.IsNullOrEmpty(tradingUrl))
                {
                    BotLogger.Warn("[PO WS] No active session found in po_collector_state. Waiting 10s...");
                    Status = "WAITING_SESSION";
                    await Task.Delay(10000, token);
                    continue;
                }

                bool wasConnected = false;
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                _connectionCts = connCts;

                var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("Origin", "https://pocketoption.com");
                ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                _webSocket = ws;

                try
                {
                    BotLogger.Info($"[PO WS] Connecting to {tradingUrl}...");
                    await ws.ConnectAsync(new Uri(tradingUrl), connCts.Token);

                    // Step 1: Wait for server handshake packet: '0{"sid":...}'
                    var (handshake, _) = await ReceiveFrameAsync(ws, connCts.Token);
                    if (handshake == null || !handshake.StartsWith("0"))
                    {
                        throw new InvalidOperationException($"Invalid server handshake: {handshake}");
                    }

                    // Step 2: Send Socket.IO connect packet: '40'
                    await SendTextAsync(ws, "40", connCts.Token);

                    // Step 3: Wait for server connect ACK: '40{"sid":...}'
                    var (ack, _) = await ReceiveFrameAsync(ws, connCts.Token);
                    if (ack == null || !ack.StartsWith("40"))
                    {
                        throw new InvalidOperationException($"Invalid connect ACK: {ack}");
                    }

                    // Step 4: Send auth packet: 42["auth", <payload>]
                    string authPacket = "42[\"auth\"," + authPayloadJson + "]";
                    await SendTextAsync(ws, authPacket, connCts.Token);
                    BotLogger.Info("[PO WS] Authentication frame sent. Awaiting subscription confirmation...");

                    wasConnected = true;
                    Status = "ONLINE";
                    _lastMessageTime = DateTime.UtcNow;
                    _lastPsHeartbeat = DateTime.UtcNow;

                    await ReceiveLoopAsync(ws, connCts.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Status = "RECONNECTING";
                    int retrySec = (wasConnected ? ReconnectAfterConnectedMs : backoffMs) / 1000;
                    BotLogger.Error($"[PO WS] Connection error: {ex.Message}. Retrying in {retrySec}s...");
                }
                finally
                {
                    try { ws.Dispose(); } catch { }
                    if (ReferenceEquals(_webSocket, ws)) _webSocket = null;
                }

                int delayMs = wasConnected ? ReconnectAfterConnectedMs : backoffMs;
                backoffMs = wasConnected ? MinBackoffMs : Math.Min(backoffMs * 2, MaxBackoffMs);
                await Task.Delay(delayMs, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref _loopRunning, 0);
            Status = "STOPPED";
        }
    }

    private static async Task<(string? text, byte[]? binary)> ReceiveFrameAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return (null, null);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        byte[] bytes = ms.ToArray();
        if (result.MessageType == WebSocketMessageType.Binary)
        {
            return (null, bytes);
        }
        return (Encoding.UTF8.GetString(bytes), null);
    }

    private static async Task SubscribeAllAssetsAsync(ClientWebSocket ws, CancellationToken connectionToken)
    {
        BotLogger.Info($"[PO WS] Authenticated! Subscribing to OTC assets: {string.Join(", ", _subscribedAssets)}");
        await Task.Delay(150, connectionToken);
        foreach (var asset in _subscribedAssets)
        {
            string sub1 = $"42[\"changeSymbol\",{{\"asset\":\"{asset}\",\"period\":60}}]";
            string sub2 = $"42[\"subfor\",\"{asset}\"]";
            await SendTextAsync(ws, sub1, connectionToken);
            await SendTextAsync(ws, sub2, connectionToken);
            await Task.Delay(50, connectionToken);
        }
        await SendTextAsync(ws, "42[\"ps\"]", connectionToken);
        _lastPsHeartbeat = DateTime.UtcNow;
        BotLogger.Info("[PO WS] Subscriptions dispatched.");
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken connectionToken)
    {
        bool subscribed = false;
        string? pendingEvent = null;

        while (ws.State == WebSocketState.Open && !connectionToken.IsCancellationRequested)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var (text, binary) = await ReceiveFrameAsync(ws, timeoutCts.Token);

            if (text == null && binary == null)
            {
                BotLogger.Warn("[PO WS] Stream closed by server.");
                break;
            }

            _lastMessageTime = DateTime.UtcNow;

            // 1. Binary attachment payload (follows a 451- packet)
            if (binary != null)
            {
                if (pendingEvent == "updateStream")
                {
                    string json = Encoding.UTF8.GetString(binary);
                    ParseUpdateStreamJson(json);
                }
                else if (pendingEvent == "updateHistoryNewFast")
                {
                    string json = Encoding.UTF8.GetString(binary);
                    ParseHistoryJson(json);
                }
                else if (pendingEvent == "successauth" && !subscribed)
                {
                    subscribed = true;
                    await SubscribeAllAssetsAsync(ws, connectionToken);
                }
                pendingEvent = null;
                continue;
            }

            // 2. Socket.IO ping/pong heartbeat
            if (text == "2")
            {
                await SendTextAsync(ws, "3", connectionToken);
                continue;
            }
            if (text == "3") continue;

            // 3. Binary placeholder header: e.g. 451-["updateStream",{"_placeholder":true,"num":0}]
            if (text != null && text.StartsWith("451-"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text.Substring(4));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        pendingEvent = doc.RootElement[0].GetString();
                    }
                }
                catch { pendingEvent = null; }
                continue;
            }

            // 4. Regular JSON event: 42["<event>", ...]
            if (text != null && text.StartsWith("42"))
            {
                if (!subscribed && text.Contains("successauth"))
                {
                    subscribed = true;
                    await SubscribeAllAssetsAsync(ws, connectionToken);
                }

                try
                {
                    using var doc = JsonDocument.Parse(text.Substring(2));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 1)
                    {
                        string ev = doc.RootElement[0].GetString() ?? "";
                        if (ev == "updateStream")
                        {
                            var dataElem = doc.RootElement[1];
                            string dataRaw = dataElem.ValueKind == JsonValueKind.String ? dataElem.GetString()! : dataElem.GetRawText();
                            ParseUpdateStreamJson(dataRaw);
                        }
                    }
                }
                catch { }
            }
        }
    }

    private static void ParseUpdateStreamJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 3)
                {
                    string asset = item[0].GetString() ?? "";
                    double srvTs = item[1].GetDouble();
                    double price = item[2].GetDouble();
                    if (!string.IsNullOrEmpty(asset) && price > 0)
                    {
                        HandleTick(asset, srvTs, price);
                    }
                }
            }
        }
        catch { }
    }

    private static void ParseHistoryJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("asset", out var aProp) && root.TryGetProperty("history", out var hProp))
            {
                string asset = aProp.GetString() ?? "";
                if (hProp.ValueKind == JsonValueKind.Array)
                {
                    int count = 0;
                    int fedCount = 0;
                    foreach (var pt in hProp.EnumerateArray())
                    {
                        if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                        {
                            double srvTs = pt[0].GetDouble();
                            double price = pt[1].GetDouble();
                            if (HandleTick(asset, srvTs, price, isBackfill: true))
                            {
                                fedCount++;
                            }
                            count++;
                        }
                    }
                    BotLogger.Info($"[PO WS] History for {asset}: {count} ticks ({fedCount} new ticks fed to candles).");
                }
            }
        }
        catch { }
    }

    private static bool TryAdvanceCandleWatermark(string candleKey, double srvTs)
    {
        while (true)
        {
            if (_lastCandleSrvTs.TryGetValue(candleKey, out double prevTs))
            {
                if (srvTs <= prevTs) return false;
                if (_lastCandleSrvTs.TryUpdate(candleKey, srvTs, prevTs)) return true;
            }
            else
            {
                if (_lastCandleSrvTs.TryAdd(candleKey, srvTs)) return true;
            }
        }
    }

    private static bool HandleTick(string asset, double srvTs, double price, bool isBackfill = false)
    {
        double utcSec = srvTs - ServerTimeOffsetSec;
        long tickTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)(utcSec * 1000)).UtcTicks;

        string baseAsset = PriceFeed.BaseAsset(asset);          // EURUSD_otc -> EURUSD
        string candleKey = baseAsset + PriceFeed.PoSuffix;      // -> EURUSD_PO (never mixed with Tiingo "EURUSD")

        if (!isBackfill)
        {
            _livePrices[baseAsset] = price;
            _lastMessageTime = DateTime.UtcNow;
            ValutaMetrics.WebSocketTicksTotal.WithLabels(candleKey).Inc();
            // Macro context gets PO prices only when PO is the active feed (Tiingo then stays out of it).
            if (PriceFeed.UsePocketOption)
                ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.UpdatePrice(
                    baseAsset, price, new DateTime(tickTimeUtc, DateTimeKind.Utc));
        }
        Interlocked.Increment(ref _totalTicksIngested);

        bool fedToCandles = TryAdvanceCandleWatermark(candleKey, srvTs);
        if (fedToCandles)
        {
            _ = RealtimeTickCollector.OnPriceUpdateAsync(candleKey, price, tickTimeUtc, isBackfill);
        }

        // Queue tick for DB persistence in po_ticks
        long recvMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tickDbQueue.TryAdd(new PoTickRow(asset, utcSec, price, recvMs));
        return fedToCandles;
    }

    private static async Task DbBatchFlusherAsync(CancellationToken token)
    {
        var batch = new List<PoTickRow>(2000);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);

                while (batch.Count < 2000 && _tickDbQueue.TryTake(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    using var conn = DbConnectionFactory.GetConnection();
                    await conn.OpenAsync(token);

                    await conn.ExecuteAsync(@"
                        INSERT INTO po_ticks (asset, ts, price, recv_ms)
                        VALUES (@Asset, @Ts, @Price, @RecvMs)
                        ON CONFLICT (asset, ts) DO NOTHING;
                    ", batch);

                    await conn.ExecuteAsync(@"
                        INSERT INTO po_collector_state (id, last_tick_at, status)
                        VALUES (1, now(), 'ONLINE')
                        ON CONFLICT (id) DO UPDATE SET last_tick_at = EXCLUDED.last_tick_at, status = EXCLUDED.status;
                    ");

                    batch.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                BotLogger.Warn($"[PO WS] DB flush error: {ex.Message}");
                if (batch.Count >= 10_000)
                {
                    batch.Clear();
                }
            }
        }
    }
}
