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
    private static readonly object _streamLifecycleLock = new(); // D5 FIX: Synchronize Start/Stop/Launch
    private static bool _isDeferredStartPending = false;
    private static long _totalTicksIngested = 0;

    private static readonly string[] BaseTrackedPairs = {
        "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF"
    };

    private static readonly string[] DefaultAssets = {
        "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF",
        "EURUSD_otc", "GBPUSD_otc", "USDJPY_otc", "AUDUSD_otc", "USDCAD_otc", "USDCHF_otc"
    };

    private static string[] _subscribedAssets = DefaultAssets;

    public sealed record PoAssetMeta(string RawSymbol, int Payout, bool IsOpen, bool IsOtc);
    private static readonly ConcurrentDictionary<string, PoAssetMeta> _assetMeta = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, double> _crowdSentimentByRawSymbol = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _activeRawSymbolByBase = new(StringComparer.OrdinalIgnoreCase);

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

    private static readonly ReadOnlyMemory<byte> PongBytes = "3"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> PingBytes = "2"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> ConnectBytes = "40"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> PsBytes = "42[\"ps\"]"u8.ToArray();

    private static readonly ConcurrentDictionary<string, string> _canonicalAssetCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (string BaseAsset, string CandleKey)> _assetKeysCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Prometheus.Counter.Child> _metricCountersCache = new(StringComparer.OrdinalIgnoreCase);

    public static (string BaseAsset, string CandleKey) GetAssetKeys(string asset)
    {
        return _assetKeysCache.GetOrAdd(asset, static a =>
        {
            string b = PriceFeed.BaseAsset(a);
            string c = b + PriceFeed.PoSuffix;
            return (b, c);
        });
    }

    public static string ResolveCanonicalAsset(ReadOnlySpan<byte> span)
    {
        if (span.SequenceEqual("EURUSD"u8)) return "EURUSD";
        if (span.SequenceEqual("GBPUSD"u8)) return "GBPUSD";
        if (span.SequenceEqual("USDJPY"u8)) return "USDJPY";
        if (span.SequenceEqual("AUDUSD"u8)) return "AUDUSD";
        if (span.SequenceEqual("USDCAD"u8)) return "USDCAD";
        if (span.SequenceEqual("USDCHF"u8)) return "USDCHF";
        if (span.SequenceEqual("EURUSD_otc"u8)) return "EURUSD_otc";
        if (span.SequenceEqual("GBPUSD_otc"u8)) return "GBPUSD_otc";
        if (span.SequenceEqual("USDJPY_otc"u8)) return "USDJPY_otc";
        if (span.SequenceEqual("AUDUSD_otc"u8)) return "AUDUSD_otc";
        if (span.SequenceEqual("USDCAD_otc"u8)) return "USDCAD_otc";
        if (span.SequenceEqual("USDCHF_otc"u8)) return "USDCHF_otc";

        string assetStr = Encoding.UTF8.GetString(span);
        return _canonicalAssetCache.GetOrAdd(assetStr, assetStr);
    }

    public static string GetActiveRawSymbol(string symbol)
    {
        string baseAsset = PriceFeed.BaseAsset(symbol);
        string otcSymbol = baseAsset + "_otc";

        if (_assetMeta.TryGetValue(baseAsset, out var realMeta))
        {
            if (realMeta.IsOpen)
            {
                return baseAsset;
            }
            if (_assetMeta.TryGetValue(otcSymbol, out var otcMeta) && otcMeta.IsOpen)
            {
                return otcSymbol;
            }
            return baseAsset;
        }

        if (_assetMeta.TryGetValue(otcSymbol, out var onlyOtcMeta) && !onlyOtcMeta.IsOpen)
        {
            return baseAsset;
        }

        var dow = DateTime.UtcNow.DayOfWeek;
        bool isWeekend = dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;
        return isWeekend ? otcSymbol : baseAsset;
    }

    public static bool IsActiveSymbolForBase(string baseAsset, string rawAsset)
    {
        string activeRaw = GetActiveRawSymbol(baseAsset);
        if (_activeRawSymbolByBase.TryGetValue(baseAsset, out var prevRaw) &&
            !string.Equals(prevRaw, activeRaw, StringComparison.OrdinalIgnoreCase))
        {
            if (_activeRawSymbolByBase.TryUpdate(baseAsset, activeRaw, prevRaw))
            {
                string candleKey = baseAsset + PriceFeed.PoSuffix;
                _lastCandleSrvTs.TryRemove(candleKey, out _);
                _lastBackfillSrvTs.TryRemove(candleKey, out _);
                if (_livePrices.TryGetValue(baseAsset, out var slot))
                {
                    slot.Reset();
                }
                RealtimeTickCollector.ResetAssetAccumulator(candleKey);
                BotLogger.Info($"[PO WS] Active feed for {baseAsset} switched: {prevRaw} -> {activeRaw}");
            }
        }
        else
        {
            _activeRawSymbolByBase.TryAdd(baseAsset, activeRaw);
        }

        return string.Equals(rawAsset, activeRaw, StringComparison.OrdinalIgnoreCase);
    }

    public static double GetCrowdSentiment(string symbol)
    {
        string baseAsset = PriceFeed.BaseAsset(symbol);
        string activeRaw = GetActiveRawSymbol(baseAsset);
        if (_crowdSentimentByRawSymbol.TryGetValue(activeRaw, out double val) && double.IsFinite(val))
        {
            return val;
        }
        if (_crowdSentimentByRawSymbol.TryGetValue(baseAsset, out double baseVal) && double.IsFinite(baseVal))
        {
            return baseVal;
        }
        if (_crowdSentimentByRawSymbol.TryGetValue(baseAsset + "_otc", out double otcVal) && double.IsFinite(otcVal))
        {
            return otcVal;
        }
        if (_livePrices.TryGetValue(baseAsset, out var slot) && slot.TryRead(out _, out _, out _, out double slotSentiment, out _))
        {
            return slotSentiment;
        }
        return 50.0;
    }

    public static int GetAssetPayout(string symbol)
    {
        string baseAsset = PriceFeed.BaseAsset(symbol);
        string activeRaw = GetActiveRawSymbol(baseAsset);
        if (_assetMeta.TryGetValue(activeRaw, out var meta))
        {
            return meta.Payout;
        }
        if (_assetMeta.TryGetValue(baseAsset, out var baseMeta))
        {
            return baseMeta.Payout;
        }
        if (_assetMeta.TryGetValue(baseAsset + "_otc", out var otcMeta))
        {
            return otcMeta.Payout;
        }
        if (_livePrices.TryGetValue(baseAsset, out var slot) && slot.TryRead(out _, out _, out _, out _, out int slotPayout))
        {
            return slotPayout;
        }
        return 82;
    }

    public static bool IsAssetOpen(string symbol)
    {
        string baseAsset = PriceFeed.BaseAsset(symbol);
        string activeRaw = GetActiveRawSymbol(baseAsset);
        if (_assetMeta.TryGetValue(activeRaw, out var meta))
        {
            return meta.IsOpen;
        }
        return true;
    }

    public static bool IsCurrentlyOtc(string symbol)
    {
        string activeRaw = GetActiveRawSymbol(symbol);
        return activeRaw.EndsWith("_otc", StringComparison.OrdinalIgnoreCase);
    }

    private struct ReceivedFrame : IDisposable
    {
        public readonly WebSocketMessageType MessageType;
        public readonly ReadOnlyMemory<byte> Memory;
        private byte[]? _rented;

        public ReceivedFrame(WebSocketMessageType messageType, ReadOnlyMemory<byte> memory, byte[]? rented)
        {
            MessageType = messageType;
            Memory = memory;
            _rented = rented;
        }

        public static ReceivedFrame Empty => new(WebSocketMessageType.Close, ReadOnlyMemory<byte>.Empty, null);

        public bool IsEmpty => Memory.IsEmpty;
        public ReadOnlySpan<byte> Span => Memory.Span;

        public void Dispose()
        {
            var r = Interlocked.Exchange(ref _rented, null);
            if (r != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(r);
            }
        }
    }

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

    // Live price store: Lock-Free Seqlock slots per symbol eliminating torn reads of multi-word structs
    private sealed class LivePriceSlot
    {
        private long _version;
        public double Price;
        public long BrokerUtcTicks;
        public long LocalSwTimestamp;
        public double CrowdSentiment = 50.0;
        public int Payout = 82;

        public void Update(double price, long brokerUtcTicks, long localSw, double crowdSentiment = 50.0, int payout = 82)
        {
            if (brokerUtcTicks < Volatile.Read(ref BrokerUtcTicks))
                return; // Prevent out-of-order broker tick overwrite

            long v = Volatile.Read(ref _version);
            Volatile.Write(ref _version, v + 1); // Odd: write in progress
            Interlocked.MemoryBarrier();
            Price = price;
            BrokerUtcTicks = brokerUtcTicks;
            LocalSwTimestamp = localSw;
            if (double.IsFinite(crowdSentiment) && crowdSentiment >= 0.0 && crowdSentiment <= 100.0)
                CrowdSentiment = crowdSentiment;
            if (payout > 0 && payout <= 100)
                Payout = payout;
            Interlocked.MemoryBarrier();
            Volatile.Write(ref _version, v + 2); // Even: write completed
        }

        public void UpdateSentimentAndPayout(double crowdSentiment, int payout)
        {
            long v = Volatile.Read(ref _version);
            Volatile.Write(ref _version, v + 1);
            Interlocked.MemoryBarrier();
            if (double.IsFinite(crowdSentiment) && crowdSentiment >= 0.0 && crowdSentiment <= 100.0)
                CrowdSentiment = crowdSentiment;
            if (payout > 0 && payout <= 100)
                Payout = payout;
            Interlocked.MemoryBarrier();
            Volatile.Write(ref _version, v + 2);
        }

        public bool TryRead(out double price, out long brokerUtcTicks, out long localSw)
            => TryRead(out price, out brokerUtcTicks, out localSw, out _, out _);

        public bool TryRead(out double price, out long brokerUtcTicks, out long localSw, out double crowdSentiment, out int payout)
        {
            var spin = new SpinWait();
            for (int retry = 0; retry < 10; retry++)
            {
                long v1 = Volatile.Read(ref _version);
                if ((v1 & 1) != 0)
                {
                    spin.SpinOnce();
                    continue;
                }
                Interlocked.MemoryBarrier();
                double p = Price;
                long b = BrokerUtcTicks;
                long l = LocalSwTimestamp;
                double cs = CrowdSentiment;
                int pay = Payout;
                Interlocked.MemoryBarrier();
                long v2 = Volatile.Read(ref _version);
                if (v1 == v2 && (v2 & 1) == 0)
                {
                    price = p;
                    brokerUtcTicks = b;
                    localSw = l;
                    crowdSentiment = cs;
                    payout = pay;
                    return true;
                }
                spin.SpinOnce();
            }
            price = 0;
            brokerUtcTicks = 0;
            localSw = 0;
            crowdSentiment = 50.0;
            payout = 82;
            return false;
        }

        public void Reset()
        {
            long v = Volatile.Read(ref _version);
            Volatile.Write(ref _version, v + 1);
            Interlocked.MemoryBarrier();
            Price = 0;
            BrokerUtcTicks = 0;
            LocalSwTimestamp = 0;
            CrowdSentiment = 50.0;
            Payout = 82;
            Interlocked.MemoryBarrier();
            Volatile.Write(ref _version, v + 2);
        }
    }

    private static readonly ConcurrentDictionary<string, LivePriceSlot> _livePrices = new(StringComparer.OrdinalIgnoreCase);

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
    /// Returns broker-aligned UTC time when the Pocket Option tick stream is active, otherwise DateTime.UtcNow.
    /// </summary>
    public static DateTime GetEffectiveUtcNow() =>
        (PriceFeed.UsePocketOption && LastBrokerTickUtc != DateTime.MinValue)
            ? GetBrokerTimeUtc()
            : DateTime.UtcNow;

    /// <summary>
    /// Thread-safe status updater synchronized under _streamLifecycleLock.
    /// Prevents background unwinding tasks from overwriting "STOPPED" with "RECONNECTING" or "ONLINE".
    /// </summary>
    private static void SetStatus(string newStatus)
    {
        lock (_streamLifecycleLock)
        {
            if (Volatile.Read(ref _cts).IsCancellationRequested && newStatus != "STOPPED")
                return;
            Status = newStatus;
        }
    }

    /// <summary>
    /// Actively forces a WebSocket reconnection (called by Watchdog or SelfDiagnosticService when feed stalls).
    /// </summary>
    private static void CancelConnection()
    {
        var ws = _webSocket;
        try { ws?.Abort(); } catch { }
        var cts = Volatile.Read(ref _connectionCts);
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        }
    }

    public static void ForceReconnect(string reason)
    {
        BotLogger.Warn($"[PO WS] ForceReconnect triggered: {reason}");
        Volatile.Write(ref _lastTickSw, System.Diagnostics.Stopwatch.GetTimestamp());
        CancelConnection();
    }

    private static async Task SendPongAsync(ClientWebSocket ws, CancellationToken token)
    {
        await SendRawAsync(ws, PongBytes, token, timeoutSec: 5).ConfigureAwait(false);
    }

    private static async Task SendRawAsync(ClientWebSocket ws, ReadOnlyMemory<byte> bytes, CancellationToken token, int timeoutSec = 5)
    {
        if (ws.State != WebSocketState.Open) return;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
        try
        {
            await _sendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            BotLogger.Warn($"[PO WS] SendRawAsync lock wait timed out ({timeoutSec}s). Aborting socket to unblock send pipeline.");
            try { ws.Abort(); } catch { }
            CancelConnection();
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            BotLogger.Warn($"[PO WS] SendRawAsync timed out ({timeoutSec}s). Aborting socket to unblock send pipeline.");
            try { ws.Abort(); } catch { }
            CancelConnection();
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation on token cancellation
        }
        catch (ObjectDisposedException) { }
        catch (WebSocketException) { }
        finally
        {
            try { _sendLock.Release(); } catch { }
        }
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string payload, CancellationToken token)
    {
        if (ws.State != WebSocketState.Open) return;
        if (payload == "3") { await SendPongAsync(ws, token).ConfigureAwait(false); return; }
        if (payload == "2") { await SendRawAsync(ws, PingBytes, token).ConfigureAwait(false); return; }
        if (payload == "40") { await SendRawAsync(ws, ConnectBytes, token).ConfigureAwait(false); return; }
        if (payload == "42[\"ps\"]") { await SendRawAsync(ws, PsBytes, token).ConfigureAwait(false); return; }

        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        await SendRawAsync(ws, bytes, token).ConfigureAwait(false);
    }

    public static bool TryGetLivePrice(string symbol, out double price, double maxAgeSeconds = 2.0)
    {
        if (_clockAnchor != null &&
            _livePrices.TryGetValue(PriceFeed.BaseAsset(symbol), out var slot) &&
            slot.TryRead(out double tickPrice, out long brokerUtcTicks, out long localSw) &&
            tickPrice > 0)
        {
            double arrivalAge = System.Diagnostics.Stopwatch.GetElapsedTime(localSw).TotalSeconds;
            double brokerAge = (GetBrokerTimeUtc().Ticks - brokerUtcTicks) / (double)TimeSpan.TicksPerSecond;
            if (arrivalAge <= maxAgeSeconds && brokerAge <= maxAgeSeconds && brokerAge >= -AnchorToleranceSec)
            {
                price = tickPrice;
                return true;
            }
        }
        price = 0;
        return false;
    }

    public static void StartStream(string[]? assets = null)
    {
        lock (_streamLifecycleLock)
        {
            if (assets != null && assets.Length > 0)
            {
                var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in assets)
                {
                    string baseSym = PriceFeed.BaseAsset(a);
                    expanded.Add(baseSym);
                    expanded.Add(baseSym + "_otc");
                }
                _subscribedAssets = expanded.ToArray();
            }

            if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) != 0)
            {
                if (Volatile.Read(ref _cts).IsCancellationRequested)
                {
                    if (!_isDeferredStartPending)
                    {
                        _isDeferredStartPending = true;
                        BotLogger.Warn("[PO WS] StartStream called while stream loop is unwinding after stop. Deferring start...");
                        _ = DeferredStartAsync();
                    }
                    return;
                }
                BotLogger.Warn("[PO WS] StartStream called while stream loop is already running. Ignored.");
                return;
            }

            _isDeferredStartPending = false;
            LaunchStreamLocked();
        }
    }

    private static async Task DeferredStartAsync()
    {
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
            lock (_streamLifecycleLock)
            {
                if (!_isDeferredStartPending)
                {
                    // Cancelled by StopStream()
                    return;
                }
                if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) == 0)
                {
                    _isDeferredStartPending = false;
                    LaunchStreamLocked();
                    return;
                }
            }
        }
        lock (_streamLifecycleLock)
        {
            _isDeferredStartPending = false;
            if (Interlocked.CompareExchange(ref _loopRunning, 0, 0) == 0)
            {
                Status = "STOPPED";
            }
        }
        BotLogger.Error("[PO WS] Deferred StartStream gave up: previous loop did not exit within 5s.");
    }

    private static void LaunchStreamLocked()
    {
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cts, cts);
        try { oldCts.Cancel(); oldCts.Dispose(); } catch { }

        Status = "INITIALIZING";
        _ = RunConnectionLoopAsync(cts.Token);
        _ = WatchdogLoopAsync(cts.Token);
        _ = DbBatchFlusherAsync(cts.Token);
        BotLogger.Info("[PO WS] Autonomous Pocket Option OTC stream started.");
    }

    public static void StopStream()
    {
        lock (_streamLifecycleLock)
        {
            _isDeferredStartPending = false;
            try { Volatile.Read(ref _cts).Cancel(); } catch (ObjectDisposedException) { }
            CancelConnection();
            Status = "STOPPED";
        }
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

            CancelConnection();
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
                if (Status != "ONLINE") continue;

                double frameIdleSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastMessageSw)).TotalSeconds;
                double tickIdleSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTickSw)).TotalSeconds;
                CancellationToken connToken = token;
                var currentConnCts = Volatile.Read(ref _connectionCts);
                if (currentConnCts != null)
                {
                    try { connToken = currentConnCts.Token; } catch (ObjectDisposedException) { connToken = token; }
                }
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
                    CancelConnection();
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
                SetStatus("CONNECTING");
                var (authPayloadJson, tradingUrl) = await LoadSessionFromDbAsync(token);

                if (string.IsNullOrEmpty(authPayloadJson) || string.IsNullOrEmpty(tradingUrl))
                {
                    BotLogger.Warn("[PO WS] No active session found in po_collector_state. Waiting 10s...");
                    SetStatus("WAITING_SESSION");
                    await Task.Delay(10000, token);
                    continue;
                }

                bool wasConnected = false;
                using var connCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                Interlocked.Exchange(ref _connectionCts, connCts);

                var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("Origin", "https://pocketoption.com");
                ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                _webSocket = ws;

                try
                {
                    using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var linkedHandshakeCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token, handshakeCts.Token);
                    var handshakeToken = linkedHandshakeCts.Token;

                    BotLogger.Info($"[PO WS] Connecting to {tradingUrl}...");
                    await ws.ConnectAsync(new Uri(tradingUrl), handshakeToken);

                    long swConnected = System.Diagnostics.Stopwatch.GetTimestamp();
                    Volatile.Write(ref _lastMessageSw, swConnected);
                    Volatile.Write(ref _lastTickSw, swConnected);
                    Volatile.Write(ref _lastPsSw, swConnected);

                    // Step 1: Wait for server handshake packet: '0{"sid":...}'
                    using var handshakeFrame = await ReceiveFrameAsync(ws, handshakeToken).ConfigureAwait(false);
                    if (handshakeFrame.IsEmpty || handshakeFrame.Span.Length == 0 || handshakeFrame.Span[0] != (byte)'0')
                    {
                        string handshakeStr = handshakeFrame.Span.Length > 0 ? Encoding.UTF8.GetString(handshakeFrame.Span) : "empty";
                        throw new InvalidOperationException($"Invalid server handshake: {handshakeStr}");
                    }

                    // Step 2: Send Socket.IO connect packet: '40'
                    await SendRawAsync(ws, ConnectBytes, handshakeToken).ConfigureAwait(false);

                    // Step 3: Wait for server connect ACK: '40{"sid":...}'
                    using var ackFrame = await ReceiveFrameAsync(ws, handshakeToken).ConfigureAwait(false);
                    if (ackFrame.IsEmpty || !ackFrame.Span.StartsWith("40"u8))
                    {
                        string ackStr = ackFrame.Span.Length > 0 ? Encoding.UTF8.GetString(ackFrame.Span) : "empty";
                        throw new InvalidOperationException($"Invalid connect ACK: {ackStr}");
                    }

                    // Step 4: Send auth packet: 42["auth", <payload>]
                    string authPacket = "42[\"auth\"," + authPayloadJson + "]";
                    await SendTextAsync(ws, authPacket, handshakeToken);
                    BotLogger.Info("[PO WS] Authentication frame sent. Awaiting subscription confirmation...");

                    wasConnected = true;
                    SetStatus("ONLINE");
                    Volatile.Write(ref _lastMessageSw, System.Diagnostics.Stopwatch.GetTimestamp());
                    Volatile.Write(ref _lastTickSw, System.Diagnostics.Stopwatch.GetTimestamp());
                    Volatile.Write(ref _lastPsSw, System.Diagnostics.Stopwatch.GetTimestamp());

                    await ReceiveLoopAsync(ws, connCts.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (connCts.IsCancellationRequested)
                {
                    SetStatus("RECONNECTING");
                }
                catch (Exception ex)
                {
                    SetStatus("RECONNECTING");
                    int retrySec = (wasConnected ? ReconnectAfterConnectedMs : backoffMs) / 1000;
                    BotLogger.Error($"[PO WS] Connection error: {ex.Message}. Retrying in {retrySec}s...");
                }
                finally
                {
                    Interlocked.CompareExchange(ref _connectionCts, null, connCts);
                    try { ws.Dispose(); } catch { }
                    if (ReferenceEquals(_webSocket, ws))
                    {
                        _webSocket = null;
                        _livePrices.Clear();
                        _lastTickTicksBySymbol.Clear();
                    }
                }

                // Exponential Backoff with Full Jitter to eliminate thundering herd connection storms
                int ceiling = wasConnected ? MinBackoffMs : backoffMs;
                int minDelay = wasConnected ? ReconnectAfterConnectedMs : (MinBackoffMs / 2);
                int delayMs = Random.Shared.Next(minDelay, ceiling + 1);
                backoffMs = wasConnected ? MinBackoffMs : Math.Min(backoffMs * 2, MaxBackoffMs);
                await Task.Delay(delayMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_streamLifecycleLock)
            {
                Interlocked.Exchange(ref _loopRunning, 0);
                try { Volatile.Read(ref _cts).Cancel(); } catch { }
                if (!_isDeferredStartPending)
                {
                    Status = "STOPPED";
                }
            }
        }
    }

    private static async Task<ReceivedFrame> ReceiveFrameAsync(ClientWebSocket ws, CancellationToken token)
    {
        byte[]? currentBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768);
        bool ownershipTransferred = false;
        try
        {
            var firstResult = await ws.ReceiveAsync(new ArraySegment<byte>(currentBuffer), token).ConfigureAwait(false);
            if (firstResult.MessageType == WebSocketMessageType.Close)
            {
                return ReceivedFrame.Empty;
            }

            if (firstResult.EndOfMessage)
            {
                var frame = new ReceivedFrame(firstResult.MessageType, currentBuffer.AsMemory(0, firstResult.Count), currentBuffer);
                ownershipTransferred = true;
                return frame;
            }

            const int maxFrameBytes = 4 * 1024 * 1024; // 4 MB safety limit
            int totalBytes = firstResult.Count;

            WebSocketReceiveResult nextResult;
            do
            {
                if (totalBytes >= currentBuffer.Length)
                {
                    int newCap = currentBuffer.Length * 2;
                    if (newCap > maxFrameBytes)
                    {
                        BotLogger.Error("[PO WS] Frame exceeded 4MB limit, aborting frame receive.");
                        return ReceivedFrame.Empty;
                    }
                    byte[] newBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(newCap);
                    Buffer.BlockCopy(currentBuffer, 0, newBuffer, 0, totalBytes);
                    System.Buffers.ArrayPool<byte>.Shared.Return(currentBuffer);
                    currentBuffer = newBuffer;
                }

                nextResult = await ws.ReceiveAsync(
                    new ArraySegment<byte>(currentBuffer, totalBytes, currentBuffer.Length - totalBytes),
                    token).ConfigureAwait(false);

                if (nextResult.MessageType == WebSocketMessageType.Close)
                {
                    return ReceivedFrame.Empty;
                }

                totalBytes += nextResult.Count;
            } while (!nextResult.EndOfMessage);

            var multiFrame = new ReceivedFrame(nextResult.MessageType, currentBuffer.AsMemory(0, totalBytes), currentBuffer);
            ownershipTransferred = true;
            return multiFrame;
        }
        finally
        {
            if (!ownershipTransferred && currentBuffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(currentBuffer);
            }
        }
    }

    private static async Task SubscribeAllAssetsAsync(ClientWebSocket ws, CancellationToken connectionToken)
    {
        Interlocked.Exchange(ref _consecutiveAuthFailures, 0);
        SetStatus("ONLINE");
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
            SetStatus("AUTH_EXPIRED");
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

        try
        {
            while (ws.State == WebSocketState.Open && !connectionToken.IsCancellationRequested)
            {
                frameTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                using var frame = await ReceiveFrameAsync(ws, frameTimeoutCts.Token).ConfigureAwait(false);
                if (!frameTimeoutCts.TryReset())
                {
                    break;
                }

                if (frame.IsEmpty || frame.Span.Length == 0)
                {
                    BotLogger.Warn("[PO WS] Stream closed by server.");
                    break;
                }

                Volatile.Write(ref _lastMessageSw, System.Diagnostics.Stopwatch.GetTimestamp());
                var span = frame.Span;

                // 1. Binary attachment payload (follows a 451- packet)
                if (frame.MessageType == WebSocketMessageType.Binary)
                {
                    if (pendingEvent == "updateStream")
                    {
                        ParseUpdateStreamUtf8(span);
                    }
                    else if (pendingEvent == "updateHistoryNewFast")
                    {
                        ParseHistoryUtf8(frame.Memory);
                    }
                    else if (pendingEvent == "chafor")
                    {
                        ParseChaforUtf8(span);
                    }
                    else if (pendingEvent == "updateAssets")
                    {
                        ParseUpdateAssetsUtf8(span);
                    }
                    else if (pendingEvent == "successauth" && !subscribed)
                    {
                        subscribed = true;
                        _ = SubscribeAllAssetsSafeAsync(ws, connectionToken);
                    }
                    pendingEvent = null;
                    continue;
                }

                // 2. Engine.IO ping/pong heartbeat & 41 namespace disconnect (Auth Rejected)
                if (span.Length == 1 && (span[0] == 0x32 || span[0] == (byte)'2'))
                {
                    // Immediate zero-copy pong reply without blocking on tick queue
                    await SendPongAsync(ws, connectionToken).ConfigureAwait(false);
                    continue;
                }
                if (span.Length == 1 && span[0] == (byte)'3')
                {
                    continue;
                }
                if (span.StartsWith("41"u8))
                {
                    if (!subscribed)
                    {
                        await HandleAuthRejectedAsync(connectionToken).ConfigureAwait(false);
                    }
                    break;
                }

                // 3. Binary placeholder header: e.g. 451-["updateStream",{"_placeholder":true,"num":0}]
                if (span.StartsWith("451-"u8))
                {
                    pendingEvent = ExtractPlaceholderEvent(span[4..]);
                    continue;
                }

                // 4. Regular JSON event: 42["<event>", ...]
                if (span.StartsWith("42"u8))
                {
                    ParseSocketIoMessage(span[2..], frame.Memory.Slice(2), ws, connectionToken, ref subscribed);
                }
            }
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
            // Expected on stream reconnect or graceful stop
        }
        catch (OperationCanceledException) when (frameTimeoutCts.IsCancellationRequested)
        {
            BotLogger.Warn("[PO WS] Receive frame timed out (30s). Closing stream to reconnect...");
        }
    }

    private static string? ExtractPlaceholderEvent(ReadOnlySpan<byte> span)
    {
        try
        {
            var reader = new Utf8JsonReader(span);
            if (reader.Read() && reader.TokenType == JsonTokenType.StartArray &&
                reader.Read() && reader.TokenType == JsonTokenType.String)
            {
                if (reader.ValueTextEquals("updateStream"u8)) return "updateStream";
                if (reader.ValueTextEquals("updateHistoryNewFast"u8)) return "updateHistoryNewFast";
                if (reader.ValueTextEquals("chafor"u8)) return "chafor";
                if (reader.ValueTextEquals("updateAssets"u8)) return "updateAssets";
                if (reader.ValueTextEquals("successauth"u8)) return "successauth";
                return reader.GetString();
            }
        }
        catch { }
        return null;
    }

    private static void ParseSocketIoMessage(ReadOnlySpan<byte> span, ReadOnlyMemory<byte> memory, ClientWebSocket ws, CancellationToken connectionToken, ref bool subscribed)
    {
        if (!subscribed && span.IndexOf("successauth"u8) >= 0)
        {
            subscribed = true;
            _ = SubscribeAllAssetsSafeAsync(ws, connectionToken);
        }

        try
        {
            var reader = new Utf8JsonReader(span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) return;

            if (reader.ValueTextEquals("updateStream"u8))
            {
                if (!reader.Read()) return;
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    ParseUpdateStreamArray(ref reader);
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    string rawJson = reader.GetString() ?? "";
                    ParseUpdateStreamJson(rawJson);
                }
            }
            else if (reader.ValueTextEquals("updateHistoryNewFast"u8))
            {
                ParseHistoryUtf8(memory);
            }
            else if (reader.ValueTextEquals("chafor"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    ParseChaforArray(ref reader);
                }
            }
            else if (reader.ValueTextEquals("updateAssets"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                {
                    ParseUpdateAssetsArray(ref reader);
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing 42 frame: {ex.Message}");
        }
    }

    private static void ParseChaforUtf8(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return;
            ParseChaforArray(ref reader);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing chafor UTF8 frame: {ex.Message}");
        }
    }

    private static void ParseChaforArray(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                if (!reader.Read()) break;
                string rawSymbol = reader.TokenType == JsonTokenType.String ? ResolveCanonicalAsset(reader.ValueSpan) : "";

                if (!reader.Read()) break;
                double pct = 50.0;
                if (reader.TokenType == JsonTokenType.Number)
                {
                    pct = reader.GetDouble();
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    if (!System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out pct, out _))
                    {
                        double.TryParse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pct);
                    }
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartArray || reader.TokenType == JsonTokenType.StartObject)
                        reader.Skip();
                }

                if (!string.IsNullOrEmpty(rawSymbol) && double.IsFinite(pct))
                {
                    double clamped = Math.Clamp(pct, 0.0, 100.0);
                    _crowdSentimentByRawSymbol[rawSymbol] = clamped;
                    string baseAsset = PriceFeed.BaseAsset(rawSymbol);
                    if (IsActiveSymbolForBase(baseAsset, rawSymbol) && _livePrices.TryGetValue(baseAsset, out var slot))
                    {
                        int payout = GetAssetPayout(baseAsset);
                        slot.UpdateSentimentAndPayout(clamped, payout);
                    }
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                reader.Skip();
            }
        }
    }

    private static void ParseUpdateAssetsUtf8(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return;
            ParseUpdateAssetsArray(ref reader);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing updateAssets UTF8 frame: {ex.Message}");
        }
    }

    private static void ParseUpdateAssetsArray(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                string rawSymbol = "";
                int payout = 82;
                bool isOtc = false;
                bool isOpen = true;
                int idx = 0;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartArray || reader.TokenType == JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        idx++;
                        continue;
                    }

                    if (idx == 1 && reader.TokenType == JsonTokenType.String)
                    {
                        rawSymbol = ResolveCanonicalAsset(reader.ValueSpan);
                    }
                    else if (idx == 5 && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int p))
                    {
                        payout = p;
                    }
                    else if (idx == 9)
                    {
                        isOtc = reader.TokenType == JsonTokenType.True ||
                                (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int otcInt) && otcInt != 0);
                    }
                    else if (idx == 14)
                    {
                        isOpen = reader.TokenType == JsonTokenType.True ||
                                 (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int openInt) && openInt != 0);
                    }
                    idx++;
                }

                if (!string.IsNullOrEmpty(rawSymbol))
                {
                    _assetMeta[rawSymbol] = new PoAssetMeta(rawSymbol, payout, isOpen, isOtc);
                }
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                reader.Skip();
            }
        }

        foreach (var basePair in BaseTrackedPairs)
        {
            string activeRaw = GetActiveRawSymbol(basePair);
            IsActiveSymbolForBase(basePair, activeRaw);
            if (_livePrices.TryGetValue(basePair, out var slot))
            {
                slot.UpdateSentimentAndPayout(GetCrowdSentiment(basePair), GetAssetPayout(basePair));
            }
        }
    }

    private static void ParseUpdateStreamUtf8(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return;
            ParseUpdateStreamArray(ref reader);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing updateStream UTF8 frame: {ex.Message}");
        }
    }

    private static void ParseUpdateStreamArray(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                if (!reader.Read()) break;
                string rawAsset = ResolveCanonicalAsset(reader.ValueSpan);

                if (!reader.Read()) break;
                double srvTs = 0;
                if (reader.TokenType == JsonTokenType.Number)
                {
                    srvTs = reader.GetDouble();
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    if (!System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out srvTs, out _))
                    {
                        double.TryParse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out srvTs);
                    }
                }

                if (!reader.Read()) break;
                double price = 0;
                if (reader.TokenType == JsonTokenType.Number)
                {
                    price = reader.GetDouble();
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    if (!System.Buffers.Text.Utf8Parser.TryParse(reader.ValueSpan, out price, out _))
                    {
                        double.TryParse(Encoding.UTF8.GetString(reader.ValueSpan), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out price);
                    }
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) { }

                if (!string.IsNullOrEmpty(rawAsset) && price > 0)
                {
                    HandleTick(rawAsset, srvTs, price);
                }
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
                    string rawAsset = item[0].GetString() ?? "";
                    string asset = ResolveCanonicalAsset(Encoding.UTF8.GetBytes(rawAsset));
                    double srvTs = item[1].ValueKind == JsonValueKind.Number ? item[1].GetDouble() :
                        (item[1].ValueKind == JsonValueKind.String && double.TryParse(item[1].GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedTs) ? parsedTs : 0);
                    double price = item[2].ValueKind == JsonValueKind.Number ? item[2].GetDouble() :
                        (item[2].ValueKind == JsonValueKind.String && double.TryParse(item[2].GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedPrice) ? parsedPrice : 0);
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

    private static void ParseHistoryUtf8(ReadOnlyMemory<byte> memory)
    {
        try
        {
            using var doc = JsonDocument.Parse(memory);
            ParseHistoryDoc(doc);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing history UTF8 frame: {ex.Message}");
        }
    }

    private static void ParseHistoryJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            ParseHistoryDoc(doc);
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error parsing history JSON frame: {ex.Message}");
        }
    }

    private static void ParseHistoryDoc(JsonDocument doc)
    {
        try
        {
            var root = doc.RootElement;
            JsonElement target = root;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 1)
            {
                target = root[1];
            }

            if (target.TryGetProperty("asset", out var aProp) && target.TryGetProperty("history", out var hProp))
            {
                string rawAsset = aProp.GetString() ?? "";
                string asset = ResolveCanonicalAsset(Encoding.UTF8.GetBytes(rawAsset));
                if (hProp.ValueKind == JsonValueKind.Array)
                {
                    var points = new List<(double srvTs, double price)>();
                    foreach (var pt in hProp.EnumerateArray())
                    {
                        if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2)
                        {
                            double srvTs = pt[0].ValueKind == JsonValueKind.Number ? pt[0].GetDouble() :
                                (pt[0].ValueKind == JsonValueKind.String && double.TryParse(pt[0].GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedTs) ? parsedTs : 0);
                            double price = pt[1].ValueKind == JsonValueKind.Number ? pt[1].GetDouble() :
                                (pt[1].ValueKind == JsonValueKind.String && double.TryParse(pt[1].GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedPrice) ? parsedPrice : 0);
                            if (srvTs > 0 && price > 0)
                            {
                                points.Add((srvTs, price));
                            }
                        }
                    }

                    // Strict ascending chronological sorting to prevent watermark dropping when broker returns history in reverse order
                    points.Sort((a, b) => a.srvTs.CompareTo(b.srvTs));

                    int fedCount = 0;
                    foreach (var (srvTs, price) in points)
                    {
                        if (HandleTick(asset, srvTs, price, isBackfill: true))
                        {
                            fedCount++;
                        }
                    }
                    BotLogger.Info($"[PO WS] History for {asset}: {points.Count} ticks ({fedCount} new ticks fed to candles).");
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[PO WS] Error in ParseHistoryDoc: {ex.Message}");
        }
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
                // Only advance anchor if advance is modest (<= 500ms) to prevent single jitter spikes from jerking the clock forward
                if (lagTicks < 0 && lagTicks >= -(long)(0.5 * TimeSpan.TicksPerSecond))
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

    internal static bool HandleTick(string asset, double srvTs, double price, bool isBackfill = false)
    {
        long nowUtcTicks = DateTime.UtcNow.Ticks;
        // Data Integrity Guard: reject poisoned broker ticks (NaN, Infinity, non-positive or excessive price, or out-of-range epoch timestamp)
        if (double.IsNaN(srvTs) || double.IsInfinity(srvTs) || srvTs <= 0 || srvTs > 3_000_000_000)
        {
            return false;
        }

        double utcSec = srvTs - ServerTimeOffsetSec;
        long tickTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds((long)(utcSec * 1000)).UtcTicks;

        // D4 upper bound: record skew before rejecting future-poisoned timestamps (>10 minutes ahead of host UTC) so DST shifts are still detected
        if (tickTimeUtc - nowUtcTicks > FutureTickGuardTicks)
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

        if (!PriceFeed.IsValidTick(price, tickTimeUtc, nowUtcTicks, isBackfill))
        {
            return false;
        }

        int epoch = Volatile.Read(ref _clockEpoch);

        long swNow = System.Diagnostics.Stopwatch.GetTimestamp();
        var (baseAsset, candleKey) = GetAssetKeys(asset);

        if (!IsActiveSymbolForBase(baseAsset, asset))
        {
            if (!isBackfill)
            {
                Volatile.Write(ref _lastMessageSw, swNow);
            }
            return false;
        }

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

        double crowdSentiment = GetCrowdSentiment(baseAsset);
        int payout = GetAssetPayout(baseAsset);

        if (!isBackfill && fedToCandles)
        {
            if (epoch != Volatile.Read(ref _clockEpoch))
            {
                return false;
            }
            // D5 fix: store broker tick timestamp and prevent out-of-order overwrite via Lock-Free Seqlock
            var priceSlot = _livePrices.GetOrAdd(baseAsset, static _ => new LivePriceSlot());
            priceSlot.Update(price, tickTimeUtc, swNow, crowdSentiment, payout);
            _lastTickTicksBySymbol[baseAsset] = swNow;

            Volatile.Write(ref _lastMessageSw, swNow);
            Volatile.Write(ref _lastTickSw, swNow);
            ValutaBot.MiniApp.Services.ClockSkewMonitor.Record(tickTimeUtc);
            _metricCountersCache.GetOrAdd(candleKey, static k => ValutaMetrics.WebSocketTicksTotal.WithLabels(k)).Inc();

            ValutaBot.MiniApp.Features.MarketAnalysis.Engines.MacroContextEngine.UpdatePrice(
                baseAsset, price, new DateTime(tickTimeUtc, DateTimeKind.Utc));
        }
        Interlocked.Increment(ref _totalTicksIngested);

        if (fedToCandles)
        {
            _ = RealtimeTickCollector.OnPriceUpdateAsync(candleKey, price, tickTimeUtc, isBackfill, crowdSentiment);
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
