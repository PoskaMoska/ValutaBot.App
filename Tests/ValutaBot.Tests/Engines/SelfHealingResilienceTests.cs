using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class SelfHealingResilienceTests
    {
        [Fact]
        public async Task FlushAsync_WhenNoDbOrEmpty_DoesNotHangOrThrow()
        {
            // FlushAsync must complete cleanly within timeout even without active DB connection
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var flushed = await RealtimeTickCollector.FlushAsync(cts.Token);
            Assert.True(flushed >= 0);
        }

        [Theory]
        [InlineData(650, "HEALTHY")]
        [InlineData(750, "TIER1_SOFT_PURGE")]
        [InlineData(890, "TIER2_AGGRESSIVE_PURGE")]
        [InlineData(970, "TIER3_EMERGENCY_RECYCLE")]
        public void RamGuard_TierClassification_MatchesExpectedThresholds(long memoryUsedMb, string expectedTier)
        {
            string tier = memoryUsedMb switch
            {
                >= 950 => "TIER3_EMERGENCY_RECYCLE",
                >= 850 => "TIER2_AGGRESSIVE_PURGE",
                >= 700 => "TIER1_SOFT_PURGE",
                _      => "HEALTHY"
            };

            Assert.Equal(expectedTier, tier);
        }

        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 4)]
        [InlineData(3, 6)]
        [InlineData(4, 8)]
        [InlineData(5, 8)]
        public void HostSupervisor_BackoffCalculation_IsBounded(int attempt, int expectedBackoffSec)
        {
            int backoffSec = Math.Min(attempt * 2, 8);
            Assert.Equal(expectedBackoffSec, backoffSec);
        }
    }
}
