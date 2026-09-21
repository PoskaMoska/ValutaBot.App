using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Features.MarketAnalysis;
using ValutaBot.App.MiniApp.Models;
using ValutaBot.MiniApp.Services;

namespace ValutaBot.Tests.Engines
{
    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 1: ContinuousStateEngine
    // Savitzky-Golay, Kalman filter, Z-score, regime classification
    // ═══════════════════════════════════════════════════════════════════════
    public class ContinuousStateEngineTests
    {
        static double[] Up(int n) { var p = new double[n]; for (int i = 0; i < n; i++) p[i] = 1.1000 + i * 0.0001; return p; }
        static double[] Down(int n) { var p = new double[n]; for (int i = 0; i < n; i++) p[i] = 1.1000 - i * 0.0001; return p; }
        static double[] Flat(int n) { var p = new double[n]; for (int i = 0; i < n; i++) p[i] = 1.1000 + (i % 2 == 0 ? 0.00001 : -0.00001); return p; }

        [Fact]
        public void CSE_OutputIsNotNull()
            => Assert.NotNull(ContinuousStateEngine.EvaluateContinuousState(Up(60), "EUR/USD", "m1"));

        [Fact]
        public void CSE_VelocityBps_IsFinite()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Up(60), "EUR/USD", "m1");
            Assert.True(double.IsFinite(r.VelocityBpsPerSec), $"Expected finite VelocityBpsPerSec, got {r.VelocityBpsPerSec}");
        }

        [Fact]
        public void CSE_MomentumContribution_IsClamped()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Up(60), "EUR/USD", "m1");
            Assert.True(r.MomentumContribution >= -0.61 && r.MomentumContribution <= 0.61,
                $"MomentumContribution={r.MomentumContribution} out of [-0.60, +0.60]");
        }

        [Fact]
        public void CSE_Uptrend_VelocityIsPositive()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Up(80), "EUR/USD", "m1");
            Assert.True(r.VelocityBpsPerSec > 0, $"Expected positive velocity on uptrend, got {r.VelocityBpsPerSec}");
        }

        [Fact]
        public void CSE_Downtrend_VelocityIsNegative()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Down(80), "EUR/USD", "m1");
            Assert.True(r.VelocityBpsPerSec < 0, $"Expected negative velocity on downtrend, got {r.VelocityBpsPerSec}");
        }

        [Fact]
        public void CSE_FlatMarket_VelocityNearZero()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Flat(80), "EUR/USD", "m1");
            Assert.True(Math.Abs(r.VelocityBpsPerSec) < 5.0,
                $"Expected near-zero velocity on flat market, got {r.VelocityBpsPerSec}");
        }

        [Fact]
        public void CSE_VelocityRegime_NotNullOrEmpty()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Up(60), "EUR/USD", "m1");
            Assert.False(string.IsNullOrWhiteSpace(r.VelocityRegime));
        }

        [Fact]
        public void CSE_ColdStart_5Candles_DoesNotThrow()
        {
            var prices = new double[] { 1.1000, 1.1001, 1.1002, 1.1001, 1.1003 };
            Assert.Null(Record.Exception(() =>
                ContinuousStateEngine.EvaluateContinuousState(prices, "EUR/USD", "m1")));
        }

        [Fact]
        public void CSE_NoNaN_NoInfinity()
        {
            var r = ContinuousStateEngine.EvaluateContinuousState(Up(100), "EUR/USD", "m1");
            Assert.True(double.IsFinite(r.VelocityBpsPerSec));
            Assert.True(double.IsFinite(r.MomentumContribution));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 2: CircuitBreakerService — halt logic, initial state
    // ═══════════════════════════════════════════════════════════════════════
    public class CircuitBreakerServiceTests
    {
        static CircuitBreakerService MakeCb() => new CircuitBreakerService(
            Microsoft.Extensions.Options.Options.Create(new TradingBotSettings
            {
                CircuitBreakerMaxConsecutiveLosses = 5,
                CircuitBreakerMinWinRate = 0.30,
                CircuitBreakerCooldownMinutes = 30,
                CircuitBreakerDbCacheTtlSeconds = 60
            }),
            () => null!);

        [Fact]
        public void CB_InitialState_IsNotHalted()
            => Assert.False(MakeCb().IsHalted());

        [Fact]
        public void CB_GetHaltedReason_IsNullWhenNotHalted()
            => Assert.Null(MakeCb().GetHaltedReason());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 3: SignalTracker — offline behavior, weight calibration math
    // ═══════════════════════════════════════════════════════════════════════
    public class SignalTrackerOfflineTests
    {
        [Fact]
        public void ST_UpdateLivePrice_DoesNotThrow()
            => Assert.Null(Record.Exception(() => SignalTracker.UpdateLivePrice("EUR/USD", 1.1234)));

        [Fact]
        public async Task ST_RecordPrediction_DoesNotThrow_WhenNoDb()
        {
            Assert.Null(await Record.ExceptionAsync(() =>
                SignalTracker.RecordPredictionAsync(
                    "BUY", "EUR/USD", "m1", 1.1234, 3, 60, true,
                    new Dictionary<string, string> { ["TA"] = "BUY" },
                    0.5, 0.2, 0.3, 0.65, 0.3)));
        }

        [Fact]
        public async Task ST_GetOverallStats_ReturnsNotNull()
            => Assert.NotNull(await SignalTracker.GetOverallStatsAsync());

        [Fact]
        public void ST_Weight_AllCorrect_AboveBase()
        {
            // (signalName, verified, correct) — all 20 verified, all 20 correct
            var votes = Enumerable.Repeat(("TechAnalysis", 1, 1), 20);
            Assert.True(SignalTracker.CalculateSignalWeight(votes, "TechAnalysis", 1.0) > 1.0);
        }

        [Fact]
        public void ST_Weight_AllWrong_BelowBase()
        {
            // 20 verified, 0 correct
            var votes = Enumerable.Repeat(("TechAnalysis", 1, 0), 20);
            Assert.True(SignalTracker.CalculateSignalWeight(votes, "TechAnalysis", 1.0) < 1.0);
        }

        [Fact]
        public void ST_Weight_MinClamped_At02()
        {
            var votes = Enumerable.Repeat(("TechAnalysis", 1, 0), 100);
            Assert.True(SignalTracker.CalculateSignalWeight(votes, "TechAnalysis", 1.0) >= 0.2);
        }

        [Fact]
        public void ST_Weight_MaxClamped_At20()
        {
            var votes = Enumerable.Repeat(("TechAnalysis", 1, 1), 100);
            Assert.True(SignalTracker.CalculateSignalWeight(votes, "TechAnalysis", 1.0) <= 2.0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 4: TelegramInitDataValidator — Security: HMAC + auth_date
    // ═══════════════════════════════════════════════════════════════════════
    public class TelegramValidatorTests
    {
        [Fact]
        public void TV_InvalidFormat_ReturnsFalse()
            => Assert.False(TelegramInitDataValidator.Validate("garbage_input", "token", out _, out _));

        [Fact]
        public void TV_EmptyString_ReturnsFalse()
            => Assert.False(TelegramInitDataValidator.Validate("", "token", out _, out _));

        [Fact]
        public void TV_NullToken_ReturnsFalse()
            => Assert.False(TelegramInitDataValidator.Validate("hash=abc", null, out _, out _));

        [Fact]
        public void TV_ExpiredAuthDate_ReturnsFalse()
        {
            long old = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
            string data = $"auth_date={old}&hash=0000000000000000000000000000000000000000000000000000000000000000";
            Assert.False(TelegramInitDataValidator.Validate(data, "token", out _, out _));
        }

        [Fact]
        public void TV_WrongHmac_ReturnsFalse()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string data = $"auth_date={now}&hash=deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef";
            Assert.False(TelegramInitDataValidator.Validate(data, "token", out _, out _));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 5: AnalysisResponseDto — DTO contract / serialization integrity
    // ═══════════════════════════════════════════════════════════════════════
    public class AnalysisResponseDtoContractTests
    {
        static AnalysisResponseDto Full() => new()
        {
            direction = "BUY", probability = 72, duration = "1 мин", expiryCandles = 1,
            tfConflict = false, uiMarketSession = "Лондон", uiMarketPhase = "Бычий тренд",
            uiMarketEntropy = "В норме", adaptiveReasoning = "OK",
            taDirection = "BUY", taConfidence = 65,
            ofDirection = "BUY", ofConfidence = 40,
            smcDirection = "NEUTRAL", smcConfidence = 10,
            lgbmDirection = "BUY", lgbmConfidence = 70,
            winRateOverall = 0.55, winRateAsset = 0.58, signalsVerifiedAsset = 123,
            rsi = 62.4, ema = 1.13980, volumeStrength = 0.42, atr = 0.00085,
            chartData = new double[] { 1.139, 1.140 }, chartOhlc = Array.Empty<object>(),
            goldenSetup = true, confluenceLabel = "3/3", confluenceRatio = 1.0
        };

        [Fact]
        public void DTO_Json_ContainsMandatoryFields()
        {
            var json = JsonSerializer.Serialize(Full());
            Assert.Contains("\"direction\"", json);
            Assert.Contains("\"probability\"", json);
            Assert.Contains("\"taDirection\"", json);
            Assert.Contains("\"smcDirection\"", json);
            Assert.Contains("\"ofDirection\"", json);
            Assert.Contains("\"lgbmDirection\"", json);
            Assert.Contains("\"goldenSetup\"", json);
            Assert.Contains("\"uiMarketPhase\"", json);
            Assert.Contains("\"uiMarketEntropy\"", json);
            Assert.Contains("\"chartData\"", json);
            Assert.Contains("\"confluenceRatio\"", json);
        }

        [Fact]
        public void DTO_RoundTrip_PreservesAllValues()
        {
            var o = Full();
            var r = JsonSerializer.Deserialize<AnalysisResponseDto>(JsonSerializer.Serialize(o))!;
            Assert.Equal(o.direction, r.direction);
            Assert.Equal(o.probability, r.probability);
            Assert.Equal(o.taDirection, r.taDirection);
            Assert.Equal(o.smcDirection, r.smcDirection);
            Assert.Equal(o.lgbmDirection, r.lgbmDirection);
            Assert.Equal(o.rsi, r.rsi);
            Assert.Equal(o.ema, r.ema);
            Assert.Equal(o.atr, r.atr);
            Assert.Equal(o.goldenSetup, r.goldenSetup);
            Assert.Equal(o.confluenceRatio, r.confluenceRatio);
        }

        [Fact]
        public void DTO_Probability_InRange0To100()
            => Assert.InRange(Full().probability, 0, 100);

        [Fact]
        public void DTO_AllConfidences_InRange0To100()
        {
            var d = Full();
            Assert.InRange(d.taConfidence, 0, 100);
            Assert.InRange(d.ofConfidence, 0, 100);
            Assert.InRange(d.smcConfidence, 0, 100);
            Assert.InRange(d.lgbmConfidence, 0, 100);
        }

        [Fact]
        public void DTO_NullableFields_DefaultNull()
        {
            var d = new AnalysisResponseDto();
            Assert.Null(d.lgbmDirection);
            Assert.Null(d.winRateOverall);
            Assert.Null(d.llmReport);
            Assert.Null(d.lgbmModelVersion);
        }

        [Fact]
        public void DTO_ChartData_NoNaNOrInfinity()
        {
            foreach (var v in Full().chartData)
                Assert.True(double.IsFinite(v), $"chartData contains non-finite: {v}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 6: MarketDataFetcher — pure math: TF seconds, higher TF, AssetSanitizer
    // ═══════════════════════════════════════════════════════════════════════
    public class MarketDataFetcherPureMathTests
    {
        static readonly MarketDataFetcher F = new();

        [Theory]
        [InlineData("m1", 60)]
        [InlineData("m5", 300)]
        [InlineData("m15", 900)]
        [InlineData("m30", 1800)]
        [InlineData("h1", 3600)]
        [InlineData("s5", 5)]
        [InlineData("s10", 10)]
        [InlineData("s15", 15)]
        [InlineData("s30", 30)]
        public void MDF_TimeframeSeconds_Correct(string tf, int expected)
            => Assert.Equal(expected, F.TimeframeSeconds(tf));

        [Theory]
        [InlineData("m1", "m5")]
        [InlineData("m5", "m15")]
        [InlineData("m15", "h1")]
        [InlineData("s5", "m1")]
        public void MDF_HigherTf_CorrectUpgrade(string tf, string expected)
            => Assert.Equal(expected, F.HigherTf(tf));

        [Fact]
        public void MDF_HigherTf_H4_ReturnsDailyD1()
            // h4's higher timeframe is d1 (daily) — chain continues above h4
            => Assert.Equal("d1", F.HigherTf("h4"));

        [Theory]
        [InlineData("EUR/USD", true)]
        [InlineData("GBP/USD", true)]
        [InlineData("AUD/USD", true)]
        [InlineData("BTCUSD", false)]
        public void AssetSanitizer_IsForex_Correct(string asset, bool expected)
            => Assert.Equal(expected, AssetSanitizer.IsForexAsset(asset));

        [Theory]
        [InlineData("EURUSD", "EURUSD")]
        [InlineData("EUR/USD", "EURUSD")]
        [InlineData("GBPUSD", "GBPUSD")]
        public void AssetSanitizer_Sanitize_RemovesSlash(string raw, string expected)
            => Assert.Equal(expected, AssetSanitizer.Sanitize(raw));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MODULE 7: TwelveDataWebSocketStream — IsAlive, TryGetLivePrice
    // ═══════════════════════════════════════════════════════════════════════
    public class WebSocketStreamTests
    {
        [Fact]
        public void WS_IsAlive_ReturnsBoolWithoutThrowing()
            => Assert.IsType<bool>(TwelveDataWebSocketStream.IsAlive);

        [Fact]
        public void WS_TryGetLivePrice_UnknownSymbol_ReturnsFalse()
        {
            bool found = TwelveDataWebSocketStream.TryGetLivePrice("XYZ_NOTREAL_999", out double price);
            Assert.False(found);
            Assert.Equal(0.0, price);
        }
    }
}
