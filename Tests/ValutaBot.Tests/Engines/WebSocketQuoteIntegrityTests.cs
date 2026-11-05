using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using ValutaBot.MiniApp;
using Xunit;

namespace ValutaBot.Tests.Engines;

[Collection("RealtimeTickCollector")]
public class WebSocketQuoteIntegrityTests
{
    [Theory]
    [InlineData("#EURUSD_otc", "EURUSD_PO")]
    [InlineData("EURUSD_otc", "EURUSD_PO")]
    [InlineData("EURUSD (OTC)", "EURUSD_PO")]
    [InlineData("EUR/USD OTC", "EURUSD_PO")]
    [InlineData("eurusd_otc", "EURUSD_PO")]
    [InlineData("EURUSD_PO", "EURUSD_PO")]
    public void SymbolNormalization_AlwaysMapsToCanonicalPoKey(string input, string expected)
    {
        string canonical = PriceFeed.CandleKey(input);
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public async Task RealtimeTickCollector_AcceptsVariousSymbolFormatsWithoutLosingTicks()
    {
        RealtimeTickCollector.ResetAllAccumulatorsAndRingBuffers();

        long baseTimeUtc = DateTime.UtcNow.Ticks;
        long intervalTicks = TimeSpan.FromSeconds(5).Ticks;
        long alignedTickTime = baseTimeUtc - (baseTimeUtc % intervalTicks) + TimeSpan.FromSeconds(1).Ticks;

        // Ingest ticks using different raw symbol variants with isolated pair
        await RealtimeTickCollector.OnPriceUpdateAsync("#NZDUSD_otc", 1.08500, alignedTickTime);
        await RealtimeTickCollector.OnPriceUpdateAsync("NZDUSD (OTC)", 1.08520, alignedTickTime + TimeSpan.FromMilliseconds(500).Ticks);
        await RealtimeTickCollector.OnPriceUpdateAsync("nzdusd_otc", 1.08490, alignedTickTime + TimeSpan.FromMilliseconds(800).Ticks);

        // Fetch using canonical or user format
        var candles1 = await RealtimeTickCollector.GetRecentCandles("NZDUSD_PO", "s5", 10);
        var candles2 = await RealtimeTickCollector.GetRecentCandles("NZDUSD", "s5", 10);
        var candles3 = await RealtimeTickCollector.GetRecentCandles("#NZDUSD_otc", "s5", 10);

        Assert.NotEmpty(candles1);
        Assert.NotEmpty(candles2);
        Assert.NotEmpty(candles3);
        Assert.Equal(candles1[^1].Close, candles2[^1].Close);
        Assert.Equal(candles1[^1].Close, candles3[^1].Close);
        Assert.Equal(3, candles1[^1].Volume); // All 3 ticks accounted for in same candle!
        Assert.Equal(1.08520, candles1[^1].High);
        Assert.Equal(1.08490, candles1[^1].Low);
    }

    [Fact]
    public void JsonParsing_ParsesNumericAndStringPricesInvariantly()
    {
        string jsonWithStrings = "[[\"#EURUSD_otc\", \"1730578800.5\", \"1.08543\"]]";
        using var doc = JsonDocument.Parse(jsonWithStrings);
        var item = doc.RootElement[0];

        double srvTs = item[1].ValueKind == JsonValueKind.Number ? item[1].GetDouble() :
            (item[1].ValueKind == JsonValueKind.String && double.TryParse(item[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTs) ? parsedTs : 0);
        double price = item[2].ValueKind == JsonValueKind.Number ? item[2].GetDouble() :
            (item[2].ValueKind == JsonValueKind.String && double.TryParse(item[2].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedPrice) ? parsedPrice : 0);

        Assert.Equal(1730578800.5, srvTs);
        Assert.Equal(1.08543, price);
    }

    [Fact]
    public void CandleBoundary_StrictlyAlignsToTimeframeBoundaryWithZeroMilliseconds()
    {
        long tickTime = DateTime.UtcNow.Ticks;
        long intervalTicks = TimeSpan.FromSeconds(5).Ticks;
        long aligned = tickTime - (tickTime % intervalTicks);
        var dt = new DateTime(aligned, DateTimeKind.Utc);

        Assert.Equal(0, dt.Millisecond);
        Assert.Equal(0, dt.Microsecond);
        Assert.Equal(0, dt.Nanosecond);
        Assert.True(dt.Second % 5 == 0);
    }

    [Fact]
    public void CandleRingBuffer_CircularOverflow_OverwritesOldestWithoutArrayCopy()
    {
        var ring = new RealtimeTickCollector.CandleRingBuffer(50);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Add 120 candles into capacity 50 ring buffer
        for (int i = 0; i < 120; i++)
        {
            var candle = new MiniAppController.OhlcCandle(
                Open: 1.0 + i,
                High: 1.0 + i + 0.5,
                Low: 1.0 + i - 0.5,
                Close: 1.0 + i + 0.1,
                Volume: 10 + i,
                Timestamp: baseTime.AddSeconds(i * 5)
            );
            ring.Add(candle);
        }

        Assert.Equal(50, ring.Count);

        // GetRecent(10) must return the 10 most recent candles in ascending order (indices 110 to 119)
        var recent10 = ring.GetRecent(10);
        Assert.Equal(10, recent10.Length);
        for (int i = 0; i < 10; i++)
        {
            int expectedI = 110 + i;
            Assert.Equal(1.0 + expectedI, recent10[i].Open);
            Assert.Equal(baseTime.AddSeconds(expectedI * 5), recent10[i].Timestamp);
        }

        // GetRecent(50) must return all 50 retained candles (indices 70 to 119)
        var all50 = ring.GetRecent(50);
        Assert.Equal(50, all50.Length);
        Assert.Equal(1.0 + 70, all50[0].Open);
        Assert.Equal(1.0 + 119, all50[^1].Open);
    }

    [Fact]
    public void CandleRingBuffer_IgnoresOutOfOrderCandles()
    {
        var ring = new RealtimeTickCollector.CandleRingBuffer(50);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        ring.Add(new MiniAppController.OhlcCandle(1.0, 1.1, 0.9, 1.05, 1, baseTime.AddSeconds(10)));
        ring.Add(new MiniAppController.OhlcCandle(1.05, 1.15, 0.95, 1.10, 1, baseTime.AddSeconds(20)));

        // Attempt to insert an older candle (out-of-order)
        ring.Add(new MiniAppController.OhlcCandle(0.9, 1.0, 0.8, 0.95, 1, baseTime.AddSeconds(5)));

        Assert.Equal(2, ring.Count);
        var recent = ring.GetRecent(10);
        Assert.Equal(baseTime.AddSeconds(10), recent[0].Timestamp);
        Assert.Equal(baseTime.AddSeconds(20), recent[1].Timestamp);
    }

    [Fact]
    public void CandleRingBuffer_ConcurrentStress_UnderBurstTicksMaintainsConsistency()
    {
        var ring = new RealtimeTickCollector.CandleRingBuffer(100);
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int totalCandles = 2000;

        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < totalCandles; i++)
            {
                ring.Add(new MiniAppController.OhlcCandle(
                    Open: i, High: i + 1, Low: i - 1, Close: i, Volume: 1, Timestamp: baseTime.AddMilliseconds(i * 10)));
            }
        });

        var readTask = Task.Run(() =>
        {
            while (!writeTask.IsCompleted)
            {
                var slice = ring.GetRecent(50);
                if (slice.Length > 1)
                {
                    // Check strict chronological ordering
                    for (int j = 1; j < slice.Length; j++)
                    {
                        Assert.True(slice[j].Timestamp > slice[j - 1].Timestamp, "Candle timestamps must be strictly monotonic ascending.");
                    }
                }
            }
        });

        Task.WaitAll(writeTask, readTask);
        Assert.Equal(100, ring.Count);
        var finalSlice = ring.GetRecent(100);
        Assert.Equal(100, finalSlice.Length);
        Assert.Equal(baseTime.AddMilliseconds((totalCandles - 1) * 10), finalSlice[^1].Timestamp);
    }
}
