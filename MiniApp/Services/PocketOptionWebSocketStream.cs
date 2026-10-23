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
    private static DateTime _lastTickTime = DateTime.UtcNow;
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

    private const string DefaultTradingUrl = "wss://demo-api-eu.po.market/socket.io/?EIO=4&transport=websocket";
    private const string TryDemoTradingUrl = "wss://try-demo-eu.po.market/socket.io/?EIO=4&transport=websocket";
    private const string AlphanumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly SemaphoreSlim _sendLock = new(1, 1);
    private static DateTime _lastPsHeartbeat = DateTime.UtcNow;
    private static string? _cachedAuthPayloadJson;
    private static string? _cachedTradingUrl;
    private static int _consecutiveAuthFailures = 0;
    private static DateTime _lastAuthAlertUtc = DateTime.MinValue;

    public static (string authPayloadJson, string tradingUrl) GenerateTryDemoSession()
    {
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = AlphanumericChars[Random.Shared.Next(AlphanumericChars.Length)];
        }
        string token = new string(chars);
        string payload = JsonSerializer.Serialize(new
        {
            token,
            balance = 50000,
            isFastHistory = true
        });
        return (payload, TryDemoTradingUrl);
    }

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
    public static DateTime LastTickUtc => _lastTickTime;
    public static int ConsecutiveAuthFailures => Volatile.Read(ref _consecutiveAuthFailures);

    /// <summary>
    /// Actively forces a WebSocket reconnection (called by Watchdog or SelfDiagnosticService when feed stalls).
    /// </summary>
    public static void ForceReconnect(string reason)
    {
        BotLogger.Warn($"[PO WS] ForceReconnect triggered: {reason}");
        _lastTickTime = DateTime.UtcNow;
        try { _connectionCts?.Cancel(); } catch { }
    }

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

                if (!string.IsNullOrEmpty(authPayloadJson) && !string.IsNullOrEmpty(tradingUrl))
                {
                    _cachedAuthPayloadJson = authPayloadJson;
                    _cachedTradingUrl = tradingUrl;
                    return (authPayloadJson, tradingUrl);
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Failed to read session from DB: {ex.Message}");
        }

        // Fallback 1: Last known valid session cached in memory
        if (!string.IsNullOrEmpty(_cachedAuthPayloadJson) && !string.IsNullOrEmpty(_cachedTradingUrl))
        {
            BotLogger.Info("[PO WS] Using cached in-memory session fallback.");
            return (_cachedAuthPayloadJson, _cachedTradingUrl);
        }

        // Fallback 2: Environment variables (PO_AUTH_PAYLOAD or PO_SESSION_TOKEN)
        string? envPayload = Environment.GetEnvironmentVariable("PO_AUTH_PAYLOAD");
        string envUrl = Environment.GetEnvironmentVariable("PO_TRADING_URL") ?? DefaultTradingUrl;
        if (!string.IsNullOrWhiteSpace(envPayload))
        {
            return (envPayload.Trim(), envUrl);
        }

        string? envToken = Environment.GetEnvironmentVariable("PO_SESSION_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            long uid = long.TryParse(Environment.GetEnvironmentVariable("PO_UID"), out var parsedUid) ? parsedUid : 129671966;
            string builtPayload = JsonSerializer.Serialize(new
            {
                session = envToken.Trim(),
                isDemo = 1,
                uid,
                platform = 2,
                isFastHistory = true,
                isOptimized = true
            });
            return (builtPayload, envUrl);
        }

        // Fallback 3: Zero-login autonomous Try-Demo session (client-generated 10-char token)
        var autoSession = GenerateTryDemoSession();
        _cachedAuthPayloadJson = autoSession.authPayloadJson;
        _cachedTradingUrl = autoSession.tradingUrl;
        BotLogger.Info("[PO WS] Generated autonomous zero-login Try-Demo session.");
        return autoSession;
    }

    public static async Task<(bool ok, string message)> UpdateSessionAsync(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return (false, "Пустой токен.");

        string trimmed = rawInput.Trim();
        string tradingUrl = _cachedTradingUrl ?? DefaultTradingUrl;
        string? newAuthPayloadJson = null;

        try
        {
            // Case 0: "auto" -> generate autonomous zero-login Try-Demo token
            if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
            {
                (newAuthPayloadJson, tradingUrl) = GenerateTryDemoSession();
            }
            // Case 1: Socket.IO frame 42["auth",{...}]
            else if (trimmed.StartsWith("42["))
            {
                using var doc = JsonDocument.Parse(trimmed.Substring(2));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() >= 2)
                {
                    newAuthPayloadJson = doc.RootElement[1].GetRawText();
                    if (doc.RootElement[1].TryGetProperty("token", out _))
                        tradingUrl = TryDemoTradingUrl;
                }
            }
            // Case 2: JSON object
            else if (trimmed.StartsWith("{"))
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.TryGetProperty("authPayload", out var ap))
                {
                    newAuthPayloadJson = ap.GetRawText();
                    if (root.TryGetProperty("tradingUrl", out var tu) && !string.IsNullOrWhiteSpace(tu.GetString()))
                        tradingUrl = tu.GetString()!;
                }
                else if (root.TryGetProperty("session", out _))
                {
                    newAuthPayloadJson = root.GetRawText();
                }
                else if (root.TryGetProperty("token", out _))
                {
                    newAuthPayloadJson = root.GetRawText();
                    tradingUrl = TryDemoTradingUrl;
                }
            }
            // Case 3: Raw session string (optionally followed by uid)
            else
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string tokenStr = parts[0].Trim('"', '\'');
                if (tokenStr.Length < 10 || tokenStr.Length > 128)
                    return (false, "Неверный формат токена (ожидается 'auto', строка session или JSON 42[\"auth\",...]).");

                if (tokenStr.Length == 10)
                {
                    newAuthPayloadJson = JsonSerializer.Serialize(new
                    {
                        token = tokenStr,
                        balance = 50000,
                        isFastHistory = true
                    });
                    tradingUrl = TryDemoTradingUrl;
                }
                else
                {
                    long uid = 129671966;
                    if (parts.Length >= 2 && long.TryParse(parts[1], out long explicitUid))
                    {
                        uid = explicitUid;
                    }
                    else if (!string.IsNullOrEmpty(_cachedAuthPayloadJson))
                    {
                        try
                        {
                            using var oldDoc = JsonDocument.Parse(_cachedAuthPayloadJson);
                            if (oldDoc.RootElement.TryGetProperty("uid", out var uProp) && uProp.TryGetInt64(out long oldUid) && oldUid > 0)
                                uid = oldUid;
                        }
                        catch { }
                    }

                    newAuthPayloadJson = JsonSerializer.Serialize(new
                    {
                        session = tokenStr,
                        isDemo = 1,
                        uid,
                        platform = 2,
                        isFastHistory = true,
                        isOptimized = true
                    });
                    tradingUrl = DefaultTradingUrl;
                }
            }

            if (string.IsNullOrEmpty(newAuthPayloadJson))
                return (false, "Не удалось извлечь authPayload из переданной строки.");

            using var verifyDoc = JsonDocument.Parse(newAuthPayloadJson);
            bool hasSession = verifyDoc.RootElement.TryGetProperty("session", out var sessProp) && !string.IsNullOrWhiteSpace(sessProp.GetString());
            bool hasToken = verifyDoc.RootElement.TryGetProperty("token", out var tokProp) && !string.IsNullOrWhiteSpace(tokProp.GetString());
            if (!hasSession && !hasToken)
                return (false, "В authPayload отсутствует поле 'session' или 'token'.");

            string fullStateJson = $"{{\"authPayload\":{newAuthPayloadJson},\"tradingUrl\":{JsonSerializer.Serialize(tradingUrl)}}}";

            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO po_collector_state (id, state, updated_at, status)
                VALUES (1, @State, now(), 'RECONNECTING')
                ON CONFLICT (id) DO UPDATE SET
                    state = EXCLUDED.state,
                    updated_at = now(),
                    status = 'RECONNECTING';
            ", new { State = fullStateJson });

            _cachedAuthPayloadJson = newAuthPayloadJson;
            _cachedTradingUrl = tradingUrl;
            Interlocked.Exchange(ref _consecutiveAuthFailures, 0);

            try { _connectionCts?.Cancel(); } catch { }
            return (true, $"Токен сохранён в po_collector_state (session={sessProp.GetString()![..Math.Min(6, sessProp.GetString()!.Length)]}***). Переподключение потока запущено.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка разбора или сохранения токена: {ex.Message}");
        }
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
                var frameIdleSeconds = (now - _lastMessageTime).TotalSeconds;
                var tickIdleSeconds = (now - _lastTickTime).TotalSeconds;
                if (frameIdleSeconds > 15)
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

                // Reconnect if raw socket is silent >40s OR if no market ticks arrived for >60s despite open socket (zombie subscription)
                bool isDead = frameIdleSeconds > 40 || tickIdleSeconds > 60;
                if (isDead && ws.State == WebSocketState.Open)
                {
                    BotLogger.Warn($"[PO WS] Watchdog detected silent drop (frameIdle={frameIdleSeconds:F0}s, tickIdle={tickIdleSeconds:F0}s). Dropping connection to reconnect...");
                    _lastTickTime = now;
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
                    _lastTickTime = DateTime.UtcNow;
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
        Interlocked.Exchange(ref _consecutiveAuthFailures, 0);
        Status = "ONLINE";
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

    private static async Task HandleAuthRejectedAsync()
    {
        int fails = Interlocked.Increment(ref _consecutiveAuthFailures);
        var (autoPayload, autoUrl) = GenerateTryDemoSession();
        _cachedAuthPayloadJson = autoPayload;
        _cachedTradingUrl = autoUrl;
        BotLogger.Warn($"[PO WS] Server returned 41 (Auth Rejected, fail #{fails}). Auto-healing with fresh Try-Demo session ({autoUrl})...");

        try
        {
            string fullStateJson = $"{{\"authPayload\":{autoPayload},\"tradingUrl\":{JsonSerializer.Serialize(autoUrl)}}}";
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO po_collector_state (id, state, updated_at, status)
                VALUES (1, @State, now(), 'AUTO_HEALED')
                ON CONFLICT (id) DO UPDATE SET state = EXCLUDED.state, updated_at = now(), status = 'AUTO_HEALED';
            ", new { State = fullStateJson });
        }
        catch { }

        if (fails >= 3)
        {
            Status = "AUTH_EXPIRED";
            if ((DateTime.UtcNow - _lastAuthAlertUtc).TotalMinutes >= 30)
            {
                _lastAuthAlertUtc = DateTime.UtcNow;
                string alertText =
                    "🚨 <b>[Pocket Option WS] Сессия авторизации отклонена сервером (код 41)!</b>\n" +
                    "Автоматическая ротация Try-Demo токена не прошла 3 раза подряд. Проверьте <code>/po_status</code> или выполните <code>/po_auth auto</code>.";
                BotLogger.NotifyAdmin("[PO WS] Auth token rejected 3x (code 41).");
                try
                {
                    string? botToken = TelegramNotifier.GetToken();
                    if (!string.IsNullOrEmpty(botToken))
                    {
                        var admins = await ValutaBot.App.MiniApp.Data.Repositories.UserRepository.GetAdminChatIdsAsync();
                        foreach (long adminId in admins)
                        {
                            await TelegramBotService.SendMessage(botToken, adminId, alertText);
                        }
                    }
                }
                catch { }
            }
        }
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

            // 2. Socket.IO ping/pong heartbeat & 41 namespace disconnect (Auth Rejected)
            if (text == "2")
            {
                await SendTextAsync(ws, "3", connectionToken);
                continue;
            }
            if (text == "3") continue;
            if (text == "41")
            {
                if (!subscribed)
                {
                    await HandleAuthRejectedAsync();
                }
                break;
            }

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
            _lastTickTime = DateTime.UtcNow;
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

        // Raw ticks are aggregated directly in-memory into subminute_candles (RealtimeTickCollector).
        // Avoid persisting millions of raw ticks into PostgreSQL to bound disk volume.
        return fedToCandles;
    }

    private static async Task DbBatchFlusherAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(15000, token);

                using var conn = DbConnectionFactory.GetConnection();
                await conn.OpenAsync(token);

                await conn.ExecuteAsync(@"
                    INSERT INTO po_collector_state (id, last_tick_at, status)
                    VALUES (1, now(), 'ONLINE')
                    ON CONFLICT (id) DO UPDATE SET last_tick_at = EXCLUDED.last_tick_at, status = EXCLUDED.status;
                ");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                BotLogger.Warn($"[PO WS] Collector state heartbeat notice: {ex.Message}");
            }
        }
    }
}
