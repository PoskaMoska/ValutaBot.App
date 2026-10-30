using System;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines;

public class DualStreamOnnxServiceTests
{
    [Fact]
    public void ServiceInitialization_DoesNotThrow()
    {
        var service = DualStreamOnnxService.Instance;
        Assert.NotNull(service);
    }

    [Fact]
    public void InvalidInputs_ReturnsNeutralWithoutThrowing()
    {
        var service = DualStreamOnnxService.Instance;
        var pred = service.Predict(new float[10], new float[10], new float[2]);

        Assert.Equal("NEUTRAL", pred.Direction);
        Assert.Equal(0.50, pred.Probability);
    }

    [Fact]
    public void ValidInputs_EvaluatesQuickly()
    {
        var service = DualStreamOnnxService.Instance;
        if (!service.IsModelLoaded)
        {
            // If model is not loaded in test runner environment, skip inference assertion
            return;
        }

        var macroData = new float[150];
        var microData = new float[150];
        var bridgeData = new float[4];

        for (int i = 0; i < 150; i++)
        {
            macroData[i] = (float)Math.Sin(i);
            microData[i] = (float)Math.Cos(i);
        }
        bridgeData[0] = 1.5f;
        bridgeData[1] = 0.8f;
        bridgeData[2] = 0.5f;
        bridgeData[3] = 0.2f;

        // Warm up before measuring steady-state inference latency
        service.Predict(macroData, microData, bridgeData);

        var pred = service.Predict(macroData, microData, bridgeData);

        Assert.Contains(pred.Direction, new[] { "BUY", "PUT" });
        Assert.InRange(pred.Probability, 0.50, 0.95);
        Assert.True(pred.ElapsedMicroseconds < 50000, $"Expected latency < 50ms, got {pred.ElapsedMicroseconds / 1000.0}ms");
    }

    [Fact]
    public void Predict_WithNaNOrInfinityInputs_SafelyReturnsNeutral()
    {
        var service = DualStreamOnnxService.Instance;

        var macroData = new float[150];
        macroData[5] = float.NaN;
        var microData = new float[150];
        microData[10] = float.PositiveInfinity;
        var bridgeData = new float[] { 1.0f, 0.5f, 0.2f, float.NaN };

        var pred = service.Predict(macroData, microData, bridgeData);

        Assert.Equal("NEUTRAL", pred.Direction);
        Assert.Equal(0.50, pred.Probability);
    }

    [Fact]
    public void BuildOnnxTensors_ComputesParityBridgeVectorWithoutNanOrZeros()
    {
        var buildMethod = typeof(ValutaBot.MiniApp.Features.MarketAnalysis.MarketAnalysisOrchestrator).GetMethod(
            "BuildOnnxTensors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(buildMethod);

        var microCandles = new MiniAppController.OhlcCandle[30];
        var macroCandles = new MiniAppController.OhlcCandle[30];
        var now = DateTime.UtcNow;

        for (int i = 0; i < 30; i++)
        {
            double baseP = 1.1000 + i * 0.0001;
            microCandles[i] = new MiniAppController.OhlcCandle(baseP, baseP + 0.0002, baseP - 0.0001, baseP + 0.0001, 10.0, now.AddSeconds(i * 5));
            macroCandles[i] = new MiniAppController.OhlcCandle(baseP, baseP + 0.0005, baseP - 0.0003, baseP + 0.0002, 50.0, now.AddSeconds(i * 60));
        }

        var result = buildMethod!.Invoke(null, new object[] { microCandles, macroCandles, 1.1030 });
        Assert.NotNull(result);

        // Extract bridgeData tuple element
        var tuple = ((float[] macro, float[] micro, float[] bridge))result!;
        Assert.Equal(150, tuple.macro.Length);
        Assert.Equal(150, tuple.micro.Length);
        Assert.Equal(4, tuple.bridge.Length);

        // Verify bridge features are calculated and finite:
        // bridge[0]: distM1, bridge[1]: atrRatio, bridge[2]: zScore, bridge[3]: wickBal
        for (int i = 0; i < 4; i++)
        {
            Assert.True(float.IsFinite(tuple.bridge[i]), $"Bridge feature {i} is not finite: {tuple.bridge[i]}");
        }
        // zScore was previously hardcoded to 0f, verify it is now actively computed
        Assert.NotEqual(0f, tuple.bridge[2]);
    }
}
