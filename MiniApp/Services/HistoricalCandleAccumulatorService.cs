using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Hosting;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp;

/// <summary>
/// Accumulates live m1 OHLCV candles from subminute_candles into historical_candles.
/// Runs every 65 seconds on weekdays (Mon-Fri, market hours UTC).
/// Caps each asset at 100,000 rows — oldest rows are deleted FIFO.
/// Keeps weekend OTC proxy data always fresh without any external API calls.
/// </summary>
public class HistoricalCandleAccumulatorService : BackgroundService
{
    private static readonly string[] BaseAssets = { "EURUSD", "GBPUSD", "AUDUSD", "USDCAD", "USDCHF", "USDJPY" };
    private static readonly string[] Assets = BaseAssets.Select(PriceFeed.CandleKey).ToArray();
    private const int MaxCandlesPerAsset = 300_000;
    private const string Interval = "1m";
    private DateTime _lastRollupMinuteUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BotLogger.Info("[HistoricalAccumulator] Started. Accumulating live m1 candles into historical_candles with strict grid snap.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
                ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
                : DateTime.UtcNow;
            var targetClosedMinute = TruncateToMinute(now);
            var wakeUpAt = targetClosedMinute.AddMinutes(1).AddSeconds(5);
            var delay = wakeUpAt - now; // Wake up at 05 seconds past the minute to ensure s5 are flushed
            
            if (delay > TimeSpan.Zero) 
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }

            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                now = PriceFeed.UsePocketOption && PocketOptionWebSocketStream.LastBrokerTickUtc != DateTime.MinValue
                    ? PocketOptionWebSocketStream.GetBrokerTimeUtc()
                    : DateTime.UtcNow;
                // OTC/PO trades 24/7; Forex is Mon-Fri market hours only
                bool isWeekday = PriceFeed.UsePocketOption || (now.DayOfWeek >= DayOfWeek.Monday && now.DayOfWeek <= DayOfWeek.Friday);
                bool isMarketOpen = PriceFeed.UsePocketOption || !(now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 22);

                if (isWeekday && isMarketOpen)
                {
                    await RealtimeTickCollector.FlushAsync(stoppingToken);
                    DateTime startMinute = _lastRollupMinuteUtc == DateTime.MinValue || (targetClosedMinute - _lastRollupMinuteUtc) > TimeSpan.FromMinutes(15) || targetClosedMinute < _lastRollupMinuteUtc ? targetClosedMinute : _lastRollupMinuteUtc.AddMinutes(1);
                    for (var min = startMinute; min <= targetClosedMinute; min = min.AddMinutes(1))
                    {
                        await FlushAndSaveAsync(min);
                        _lastRollupMinuteUtc = min;
                    }
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[HistoricalAccumulator] Error: {ex.Message}");
            }
        }
    }

    private static async Task FlushAndSaveAsync(DateTime closedMinute)
    {
        using var conn = DbConnectionFactory.GetConnection();
        await conn.OpenAsync();

        int totalSaved = 0;

        foreach (var asset in Assets)
        {
            try
            {
                // Aggregate s5 candles from the closed minute into one m1 candle
                var s5Rows = (await conn.QueryAsync<dynamic>(@"
                    SELECT open_price, high_price, low_price, close_price, volume
                    FROM subminute_candles
                    WHERE asset = @Asset
                      AND interval = 's5'
                      AND open_time >= @MinStart
                      AND open_time <  @MinEnd
                    ORDER BY open_time ASC
                ", new
                {
                    Asset    = asset,
                    MinStart = closedMinute.ToString("O"),
                    MinEnd   = closedMinute.AddMinutes(1).ToString("O")
                })).AsList();

                if (s5Rows.Count == 0) continue;

                double open   = (double)s5Rows[0].open_price;
                double close  = (double)s5Rows[s5Rows.Count - 1].close_price;
                double high   = double.MinValue;
                double low    = double.MaxValue;
                double volume = 0;

                foreach (var r in s5Rows)
                {
                    double h = (double)r.high_price;
                    double l = (double)r.low_price;
                    double v = (double)r.volume;
                    if (h > high && h <= PriceFeed.MaxAllowedPrice) high = h;
                    if (l < low && l > 0) low = l;
                    if (v > 0 && !double.IsNaN(v) && !double.IsInfinity(v)) volume += v;
                }

                if (open <= 0 || open > PriceFeed.MaxAllowedPrice || close <= 0 || close > PriceFeed.MaxAllowedPrice || high < low)
                    continue;

                // ON CONFLICT DO NOTHING — safe to re-run without duplicating
                int affected = await conn.ExecuteAsync(@"
                    INSERT INTO historical_candles (asset, interval, open_time, open, high, low, close, volume)
                    VALUES (@Asset, @Interval, @OpenTime, @Open, @High, @Low, @Close, @Volume)
                    ON CONFLICT (asset, interval, open_time) DO NOTHING
                ", new
                {
                    Asset    = asset,
                    Interval,
                    OpenTime = closedMinute.ToString("O"),
                    Open     = open,
                    High     = high,
                    Low      = low,
                    Close    = close,
                    Volume   = volume
                });

                if (affected <= 0) continue;

                totalSaved++;

                // FIFO cap: keep max 100,000 rows per asset via single query without N+1 COUNT(*)
                await conn.ExecuteAsync(@"
                    DELETE FROM historical_candles
                    WHERE id IN (
                        SELECT id FROM historical_candles
                        WHERE asset = @Asset AND interval = @Interval
                        ORDER BY open_time DESC
                        OFFSET @MaxCandles
                    );", new { Asset = asset, Interval, MaxCandles = MaxCandlesPerAsset });
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[HistoricalAccumulator] {asset}: {ex.Message}");
            }
        }

        if (totalSaved > 0)
            BotLogger.Info($"[HistoricalAccumulator] +{totalSaved} m1 candles saved for {closedMinute:HH:mm} UTC.");
    }

    private static DateTime TruncateToMinute(DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Utc);
}
