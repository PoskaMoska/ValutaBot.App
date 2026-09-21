using System;
using System.Text.Json;
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
}
