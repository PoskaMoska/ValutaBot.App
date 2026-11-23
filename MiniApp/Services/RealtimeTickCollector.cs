using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
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
            public double CrowdSentiment { get; set; } = 50.0;
            public DateTime OpenTime { get; set; }
            public MiniAppController.OhlcCandle? LastClosed { get; set; }
            
            public void AddTick(double price, double crowdSentiment = 50.0)
            {
                if (!Open.HasValue) Open = price;
                if (price > High) High = price;
                if (price < Low) Low = price;
                Close = price;
                TickCount++;
                if (double.IsFinite(crowdSentiment) && crowdSentiment >= 0.0 && crowdSentiment <= 100.0)
                {
                    CrowdSentiment = crowdSentiment;
                }
            }
            
            public void Reset(DateTime openTime)
            {
                if (Open.HasValue) LastClosed = new MiniAppController.OhlcCandle(Open.Value, High, Low, Close, TickCount, OpenTime, CrowdSentiment);
                Open = null;
                High = double.MinValue;
                Low = double.MaxValue;
                Close = 0;
                TickCount = 0;
                OpenTime = openTime;
            }
        }

        private record struct TickEvent(string Asset, string Interval, double Price, DateTime OpenTime, double CrowdSentiment = 50.0, bool ArchiveOtcOnly = false);

        private sealed class CandleBatchAggregate
        {
            public required string Asset { get; init; }
            public required string Interval { get; init; }
            public required DateTime OpenTime { get; init; }
            public required bool ArchiveOtcOnly { get; init; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
            public int Volume { get; set; }
            public double CrowdSentiment { get; set; } = 50.0;
        }

        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s5  = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s10 = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s15 = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _s30 = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CandleAccumulator> _m1  = new(StringComparer.OrdinalIgnoreCase);

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
                var sorted = candles.OrderBy(c => c.Timestamp).ToList();
                lock (_lock)
                {
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

        internal readonly record struct LiveTickSample(long BrokerUtcTicks, double Price);

        private sealed class LiveTickRingBuffer
        {
            private const int MaxLiveTicksPerAsset = 3000;
            private readonly LiveTickSample[] _samples = new LiveTickSample[MaxLiveTicksPerAsset];
            private int _start;
            private int _count;
            private readonly object _lock = new();

            public void Add(long brokerUtcTicks, double price)
            {
                if (brokerUtcTicks <= 0 || price <= 1e-8 || !double.IsFinite(price)) return;

                lock (_lock)
                {
                    if (_count > 0)
                    {
                        int lastIdx = (_start + _count - 1) % MaxLiveTicksPerAsset;
                        long lastTicks = _samples[lastIdx].BrokerUtcTicks;
                        if (brokerUtcTicks < lastTicks) return;
                        if (brokerUtcTicks == lastTicks)
                        {
                            _samples[lastIdx] = new LiveTickSample(brokerUtcTicks, price);
                            return;
                        }
                    }

                    if (_count == MaxLiveTicksPerAsset)
                    {
                        _samples[_start] = new LiveTickSample(brokerUtcTicks, price);
                        _start = (_start + 1) % MaxLiveTicksPerAsset;
                    }
                    else
                    {
                        int idx = (_start + _count) % MaxLiveTicksPerAsset;
                        _samples[idx] = new LiveTickSample(brokerUtcTicks, price);
                        _count++;
                    }
                }
            }

            public LiveTickSample[] Snapshot()
            {
                lock (_lock)
                {
                    if (_count == 0) return Array.Empty<LiveTickSample>();
                    var result = new LiveTickSample[_count];
                    for (int i = 0; i < _count; i++)
                    {
                        result[i] = _samples[(_start + i) % MaxLiveTicksPerAsset];
                    }
                    return result;
                }
            }
        }

        private static readonly ConcurrentDictionary<string, LiveTickRingBuffer> _liveTickHistory = new(StringComparer.OrdinalIgnoreCase);

        // FIX 4 (2026-09-13): Bounded channel prevents unbounded memory growth during DB outages.
        // With CreateUnbounded, a PostgreSQL outage at ~100 ticks/sec fills ~30k events in 5min → OOM.
        // DropOldest: gaps in subminute_candles are acceptable; OOM is not.
        private const int ChannelCapacity = 100_000;
        private static Channel<TickEvent> _tickChannel = Channel.CreateBounded<TickEvent>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false   // multiple WS ticks write concurrently
            });

        private static int _droppedTickCount = 0;
        private static long _archivedOtcTicksQueued = 0;
        internal static long ArchivedOtcTicksQueued => Interlocked.Read(ref _archivedOtcTicksQueued);
        private static DateTime _lastDropWarningAt = DateTime.MinValue;

        private static int _isInitialized = 0;
        private static readonly SemaphoreSlim _dbWriteLock = new(1, 1);
        private static CancellationTokenSource _collectorCts = new();
        private static readonly List<TickEvent> _pendingBatchBuffer = new(8000);

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
            _recentCandlesCache.Clear();
            _liveTickHistory.Clear();
            ValutaBot.MiniApp.Services.AutoTradingScannerService.ResetLastScannedTimes();
            Interlocked.Exchange(ref _archivedOtcTicksQueued, 0);
            EnsureChannelOpen();
        }

        internal static void EnsureChannelOpen()
        {
            if (!_tickChannel.Reader.Completion.IsCompleted) return;

            bool acquired = false;
            try
            {
                acquired = _dbWriteLock.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            try
            {
                if (_tickChannel.Reader.Completion.IsCompleted)
                {
                    var oldCts = _collectorCts;
                    try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
                    _collectorCts = new CancellationTokenSource();
                    _tickChannel = Channel.CreateBounded<TickEvent>(new BoundedChannelOptions(ChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = false,
                        SingleWriter = false
                    });
                    Interlocked.Exchange(ref _isInitialized, 0);
                }
            }
            finally
            {
                if (acquired)
                {
                    try { _dbWriteLock.Release(); } catch { }
                }
            }
        }

        public static void ResetAssetAccumulator(string asset)
        {
            string cleanAsset = PriceFeed.CandleKey(asset);
            _s5.TryRemove(cleanAsset, out _);
            _s10.TryRemove(cleanAsset, out _);
            _s15.TryRemove(cleanAsset, out _);
            _s30.TryRemove(cleanAsset, out _);
            _m1.TryRemove(cleanAsset, out _);
            _liveTickHistory.TryRemove(cleanAsset, out _);
            foreach (var tf in new[] { "s5", "s10", "s15", "s30", "1min", "m1" })
            {
                _ringBuffers.TryRemove($"{cleanAsset}:{tf}", out _);
                _recentCandlesCache.TryRemove($"{cleanAsset}:{tf}", out _);
            }
        }

        public static void Initialize()
        {
            InitializeAsync().GetAwaiter().GetResult();
        }

        public static async Task PrewarmRingBuffersAsync()
        {
            try
            {
                await using var conn = await ValutaBot.App.MiniApp.Data.DbConnectionFactory.OpenConnectionAsync();
                
                var records = (await Dapper.SqlMapper.QueryAsync(conn, new CommandDefinition(@"
                    SELECT c.asset, c.interval, c.open_time as ""OpenTime"", c.open_price as ""Open"", c.high_price as ""High"", c.low_price as ""Low"", c.close_price as ""Close"", c.volume as ""Volume"", COALESCE(c.crowd_sentiment, 50.0) as ""CrowdSentiment""
                    FROM (
                        SELECT DISTINCT asset, interval 
                        FROM subminute_candles 
                        WHERE interval IN ('s5', 's10', 's15', 's30', '1min')
                    ) pairs
                    CROSS JOIN LATERAL (
                        SELECT open_time, open_price, high_price, low_price, close_price, volume, crowd_sentiment
                        FROM subminute_candles s
                        WHERE s.asset = pairs.asset AND s.interval = pairs.interval
                        ORDER BY open_time DESC
                        LIMIT 250
                    ) c
                    ORDER BY c.asset, c.interval, c.open_time ASC;", commandTimeout: 10))).ToList();

                int loaded = 0;
                foreach (var g in records.GroupBy(r => $"{r.asset}:{r.interval}"))
                {
                    var ring = _ringBuffers.GetOrAdd(g.Key, _ => new CandleRingBuffer(400));
                    var candles = g.Select(r =>
                    {
                        string intervalStr = (string)r.interval;
                        long intervalTicks = intervalStr switch
                        {
                            "s5" => TimeSpan.FromSeconds(5).Ticks,
                            "s10" => TimeSpan.FromSeconds(10).Ticks,
                            "s15" => TimeSpan.FromSeconds(15).Ticks,
                            "s30" => TimeSpan.FromSeconds(30).Ticks,
                            _ => TimeSpan.FromSeconds(60).Ticks
                        };
                        DateTime rawTime = r.OpenTime is string s
                            ? DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
                            : Convert.ToDateTime(r.OpenTime, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
                        DateTime alignedTime = new(rawTime.Ticks - (rawTime.Ticks % intervalTicks), DateTimeKind.Utc);
                        double crowd = r.CrowdSentiment != null ? Convert.ToDouble(r.CrowdSentiment, System.Globalization.CultureInfo.InvariantCulture) : 50.0;

                        return new MiniAppController.OhlcCandle(
                            Convert.ToDouble(r.Open, System.Globalization.CultureInfo.InvariantCulture),
                            Convert.ToDouble(r.High, System.Globalization.CultureInfo.InvariantCulture),
                            Convert.ToDouble(r.Low, System.Globalization.CultureInfo.InvariantCulture),
                            Convert.ToDouble(r.Close, System.Globalization.CultureInfo.InvariantCulture),
                            Convert.ToDouble(r.Volume, System.Globalization.CultureInfo.InvariantCulture),
                            alignedTime,
                            crowd
                        );
                    }).ToList();
                    
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
        public static void PrepareForInProcessRestart()
        {
            bool acquired = false;
            try
            {
                acquired = _dbWriteLock.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }

            try
            {
                Interlocked.Exchange(ref _isInitialized, 0);
                if (acquired)
                {
                    _pendingBatchBuffer.Clear();
                }
                var oldCts = _collectorCts;
                try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
                _tickChannel.Writer.TryComplete();
                _collectorCts = new CancellationTokenSource();
                _tickChannel = Channel.CreateBounded<TickEvent>(new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = false,
                    SingleWriter = false
                });
            }
            finally
            {
                if (acquired)
                {
                    try { _dbWriteLock.Release(); } catch { }
                }
            }
        }

        public static async Task<int> FlushAsync(CancellationToken ct = default)
        {
            // D3 FIX: Wrap timeoutToken in using to prevent timer leak on every flush (~3/sec)
            using var timeoutToken = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
                var drainList = _pendingBatchBuffer;
                while (_tickChannel.Reader.TryRead(out var tick))
                {
                    drainList.Add(tick);
                }

                if (drainList.Count == 0) return 0;

                var (ok, _) = await WriteBatchInternalAsync(drainList, linkedCts.Token);
                if (ok)
                {
                    int flushedCount = drainList.Count;
                    drainList.Clear();
                    BotLogger.Info($"[TickCollector] Flushed {flushedCount} buffered ticks to PostgreSQL.");
                    return flushedCount;
                    // drained and cleared above
                }
                return 0;
            }
            finally
            {
                try { _dbWriteLock.Release(); } catch { }
            }
        }

        private static async Task<(bool Success, bool IsTransient)> WriteBatchInternalAsync(List<TickEvent> buffer, CancellationToken ct = default)
        {
            if (buffer.Count == 0) return (true, false);

            try
            {
                await using var conn = await DbConnectionFactory.OpenConnectionAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                
                // Single-pass O(N) aggregation: eliminates nested LINQ GroupBy/OrderBy/ToList allocations
                // and guarantees correct Open (chronologically first tick) and Close (chronologically last tick).
                var map = new Dictionary<(string Asset, string Interval, DateTime OpenTime, bool ArchiveOtcOnly), CandleBatchAggregate>();
                foreach (var tick in buffer)
                {
                    var key = (tick.Asset, tick.Interval, tick.OpenTime, tick.ArchiveOtcOnly);
                    if (!map.TryGetValue(key, out var agg))
                    {
                        agg = new CandleBatchAggregate
                        {
                            Asset = tick.Asset,
                            Interval = tick.Interval,
                            OpenTime = tick.OpenTime,
                            ArchiveOtcOnly = tick.ArchiveOtcOnly,
                            Open = tick.Price,
                            High = tick.Price,
                            Low = tick.Price,
                            Close = tick.Price,
                            Volume = 1,
                            CrowdSentiment = tick.CrowdSentiment
                        };
                        map[key] = agg;
                    }
                    else
                    {
                        if (tick.Price > agg.High) agg.High = tick.Price;
                        if (tick.Price < agg.Low) agg.Low = tick.Price;
                        agg.Close = tick.Price;
                        agg.Volume++;
                        agg.CrowdSentiment = tick.CrowdSentiment;
                    }
                }

                // Гарантируем строгий детерминированный порядок строк для исключения взаимных блокировок (Deadlocks)
                var grouped = map.Values
                    .OrderBy(g => g.ArchiveOtcOnly)
                    .ThenBy(g => g.Asset, StringComparer.Ordinal)
                    .ThenBy(g => g.Interval, StringComparer.Ordinal)
                    .ThenBy(g => g.OpenTime)
                    .ToList();

                // Pipeline all grouped records in a single network round-trip via NpgsqlBatch
                await using var batch = new NpgsqlBatch(conn, tx) { Timeout = 8 };
                const string batchSql = @"
                    INSERT INTO subminute_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume, crowd_sentiment)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                    ON CONFLICT (asset, interval, open_time) DO UPDATE SET
                        high_price      = GREATEST(subminute_candles.high_price,  EXCLUDED.high_price),
                        low_price       = LEAST(subminute_candles.low_price,      EXCLUDED.low_price),
                        close_price     = EXCLUDED.close_price,
                        volume          = subminute_candles.volume + EXCLUDED.volume,
                        crowd_sentiment = EXCLUDED.crowd_sentiment;";
                // Long-retention OTC s5 archive (weekend archive + parallel weekday *_OTC research stream)
                const string weekendBatchSql = @"
                    INSERT INTO weekend_otc_candles (asset, interval, open_time, open_price, high_price, low_price, close_price, volume, crowd_sentiment)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                    ON CONFLICT (asset, interval, open_time) DO UPDATE SET
                        high_price      = GREATEST(weekend_otc_candles.high_price,  EXCLUDED.high_price),
                        low_price       = LEAST(weekend_otc_candles.low_price,      EXCLUDED.low_price),
                        close_price     = EXCLUDED.close_price,
                        volume          = weekend_otc_candles.volume + EXCLUDED.volume,
                        crowd_sentiment = EXCLUDED.crowd_sentiment;";

                static void AppendCandleCmd(NpgsqlBatch targetBatch, string sql, CandleBatchAggregate item)
                {
                    var cmd = new NpgsqlBatchCommand(sql);
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.Asset });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.Interval });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.OpenTime.ToString("o") });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.Open });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.High });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.Low });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.Close });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = (double)item.Volume });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = item.CrowdSentiment });
                    targetBatch.BatchCommands.Add(cmd);
                }

                foreach (var item in grouped)
                {
                    if (!item.ArchiveOtcOnly)
                    {
                        AppendCandleCmd(batch, batchSql, item);
                        if (string.Equals(item.Interval, "s5", StringComparison.OrdinalIgnoreCase)
                            && ValutaBot.MiniApp.Services.WeekendShadowExperimentService.IsWeekendOtcWindowUtc(item.OpenTime))
                        {
                            AppendCandleCmd(batch, weekendBatchSql, item);
                        }
                    }
                    else
                    {
                        AppendCandleCmd(batch, weekendBatchSql, item);
                    }
                }

                await batch.ExecuteNonQueryAsync(ct);
                
                await tx.CommitAsync(ct);
                return (true, false);
            }
            catch (Exception ex)
            {
                bool isReadOnlyOrDiskFull = DbConnectionFactory.IsReadOnlyOrDiskFull(ex);
                bool isTransient = DbConnectionFactory.IsNetworkTransient(ex) || isReadOnlyOrDiskFull;
                BotLogger.Warn($"[TickSync] Failed to batch save ticks (isTransient={isTransient}, isDiskFull={isReadOnlyOrDiskFull}): {ex.Message}. Retrying...");
                return (false, isTransient);
            }
        }

        private static async Task ProcessTickQueueAsync()
        {
            var buffer = _pendingBatchBuffer;
            var token = _collectorCts.Token;
            int consecutiveBatchFailures = 0;

            try
            {
                while (!token.IsCancellationRequested && (buffer.Count > 0 || await _tickChannel.Reader.WaitToReadAsync(token)))
                {
                    if (buffer.Count == 0 && _tickChannel.Reader.Count < 200)
                    {
                        // Wait for micro-batching window while keeping ticks inside _tickChannel so FlushAsync() can drain them immediately
                        try { await Task.Delay(250, token); } catch (OperationCanceledException) { }
                    }

                    await _dbWriteLock.WaitAsync(token);
                    try
                    {
                        while (buffer.Count < 8000 && _tickChannel.Reader.TryRead(out var tick))
                        {
                            buffer.Add(tick);
                        }
                        if (buffer.Count > 0)
                        {
                            var (batchOk, isTransient) = await WriteBatchInternalAsync(buffer, token);
                            if (batchOk)
                            {
                                buffer.Clear();
                                consecutiveBatchFailures = 0;
                            }
                            else if (isTransient)
                            {
                                // Cluster restart or temporary network blink: do NOT count towards poisoned threshold!
                                BotLogger.Warn($"[TickCollector] Transient DB disconnect / disk read-only during batch write. Preserving {buffer.Count} ticks in buffer for cluster recovery.");
                                try { await Task.Delay(500, token); } catch (OperationCanceledException) { }
                            }
                            else
                            {
                                consecutiveBatchFailures++;
                                if (consecutiveBatchFailures >= 5)
                                {
                                    BotLogger.Error($"[TickCollector] Batch write failed {consecutiveBatchFailures} times consecutively with non-transient error. Discarding {buffer.Count} poisoned ticks to restore channel ingestion.");
                                    buffer.Clear();
                                    consecutiveBatchFailures = 0;
                                }
                            }
                        }
                    }
                    finally
                    {
                        _dbWriteLock.Release();
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
                        bool acquired = await _dbWriteLock.WaitAsync(TimeSpan.FromSeconds(3));
                        if (acquired)
                        {
                            try { await WriteBatchInternalAsync(buffer, CancellationToken.None); }
                            finally { _dbWriteLock.Release(); }
                        }
                    }
                    await FlushAsync(CancellationToken.None);
                }
                catch { }
            }
        }

        private static readonly ConcurrentDictionary<string, (long TimestampTicks, int Limit, MiniAppController.OhlcCandle[] Candles)> _recentCandlesCache = new(StringComparer.OrdinalIgnoreCase);

        private static MiniAppController.OhlcCandle ParseDbCandleRow(dynamic r, long intervalTicks)
        {
            DateTime rawTime = r.OpenTime is string s
                ? DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
                : Convert.ToDateTime(r.OpenTime, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
            DateTime alignedTime = new(rawTime.Ticks - (rawTime.Ticks % intervalTicks), DateTimeKind.Utc);
            double crowd = r.CrowdSentiment != null
                ? Convert.ToDouble(r.CrowdSentiment, System.Globalization.CultureInfo.InvariantCulture)
                : 50.0;

            return new MiniAppController.OhlcCandle(
                Convert.ToDouble(r.Open, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(r.High, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(r.Low, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(r.Close, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(r.Volume, System.Globalization.CultureInfo.InvariantCulture),
                alignedTime,
                crowd);
        }

        internal static MiniAppController.OhlcCandle[] MergeDbCandlesWithLive(
            IEnumerable<MiniAppController.OhlcCandle> dbCandles,
            IEnumerable<MiniAppController.OhlcCandle> ringTail,
            MiniAppController.OhlcCandle? liveLastClosed,
            MiniAppController.OhlcCandle? currentLiveCandle,
            DateTime? liveOpenTimeUtc,
            int limit)
        {
            if (limit <= 0) return Array.Empty<MiniAppController.OhlcCandle>();

            var byTick = new SortedDictionary<long, MiniAppController.OhlcCandle>();
            foreach (var c in dbCandles)
            {
                byTick.TryAdd(c.Timestamp.ToUniversalTime().Ticks, c);
            }

            foreach (var c in ringTail)
            {
                byTick.TryAdd(c.Timestamp.ToUniversalTime().Ticks, c);
            }

            if (liveLastClosed != null)
            {
                byTick.TryAdd(liveLastClosed.Timestamp.ToUniversalTime().Ticks, liveLastClosed);
            }

            if (liveOpenTimeUtc.HasValue)
            {
                byTick.Remove(liveOpenTimeUtc.Value.ToUniversalTime().Ticks);
            }

            if (currentLiveCandle != null)
            {
                long liveTicks = currentLiveCandle.Timestamp.ToUniversalTime().Ticks;
                if (byTick.Count == 0 || liveTicks >= byTick.Keys.Last())
                {
                    byTick[liveTicks] = currentLiveCandle;
                }
            }

            if (byTick.Count == 0) return Array.Empty<MiniAppController.OhlcCandle>();

            int count = Math.Min(limit, byTick.Count);
            int skip = byTick.Count - count;
            var result = new MiniAppController.OhlcCandle[count];
            int idx = 0;
            int i = 0;
            foreach (var kv in byTick)
            {
                if (i++ < skip) continue;
                result[idx++] = kv.Value;
            }
            return result;
        }

        public static async Task<MiniAppController.OhlcCandle[]> GetRecentCandles(string asset, string interval, int limit)
        {
            string cleanAsset = PriceFeed.CandleKey(asset);
            string dbInterval = interval == "m1" ? "1min" : interval;
            string cacheKey = $"{cleanAsset}:{dbInterval}";
            try
            {
                long intervalTicks = dbInterval switch
                {
                    "s5" => TimeSpan.FromSeconds(5).Ticks,
                    "s10" => TimeSpan.FromSeconds(10).Ticks,
                    "s15" => TimeSpan.FromSeconds(15).Ticks,
                    "s30" => TimeSpan.FromSeconds(30).Ticks,
                    _ => TimeSpan.FromSeconds(60).Ticks
                };
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
                bool hasRing = _ringBuffers.TryGetValue(cacheKey, out var ring);

                ConcurrentDictionary<string, CandleAccumulator>? tDict = interval switch
                {
                    "s5" => _s5, "s10" => _s10, "s15" => _s15, "s30" => _s30, "m1" => _m1, "1min" => _m1, _ => null
                };

                MiniAppController.OhlcCandle? liveCandle = null;
                if (tDict != null && tDict.TryGetValue(cleanAsset, out var live))
                {
                    // D1 FIX: Lock accumulator only to snapshot scalar fields (fast, <10ns).
                    // Never call ring.GetRecent() inside lock(live) to avoid nested lock contention
                    // with incoming WebSocket ticks.
                    lock (live)
                    {
                        if (live.Open.HasValue)
                        {
                            liveCandle = new MiniAppController.OhlcCandle(
                                live.Open.Value, live.High, live.Low, live.Close, live.TickCount, live.OpenTime, live.CrowdSentiment);
                        }
                    }
                }

                bool hasDb = !string.IsNullOrEmpty(ValutaBot.App.MiniApp.Data.DbConnectionFactory.GetConnectionString());

                if (hasRing && ring != null && (ring.Count > 0 || liveCandle != null))
                {
                    int closedToTake = liveCandle != null ? Math.Max(0, limit - 1) : limit;
                    var closedCandles = closedToTake > 0
                        ? ring.GetRecent(closedToTake)
                        : Array.Empty<MiniAppController.OhlcCandle>();

                    // Only serve purely from the 400-bar ring buffer when it satisfies the requested limit
                    // (or when no PostgreSQL DB is configured, e.g. unit tests / offline mode).
                    // Larger requests (1,500-14,400 m1 bars for M30/H1/H4) fall through to SQL.
                    if (!hasDb || closedCandles.Length >= closedToTake)
                    {
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
                }

                if (!hasDb && !hasRing && liveCandle != null)
                {
                    return new[] { liveCandle };
                }

                if (!hasDb)
                {
                    return Array.Empty<MiniAppController.OhlcCandle>();
                }

                await using var conn = await ValutaBot.App.MiniApp.Data.DbConnectionFactory.OpenConnectionAsync();
                int cmdTimeout = limit > 1000 ? 8 : 4;

                var records = (await Dapper.SqlMapper.QueryAsync(conn, new CommandDefinition(@"
                    SELECT open_time as ""OpenTime"", open_price as ""Open"", high_price as ""High"", low_price as ""Low"", close_price as ""Close"", volume as ""Volume"", COALESCE(crowd_sentiment, 50.0) as ""CrowdSentiment""
                    FROM subminute_candles
                    WHERE asset = @Asset AND interval = @Interval
                    ORDER BY open_time DESC
                    LIMIT @Limit;
                ", new { Asset = cleanAsset, Interval = dbInterval, Limit = limit }, commandTimeout: cmdTimeout))).ToList();

                var dbCandles = new List<MiniAppController.OhlcCandle>(records.Count);
                foreach (var r in records)
                {
                    dbCandles.Add(ParseDbCandleRow(r, intervalTicks));
                }

                // For deeper M30/H1/H4 history on 1min, top up older bars from historical_candles if subminute_candles was pruned.
                if (dbInterval == "1min" && dbCandles.Count < limit)
                {
                    int missingLimit = limit - dbCandles.Count;
                    string? oldestCutoff = dbCandles.Count > 0
                        ? dbCandles.Min(c => c.Timestamp).AddMinutes(1).ToString("O")
                        : null;

                    string histSql = oldestCutoff != null
                        ? @"
                            SELECT open_time as ""OpenTime"", open as ""Open"", high as ""High"", low as ""Low"", close as ""Close"", volume as ""Volume"", 50.0 as ""CrowdSentiment""
                            FROM historical_candles
                            WHERE asset = @Asset AND interval = '1m' AND open_time < @OldestCutoff
                            ORDER BY open_time DESC
                            LIMIT @MissingLimit;"
                        : @"
                            SELECT open_time as ""OpenTime"", open as ""Open"", high as ""High"", low as ""Low"", close as ""Close"", volume as ""Volume"", 50.0 as ""CrowdSentiment""
                            FROM historical_candles
                            WHERE asset = @Asset AND interval = '1m'
                            ORDER BY open_time DESC
                            LIMIT @MissingLimit;";

                    var histRecords = (await Dapper.SqlMapper.QueryAsync(conn, new CommandDefinition(
                        histSql,
                        new { Asset = cleanAsset, OldestCutoff = oldestCutoff, MissingLimit = missingLimit + 4 },
                        commandTimeout: cmdTimeout))).ToList();

                    if (histRecords.Count > 0)
                    {
                        var seenTicks = new HashSet<long>(dbCandles.Select(c => c.Timestamp.Ticks));
                        foreach (var r in histRecords)
                        {
                            var hc = ParseDbCandleRow(r, intervalTicks);
                            if (seenTicks.Add(hc.Timestamp.Ticks))
                            {
                                dbCandles.Add(hc);
                            }
                        }
                    }
                }

                dbCandles.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

                // Warm up the 400-bar ring buffer with only the newest tail so future short queries stay in-memory
                if (dbCandles.Count > 0)
                {
                    var ringToWarm = _ringBuffers.GetOrAdd(cacheKey, _ => new CandleRingBuffer(400));
                    int warmStart = Math.Max(0, dbCandles.Count - 400);
                    ringToWarm.AddRange(dbCandles.Skip(warmStart));
                }

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
                                acc.Open.Value, acc.High, acc.Low, acc.Close, acc.TickCount, acc.OpenTime, acc.CrowdSentiment);
                        }
                    }
                }

                var ringTail = hasRing && ring != null ? ring.GetRecent(16) : Array.Empty<MiniAppController.OhlcCandle>();
                var result = MergeDbCandlesWithLive(dbCandles, ringTail, liveLastClosed, currentLiveCandle, liveOpenTime, limit);

                _recentCandlesCache[cacheKey] = (DateTime.UtcNow.Ticks, limit, result);
                return result;
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[RealtimeTickCollector] Failed to fetch recent {interval} candles for {asset}: {ex.Message}");
                ConcurrentDictionary<string, CandleAccumulator>? fbDict = interval switch
                {
                    "s5" => _s5, "s10" => _s10, "s15" => _s15, "s30" => _s30, "m1" => _m1, "1min" => _m1, _ => null
                };
                if (_ringBuffers.TryGetValue(cacheKey, out var ringFallback) || fbDict != null)
                {
                    MiniAppController.OhlcCandle? liveCandle = null;
                    if (fbDict != null && fbDict.TryGetValue(cleanAsset, out var live))
                    {
                        lock (live)
                        {
                            if (live.Open.HasValue)
                            {
                                liveCandle = new MiniAppController.OhlcCandle(
                                    live.Open.Value, live.High, live.Low, live.Close, live.TickCount, live.OpenTime, live.CrowdSentiment);
                            }
                        }
                    }
                    var closed = ringFallback != null ? ringFallback.GetRecent(liveCandle != null ? limit - 1 : limit) : Array.Empty<MiniAppController.OhlcCandle>();
                    if (liveCandle != null)
                    {
                        var combined = new MiniAppController.OhlcCandle[closed.Length + 1];
                        Array.Copy(closed, 0, combined, 0, closed.Length);
                        combined[^1] = liveCandle;
                        return combined;
                    }
                    if (closed.Length > 0) return closed;
                }
                return Array.Empty<MiniAppController.OhlcCandle>();
            }
        }

        public static Task OnPriceUpdateAsync(string asset, double price, long tickTimeUtc, bool isBackfill = false, double crowdSentiment = 50.0)
        {
            // Data Integrity Guard: reject poisoned ticks (NaN, Infinity, non-positive price or invalid time)
            long nowUtcTicks = DateTime.UtcNow.Ticks;
            if (!PriceFeed.IsValidTick(price, tickTimeUtc, nowUtcTicks, isBackfill))
            {
                return Task.CompletedTask;
            }

            string cleanAsset = PriceFeed.CandleKey(asset);
            long nowTicks = tickTimeUtc;

            if (!isBackfill)
            {
                RecordLiveTick(cleanAsset, nowTicks, price);
            }

            QueueTick(cleanAsset, "s5", price, nowTicks, TimeSpan.FromSeconds(5).Ticks, crowdSentiment);
            QueueTick(cleanAsset, "s10", price, nowTicks, TimeSpan.FromSeconds(10).Ticks, crowdSentiment);
            QueueTick(cleanAsset, "s15", price, nowTicks, TimeSpan.FromSeconds(15).Ticks, crowdSentiment);
            QueueTick(cleanAsset, "s30", price, nowTicks, TimeSpan.FromSeconds(30).Ticks, crowdSentiment);
            QueueTick(cleanAsset, "1min", price, nowTicks, TimeSpan.FromSeconds(60).Ticks, crowdSentiment);

            UpdateAccumulator(_s5,  cleanAsset, price, nowTicks, TimeSpan.FromSeconds(5).Ticks, isBackfill, crowdSentiment);
            UpdateAccumulator(_s10, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(10).Ticks, isBackfill, crowdSentiment);
            UpdateAccumulator(_s15, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(15).Ticks, isBackfill, crowdSentiment);
            UpdateAccumulator(_s30, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(30).Ticks, isBackfill, crowdSentiment);
            UpdateAccumulator(_m1, cleanAsset, price, nowTicks, TimeSpan.FromSeconds(60).Ticks, isBackfill, crowdSentiment);
            
            return Task.CompletedTask;
        }

        private static void RecordLiveTick(string cleanAsset, long brokerUtcTicks, double price)
        {
            var ring = _liveTickHistory.GetOrAdd(cleanAsset, static _ => new LiveTickRingBuffer());
            ring.Add(brokerUtcTicks, price);
        }

        /// <summary>
        /// Looks up the exact live broker quote held at targetUtc (last recorded tick at or before targetUtc)
        /// from the in-memory tick ring buffer. Returns false if the buffer does not cover targetUtc or
        /// if the feed was disconnected across targetUtc, so the caller can fall back to s5 candles.
        /// </summary>
        public static bool TryGetTickPriceAtOrBefore(
            string asset,
            DateTime targetUtc,
            bool streamIsFlowingNow,
            out double price,
            double maxStaleTickAgeSec = 30.0)
        {
            string cleanAsset = PriceFeed.CandleKey(asset);
            if (!_liveTickHistory.TryGetValue(cleanAsset, out var ring))
            {
                price = 0;
                return false;
            }

            long targetTicks = (targetUtc.Kind == DateTimeKind.Utc ? targetUtc : targetUtc.ToUniversalTime()).Ticks;
            var snapshot = ring.Snapshot();
            return TryFindTickPriceAtOrBefore(snapshot, targetTicks, streamIsFlowingNow, out price, maxStaleTickAgeSec);
        }

        internal static bool TryFindTickPriceAtOrBefore(
            ReadOnlySpan<LiveTickSample> samples,
            long targetUtcTicks,
            bool streamIsFlowingNow,
            out double price,
            double maxStaleTickAgeSec = 30.0)
        {
            price = 0;
            if (samples.IsEmpty || samples[0].BrokerUtcTicks > targetUtcTicks)
            {
                return false;
            }

            int lo = 0;
            int hi = samples.Length - 1;
            int idx = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (samples[mid].BrokerUtcTicks <= targetUtcTicks)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (idx < 0) return false;

            var chosen = samples[idx];
            if (chosen.Price <= 1e-8 || !double.IsFinite(chosen.Price)) return false;

            double ageBeforeTargetSec = (targetUtcTicks - chosen.BrokerUtcTicks) / (double)TimeSpan.TicksPerSecond;
            if (ageBeforeTargetSec > maxStaleTickAgeSec) return false;

            bool hasNewerTickAfterTarget = idx + 1 < samples.Length;
            if (!hasNewerTickAfterTarget && !streamIsFlowingNow) return false;

            price = chosen.Price;
            return true;
        }

        internal readonly record struct SubsecondTickMetrics(
            int Ticks1_5s,
            int Ticks5s,
            int Ticks15s,
            double AccelRatio1_5s,
            double Ret1_5sBps,
            double Ret5sBps,
            double DirPurity5s,
            double LastTickAgeMs)
        {
            public static readonly SubsecondTickMetrics Empty = new(0, 0, 0, 0.0, 0.0, 0.0, 0.0, -1.0);
        }

        /// <summary>
        /// Computes compact sub-second microstructure telemetry (tick arrival burst, 1.5s/5s return,
        /// directional path purity, and tick staleness) purely from the in-memory live tick ring buffer.
        /// </summary>
        internal static SubsecondTickMetrics ComputeSubsecondTickMetrics(string asset, DateTime refUtc)
        {
            string cleanAsset = PriceFeed.CandleKey(asset);
            if (!_liveTickHistory.TryGetValue(cleanAsset, out var ring))
                return SubsecondTickMetrics.Empty;

            var snapshot = ring.Snapshot();
            long refTicks = (refUtc.Kind == DateTimeKind.Utc ? refUtc : refUtc.ToUniversalTime()).Ticks;
            return ComputeSubsecondMetricsFromSamples(snapshot, refTicks);
        }

        internal static SubsecondTickMetrics ComputeSubsecondMetricsFromSamples(
            ReadOnlySpan<LiveTickSample> samples,
            long refUtcTicks)
        {
            if (samples.IsEmpty)
                return SubsecondTickMetrics.Empty;

            long maxAllowedFutureTicks = refUtcTicks + (5L * TimeSpan.TicksPerSecond);
            int endIdx = samples.Length - 1;
            while (endIdx >= 0 && samples[endIdx].BrokerUtcTicks > maxAllowedFutureTicks)
            {
                endIdx--;
            }
            if (endIdx < 0)
                return SubsecondTickMetrics.Empty;

            var latest = samples[endIdx];
            if (latest.Price <= 1e-8 || !double.IsFinite(latest.Price))
                return SubsecondTickMetrics.Empty;

            long windowAnchorTicks = (latest.BrokerUtcTicks > refUtcTicks && (latest.BrokerUtcTicks - refUtcTicks) <= TimeSpan.TicksPerSecond)
                ? latest.BrokerUtcTicks
                : refUtcTicks;
            double lastTickAgeMs = Math.Round(
                Math.Max(0.0, (refUtcTicks - latest.BrokerUtcTicks) / (double)TimeSpan.TicksPerMillisecond),
                1);

            long win1_5Ticks = TimeSpan.FromSeconds(1.5).Ticks;
            long win5Ticks = TimeSpan.FromSeconds(5.0).Ticks;
            long win15Ticks = TimeSpan.FromSeconds(15.0).Ticks;
            long cutoff1_5 = windowAnchorTicks - win1_5Ticks;
            long cutoff5 = windowAnchorTicks - win5Ticks;
            long cutoff15 = windowAnchorTicks - win15Ticks;
            long stopLookback = cutoff5 - TimeSpan.FromSeconds(20.0).Ticks;
            if (cutoff15 < stopLookback) stopLookback = cutoff15;

            int ticks1_5 = 0;
            int ticks5 = 0;
            int ticks15 = 0;
            int firstIdxIn1_5 = -1;
            int firstIdxIn5 = -1;
            int idxBefore1_5 = -1;
            int idxBefore5 = -1;

            for (int i = endIdx; i >= 0; i--)
            {
                long t = samples[i].BrokerUtcTicks;
                if (t >= cutoff1_5)
                {
                    ticks1_5++;
                    firstIdxIn1_5 = i;
                }
                else if (idxBefore1_5 < 0)
                {
                    idxBefore1_5 = i;
                }

                if (t >= cutoff5)
                {
                    ticks5++;
                    firstIdxIn5 = i;
                }
                else if (idxBefore5 < 0)
                {
                    idxBefore5 = i;
                }

                if (t >= cutoff15)
                {
                    ticks15++;
                }

                if (t < stopLookback && idxBefore5 >= 0)
                {
                    break;
                }
            }

            static double? ResolveBaseline(
                ReadOnlySpan<LiveTickSample> s,
                int end,
                int idxBefore,
                int firstInWin,
                long cutoff,
                long winLenTicks,
                long maxStaleBaselineTicks)
            {
                if (idxBefore >= 0 && (cutoff - s[idxBefore].BrokerUtcTicks) <= maxStaleBaselineTicks)
                {
                    double px = s[idxBefore].Price;
                    if (px > 1e-8 && double.IsFinite(px)) return px;
                }
                if (firstInWin >= 0 && firstInWin < end && (s[end].BrokerUtcTicks - s[firstInWin].BrokerUtcTicks) >= (winLenTicks / 2))
                {
                    double px = s[firstInWin].Price;
                    if (px > 1e-8 && double.IsFinite(px)) return px;
                }
                return null;
            }

            double? base1_5 = ResolveBaseline(samples, endIdx, idxBefore1_5, firstIdxIn1_5, cutoff1_5, win1_5Ticks, TimeSpan.FromSeconds(15.0).Ticks);
            double? base5 = ResolveBaseline(samples, endIdx, idxBefore5, firstIdxIn5, cutoff5, win5Ticks, TimeSpan.FromSeconds(20.0).Ticks);

            double ret1_5Bps = base1_5 is > 1e-8 ? Math.Round((latest.Price - base1_5.Value) / base1_5.Value * 10000.0, 3) : 0.0;
            double ret5Bps = base5 is > 1e-8 ? Math.Round((latest.Price - base5.Value) / base5.Value * 10000.0, 3) : 0.0;

            double rate1_5 = ticks1_5 / 1.5;
            double rate5 = ticks5 / 5.0;
            double accelRatio1_5 = rate5 > 0.0 ? Math.Round(rate1_5 / rate5, 3) : 0.0;

            double dirPurity5s = 0.0;
            if (ticks5 > 0 && firstIdxIn5 >= 0)
            {
                bool hasBaseline5 = idxBefore5 >= 0 && (cutoff5 - samples[idxBefore5].BrokerUtcTicks) <= TimeSpan.FromSeconds(20.0).Ticks
                                    && samples[idxBefore5].Price > 1e-8 && double.IsFinite(samples[idxBefore5].Price);
                if (hasBaseline5 || firstIdxIn5 < endIdx)
                {
                    double startPx = hasBaseline5 ? samples[idxBefore5].Price : samples[firstIdxIn5].Price;
                    int loopStart = hasBaseline5 ? firstIdxIn5 : firstIdxIn5 + 1;
                    double prevPx = startPx;
                    double pathLen = 0.0;
                    for (int i = loopStart; i <= endIdx; i++)
                    {
                        double p = samples[i].Price;
                        if (p > 1e-8 && double.IsFinite(p))
                        {
                            pathLen += Math.Abs(p - prevPx);
                            prevPx = p;
                        }
                    }
                    if (pathLen > 1e-12)
                    {
                        dirPurity5s = Math.Round(Math.Clamp((latest.Price - startPx) / pathLen, -1.0, 1.0), 3);
                    }
                }
            }

            return new SubsecondTickMetrics(
                Ticks1_5s: ticks1_5,
                Ticks5s: ticks5,
                Ticks15s: ticks15,
                AccelRatio1_5s: accelRatio1_5,
                Ret1_5sBps: ret1_5Bps,
                Ret5sBps: ret5Bps,
                DirPurity5s: dirPurity5s,
                LastTickAgeMs: lastTickAgeMs);
        }

        public static bool IsAccumulatorCorrupted(string asset)
        {
            string cleanAsset = PriceFeed.CandleKey(asset);
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

        /// <summary>
        /// Archives parallel *_OTC s5 ticks strictly into weekend_otc_candles (24/7 research dataset)
        /// without touching subminute_candles, in-memory ring buffers, or AutoTradingScanner events.
        /// </summary>
        public static void ArchiveOtcTick(string otcAssetKey, double price, long tickTimeUtc, bool isBackfill = false, double crowdSentiment = 50.0)
        {
            if (string.IsNullOrWhiteSpace(otcAssetKey)) return;
            long nowUtcTicks = DateTime.UtcNow.Ticks;
            if (!PriceFeed.IsValidTick(price, tickTimeUtc, nowUtcTicks, isBackfill)) return;

            long ticksPerBar = TimeSpan.FromSeconds(5).Ticks;
            var openTime = new DateTime(tickTimeUtc - (tickTimeUtc % ticksPerBar), DateTimeKind.Utc);
            double safeSentiment = double.IsFinite(crowdSentiment) && crowdSentiment >= 0.0 && crowdSentiment <= 100.0
                ? crowdSentiment
                : 50.0;
            EnqueueTickEvent(new TickEvent(otcAssetKey, "s5", price, openTime, safeSentiment, ArchiveOtcOnly: true));
        }

        private static void QueueTick(string asset, string interval, double price, long nowTicks, long intervalTicks, double crowdSentiment = 50.0)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            EnqueueTickEvent(new TickEvent(asset, interval, price, openTime, crowdSentiment));
        }

        private static void EnqueueTickEvent(in TickEvent ev)
        {
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

            if (!_tickChannel.Writer.TryWrite(ev))
            {
                if (_tickChannel.Reader.Completion.IsCompleted)
                {
                    EnsureChannelOpen();
                    if (_tickChannel.Writer.TryWrite(ev))
                    {
                        if (ev.ArchiveOtcOnly)
                        {
                            Interlocked.Increment(ref _archivedOtcTicksQueued);
                        }
                        return;
                    }
                }
                Interlocked.Increment(ref _droppedTickCount);
            }
            else if (ev.ArchiveOtcOnly)
            {
                Interlocked.Increment(ref _archivedOtcTicksQueued);
            }
        }

        private static void UpdateAccumulator(
            ConcurrentDictionary<string, CandleAccumulator> dict,
            string asset, double price, long nowTicks, long intervalTicks, bool isBackfill = false, double crowdSentiment = 50.0)
        {
            var openTime = new DateTime(nowTicks - (nowTicks % intervalTicks), DateTimeKind.Utc);
            var acc = dict.GetOrAdd(asset, _ => new CandleAccumulator());
            Action<string, string>? notifyCandleClosed = null;
            string? closedTf = null;
            string? closedDbInterval = null;
            MiniAppController.OhlcCandle? closedCandle = null;

            lock (acc)
            {
                if (acc.OpenTime != default && openTime < acc.OpenTime)
                {
                    return; // late or out-of-order tick: never rewind the live candle accumulator
                }
                if (acc.OpenTime != openTime && acc.OpenTime != default)
                {
                    acc.Reset(openTime);
                    closedTf = dict == _s5 ? "s5" : dict == _s10 ? "s10" : dict == _s15 ? "s15" : dict == _s30 ? "s30" : "m1";
                    closedDbInterval = closedTf == "m1" ? "1min" : closedTf;
                    closedCandle = acc.LastClosed;
                    if (!isBackfill)
                        notifyCandleClosed = OnCandleClosed;
                }
                else if (acc.OpenTime == default)
                    acc.OpenTime = openTime;

                acc.AddTick(price, crowdSentiment);
            }

            // D1 FIX: Update ring buffer and invalidate cache outside lock(acc) (<10ns under lock)
            if (closedCandle != null && closedDbInterval != null)
            {
                string bufferKey = $"{asset}:{closedDbInterval}";
                var ring = _ringBuffers.GetOrAdd(bufferKey, _ => new CandleRingBuffer(400));
                ring.Add(closedCandle);
                _recentCandlesCache.TryRemove(bufferKey, out _);
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
            finally
            {
                // Re-arm channel and state so subsequent in-process tests or restarts can safely ingest ticks
                PrepareForInProcessRestart();
            }
        }
    }
}
