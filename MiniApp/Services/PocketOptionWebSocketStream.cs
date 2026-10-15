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

    private const int MinBackoffMs = 5_000;
    private const int MaxBackoffMs = 60_000;
    private const double ServerTimeOffsetSec = 7200.0; // Pocket Option server time is UTC+2

    // Live price store: last tick per symbol
    private static readonly ConcurrentDictionary<string, double> _livePrices = new(StringComparer.OrdinalIgnoreCase);

    // Queue for batching tick inserts into PostgreSQL
    private static readonly BlockingCollection<(string asset, double ts, double price, long recvMs)> _tickDbQueue =
        new(new ConcurrentQueue<(string asset, double ts, double price, long recvMs)>(), 50_000);

    public static bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public static long TotalTicksIngested => Interlocked.Read(ref _totalTicksIngested);
    public static string Status { get; private set; } = "IDLE";

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
                bool isDead = (DateTime.UtcNow - _lastMessageTime).TotalSeconds > 40;
                if (isDead && _webSocket?.State == WebSocketState.Open)
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
                    await ws.SendAsync(Encoding.UTF8.GetBytes("40"), WebSocketMessageType.Text, true, connCts.Token);

                    // Step 3: Wait for server connect ACK: '40{"sid":...}'
                    var (ack, _) = await ReceiveFrameAsync(ws, connCts.Token);
                    if (ack == null || !ack.StartsWith("40"))
                    {
                        throw new InvalidOperationException($"Invalid connect ACK: {ack}");
                    }

                    // Step 4: Send auth packet: 42["auth", <payload>]
                    string authPacket = "42[\"auth\"," + authPayloadJson + "]";
                    await ws.SendAsync(Encoding.UTF8.GetBytes(authPacket), WebSocketMessageType.Text, true, connCts.Token);
                    BotLogger.Info("[PO WS] Authentication frame sent. Awaiting subscription confirmation...");

                    wasConnected = true;
                    Status = "ONLINE";
                    _lastMessageTime = DateTime.UtcNow;

                    await ReceiveLoopAsync(ws, connCts.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Status = "RECONNECTING";
                    BotLogger.Error($"[PO WS] Connection error: {ex.Message}. Retrying in {backoffMs / 1000}s...");
                }
                finally
                {
                    try { ws.Dispose(); } catch { }
                    if (ReferenceEquals(_webSocket, ws)) _webSocket = null;
                }

                backoffMs = wasConnected ? MinBackoffMs : Math.Min(backoffMs * 2, MaxBackoffMs);
                await Task.Delay(backoffMs, token);
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
                pendingEvent = null;
                continue;
            }

            // 2. Socket.IO ping/pong heartbeat
            if (text == "2")
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes("3"), WebSocketMessageType.Text, true, connectionToken);
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

            // 5. Auth success trigger: subscribe to all OTC assets
            if (text != null && (text.Contains("successauth") || text.Contains("updateAssets")) && !subscribed)
            {
                subscribed = true;
                BotLogger.Info($"[PO WS] Authenticated! Subscribing to OTC assets: {string.Join(", ", _subscribedAssets)}");
                foreach (var asset in _subscribedAssets)
                {
                    string sub1 = $"42[\"changeSymbol\",{{\"asset\":\"{asset}\",\"period\":60}}]";
                    string sub2 = $"42[\"subfor\",\"{asset}\"]";
                    await ws.SendAsync(Encoding.UTF8.GetBytes(sub1), WebSocketMessageType.Text, true, connectionToken);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(sub2), WebSocketMessageType.Text, true, connectionToken);
                }
                BotLogger.Info("[PO WS] Subscriptions dispatched.");
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
                    // History is replayed into candles only once per asset per process (cold start).
                    // Later history frames (sent on every re-subscribe) are stored in po_ticks only.
                    bool feedCandles = _backfilledAssets.TryAdd(asset, 0);
                    int count = 0;
                    foreach (var pt in hProp.EnumerateArray())
                    {
                        if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                        {
                            double srvTs = pt[0].GetDouble();
                            double price = pt[1].GetDouble();
                            HandleTick(asset, srvTs, price, isBackfill: true, feedCandles: feedCandles);
                            count++;
                        }
                    }
                    BotLogger.Info($"[PO WS] History for {asset}: {count} ticks (candle backfill: {feedCandles}).");
                }
            }
        }
        catch { }
    }

    private static readonly ConcurrentDictionary<string, byte> _backfilledAssets = new(StringComparer.OrdinalIgnoreCase);

    private static void HandleTick(string asset, double srvTs, double price, bool isBackfill = false, bool feedCandles = true)
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

        if (feedCandles)
            _ = RealtimeTickCollector.OnPriceUpdateAsync(candleKey, price, tickTimeUtc, isBackfill);

        // Queue tick for DB persistence in po_ticks
        long recvMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _tickDbQueue.TryAdd((asset, utcSec, price, recvMs));
    }

    private static async Task DbBatchFlusherAsync(CancellationToken token)
    {
        var batch = new List<(string asset, double ts, double price, long recvMs)>(1000);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);
                batch.Clear();

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
                        VALUES (@asset, @ts, @price, @recvMs)
                        ON CONFLICT (asset, ts) DO NOTHING;
                    ", batch);

                    await conn.ExecuteAsync(@"
                        INSERT INTO po_collector_state (id, last_tick_at, status)
                        VALUES (1, now(), 'ONLINE')
                        ON CONFLICT (id) DO UPDATE SET last_tick_at = EXCLUDED.last_tick_at, status = EXCLUDED.status;
                    ");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                BotLogger.Warn($"[PO WS] DB flush error: {ex.Message}");
            }
        }
    }
}
