using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.App.MiniApp.Services;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests
{
    public class MlModuleTests
    {
        private readonly ITestOutputHelper _output;

        public MlModuleTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void LlmReportingService_ShouldGenerateAccurateText()
        {
            // Arrange
            var llmService = new LlmReportingService();
            var dummyPrediction = new MLPythonService.MLPythonPrediction(
                Direction: "BUY",
                Confidence: 0.90,
                ModelVersion: "lgbm-v1-BTC",
                Accuracy: 0.85,
                Auc: 0.88, NTrain: 1000);

            // Act
            string report = llmService.GenerateMarketSummary(
                asset: "BTCUSDT", 
                regime: "Uptrend", 
                mlPrediction: dummyPrediction, 
                l1IsBuy: true, 
                l2IsBuy: true, 
                l3IsBuy: true);

            // Assert
            Assert.Contains("BTCUSDT", report);
            Assert.Contains("90%", report);
            Assert.Contains("ВВЕРХ", report);
            Assert.Contains("3/3", report);
            _output.WriteLine(report);
        }
    }
}

