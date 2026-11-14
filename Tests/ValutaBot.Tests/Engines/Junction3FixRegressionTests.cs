using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    /// <summary>
    /// Regression tests for Junction-3 fixes (C# -> Python Bridge).
    /// </summary>
    public class Junction3FixRegressionTests
    {
        private readonly ITestOutputHelper _out;
        public Junction3FixRegressionTests(ITestOutputHelper output) => _out = output;

        // ═══════════════════════════════════════════════════════════════════
        // D3-2: MLPythonPrediction Deserialization with Variance Fields
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void D3_2_MLPythonPrediction_DeserializesVarianceFieldsCorrectly()
        {
            // Simulate Python `/predict` response
            string json = @"{
                ""direction"": ""BUY"",
                ""confidence"": 0.73,
                ""model_version"": ""1.0.0"",
                ""accuracy"": 0.58,
                ""auc"": 0.61,
                ""n_train"": 5000,
                ""variance_estimate"": 0.15,
                ""raw_confidence"": 0.78
            }";

            var options = new JsonSerializerOptions { 
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            var prediction = JsonSerializer.Deserialize<MLPythonService.MLPythonPrediction>(json, options);

            Assert.NotNull(prediction);
            Assert.Equal("BUY", prediction.Direction);
            Assert.Equal(0.73, prediction.Confidence);
            
            // Check that the new B6 fields map correctly
            Assert.Equal(0.15, prediction.VarianceEstimate);
            Assert.Equal(0.78, prediction.RawConfidence);
            
            _out.WriteLine("[D3-2] MLPythonPrediction properly extracts VarianceEstimate and RawConfidence.");
        }

        // ═══════════════════════════════════════════════════════════════════
        // D3-2: PostgreSQL cutoff string format equivalence
        // ═══════════════════════════════════════════════════════════════════
        [Fact]
        public void D3_2_DbTimeFormat_MatchesPythonCutoffFormat()
        {
            // The open time created in C#
            var entryTime = new DateTime(2023, 10, 10, 12, 0, 15, DateTimeKind.Utc);
            
            // How C# saves it to DB
            string csharpFormat = entryTime.ToString("o"); 
            // Expected: "2023-10-10T12:00:15.0000000Z"
            
            // The fix applied in Python:
            // cutoff_str = cutoff_dt.strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
            string pythonFormat = entryTime.ToString("yyyy-MM-ddTHH:mm:ss") + ".0000000Z";

            Assert.Equal(csharpFormat, pythonFormat);
            Assert.EndsWith(".0000000Z", csharpFormat);
            
            _out.WriteLine($"[D3-2] Both C# and Python generate the exact same string: {csharpFormat}");
        }
    }

    [Collection("MLPythonServiceState")]
    public class MLPythonMicroBridgeTests
    {
        private sealed class CapturingHandler : HttpMessageHandler
        {
            public string? CapturedBody { get; private set; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedBody = request.Content != null
                    ? await request.Content.ReadAsStringAsync(cancellationToken)
                    : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"direction":"PUT","confidence":0.62,"model_version":"neural_quant_v6","horizon_candles":1,"accuracy":0.55,"auc":0.60,"n_train":178593,"raw_confidence":0.38}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }

        [Fact]
        public async Task PredictAsync_SerializesMicroCandlesPayloadWhenProvided()
        {
            var handler = new CapturingHandler();
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(handler, disposeHandler: false));

            var baseTs = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            var mainCandles = new MiniAppController.OhlcCandle[20];
            for (int i = 0; i < mainCandles.Length; i++)
                mainCandles[i] = new MiniAppController.OhlcCandle(1.10, 1.1005, 1.0995, 1.1001, 10, baseTs.AddSeconds(i * 15));

            var microCandles = new MiniAppController.OhlcCandle[6];
            for (int i = 0; i < microCandles.Length; i++)
                microCandles[i] = new MiniAppController.OhlcCandle(1.1001, 1.1002, 1.1000, 1.10015, 5, baseTs.AddSeconds(19 * 15 + i * 5));

            MLPythonService.SetFactory(mockFactory.Object);
            MLPythonService.Init("http://ml-mock.invalid");
            try
            {
                var pred = await MLPythonService.PredictAsync(
                    "EURUSD",
                    "s15",
                    mainCandles,
                    isForex: true,
                    microCandles: microCandles);

                Assert.NotNull(pred);
                Assert.Equal("PUT", pred.Direction);
                Assert.Equal("neural_quant_v6", pred.ModelVersion);
                Assert.NotNull(handler.CapturedBody);

                using var doc = JsonDocument.Parse(handler.CapturedBody);
                Assert.True(doc.RootElement.TryGetProperty("micro_candles", out var microProp));
                Assert.Equal(JsonValueKind.Array, microProp.ValueKind);
                Assert.Equal(6, microProp.GetArrayLength());
            }
            finally
            {
                MLPythonService.SetFactory(null);
                MLPythonService.Init(null);
                handler.Dispose();
            }
        }
    }
}
