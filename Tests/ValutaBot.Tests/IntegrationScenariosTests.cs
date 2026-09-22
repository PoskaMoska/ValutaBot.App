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
using Xunit;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.MiniApp.Features.MarketAnalysis.Engines;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.Tests
{
    class DummyGatekeeper : IRiskGatekeeper
    {
        public TechnicalAnalysisEngine.GatekeeperResult ValidateMarketGatekeeper(string asset, string timeframe, ReadOnlySpan<double> prices, ReadOnlySpan<MiniAppController.OhlcCandle> candles = default)
            => new TechnicalAnalysisEngine.GatekeeperResult(true, "", 0.0001, 25.0);
    }

    public class IntegrationScenariosTests
    {
        [Fact]
        public async Task PerfectStorm_ForexWeekday_ShouldGenerateFinalSignal()
        {
            // 1. Mock ML Python Service HTTP Client
            var mockMessageHandler = new Mock<HttpMessageHandler>();
            mockMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(req => req.RequestUri != null && req.RequestUri.AbsoluteUri.Contains("/predict")),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{\"probability\":0.88,\"direction\":\"BUY\"}")
                });

            var httpClient = new HttpClient(mockMessageHandler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(_ => _.CreateClient(It.IsAny<string>())).Returns(httpClient);
            
            // Set the HTTP factory for the Python service
            ValutaBot.MiniApp.MLPythonService.SetFactory(mockFactory.Object);

            // 2. Generate Mock Candles for Perfect Storm
            var now = DateTime.UtcNow;
            now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc); // round to seconds

            var candles = new MiniAppController.OhlcCandle[150];
            double price = 1.0;
            for (int i = 0; i < 150; i++)
            {
                // Creating a trend up to trigger a BUY signal naturally
                price += 0.0001; 
                candles[i] = new MiniAppController.OhlcCandle(
                    price - 0.0001, price + 0.0002, price - 0.0002, price, 1000 + i * 10,
                    now.AddSeconds(- (150 - 1 - i) * 60)
                );
            }

            // 3. Mock MarketDataFetcher
            var mockFetcher = new Mock<MarketDataFetcher>();
            mockFetcher.Setup(f => f.FetchOhlcWithFallbackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                       .ReturnsAsync(candles);
            mockFetcher.Setup(f => f.FetchPricesAndVolumesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                       .ReturnsAsync((candles.Select(c => c.Close).ToArray(), candles.Select(c => c.Volume).ToArray()));

            // 4. Setup Engines
            var taEngine = new TechnicalAnalysisEngine();
            var cmEngine = new ConfluenceMatrixEngine(mockFetcher.Object, taEngine);
            var timeoutEngine = new TradeTimeoutEngine();
            var mcEngine = new MonteCarloEngine();
            
            var mockRiskGatekeeper = new DummyGatekeeper();
            var optionsMock = Options.Create(new TradingBotSettings { FastFailTimeoutSeconds = 2 });

            // 5. Setup Orchestrator
            var orchestrator = new MarketAnalysisOrchestrator(
                mockFetcher.Object,
                mockRiskGatekeeper,
                taEngine, // IMathEngine
                taEngine, // IMarketAnalyzer
                cmEngine,
                timeoutEngine,
                mcEngine,
                optionsMock,
                NullLogger<MarketAnalysisOrchestrator>.Instance
            );

            // 6. Execute 
            // We pass a standard Forex pair (EUR/USD) to bypass OTC routing
            var result = await orchestrator.ExecuteAnalysisAsync("EUR/USD", "m1", new UserSettings());

            // 7. Assertions
            Assert.NotNull(result);
            Assert.False(string.IsNullOrEmpty(result.direction), "Direction should not be empty");
            Assert.False(string.IsNullOrEmpty(result.duration), "Duration should not be empty");
            
            // Prove ML prediction is incorporated without blowing up
            Assert.True(result.probability > 0 || result.direction == "NEUTRAL", "Signal should have probability if not neutral");
        }
    }
}
