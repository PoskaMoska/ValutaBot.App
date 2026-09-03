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
    private static readonly string[] Assets = { "EURUSD", "GBPUSD", "AUDUSD", "USDCAD", "USDCHF", "USDJPY" };
    private const int MaxCandlesPerAsset = 100_000;
    private const string Interval = "1m";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Align to next minute boundary
        int secsToNext = 60 - DateTime.UtcNow.Second;
        await Task.Delay(TimeSpan.FromSeconds(secsToNext + 2), stoppingToken);

        BotLogger.Info("[HistoricalAccumulator] Started. Accumulating live m1 candles into historical_candles.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                // Only on weekdays, exclude Fri 22:00+ and all weekend
                bool isWeekday = now.DayOfWeek >= DayOfWeek.Monday && now.DayOfWeek <= DayOfWeek.Friday;
                bool isMarketOpen = !(now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 22);

                if (isWeekday && isMarketOpen)
                    await FlushAndSaveAsync();
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[HistoricalAccumulator] Error: {ex.Message}");
            }

            // 65s to ensure previous minute is fully closed
            await Task.Delay(TimeSpan.FromSeconds(65), stoppingToken);
        }
    }

    private static async Task FlushAndSaveAsync()
    {
        // The minute that just completed (1 minute ago)
        DateTime closedMinute = TruncateToMinute(DateTime.UtcNow).AddMinutes(-1);

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
                    if ((double)r.high_price > high) high = (double)r.high_price;
                    if ((double)r.low_price  < low)  low  = (double)r.low_price;
                    volume += (double)r.volume;
                }

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

                // FIFO cap: keep max 100,000 rows per asset
                long count = await conn.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM historical_candles WHERE asset = @Asset AND interval = @Interval",
                    new { Asset = asset, Interval });

                if (count > MaxCandlesPerAsset)
                {
                    await conn.ExecuteAsync(@"
                        DELETE FROM historical_candles
                        WHERE id IN (
                            SELECT id FROM historical_candles
                            WHERE asset = @Asset AND interval = @Interval
                            ORDER BY open_time ASC
                            LIMIT @ToDelete
                        )
                    ", new { Asset = asset, Interval, ToDelete = count - MaxCandlesPerAsset });
                }
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
