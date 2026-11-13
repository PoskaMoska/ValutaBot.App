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
}
