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
    private static long _lastMessageSw = System.Diagnostics.Stopwatch.GetTimestamp();
    private static long _lastTickSw = System.Diagnostics.Stopwatch.GetTimestamp();
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
    private static double _serverTimeOffsetSec = 7200.0;
    public static double ServerTimeOffsetSec
    {
        get => Volatile.Read(ref _serverTimeOffsetSec);
        set => Volatile.Write(ref _serverTimeOffsetSec, value);
    }

    private const string DefaultTradingUrl = "wss://demo-api-eu.po.market/socket.io/?EIO=4&transport=websocket";
    private const string TryDemoTradingUrl = "wss://try-demo-eu.po.market/socket.io/?EIO=4&transport=websocket";
    private const string AlphanumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private static readonly SemaphoreSlim _sendLock = new(1, 1);
    private static long _lastPsSw = System.Diagnostics.Stopwatch.GetTimestamp();
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

    // Live price store: last tick per symbol (Price, BrokerUtcTicks, LocalSwTimestamp)
    private static readonly ConcurrentDictionary<string, (double Price, long BrokerUtcTicks, long LocalSwTimestamp)> _livePrices = new(StringComparer.OrdinalIgnoreCase);

    // Monotonic tick arrival per symbol for Dead Man's Switch detection
    private static readonly ConcurrentDictionary<string, long> _lastTickTicksBySymbol = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-symbol Dead Man's Switch: returns true if ticks for the specified asset arrived within maxIdleSeconds.
    /// </summary>
    public static bool IsSymbolFeedAlive(string symbol, double maxIdleSeconds = 2.0)
    {
        string clean = PriceFeed.BaseAsset(symbol);
        if (!_lastTickTicksBySymbol.TryGetValue(clean, out long lastVal)) return false;
        double elapsedSec = lastVal > 500_000_000_000_000_000L
            ? (DateTime.UtcNow.Ticks - lastVal) / (double)TimeSpan.TicksPerSecond
            : System.Diagnostics.Stopwatch.GetElapsedTime(lastVal).TotalSeconds;
        return elapsedSec <= maxIdleSeconds;
    }

    // Watermark per candle asset key (e.g. EURUSD_PO) to prevent duplicate volume while backfilling reconnect gaps
    private static readonly ConcurrentDictionary<string, double> _lastCandleSrvTs = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public static long TotalTicksIngested => Interlocked.Read(ref _totalTicksIngested);
    public static string Status { get; private set; } = "IDLE";
    private sealed record BrokerClockAnchor(long BrokerUtcTicks, long LocalStopwatchTimestamp);
    private static volatile BrokerClockAnchor? _clockAnchor;
    private const double AnchorToleranceSec = 5.0;
    private const double AnchorConfirmSec = 2.0;
    private const double AnchorSoloConfirmSec = 5.0;
    private const double AnchorAgreeSec = 1.0;
    private const int AnchorQuorum = 2;
    private const long FutureTickGuardTicks = 10L * 60 * TimeSpan.TicksPerSecond;

    private sealed class AnchorSuspect
    {
        public long Sw0;
        public long Off0;
        public long LastSw;
        public long LastOff;
        public bool Confirmed;
    }

    private static readonly object _anchorLock = new();
    private static readonly Dictionary<string, AnchorSuspect> _anchorSuspects = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long _swEpoch = System.Diagnostics.Stopwatch.GetTimestamp();
    private static long _lastInToleranceSw;
    private static int _clockEpoch;

    private static long BrokerMinusLocalTicks(long brokerUtcTicks, long sw)
        => brokerUtcTicks - System.Diagnostics.Stopwatch.GetElapsedTime(_swEpoch, sw).Ticks;

    public static DateTime LastTickUtc => DateTime.UtcNow - System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTickSw));
    public static DateTime LastBrokerTickUtc =>
        _clockAnchor is { } a ? new DateTime(a.BrokerUtcTicks, DateTimeKind.Utc) : DateTime.MinValue;
    public static int ConsecutiveAuthFailures => Volatile.Read(ref _consecutiveAuthFailures);

    private static void ResetClockAndWatermarksCore(BrokerClockAnchor? preservedAnchor)
    {
        lock (_anchorLock)
        {
            _clockAnchor = preservedAnchor;
            _anchorSuspects.Clear();
            _lastInToleranceSw = preservedAnchor?.LocalStopwatchTimestamp ?? 0;
        }
        Interlocked.Increment(ref _clockEpoch);
        _lastCandleSrvTs.Clear();
        _livePrices.Clear();
        _lastTickTicksBySymbol.Clear();
    }

    public static void ResetClockAndWatermarksOnDstShift()
    {
        lock (_anchorLock)
        {
            _clockAnchor = null;
            _anchorSuspects.Clear();
            _lastInToleranceSw = 0;
        }
        Interlocked.Increment(ref _clockEpoch);
        _lastCandleSrvTs.Clear();
        _livePrices.Clear();
        _lastTickTicksBySymbol.Clear();
    }

    /// <summary>
    /// Returns the current extrapolated Pocket Option broker time in UTC.
    /// Uses monotonic hardware Stopwatch delta since the last broker tick to maintain sub-millisecond continuity.
    /// Falls back to DateTime.UtcNow if no broker ticks have been received yet.
    /// </summary>
    public static DateTime GetBrokerTimeUtc()
    {
        var anchor = _clockAnchor;
        if (anchor != null)
        {
            double elapsedSec = System.Diagnostics.Stopwatch.GetElapsedTime(anchor.LocalStopwatchTimestamp).TotalSeconds;
            if (elapsedSec >= 0)
            {
                // Monotonic continuous hardware stopwatch extrapolation (never freezes in the past)
                return new DateTime(anchor.BrokerUtcTicks + (long)(elapsedSec * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            }
        }
        return DateTime.UtcNow;
    }

    /// <summary>
    /// Actively forces a WebSocket reconnection (called by Watchdog or SelfDiagnosticService when feed stalls).
    /// </summary>
    private static void CancelConnection()
    {
        var cts = Volatile.Read(ref _connectionCts);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public static void ForceReconnect(string reason)
    {
        BotLogger.Warn($"[PO WS] ForceReconnect triggered: {reason}");
        Volatile.Write(ref _lastTickSw, System.Diagnostics.Stopwatch.GetTimestamp());
        CancelConnection();
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string payload, CancellationToken token)
    {
        if (ws.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        try
        {
            await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            BotLogger.Warn("[PO WS] SendTextAsync timed out (2s). Aborting socket to unblock send pipeline.");
            try { ws.Abort(); } catch { }
        }
        catch (ObjectDisposedException) { }
        catch (WebSocketException) { }
        finally
        {
            try { _sendLock.Release(); } catch { }
        }
    }

    public static bool TryGetLivePrice(string symbol, out double price, double maxAgeSeconds = 2.0)
    {
        if (_clockAnchor != null &&
            _livePrices.TryGetValue(AssetSanitizer.Sanitize(symbol), out var entry) && entry.Price > 0)
        {
            double arrivalAge = System.Diagnostics.Stopwatch.GetElapsedTime(entry.LocalSwTimestamp).TotalSeconds;
            double brokerAge = (GetBrokerTimeUtc().Ticks - entry.BrokerUtcTicks) / (double)TimeSpan.TicksPerSecond;
            if (arrivalAge <= maxAgeSeconds && brokerAge <= maxAgeSeconds && brokerAge >= -AnchorToleranceSec)
            {
                price = entry.Price;
                return true;
            }
        }
        price = 0;
        return false;
    }

    public static void StartStream(string[]? assets = null)
    {
        if (assets != null && assets.Length > 0)
        {
            _subscribedAssets = assets;
        }

        if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) != 0)
        {
            if (Volatile.Read(ref _cts).IsCancellationRequested)
            {
                BotLogger.Warn("[PO WS] StartStream called while stream loop is unwinding after stop. Deferring start...");
                _ = DeferredStartAsync();
                return;
            }
            BotLogger.Warn("[PO WS] StartStream called while stream loop is already running. Ignored.");
            return;
        }

        LaunchStream();
    }

    private static async Task DeferredStartAsync()
    {
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) == 0)
            {
                LaunchStream();
                return;
            }
        }
        BotLogger.Error("[PO WS] Deferred StartStream gave up: previous loop did not exit within 5s.");
    }

    private static void LaunchStream()
    {
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cts, cts);
        try { oldCts.Cancel(); oldCts.Dispose(); } catch { }

        _ = RunConnectionLoopAsync(cts.Token);
        _ = WatchdogLoopAsync(cts.Token);
        _ = DbBatchFlusherAsync(cts.Token);
        BotLogger.Info("[PO WS] Autonomous Pocket Option OTC stream started.");
    }

    public static void StopStream()
    {
        try { Volatile.Read(ref _cts).Cancel(); } catch (ObjectDisposedException) { }
        CancelConnection();
        Status = "STOPPED";
    }

    private static async Task<(string? authPayloadJson, string? tradingUrl)> LoadSessionFromDbAsync(CancellationToken token = default)
    {
        try
        {
            using var conn = DbConnectionFactory.GetConnection();
            await conn.OpenAsync(token);
            var stateJson = await conn.QueryFirstOrDefaultAsync<string>(
                new CommandDefinition("SELECT state FROM po_collector_state WHERE id = 1;", cancellationToken: token));

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

    public static async Task<(bool ok, string message)> UpdateSessionAsync(string rawInput, CancellationToken token = default)
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
            await conn.OpenAsync(token);
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
            string sessStr = sessProp.GetString() ?? "";
            string sessPrefix = sessStr.Length > 6 ? sessStr[..6] : sessStr;
            return (true, $"Токен сохранён в po_collector_state (session={sessPrefix}***). Переподключение потока запущено.");
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
                await Task.Delay(1000, token);
                var ws = _webSocket;
                if (ws == null || ws.State != WebSocketState.Open) continue;

                double frameIdleSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastMessageSw)).TotalSeconds;
                double tickIdleSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTickSw)).TotalSeconds;
                var connToken = _connectionCts?.Token ?? token;
                if (frameIdleSeconds > 3)
                {
                    try { await SendTextAsync(ws, "2", connToken); } catch { }
                }

                // Send Socket.IO application-level status heartbeat 42["ps"] every ~30s
                // to prevent demo-api-eu.po.market from dropping the connection on its 3-minute timer.
                if (System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastPsSw)).TotalSeconds >= 30)
                {
                    try
                    {
                        await SendTextAsync(ws, "42[\"ps\"]", connToken);
                        Volatile.Write(ref _lastPsSw, System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                    catch { }
                }

                // Fast-fail: Reconnect if raw socket is silent >6s OR if no market ticks arrived for >8s despite open socket (zombie subscription)
                bool isDead = frameIdleSeconds > 6 || tickIdleSeconds > 8;
                if (isDead && ws.State == WebSocketState.Open)
                {
                    BotLogger.Warn($"[PO WS] Watchdog detected silent drop or feed stall (frameIdle={frameIdleSeconds:F1}s, tickIdle={tickIdleSeconds:F1}s). Dropping connection to reconnect immediately...");
                    Volatile.Write(ref _lastTickSw, System.Diagnostics.Stopwatch.GetTimestamp());
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
                var (authPayloadJson, tradingUrl) = await LoadSessionFromDbAsync(token);

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
                    long swConnected = System.Diagnostics.Stopwatch.GetTimestamp();
                    Volatile.Write(ref _lastMessageSw, swConnected);
                    Volatile.Write(ref _lastTickSw, swConnected);
                    Volatile.Write(ref _lastPsSw, swConnected);

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
                    if (ReferenceEquals(_webSocket, ws))
                    {
                        _webSocket = null;
                        _livePrices.Clear();
                        _lastTickTicksBySymbol.Clear();
                    }
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
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            var firstResult = await ws.ReceiveAsync(new ArraySegment<byte>(rented), token);
            if (firstResult.MessageType == WebSocketMessageType.Close) return (null, null);

            if (firstResult.EndOfMessage)
            {
                if (firstResult.MessageType == WebSocketMessageType.Binary)
                {
                    byte[] binary = new byte[firstResult.Count];
                    Buffer.BlockCopy(rented, 0, binary, 0, firstResult.Count);
                    return (null, binary);
                }
                return (Encoding.UTF8.GetString(rented, 0, firstResult.Count), null);
            }

            const int maxFrameBytes = 4 * 1024 * 1024; // 4 MB safety limit
            using var ms = new MemoryStream(Math.Min(65536, firstResult.Count * 2));
            ms.Write(rented, 0, firstResult.Count);
            WebSocketReceiveResult nextResult;
            do
            {
                if (ms.Length > maxFrameBytes)
                {
                    BotLogger.Error("[PO WS] Frame exceeded 4MB limit, aborting frame receive.");
                    return (null, null);
                }
                nextResult = await ws.ReceiveAsync(new ArraySegment<byte>(rented), token);
                if (nextResult.MessageType == WebSocketMessageType.Close) return (null, null);
                ms.Write(rented, 0, nextResult.Count);
            } while (!nextResult.EndOfMessage);

            byte[] bytes = ms.ToArray();
            if (nextResult.MessageType == WebSocketMessageType.Binary)
            {
                return (null, bytes);
            }
            return (Encoding.UTF8.GetString(bytes), null);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task SubscribeAllAssetsAsync(ClientWebSocket ws, CancellationToken connectionToken)
    {
        Interlocked.Exchange(ref _consecutiveAuthFailures, 0);
        Status = "ONLINE";
        string assetList = string.Join(", ", _subscribedAssets);
        BotLogger.Info($"[PO WS] Authenticated! Subscribing to OTC assets: {assetList}");
        foreach (var asset in _subscribedAssets)
        {
            string sub1 = "42[\"changeSymbol\",{\"asset\":\"" + asset + "\",\"period\":60}]";
            string sub2 = "42[\"subfor\",\"" + asset + "\"]";
            await SendTextAsync(ws, sub1, connectionToken);
            await SendTextAsync(ws, sub2, connectionToken);
        }
        await SendTextAsync(ws, "42[\"ps\"]", connectionToken);
        Volatile.Write(ref _lastPsSw, System.Diagnostics.Stopwatch.GetTimestamp());
        BotLogger.Info("[PO WS] Subscriptions dispatched.");
    }

    private static async Task HandleAuthRejectedAsync(CancellationToken token = default)
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
            await conn.OpenAsync(token);
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

        using var frameTimeoutCts = new CancellationTokenSource();
        using var reg = connectionToken.Register(() =>
        {
            try { frameTimeoutCts.Cancel(); } catch { }
        });

        while (ws.State == WebSocketState.Open && !connectionToken.IsCancellationRequested)
        {
            frameTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var (text, binary) = await ReceiveFrameAsync(ws, frameTimeoutCts.Token);
            if (!frameTimeoutCts.TryReset())
            {
                break;
            }

            if (text == null && binary == null)
            {
                BotLogger.Warn("[PO WS] Stream closed by server.");
                break;
            }

            Volatile.Write(ref _lastMessageSw, System.Diagnostics.Stopwatch.GetTimestamp());

            // 1. Binary attachment payload (follows a 451- packet)
            if (binary != null)
            {
                if (pendingEvent == "updateStream")
                {
                    ParseUpdateStreamUtf8(binary);
                }
                else if (pendingEvent == "updateHistoryNewFast")
                {
                    string json = Encoding.UTF8.GetString(binary);
                    ParseHistoryJson(json);
                }
                else if (pendingEvent == "successauth" && !subscribed)
                {
                    subscribed = true;
                    _ = SubscribeAllAssetsSafeAsync(ws, connectionToken);
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
                    await HandleAuthRejectedAsync(connectionToken);
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
                    _ = SubscribeAllAssetsSafeAsync(ws, connectionToken);
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

    private static void ParseUpdateStreamUtf8(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(utf8Json.Span);
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartArray) return;
            while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
            {
                if (reader.TokenType == System.Text.Json.JsonTokenType.StartArray)
                {
                    if (!reader.Read()) break;
                    string rawAsset = reader.GetString() ?? "";
                    if (!reader.Read()) break;
                    double srvTs = reader.GetDouble();
                    if (!reader.Read()) break;
                    double price = reader.GetDouble();
                    while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray) { }

                    if (!string.IsNullOrEmpty(rawAsset) && price > 0)
                    {
                        HandleTick(rawAsset, srvTs, price);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing updateStream UTF8 frame: {ex.Message}");
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
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing updateStream JSON frame: {ex.Message}");
        }
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

    private static readonly ConcurrentDictionary<string, double> _lastBackfillSrvTs = new(StringComparer.OrdinalIgnoreCase);
    private static bool TryAdvanceCandleWatermark(string candleKey, double srvTs, bool isBackfill = false)
    {
        if (isBackfill)
        {
            // Бэкфилл защищен отдельной монотонной ватермаркой, не блокируемой текущими тиками
            while (true)
            {
                if (_lastBackfillSrvTs.TryGetValue(candleKey, out double prevBf))
                {
                    if (srvTs <= prevBf) return false;
                    if (_lastBackfillSrvTs.TryUpdate(candleKey, srvTs, prevBf)) return true;
                }
                else if (_lastBackfillSrvTs.TryAdd(candleKey, srvTs))
                {
                    return true;
                }
            }
        }
        while (true)
        {
            if (_lastCandleSrvTs.TryGetValue(candleKey, out double prevTs))
            {
                if (prevTs - srvTs > 300.0)
                {
                    if (_lastCandleSrvTs.TryUpdate(candleKey, srvTs, prevTs)) return true;
                    continue;
                }
                if (srvTs <= prevTs) return false;
                if (_lastCandleSrvTs.TryUpdate(candleKey, srvTs, prevTs)) return true;
            }
            else if (_lastCandleSrvTs.TryAdd(candleKey, srvTs))
            {
                return true;
            }
        }
    }

    private static async Task SubscribeAllAssetsSafeAsync(ClientWebSocket ws, CancellationToken connectionToken)
    {
        try
        {
            await SubscribeAllAssetsAsync(ws, connectionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Subscription dispatch failed: {ex.Message}. Aborting socket to trigger clean reconnect.");
            try { ws.Abort(); } catch { }
        }
    }

    private static bool TryUpdateAnchor(string symbol, long tickTimeUtc, long swNow, out bool rebasedBackward)
    {
        rebasedBackward = false;
        lock (_anchorLock)
        {
            var prev = _clockAnchor;
            if (prev == null)
            {
                _clockAnchor = new BrokerClockAnchor(tickTimeUtc, swNow);
                _lastInToleranceSw = swNow;
                return true;
            }

            long tickOff = BrokerMinusLocalTicks(tickTimeUtc, swNow);
            long anchorOff = BrokerMinusLocalTicks(prev.BrokerUtcTicks, prev.LocalStopwatchTimestamp);
            long lagTicks = anchorOff - tickOff; // >0: тик старше экстраполированного времени

            if (Math.Abs(lagTicks) <= (long)(AnchorToleranceSec * TimeSpan.TicksPerSecond))
            {
                _anchorSuspects.Remove(symbol);
                _lastInToleranceSw = swNow;
                if (lagTicks < 0)
                {
                    _clockAnchor = new BrokerClockAnchor(tickTimeUtc, swNow);
                }
                return true;
            }

            // |lag| > 5s: испытательный срок строго для ТЕКУЩЕГО символа
            if (!_anchorSuspects.TryGetValue(symbol, out var s))
            {
                _anchorSuspects[symbol] = new AnchorSuspect
                {
                    Sw0 = swNow,
                    Off0 = tickOff,
                    LastSw = swNow,
                    LastOff = tickOff,
                    Confirmed = false
                };
                return false;
            }

            double runSec = System.Diagnostics.Stopwatch.GetElapsedTime(s.Sw0, swNow).TotalSeconds;
            double driftSec = Math.Abs(tickOff - s.Off0) / (double)TimeSpan.TicksPerSecond;
            if (driftSec > Math.Max(0.5, 0.25 * runSec))
            {
                s.Sw0 = swNow; s.Off0 = tickOff; s.LastSw = swNow; s.LastOff = tickOff; s.Confirmed = false;
                return false;
            }

            s.LastSw = swNow;
            s.LastOff = tickOff;
            if (runSec >= AnchorConfirmSec) s.Confirmed = true;
            if (!s.Confirmed) return false;

            int agree = 0;
            long agreeTicks = (long)(AnchorAgreeSec * TimeSpan.TicksPerSecond);
            foreach (var o in _anchorSuspects.Values)
            {
                if (o.Confirmed &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(o.LastSw, swNow).TotalSeconds <= 3.0 &&
                    Math.Abs(o.LastOff - tickOff) <= agreeTicks)
                {
                    agree++;
                }
            }

            int quorum = Math.Min(AnchorQuorum, Math.Max(1, _subscribedAssets.Length));
            bool soloOk = runSec >= AnchorSoloConfirmSec &&
                          System.Diagnostics.Stopwatch.GetElapsedTime(_lastInToleranceSw, swNow).TotalSeconds >= AnchorSoloConfirmSec;
            if (agree < quorum && !soloOk) return false;

            rebasedBackward = lagTicks > 0;
            _anchorSuspects.Clear();
            _lastInToleranceSw = swNow;
            _clockAnchor = new BrokerClockAnchor(tickTimeUtc, swNow);
            return true;
        }
    }

    private static bool HandleTick(string asset, double srvTs, double price, bool isBackfill = false)
    {
        // Data Integrity Guard: reject poisoned broker ticks (NaN, Infinity, non-positive price, or out-of-range epoch timestamp)
        if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0 ||
            double.IsNaN(srvTs) || double.IsInfinity(srvTs) || srvTs <= 0 || srvTs > 3_000_000_000)
        {
            return false;
        }

        int epoch = Volatile.Read(ref _clockEpoch);
        double utcSec = srvTs - ServerTimeOffsetSec;
        long tickTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)(utcSec * 1000)).UtcTicks;

        // D4 upper bound: record skew before rejecting future-poisoned timestamps (>10 minutes ahead of host UTC) so DST shifts are still detected
        if (tickTimeUtc - DateTime.UtcNow.Ticks > FutureTickGuardTicks)
        {
            if (!isBackfill)
            {
                var nowSwForGuard = System.Diagnostics.Stopwatch.GetTimestamp();
                Volatile.Write(ref _lastMessageSw, nowSwForGuard);
                Volatile.Write(ref _lastTickSw, nowSwForGuard);
                ValutaBot.MiniApp.Services.ClockSkewMonitor.Record(tickTimeUtc);
            }
            return false;
        }

        long swNow = System.Diagnostics.Stopwatch.GetTimestamp();
        string baseAsset = PriceFeed.BaseAsset(asset);          // EURUSD_otc -> EURUSD
        string candleKey = baseAsset + PriceFeed.PoSuffix;      // -> EURUSD_PO

        if (!isBackfill)
        {
            if (!TryUpdateAnchor(baseAsset, tickTimeUtc, swNow, out bool rebasedBackward))
            {
                return false;
            }
            if (rebasedBackward)
            {
                ResetClockAndWatermarksCore(_clockAnchor);
                epoch = Volatile.Read(ref _clockEpoch);
                RealtimeTickCollector.ResetAllAccumulatorsAndRingBuffers();
                BotLogger.Warn("[PO WS] Broker clock anchor rebased BACKWARD (quorum confirmed). Cleaned watermarks and live prices.");
            }
        }

        if (epoch != Volatile.Read(ref _clockEpoch)) return false;
        bool fedToCandles = TryAdvanceCandleWatermark(candleKey, srvTs, isBackfill);
        if (epoch != Volatile.Read(ref _clockEpoch)) return false;

        if (!isBackfill && fedToCandles)
        {
            if (epoch != Volatile.Read(ref _clockEpoch))
            {
                return false;
            }
            // D5 fix: store broker tick timestamp and prevent out-of-order overwrite
            _livePrices.AddOrUpdate(
                baseAsset,
                _ => (price, tickTimeUtc, swNow),
                (_, old) => tickTimeUtc >= old.BrokerUtcTicks ? (price, tickTimeUtc, swNow) : old);
            _lastTickTicksBySymbol[baseAsset] = swNow;

            Volatile.Write(ref _lastMessageSw, swNow);
            Volatile.Write(ref _lastTickSw, swNow);
            ValutaBot.MiniApp.Services.ClockSkewMonitor.Record(tickTimeUtc);
            ValutaMetrics.WebSocketTicksTotal.WithLabels(candleKey).Inc();

            ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.UpdatePrice(
                baseAsset, price, new DateTime(tickTimeUtc, DateTimeKind.Utc));
        }
        Interlocked.Increment(ref _totalTicksIngested);

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
