using System;
using Xunit;
using ValutaBot.MiniApp.Indicators;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class StatefulIndicatorsTests
    {
        // ── StatefulRsi Tests ──────────────────────────────────────────────

        [Fact]
        public void StatefulRsi_WarmupPeriod_Returns50()
        {
            var rsi = new StatefulRsi(14);
            for (int i = 0; i < 14; i++)
            {
                Assert.Equal(50.0, rsi.Update(100.0));
                Assert.False(rsi.IsWarm);
            }
        }

        [Fact]
        public void StatefulRsi_ConstantUp_Returns100()
        {
            var rsi = new StatefulRsi(14);
            double price = 100.0;
            // Warmup (14 periods) + some more to let it stabilize
            for (int i = 0; i < 30; i++)
            {
                price += 1.0;
                rsi.Update(price);
            }
            Assert.True(rsi.IsWarm);
            Assert.Equal(100.0, rsi.Update(price + 1.0), precision: 2);
        }

        [Fact]
        public void StatefulRsi_ConstantDown_Returns0()
        {
            var rsi = new StatefulRsi(14);
            double price = 100.0;
            for (int i = 0; i < 30; i++)
            {
                price -= 1.0;
                rsi.Update(price);
            }
            Assert.True(rsi.IsWarm);
            Assert.Equal(0.0, rsi.Update(price - 1.0), precision: 2);
        }

        // ── StatefulTrueAdx Tests ──────────────────────────────────────────

        [Fact]
        public void StatefulTrueAdx_WarmupPeriod_Returns20()
        {
            var adx = new StatefulTrueAdx(14);
            for (int i = 0; i < 28; i++) // 14 period requires 28 for ADX warmup
            {
                Assert.Equal(20.0, adx.Update(105, 95, 100));
            }
        }

        [Fact]
        public void StatefulTrueAdx_StrongUptrend_AdxRises()
        {
            var adx = new StatefulTrueAdx(14);
            double high = 101, low = 99, close = 100;
            for (int i = 0; i < 40; i++)
            {
                high += 2; low += 2; close += 2;
                adx.Update(high, low, close);
            }
            Assert.True(adx.IsWarm);
            // In a strong strict uptrend, ADX should be high (> 50) and PDI > MDI
            Assert.True(adx.LastAdx > 50.0);
            Assert.True(adx.LastPdi > adx.LastMdi);
        }

        [Fact]
        public void StatefulTrueAdx_FirstLiveTick_ValidPdiMdi()
        {
            // This test would have failed before our ADX bug fix
            var adx = new StatefulTrueAdx(14);
            double h = 100, l = 90, c = 95;
            for (int i = 0; i <= 14; i++) // warmup is 14 ticks
            {
                h += 1; l += 1; c += 1;
                adx.Update(h, l, c);
            }
            
            // First tick after basic warmup (period + 1)
            adx.Update(h + 2, l + 2, c + 2);
            
            // PDI and MDI should be valid numbers, not NaN or arbitrarily massive
            Assert.False(double.IsNaN(adx.LastPdi));
            Assert.False(double.IsNaN(adx.LastMdi));
            Assert.True(adx.LastPdi >= 0 && adx.LastPdi <= 100);
            Assert.True(adx.LastMdi >= 0 && adx.LastMdi <= 100);
        }

        // ── StatefulEma Tests ──────────────────────────────────────────────

        [Fact]
        public void StatefulEma_WarmupPeriod_ReturnsZero()
        {
            var ema = new StatefulEma(9);
            for (int i = 0; i < 8; i++)
            {
                Assert.Equal(0.0, ema.Update(100.0));
                Assert.False(ema.IsWarm);
            }
            Assert.Equal(100.0, ema.Update(100.0)); // 9th tick returns SMA
            Assert.True(ema.IsWarm);
        }

        [Fact]
        public void StatefulEma_ConstantPrice_ConvergesToPrice()
        {
            var ema = new StatefulEma(9);
            for (int i = 0; i < 20; i++) ema.Update(100.0);
            Assert.Equal(100.0, ema.Update(100.0), precision: 5);
        }

        // ── Gatekeeper Tests ───────────────────────────────────────────────

        [Fact]
        public void Gatekeeper_DeadMarket_ReturnsFalse()
        {
            var engine = new TechnicalAnalysisEngine();
            double[] prices = new double[20];
            for (int i = 0; i < 20; i++) prices[i] = 1.1000;
            
            var result = engine.ValidateMarketGatekeeper("EURUSD", "1m", prices);
            Assert.False(result.IsTradeable);
            Assert.Contains("состоянии застоя", result.Reason);
        }
        
        [Fact]
        public void Gatekeeper_ZeroAtrFallback_WorksCorrectly()
        {
            var engine = new TechnicalAnalysisEngine();
            double[] prices = new double[20];
            // Price range is 0.000005, which is less than the new EURUSD fallback threshold (0.00001)
            for (int i = 0; i < 20; i++) prices[i] = 1.10000 + (i % 2 == 0 ? 0.000005 : 0);
            
            var result = engine.ValidateMarketGatekeeper("EURUSD", "1m", prices);
            Assert.False(result.IsTradeable);
        }
    }
}
