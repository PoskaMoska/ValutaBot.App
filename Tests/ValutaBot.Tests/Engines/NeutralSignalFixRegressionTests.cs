using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using ValutaBot.App.MiniApp.Data.Repositories;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;
using Xunit;

namespace ValutaBot.Tests.Engines;

[Collection("MLPythonServiceState")]
public class NeutralSignalFixRegressionTests : IDisposable
{
    private sealed class PermissiveGatekeeper : IRiskGatekeeper
    {
        public TechnicalAnalysisEngine.GatekeeperResult ValidateMarketGatekeeper(
            string asset,
            string timeframe,
            ReadOnlySpan<double> prices,
            ReadOnlySpan<MiniAppController.OhlcCandle> candles = default)
            => new(true, "", 0.0001, 25.0);
    }

    public void Dispose()
    {
        MLPythonService.SetFactory(null);
        MLPythonService.Init(null);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_DowntrendWithGreenBounceCandleAndMlHold_ResolvesPutWithoutDeadMarketOrBuyFlip()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.AbsoluteUri.Contains("/predict")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{\"confidence\":0.50,\"raw_confidence\":0.50,\"probability\":0.50,\"direction\":\"HOLD\",\"model_version\":\"patch_tst_v4\"}")
            });

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(mockHandler.Object));
        MLPythonService.SetFactory(mockFactory.Object);
        MLPythonService.Init("http://ml-mock.invalid");

        // Strong 80-candle M1 downtrend ending with two small green pullback/bounce candles at the bottom.
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        var candles = new MiniAppController.OhlcCandle[80];
        double px = 1.1355;
        for (int i = 0; i < 77; i++)
        {
            double o = px;
            double c = px - 0.00005;
            candles[i] = new MiniAppController.OhlcCandle(o, o + 0.00004, c - 0.00004, c, 120, now.AddMinutes(-(80 - i)));
            px = c;
        }
        // Two small green consolidation/bounce candles at bottom (open < close), matching screenshot scenario.
        candles[77] = new MiniAppController.OhlcCandle(px, px + 0.00008, px - 0.00004, px + 0.00006, 110, now.AddMinutes(-3));
        candles[78] = new MiniAppController.OhlcCandle(px + 0.00006, px + 0.00012, px + 0.00002, px + 0.00009, 105, now.AddMinutes(-2));
        // Live forming candle (excluded from ClosedCandles)
        candles[79] = new MiniAppController.OhlcCandle(px + 0.00009, px + 0.00014, px + 0.00004, px + 0.00010, 90, now.AddMinutes(-1));

        var mockFetcher = new Mock<MarketDataFetcher>();
        mockFetcher.Setup(f => f.FetchOhlcWithFallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(candles);
        mockFetcher.Setup(f => f.FetchPricesAndVolumesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((candles.Select(c => c.Close).ToArray(), candles.Select(c => c.Volume).ToArray()));

        var taEngine = new TechnicalAnalysisEngine();
        var cmEngine = new ConfluenceMatrixEngine(mockFetcher.Object, taEngine);
        var orchestrator = new MarketAnalysisOrchestrator(
            mockFetcher.Object,
            new PermissiveGatekeeper(),
            taEngine,
            taEngine,
            cmEngine,
            new TradeTimeoutEngine(),
            Options.Create(new TradingBotSettings { FastFailTimeoutSeconds = 2 }),
            NullLogger<MarketAnalysisOrchestrator>.Instance);

        var result = await orchestrator.ExecuteAnalysisAsync("EUR/USD", "m1", new UserSettings());

        Assert.Equal("PUT", result.direction);
        Assert.NotEqual("Мертвый рынок", result.uiMarketEntropy);
        Assert.Contains("Медвеж", result.uiMarketPhase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_TrulyFlatLowAtrMarket_StillDetectsDeadMarketEntropy()
    {
        MLPythonService.SetFactory(null);
        MLPythonService.Init(null);

        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        var candles = new MiniAppController.OhlcCandle[80];
        for (int i = 0; i < 80; i++)
        {
            // Virtually zero range (~0.18 bps ATR) flat stream -> dead market
            double delta = (i % 2 == 0) ? 0.00001 : -0.00001;
            double o = 1.13200;
            double c = 1.13200 + delta;
            candles[i] = new MiniAppController.OhlcCandle(o, Math.Max(o, c) + 0.00001, Math.Min(o, c) - 0.00001, c, 20, now.AddMinutes(-(80 - i)));
        }

        var mockFetcher = new Mock<MarketDataFetcher>();
        mockFetcher.Setup(f => f.FetchOhlcWithFallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(candles);
        mockFetcher.Setup(f => f.FetchPricesAndVolumesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((candles.Select(c => c.Close).ToArray(), candles.Select(c => c.Volume).ToArray()));

        var taEngine = new TechnicalAnalysisEngine();
        var cmEngine = new ConfluenceMatrixEngine(mockFetcher.Object, taEngine);
        var orchestrator = new MarketAnalysisOrchestrator(
            mockFetcher.Object,
            new PermissiveGatekeeper(),
            taEngine,
            taEngine,
            cmEngine,
            new TradeTimeoutEngine(),
            Options.Create(new TradingBotSettings { FastFailTimeoutSeconds = 2 }),
            NullLogger<MarketAnalysisOrchestrator>.Instance);

        var result = await orchestrator.ExecuteAnalysisAsync("EUR/USD", "m1", new UserSettings());

        Assert.Equal("Мертвый рынок", result.uiMarketEntropy);
    }

    [Fact]
    public void DeepM1History_Exceeds400BarRingCap_AndAggregatesM30H1H4AboveGatekeeperMinimum()
    {
        var start = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        int totalM1 = 3600; // 2.5 days of 1m bars (> 400 ring cap)
        var dbCandles = new List<MiniAppController.OhlcCandle>(totalM1);
        double px = 1.0800;
        for (int i = 0; i < totalM1; i++)
        {
            double delta = (i % 3 == 0 ? 0.00010 : -0.00006);
            double o = px;
            double c = px + delta;
            dbCandles.Add(new MiniAppController.OhlcCandle(
                o, Math.Max(o, c) + 0.00005, Math.Min(o, c) - 0.00005, c, 25, start.AddMinutes(i)));
            px = c;
        }

        var merged = RealtimeTickCollector.MergeDbCandlesWithLive(
            dbCandles,
            ringTail: dbCandles.TakeLast(10),
            liveLastClosed: null,
            currentLiveCandle: null,
            liveOpenTimeUtc: null,
            limit: totalM1);

        Assert.Equal(totalM1, merged.Length);

        var m30 = MarketDataFetcher.AggregateCalendarCandles(merged, 1800);
        var h1 = MarketDataFetcher.AggregateCalendarCandles(merged, 3600);
        var h4 = MarketDataFetcher.AggregateCalendarCandles(merged, 14400);

        Assert.True(m30.Length >= 15, $"M30 yielded only {m30.Length} candles");
        Assert.True(h1.Length >= 15, $"H1 yielded only {h1.Length} candles");
        Assert.True(h4.Length >= 15, $"H4 yielded only {h4.Length} candles");
    }

    [Fact]
    public async Task ExecuteAnalysisAsync_ActiveChopWithMlHoldFallback_NeverEmits50PercentWithDirection()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"confidence\":0.50,\"raw_confidence\":0.50,\"probability\":0.50,\"direction\":\"HOLD\",\"model_version\":\"patch_tst_v3\"}")
            });

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(mockHandler.Object));
        MLPythonService.SetFactory(mockFactory.Object);
        MLPythonService.Init("http://ml-mock.invalid");

        try
        {
            var now = DateTime.UtcNow;
            now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            var candles = new MiniAppController.OhlcCandle[80];
            double px = 1.0950;
            for (int i = 0; i < 80; i++)
            {
                double delta = (i % 2 == 0) ? 0.00018 : -0.00017;
                double o = px;
                double c = px + delta;
                candles[i] = new MiniAppController.OhlcCandle(
                    o, Math.Max(o, c) + 0.00005, Math.Min(o, c) - 0.00005, c, 80, now.AddMinutes(-(80 - i)));
                px = c;
            }

            var mockFetcher = new Mock<MarketDataFetcher>();
            mockFetcher.Setup(f => f.FetchOhlcWithFallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(candles);
            mockFetcher.Setup(f => f.FetchPricesAndVolumesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync((candles.Select(c => c.Close).ToArray(), candles.Select(c => c.Volume).ToArray()));

            var taEngine = new TechnicalAnalysisEngine();
            var cmEngine = new ConfluenceMatrixEngine(mockFetcher.Object, taEngine);
            var orchestrator = new MarketAnalysisOrchestrator(
                mockFetcher.Object,
                new PermissiveGatekeeper(),
                taEngine,
                taEngine,
                cmEngine,
                new TradeTimeoutEngine(),
                Options.Create(new TradingBotSettings { FastFailTimeoutSeconds = 2 }),
                NullLogger<MarketAnalysisOrchestrator>.Instance);

            var result = await orchestrator.ExecuteAnalysisAsync("EUR/USD OTC", "m1", new UserSettings());

            Assert.True(result.direction is "BUY" or "PUT", $"Expected BUY or PUT, got {result.direction}");
            Assert.InRange(result.probability, 51, 92);
        }
        finally
        {
            MLPythonService.SetFactory(null);
            MLPythonService.Init(null);
        }
    }
}
