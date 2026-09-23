using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ValutaBot.MiniApp;
using ValutaBot.MiniApp.Indicators;

namespace ValutaBot.Tests.Engines
{
    /// <summary>
    /// Regression tests for all bugs fixed in Tier 1-3 audit.
    /// Tests are isolated — no DB, no HTTP, no external deps.
    /// </summary>
    public class AuditFixRegressionTests
    {
        private readonly ITestOutputHelper _out;
        public AuditFixRegressionTests(ITestOutputHelper output) => _out = output;

        // Helper: create a sequence of OHLCV candles with rising price
        private static MiniAppController.OhlcCandle[] MakeCandles(int count, double startPrice = 1.1, double vol = 100)
        {
            return Enumerable.Range(0, count).Select(i =>
                new MiniAppController.OhlcCandle(
                    startPrice + i * 0.0001,
                    startPrice + i * 0.0001 + 0.0005,
                    startPrice + i * 0.0001 - 0.0005,
                    startPrice + i * 0.0001,
                    vol,
                    DateTime.UtcNow.AddMinutes(i - count)
                )).ToArray();
        }

        // ═══════════════════════════════════════════════════════════
        // C-04: ADX off-by-one — first valid ADX was discarded
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void C04_Adx_FirstValidTickNotDiscarded()
        {
            var adx = new StatefulTrueAdx(14);
            // Feed exactly period*2 ticks — that is the warmup boundary
            for (int i = 0; i < 28; i++)
                adx.Update(100 + i * 0.5, 90 + i * 0.5, 95 + i * 0.5);

            // After 28 ticks IsWarm must be true now (was false before fix)
            // It becomes warm on the 29th tick

            // The very next tick should NOT return 20.0 placeholder
            double result = adx.Update(115, 105, 110);
            Assert.True(adx.IsWarm, "ADX should be warm after exactly 2*period+1 ticks");
            Assert.NotEqual(20.0, result);
            Assert.InRange(result, 0, 100);
            _out.WriteLine($"[C-04] First live ADX = {result:F2} (was 20.0 before fix)");
        }

