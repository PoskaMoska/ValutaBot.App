using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp
{
    public static class RealtimeTickCollector
    {
        public static event Action<string, string>? OnCandleClosed;
        private class CandleAccumulator
        {
            public double? Open { get; set; }
            public double High { get; set; } = double.MinValue;
            public double Low { get; set; } = double.MaxValue;
            public double Close { get; set; }
            public int TickCount { get; set; }
            public DateTime OpenTime { get; set; }
            public MiniAppController.OhlcCandle? LastClosed { get; set; }
            
            public void AddTick(double price)
            {
                if (!Open.HasValue) Open = price;
                if (price > High) High = price;
                if (price < Low) Low = price;
                Close = price;
                TickCount++;
            }
            
            public void Reset(DateTime openTime)
            {
                if (Open.HasValue) LastClosed = new MiniAppController.OhlcCandle(Open.Value, High, Low, Close, TickCount, OpenTime);
                Open = null;
                High = double.MinValue;
                Low = double.MaxValue;
                Close = 0;
                TickCount = 0;
                OpenTime = openTime;
            }
        }

        private record struct TickEvent(string Asset, string Interval, double Price, DateTime OpenTime);

        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s5  = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s10 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s15 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s30 = new();
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _m1 = new();

        public class CandleRingBuffer
        {
            private readonly MiniAppController.OhlcCandle[] _buffer;
            private readonly int _capacity;
            private int _start;
            private int _count;
            private readonly object _lock = new();

            public CandleRingBuffer(int capacity = 400)
            {
                _capacity = Math.Max(50, capacity);
                _buffer = new MiniAppController.OhlcCandle[_capacity];
            }

            public int Count
            {
                get { lock (_lock) return _count; }
            }

            public void Add(MiniAppController.OhlcCandle candle)
            {
                lock (_lock)
                {
                    if (_count > 0)
                    {
                        int lastIdx = (_start + _count - 1) % _capacity;
                        if (_buffer[lastIdx].Timestamp == candle.Timestamp)
                        {
                            _buffer[lastIdx] = candle;
                            return;
                        }
                        if (candle.Timestamp < _buffer[lastIdx].Timestamp)
                        {
                            return; // Ignore stale/out-of-order historical candle
                        }
                    }

                    if (_count == _capacity)
                    {
                        _buffer[_start] = candle;
                        _start = (_start + 1) % _capacity;
                    }
                    else
                    {
                        int idx = (_start + _count) % _capacity;
                        _buffer[idx] = candle;
                        _count++;
                    }
                }
            }

            public void AddRange(IEnumerable<MiniAppController.OhlcCandle> candles)
            {
                lock (_lock)
                {
                    var sorted = candles.OrderBy(c => c.Timestamp);
                    foreach (var c in sorted)
                    {
                        if (_count > 0)
                        {
                            int lastIdx = (_start + _count - 1) % _capacity;
                            if (_buffer[lastIdx].Timestamp == c.Timestamp)
                            {
                                _buffer[lastIdx] = c;
                                continue;
                            }
                            if (c.Timestamp < _buffer[lastIdx].Timestamp)
                            {
                                continue; // Ignore out-of-order candle
                            }
                        }

                        if (_count == _capacity)
                        {
                            _buffer[_start] = c;
                            _start = (_start + 1) % _capacity;
                        }
                        else
                        {
                            int idx = (_start + _count) % _capacity;
                            _buffer[idx] = c;
                            _count++;
                        }
                    }
                }
            }

            public MiniAppController.OhlcCandle[] GetRecent(int count)
            {
                lock (_lock)
                {
                    int toTake = Math.Min(count, _count);
                    if (toTake <= 0) return Array.Empty<MiniAppController.OhlcCandle>();

                    var result = new MiniAppController.OhlcCandle[toTake];
                    int offset = _count - toTake;
                    for (int i = 0; i < toTake; i++)
                    {
                        int idx = (_start + offset + i) % _capacity;
                        result[i] = _buffer[idx];
                    }
                    return result;
                }
            }
        }

        private static readonly ConcurrentDictionary<string, CandleRingBuffer> _ringBuffers = new(StringComparer.OrdinalIgnoreCase);

        // FIX 4 (2026-09-13): Bounded channel prevents unbounded memory growth during DB outages.
        // With CreateUnbounded, a PostgreSQL outage at ~100 ticks/sec fills ~30k events in 5min → OOM.
        // DropOldest: gaps in subminute_candles are acceptable; OOM is not.
        private const int ChannelCapacity = 100_000;
        private static readonly Channel<TickEvent> _tickChannel = Channel.CreateBounded<TickEvent>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,   // only ProcessTickQueueAsync reads
                SingleWriter = false   // multiple WS ticks write concurrently
            });

        private static int _droppedTickCount = 0;
        private static DateTime _lastDropWarningAt = DateTime.MinValue;

        private static int _isInitialized = 0;
        private static readonly SemaphoreSlim _dbWriteLock = new(1, 1);
        private static readonly CancellationTokenSource _collectorCts = new();

        public static Task InitializeAsync()
        {
            if (Interlocked.Exchange(ref _isInitialized, 1) == 0)
            {
                // Hook process exit to flush in-flight ticks on graceful shutdown or container stop
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    try
                    {
                        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch { }
                };

                _ = Task.Run(ProcessTickQueueAsync);
                _ = Task.Run(PrewarmRingBuffersAsync);
                BotLogger.Info("[TickCollector] Initialized continuous real-time subminute candle accumulation (Batched & Flush-Protected).");
            }
            return Task.CompletedTask;
        }

        public static void ResetAllAccumulatorsAndRingBuffers()
        {
            _s5.Clear();
            _s10.Clear();
            _s15.Clear();
            _s30.Clear();
            _m1.Clear();
            _ringBuffers.Clear();
        }

        public static void Initialize()
        {
            InitializeAsync().GetAwaiter().GetResult();
        }

        public static async Task PrewarmRingBuffersAsync()
        {
            try
            {
                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                var records = (await Dapper.SqlMapper.QueryAsync(conn, @"
                    WITH ranked AS (
                        SELECT asset, interval, open_time, open_price, high_price, low_price, close_price, volume,
                               ROW_NUMBER() OVER (PARTITION BY asset, interval ORDER BY open_time DESC) as rn
                        FROM subminute_candles
                        WHERE interval IN ('s5', 's10', 's15', 's30', '1min')
                    )
                    SELECT asset, interval, open_time as ""OpenTime"", open_price as ""Open"", high_price as ""High"", low_price as ""Low"", close_price as ""Close"", volume as ""Volume""
                    FROM ranked
                    WHERE rn <= 250
                    ORDER BY asset, interval, open_time ASC;
                ")).ToList();

                int loaded = 0;
                foreach (var g in records.GroupBy(r => $"{r.asset}:{r.interval}"))
                {
                    var ring = _ringBuffers.GetOrAdd(g.Key, _ => new CandleRingBuffer(400));
                    var candles = g.Select(r => new MiniAppController.OhlcCandle(
                        Convert.ToDouble(r.Open),
                        Convert.ToDouble(r.High),
                        Convert.ToDouble(r.Low),
                        Convert.ToDouble(r.Close),
                        Convert.ToDouble(r.Volume),
                        r.OpenTime is string s ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal) : Convert.ToDateTime(r.OpenTime).ToUniversalTime()
                    )).ToList();
                    
                    ring.AddRange(candles);
                    loaded += candles.Count;
                }
                BotLogger.Info($"[TickCollector] In-Memory RingBuffer pre-warmed with {loaded} historical candles across {_ringBuffers.Count} buffers.");
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TickCollector] RingBuffer pre-warm notice: {ex.Message}");
            }
        }

        /// <summary>
        /// Actively flushes all buffered ticks in memory and in the Channel to PostgreSQL.
        /// Called on graceful host shutdown, ProcessExit, before emergency self-recycle, or during in-process restart.
        /// </summary>
        public static async Task<int> FlushAsync(CancellationToken ct = default)
        {
            var timeoutToken = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutToken.Token);

            try
            {
                await _dbWriteLock.WaitAsync(linkedCts.Token);
            }
            catch
            {
                return 0; // Lock timeout or canceled
            }

            try
            {
                var drainList = new List<TickEvent>(2000);
                while (_tickChannel.Reader.TryRead(out var tick))
                {
                    drainList.Add(tick);
                }

                if (drainList.Count == 0) return 0;

                bool ok = await WriteBatchInternalAsync(drainList, linkedCts.Token);
                if (ok)
                {
                    BotLogger.Info($"[TickCollector] Flushed {drainList.Count} buffered ticks to PostgreSQL.");
                    return drainList.Count;
                }
                return 0;
            }
            finally
            {
                try { _dbWriteLock.Release(); } catch { }
            }
        }

        private static async Task<bool> WriteBatchInternalAsync(List<TickEvent> buffer, CancellationToken ct = default)
        {
            if (buffer.Count == 0) return true;

            try
            {
                await using var conn = await DbConnectionFactory.OpenConnectionAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                
                // D1-1 FIX: Materialise each group into a List before First/Last.
                // Channel.CreateBounded with DropOldest evicts the oldest entries when full,
                // but the items already consumed into 'buffer' retain their insertion (FIFO) order.
                // Since Channel is SingleReader, buffer preserves the order ticks arrived from WS.
                // Calling g.ToList() locks in that FIFO order so First()=oldest=Open, Last()=newest=Close.
                var grouped = buffer
                    .GroupBy(t => new { t.Asset, t.Interval, t.OpenTime })
                    .Select(g =>
                    {
                        var ordered = g.ToList(); // FIFO insertion order = chronological
                        return new {
                            Asset    = g.Key.Asset,
                            Interval = g.Key.Interval,
                            OpenTime = g.Key.OpenTime.ToString("o"),
                            Open     = ordered.First().Price,  // chronologically first tick
                            High     = ordered.Max(t => t.Price),
                            Low      = ordered.Min(t => t.Price),
                            Close    = ordered.Last().Price,   // chronologically last tick
                            Volume   = ordered.Count
                        };
                    });

                // D1-3 FIX: commandTimeout=8s prevents the consumer loop from blocking
                // for Npgsql's default 30s ConnectTimeout when PostgreSQL is flapping.
                // With 8s cap: retry cycle is 8s + 0.5s delay = ~8.5s max blocked per attempt.
                const int DbCommandTimeoutSecs = 8;

                // High-load optimization: execute the entire grouped batch in one command instead of N sequential proxy roundtrips

                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO subminute_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume)
                        VALUES (@Asset, @Interval, @OpenTime, @Open, @High, @Low, @Close, @Volume)
                        ON CONFLICT (asset, interval, open_time) DO UPDATE SET
                            high_price  = GREATEST(subminute_candles.high_price,  EXCLUDED.high_price),
                            low_price   = LEAST(subminute_candles.low_price,      EXCLUDED.low_price),
                            close_price = EXCLUDED.close_price,
                            volume      = subminute_candles.volume + EXCLUDED.volume;
                    ", grouped.ToList(), tx, commandTimeout: DbCommandTimeoutSecs);
                }
                
                await tx.CommitAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TickSync] Failed to batch save ticks: {ex.Message}. Retrying...");
                return false;
            }
        }

        private static async Task ProcessTickQueueAsync()
        {
            var buffer = new List<TickEvent>(1000);
            var token = _collectorCts.Token;
            int consecutiveBatchFailures = 0;

            try
            {
                while (!token.IsCancellationRequested && await _tickChannel.Reader.WaitToReadAsync(token))
                {
                    if (buffer.Count == 0 && _tickChannel.Reader.Count < 200)
                    {
                        // Wait for micro-batching window while keeping ticks inside _tickChannel so FlushAsync() can drain them immediately
                        try { await Task.Delay(250, token); } catch (OperationCanceledException) { }
                    }

                    bool wroteAny = false;
                    bool success = false;
                    await _dbWriteLock.WaitAsync(token);
                    try
                    {
                        while (buffer.Count < 8000 && _tickChannel.Reader.TryRead(out var tick))
                        {
                            buffer.Add(tick);
                        }
                        if (buffer.Count > 0)
                        {
                            wroteAny = true;
                            success = await WriteBatchInternalAsync(buffer, token);
                        }
                    }
                    finally
                    {
                        _dbWriteLock.Release();
                    }

                    if (wroteAny)
                    {

                        if (success)
                        {
                            buffer.Clear(); // Only clear on success to prevent tick loss
                            consecutiveBatchFailures = 0;
                        }
                        else
                        {
                            consecutiveBatchFailures++;
                            if (consecutiveBatchFailures >= 5)
                            {
                                BotLogger.Error($"[TickCollector] Batch write failed {consecutiveBatchFailures} times consecutively. Discarding {buffer.Count} poisoned ticks to restore channel ingestion.");
                                buffer.Clear();
                                consecutiveBatchFailures = 0;
                            }
                        }
                    }
                    
                    // Throttle conditionally: if buffer has items (error retry), wait 300ms.
                    // If channel is empty, wait 300ms to allow batching.
                    if (buffer.Count > 0 || _tickChannel.Reader.Count == 0)
                    {
                        try { await Task.Delay(300, token); } catch (OperationCanceledException) { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Ensure remaining ticks in buffer or channel are committed before task terminates
                try
                {
                    if (buffer.Count > 0)
                    {
                        await _dbWriteLock.WaitAsync(TimeSpan.FromSeconds(3));
                        try { await WriteBatchInternalAsync(buffer, CancellationToken.None); }
                        finally { _dbWriteLock.Release(); }
                    }
                    await FlushAsync(CancellationToken.None);
                }
                catch { }
            }
        }

        private static readonly ConcurrentDictionary<string, (long TimestampTicks, int Limit, MiniAppController.OhlcCandle[] Candles)> _recentCandlesCache = new();

        public static async Task<MiniAppController.OhlcCandle[]> GetRecentCandles(string asset, string interval, int limit)
        {
            try
            {
                string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
                string dbInterval = interval == "m1" ? "1min" : interval;
                string cacheKey = $"{cleanAsset}:{dbInterval}";
                long nowTicks = DateTime.UtcNow.Ticks;

                // Fast path: reuse recent SQL fetch within 1.5s if limit is satisfied (eliminates duplicate scanner -> orchestrator SQL queries)
                if (_recentCandlesCache.TryGetValue(cacheKey, out var cached) &&
                    (nowTicks - cached.TimestampTicks) <= TimeSpan.FromMilliseconds(1500).Ticks &&
                    cached.Limit >= limit &&
                    cached.Candles.Length > 0)
                {
                    if (cached.Candles.Length <= limit)
                        return cached.Candles;
                    var slice = new MiniAppController.OhlcCandle[limit];
                    Array.Copy(cached.Candles, cached.Candles.Length - limit, slice, 0, limit);
                    return slice;
                }

                // --- FAST PATH: IN-MEMORY RING BUFFER (0.001ms, NO DB, NO LOCKS) ---
                if (_ringBuffers.TryGetValue(cacheKey, out var ring) && ring.Count >= Math.Min(limit - 1, 40))
                {
                    ConcurrentDictionary<string, CandleAccumulator>? tDict = interval switch
                    {
                        "s5" => _s5, "s10" => _s10, "s15" => _s15, "s30" => _s30, "m1" => _m1, "1min" => _m1, _ => null
                    };

                    MiniAppController.OhlcCandle? liveCandle = null;
                    if (tDict != null && tDict.TryGetValue(cleanAsset, out var live))
                    {
                        lock (live)
                        {
                            if (live.Open.HasValue)
                            {
                                liveCandle = new MiniAppController.OhlcCandle(
                                    live.Open.Value, live.High, live.Low, live.Close, live.TickCount, live.OpenTime);
                            }
                        }
                    }

                    int closedToTake = liveCandle != null ? limit - 1 : limit;
                    var closedCandles = ring.GetRecent(closedToTake);

                    if (liveCandle != null)
                    {
                        if (closedCandles.Length > 0 && closedCandles[^1].Timestamp >= liveCandle.Timestamp)
                        {
                            return closedCandles.Length > limit
                                ? closedCandles[^limit..]
                                : closedCandles;
                        }

                        var combined = new MiniAppController.OhlcCandle[closedCandles.Length + 1];
                        Array.Copy(closedCandles, 0, combined, 0, closedCandles.Length);
                        combined[^1] = liveCandle;
                        return combined;
                    }
                    return closedCandles;
                }

                using var conn = ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnection();
                await conn.OpenAsync();
                
                var records = (await Dapper.SqlMapper.QueryAsync(conn, @"
                    SELECT open_time as ""OpenTime"", open_price as ""Open"", high_price as ""High"", low_price as ""Low"", close_price as ""Close"", volume as ""Volume""
                    FROM subminute_candles
                    WHERE asset = @Asset AND interval = @Interval
                    ORDER BY open_time DESC
                    LIMIT @Limit;
                ", new { Asset = cleanAsset, Interval = dbInterval, Limit = limit })).ToList();

                ConcurrentDictionary<string, CandleAccumulator>? targetDict = interval switch
                {
                    "s5" => _s5, "s10" => _s10, "s15" => _s15, "s30" => _s30, "m1" => _m1, "1min" => _m1, _ => null
                };

                DateTime? liveOpenTime = null;
                MiniAppController.OhlcCandle? liveLastClosed = null;
                MiniAppController.OhlcCandle? currentLiveCandle = null;

                if (targetDict != null && targetDict.TryGetValue(cleanAsset, out var acc))
                {
                    lock (acc)
                    {
                        if (acc.Open.HasValue)
                        {
                            liveOpenTime = acc.OpenTime.ToUniversalTime();
                            liveLastClosed = acc.LastClosed;
                            currentLiveCandle = new MiniAppController.OhlcCandle(
                                acc.Open.Value, acc.High, acc.Low, acc.Close, acc.TickCount, acc.OpenTime);
                        }
                    }
                }

                MiniAppController.OhlcCandle? missedClosed = null;
                if (liveOpenTime.HasValue)
                {
                    long openTicks = liveOpenTime.Value.Ticks;
                    records.RemoveAll(r =>
                    {
                        DateTime dbTime = r.OpenTime is string s
                            ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                            : Convert.ToDateTime(r.OpenTime).ToUniversalTime();
                        return dbTime.Ticks == openTicks;
                    });

                    if (liveLastClosed != null)
                    {
                        long lastClosedTicks = liveLastClosed.Timestamp.ToUniversalTime().Ticks;
                        bool found = false;
                        foreach (var r in records)
                        {
                            DateTime dbTime = r.OpenTime is string s
                                ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                                : Convert.ToDateTime(r.OpenTime).ToUniversalTime();
                            if (dbTime.Ticks == lastClosedTicks) { found = true; break; }
                        }
                        if (!found) missedClosed = liveLastClosed;
                    }
                }

                int totalCount = records.Count + (currentLiveCandle != null ? 1 : 0) + (missedClosed != null ? 1 : 0);
                int resultSize = Math.Min(limit, totalCount);
                var result = new MiniAppController.OhlcCandle[resultSize];
                
                int resultIdx = 0;
                int dbRecordsToTake = Math.Min(records.Count, resultSize - (currentLiveCandle != null ? 1 : 0) - (missedClosed != null ? 1 : 0));
                
                for (int i = dbRecordsToTake - 1; i >= 0; i--)
                {
                    var r = records[i];
                    DateTime parsedTime = r.OpenTime is string s
                        ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                        : Convert.ToDateTime(r.OpenTime);

                    result[resultIdx++] = new MiniAppController.OhlcCandle(
                        Convert.ToDouble(r.Open), 
                        Convert.ToDouble(r.High), 
                        Convert.ToDouble(r.Low), 
                        Convert.ToDouble(r.Close), 
                        Convert.ToDouble(r.Volume),
                        parsedTime);
                }

                if (missedClosed != null && resultIdx < resultSize)
                {
                    result[resultIdx++] = missedClosed;
                }
                if (currentLiveCandle != null && resultIdx < resultSize)
                {
                    result[resultIdx++] = currentLiveCandle;
                }

                if (resultIdx < resultSize)
                {
                    Array.Resize(ref result, resultIdx);
                }

                _recentCandlesCache[cacheKey] = (DateTime.UtcNow.Ticks, limit, result);
                return result;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[RealtimeTickCollector] Failed to fetch recent {interval} candles for {asset}: {ex.Message}");
                return Array.Empty<MiniAppController.OhlcCandle>();
            }
        }

        public static Task OnPriceUpdateAsync(string asset, double price, long tickTimeUtc, bool isBackfill = false)
        {
            // Data Integrity Guard: reject poisoned ticks (NaN, Infinity, non-positive price or invalid time)
            if (double.IsNaN(price) || double.IsInfinity(price) || price <= 0 || tickTimeUtc <= 0)
            {
                return Task.CompletedTask;
            }

            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
            long nowTicks = tickTimeUtc;

            QueueTick(cleanAsset, "s5", price, nowTicks, TimeSpan.FromSeconds(5).Ticks);
            QueueTick(cleanAsset, "s10", price, nowTicks, TimeSpan.FromSeconds(10).Ticks);
            QueueTick(cleanAsset, "s15", price, nowTicks, TimeSpan.FromSeconds(15).Ticks);
            QueueTick(cleanAsset, "s30", price, nowTicks, TimeSpan.FromSeconds(30).Ticks);
            QueueTick(cleanAsset, "1min", price, nowTicks, TimeSpan.FromSeconds(60).Ticks);

            UpdateAccumulator(_s5,  cleanAsset, price, nowTicks, TimeSpan.FromSeconds(5).Ticks, isBackfill);
            UpdateAccumulator(_s10, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(10).Ticks, isBackfill);
            UpdateAccumulator(_s15, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(15).Ticks, isBackfill);
            UpdateAccumulator(_s30, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(30).Ticks, isBackfill);
            UpdateAccumulator(_m1, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(60).Ticks, isBackfill);
            
            return Task.CompletedTask;
        }

        public static bool IsAccumulatorCorrupted(string asset)
        {
            string cleanAsset = asset.ToUpper().Replace("/", "").Replace("-", "").Replace("_OTC", "");
            foreach (var dict in new[] { _s5, _s10, _s15, _s30, _m1 })
            {
                if (dict.TryGetValue(cleanAsset, out var acc))
                {
                    lock (acc)
                    {
                        if (acc.Open.HasValue && (double.IsNaN(acc.Open.Value) || double.IsInfinity(acc.Open.Value) || acc.Open.Value <= 0))
                            return true;
                        if (acc.Open.HasValue && (double.IsNaN(acc.Close) || double.IsInfinity(acc.Close) || acc.Close <= 0))
                            return true;
                    }
                }
            }
            return false;
        }

        private static void QueueTick(string asset, string interval, double price, long nowTicks, long intervalTicks)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            if (_tickChannel.Reader.Count >= ChannelCapacity)
            {
                long dropped = Interlocked.Increment(ref _droppedTickCount);
                var now = DateTime.UtcNow;
                if ((now - _lastDropWarningAt).TotalSeconds >= 30)
                {
                    _lastDropWarningAt = now;
                    BotLogger.Warn($"[TickCollector] Channel full ({ChannelCapacity}) — backpressure evicting ticks. Dropped count: {dropped}. PostgreSQL write pipeline is lagging.");
                }
            }

            if (!_tickChannel.Writer.TryWrite(new TickEvent(asset, interval, price, openTime)))
            {
                Interlocked.Increment(ref _droppedTickCount);
            }
        }

        private static void UpdateAccumulator(
            ConcurrentDictionary<string, CandleAccumulator> dict,
            string asset, double price, long nowTicks, long intervalTicks, bool isBackfill = false)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            var acc = dict.GetOrAdd(asset, _ => new CandleAccumulator());
            Action<string, string>? notifyCandleClosed = null;
            string? closedTf = null;

            lock (acc)
            {
                if (acc.OpenTime != default && openTime < acc.OpenTime)
                {
                    if ((acc.OpenTime - openTime) > TimeSpan.FromMinutes(5))
                        acc.Reset(openTime); // DST/timezone jump backwards >5m: reset accumulator
                    else
                        return; // late tick: never rewind the live candle
                }
                if (acc.OpenTime != openTime && acc.OpenTime != default)
                {
                    acc.Reset(openTime);
                    closedTf = dict == _s5 ? "s5" : dict == _s10 ? "s10" : dict == _s15 ? "s15" : dict == _s30 ? "s30" : "m1";
                    string closedDbInterval = closedTf == "m1" ? "1min" : closedTf;
                    if (acc.LastClosed != null)
                    {
                        var ring = _ringBuffers.GetOrAdd($"{asset}:{closedDbInterval}", _ => new CandleRingBuffer(400));
                        ring.Add(acc.LastClosed);
                    }
                    _recentCandlesCache.TryRemove($"{asset}:{closedDbInterval}", out _);
                    if (!isBackfill)
                        notifyCandleClosed = OnCandleClosed;
                }
                else if (acc.OpenTime == default)
                    acc.OpenTime = openTime;

                acc.AddTick(price);
            }

            if (notifyCandleClosed != null && closedTf != null)
            {
                try
                {
                    notifyCandleClosed.Invoke(asset, closedTf);
                }
                catch (Exception ex)
                {
                    BotLogger.Warn($"[TickCollector] OnCandleClosed notification error: {ex.Message}");
                }
            }
        }

        public static async Task StopAsync(CancellationToken ct = default)
        {
            try
            {
                _collectorCts.Cancel();
                _tickChannel.Writer.TryComplete();
                await FlushAsync(ct);
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[TickCollector] StopAsync notice: {ex.Message}");
            }
        }
    }
}
