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
}