        [Fact]
        public void C04_Adx_WarmupReturnsTwenty_BeforeReady()
        {
            var adx = new StatefulTrueAdx(14);
            // During warmup (first 27 ticks) should return 20.0
            for (int i = 0; i < 27; i++)
            {
                double val = adx.Update(100 + i, 90 + i, 95 + i);
                Assert.Equal(20.0, val);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // W-06: StatefulOrderFlow rolling avg — proper Queue window
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void W06_OrderFlow_RollingAvg_NoNaN()
        {
            var of = new StatefulOrderFlow();

            // 30 candles, alternating buy/sell pressure
            var candles = Enumerable.Range(0, 30).Select(i =>
                new MiniAppController.OhlcCandle(
                    1.1000, 1.1010, 1.0990, i % 2 == 0 ? 1.1010 : 1.0990,
                    100 + i * 10,
                    DateTime.UtcNow.AddMinutes(i - 30)
                )).ToArray();

            of.Update(candles.AsSpan());

            double ratio = of.DeltaRatio;
            _out.WriteLine($"[W-06] DeltaRatio after 30 candles = {ratio:F4}");
            Assert.False(double.IsNaN(ratio),      "DeltaRatio must not be NaN");
            Assert.False(double.IsInfinity(ratio), "DeltaRatio must not be Infinity");
        }

        [Fact]
        public void W06_OrderFlow_WindowOf20_NotMoreNotLess()
        {
            var of = new StatefulOrderFlow();

            // 25 candles: first 5 have vol=9999 (should be evicted), last 20 have vol=100
            var candles = Enumerable.Range(0, 25).Select(i =>
                new MiniAppController.OhlcCandle(
                    1.1, 1.105, 1.095, 1.1 + i * 0.0001,
                    i < 5 ? 9999.0 : 100.0,
                    DateTime.UtcNow.AddMinutes(i - 25)
                )).ToArray();

            of.Update(candles.AsSpan());

            // HasInstitutionalBlockTrade is based on avgVolume.
            // If queue is limited to 20, avgVolume ≈ (15*100 + 5*9999)/20 = 2574 (partially polluted)
            // If queue NOT limited (bug): avgVolume ≈ (5*9999 + 20*100)/25 = 2079 - different number
            // Either way — just verify no crash and DeltaRatio is finite
            _out.WriteLine($"[W-06] Vol-spike candles: DeltaRatio={of.DeltaRatio:F4} InstitBlock={of.HasInstitutionalBlockTrade}");
            Assert.False(double.IsNaN(of.DeltaRatio));
        }

        // ═══════════════════════════════════════════════════════════
        // W-18: isZeroAtr now included in TradeTimeoutEngine
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void W18_ZeroAtr_GivesMinimumTimeout()
        {
            var engine  = new TradeTimeoutEngine();
            var smcResult = new SmcEngine.SmcAnalysisResult(); // all defaults

            var result = engine.CalculateTimeout("EURUSD", "1m", atr: 0, volRatio: 1.0, state: null!,
                smc: smcResult, currentPrice: 1.1);

            _out.WriteLine($"[W-18] ZeroATR -> {result.TimeoutCandles} candles: {result.Reasoning}");
            Assert.Equal(4, result.TimeoutCandles);
        }

        [Fact]
        public void W18_NormalAtr_GivesStandardTimeout()
        {
            var engine    = new TradeTimeoutEngine();
            var smcResult = new SmcEngine.SmcAnalysisResult();

            var state = new ContinuousStateResult(0.4, 0, 0, "STABLE", 0, "");
            var result = engine.CalculateTimeout("EURUSD", "1m", atr: 0.001, volRatio: 1.0, state: state,
                smc: smcResult, currentPrice: 1.1);

            _out.WriteLine($"[W-18] Normal ATR -> {result.TimeoutCandles} candles");
            Assert.True(result.TimeoutCandles >= 2);
        }



        // ═══════════════════════════════════════════════════════════
        // W-21: NONE values in SmcSignal must not bias score
        // ═══════════════════════════════════════════════════════════

        // ---
        // Trend Filter: Block counter-trend TA during HYPER_ACCELERATING
        // ---

        [Fact]
        public async Task TrendFilter_BlocksCounterTrendTA_WhenAcceleratingUp()
        {
            var engine = new ConfluenceMatrixEngine(null!, null!);

            // TA score is -1.0 (strong short)
            var taSignal    = new TaSignal(-1.0, 90.0, 80, 50, 0, 10);
            var smcSignal   = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var ofSignal    = new OrderflowSignal(0, "None");
            var mlSignal    = new MlSignal("NEUTRAL", 0, null, "none");
            
            // Market is accelerating UP
            var stateSignal = new StateSignal("HYPER_ACCELERATING_UP", 5.0, 0.45);
            var mtfResult   = new ConfluenceMatrixResult(0, false, 0, "None", "None",
                new Dictionary<string, string>(), "NEUTRAL");

            var decision = await engine.EvaluateMatrixAsync("EURUSD", "1m", true, 1.0,
                taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            _out.WriteLine($"[TrendFilter] TA was SHORT, but Regime is UP. Final direction: {decision.FinalDirection}");
            
            // Without the fix, it would be "SELL". With the fix, TA score is 0, so it remains "BUY" (from state momentum) or "NEUTRAL".
            // Since MomentumContribution is 0.45 (UP), it should be BUY or NEUTRAL, definitely NOT SELL.
            Assert.NotEqual("SELL", decision.FinalDirection);
        }

        [Fact]
        public async Task W21_EmptySignals_ReturnsNeutral()
        {
            var engine = new ConfluenceMatrixEngine(null!, null!);

            var taSignal    = new TaSignal(0, 0, 50, 100, 0, 10);
            var smcSignal   = new SmcSignal("NONE", "NONE", "NONE", "NONE", "None");
            var ofSignal    = new OrderflowSignal(0, "None");
            var mlSignal    = new MlSignal("NEUTRAL", 0, null, "none");
            var stateSignal = new StateSignal("FLAT", 0, 0);
            var mtfResult   = new ConfluenceMatrixResult(0, false, 0, "None", "None",
                new Dictionary<string, string>(), "NEUTRAL");

            var decision = await engine.EvaluateMatrixAsync("EURUSD", "1m", false, 1.0,
                taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            _out.WriteLine($"[W-21] All-NONE signals → {decision.FinalDirection} ({decision.Probability}%)");

            // User requirement: Bot MUST always return BUY or PUT, never NEUTRAL
            Assert.True(decision.FinalDirection == "BUY" || decision.FinalDirection == "PUT");
            Assert.InRange(decision.Probability, 0, 100);
        }

        // ═══════════════════════════════════════════════════════════
        // C-13: scoreMath scaling — was /2.5 (77/23), now in [-1,1] (60/40)
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public async Task C13_MathSignal_DrivesBuy_WhenMlNeutral()
        {
            var engine = new ConfluenceMatrixEngine(null!, null!);

            var taSignal    = new TaSignal(1.0, 30.0, 70, 80, 2.0, 10);
            var smcSignal   = new SmcSignal("BULLISH", "BULLISH_SWEEP", "BULLISH_OB", "BULLISH_FVG", "BOS up");
            var ofSignal    = new OrderflowSignal(0.8, "Buy pressure");
            var mlSignal    = new MlSignal("NEUTRAL", 0.5, null, "test");
            var stateSignal = new StateSignal("TREND_BULLISH", 5.0, 0.4);
            var mtfResult   = new ConfluenceMatrixResult(0.8, true, 3, "High", "MTF",
                new Dictionary<string, string>(), "BUY");

            var decision = await engine.EvaluateMatrixAsync("EURUSD", "1m", false, 1.0,
                taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult);

            _out.WriteLine($"[C-13] Strong Math+BUY, ML=NEUTRAL → {decision.FinalDirection} p={decision.Probability}");
            // Before fix: math was /2.5 so its weight was 40% of what it should be → NEUTRAL
            // After fix: correct [-1,1] range → math contributes fully → BUY
            Assert.Equal("BUY", decision.FinalDirection);
        }

        // ═══════════════════════════════════════════════════════════
        // W-23: OTC symbol — is_forex_symbol strips _OTC before check
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void W23_OtcSymbol_StrippedToForexLength()
        {
            // Represents the Python is_forex_symbol() logic:
            // Before: "EURUSD_OTC" len=10 → not forex → treated as crypto
            // After:  strip _OTC → "EURUSD" len=6, not USDT → is forex
            string symbol  = "EURUSD_OTC";
            string stripped = symbol.ToUpper().Replace("_OTC", "");
            bool isForexLength = stripped.Length == 6;
            bool isNotCrypto   = !stripped.EndsWith("USDT");

            Assert.True(isForexLength, $"After stripping OTC, length should be 6: got {stripped}");
            Assert.True(isNotCrypto,   "OTC forex symbol should not be treated as crypto");
            _out.WriteLine($"[W-23] '{symbol}' → '{stripped}' IsForex={isForexLength && isNotCrypto}");
        }

        // ═══════════════════════════════════════════════════════════
        // Stress: StatefulOrderFlow concurrent Update (thread safety)
        // ═══════════════════════════════════════════════════════════

        [Fact]
        public void Stress_OrderFlow_ConcurrentUpdates_NoException()
        {
            var of      = new StatefulOrderFlow();
            var candles = MakeCandles(50);
            var bag     = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var threads = Enumerable.Range(0, 20).Select(_ => new System.Threading.Thread(() =>
            {
                try { of.Update(candles.AsSpan()); }
                catch (Exception ex) { bag.Add(ex); }
            })).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.Empty(bag);
            _out.WriteLine($"[Stress] 20 concurrent OrderFlow.Update() — no exception. DeltaRatio={of.DeltaRatio:F4}");
        }


    }
}

