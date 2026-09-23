using System;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.Tests.Engines
{
    public class OrchestratorE2ETests
    {
        [Fact]
        public async Task E2ETrend_FinalDirectionIsBuy()
        {
            var ta = new TechnicalAnalysisEngine();
            var cmEngine = new ConfluenceMatrixEngine(new ValutaBot.MiniApp.Tests.MockMarketDataFetcher(), ta, new AutoCalibrationEngine());
            var aeEngine = new TradeTimeoutEngine();
            var mockFetcher = new ValutaBot.MiniApp.Tests.MockMarketDataFetcher();
            
            var e2eSettings = new TradingBotSettings { EnableMachineLearning = false, EnableSmc = true, EnableOrderFlow = true, EnableAutoCalibration = true };
            var orch = new MarketAnalysisOrchestrator(
                mockFetcher, ta, ta, ta, cmEngine, aeEngine, Options.Create(e2eSettings),
                new NullLogger<MarketAnalysisOrchestrator>()
            );
            
            var trendRes = await orch.ExecuteAnalysisAsync("TEST_TREND", "m1", new UserSettings());
            string trendJson = JsonSerializer.Serialize(trendRes);
            
            Assert.Contains("\"direction\":\"BUY\"", trendJson);
        }

        [Fact]
        public async Task E2EReversal_ConfluenceOverridesRawTA()
        {
            var ta = new TechnicalAnalysisEngine();
            var cmEngine = new ConfluenceMatrixEngine(new ValutaBot.MiniApp.Tests.MockMarketDataFetcher(), ta, new AutoCalibrationEngine());
            var aeEngine = new TradeTimeoutEngine();
            var mockFetcher = new ValutaBot.MiniApp.Tests.MockMarketDataFetcher();
            
            var e2eSettings = new TradingBotSettings { EnableMachineLearning = false, EnableSmc = true, EnableOrderFlow = true, EnableAutoCalibration = true };
            var orch = new MarketAnalysisOrchestrator(
                mockFetcher, ta, ta, ta, cmEngine, aeEngine, Options.Create(e2eSettings),
                new NullLogger<MarketAnalysisOrchestrator>()
            );
            
            var revRes = await orch.ExecuteAnalysisAsync("TEST_REVERSAL", "m1", new UserSettings());
            string revJson = JsonSerializer.Serialize(revRes);
            
            Assert.True(revJson.Contains("\"direction\":\"PUT\"") || revJson.Contains("\"direction\":\"NEUTRAL\""));
        }

        [Fact]
        public async Task E2EDeadMarket_GatekeeperBlocksExecution()
        {
            var ta = new TechnicalAnalysisEngine();
            var cmEngine = new ConfluenceMatrixEngine(new ValutaBot.MiniApp.Tests.MockMarketDataFetcher(), ta, new AutoCalibrationEngine());
            var aeEngine = new TradeTimeoutEngine();
            var mockFetcher = new ValutaBot.MiniApp.Tests.MockMarketDataFetcher();
            
            var e2eSettings = new TradingBotSettings { EnableMachineLearning = false, EnableSmc = true, EnableOrderFlow = true, EnableAutoCalibration = true };
            var orch = new MarketAnalysisOrchestrator(
                mockFetcher, ta, ta, ta, cmEngine, aeEngine, Options.Create(e2eSettings),
                new NullLogger<MarketAnalysisOrchestrator>()
            );
            
            var ex = await Assert.ThrowsAsync<Exception>(() => orch.ExecuteAnalysisAsync("TEST_DEAD", "m1", new UserSettings()));
            Assert.Contains("засто", ex.Message);
        }
    }
}
