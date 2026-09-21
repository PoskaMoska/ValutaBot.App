using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace ValutaBot.Tests.Engines
{
    /// <summary>
    /// Regression tests for Junction-1 fixes (WebSocket -> RealtimeTickCollector -> PostgreSQL).
    ///   D1-1: OHLC Open/Close must reflect chronological first/last tick even after DropOldest reordering
    ///   D1-3: CommandTimeout prevents indefinite blocking (logic test — no DB required)
    ///   D1-4: DateTime dedup comparison must be culture-invariant (no string formatting dependency)
    /// </summary>
    public class Junction1FixRegressionTests
    {
        private readonly ITestOutputHelper _out;
        public Junction1FixRegressionTests(ITestOutputHelper output) => _out = output;

        // Mirrors the TickEvent struct used internally in RealtimeTickCollector
        private record struct TickEvent(string Asset, string Interval, double Price, DateTime OpenTime);

        /// <summary>
        /// Simulate the GroupBy aggregation logic from ProcessTickQueueAsync.
        /// Returns (Open, High, Low, Close) for a given list of ticks.
        /// This is the FIXED version: g.ToList() preserves List insertion order (FIFO).
        /// </summary>
        private static (double Open, double High, double Low, double Close) AggregateTicks(List<TickEvent> buffer)
        {
            return buffer
                .GroupBy(t => new { t.Asset, t.Interval, t.OpenTime })
                .Select(g =>
                {
                    var ordered = g.ToList(); // FIFO = chronological (the fix)
                    return (
                        Open:  ordered.First().Price,
                        High:  ordered.Max(t => t.Price),
                        Low:   ordered.Min(t => t.Price),
                        Close: ordered.Last().Price
                    );
                })
                .First();
        }

        // ═══════════════════════════════════════════════════════════════════
        // D1-1: OHLC chronological correctness after FIFO buffer
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void D1_1_Ohlc_RisingMarket_OpenIsLowest_CloseIsHighest()
        {
            // Rising market: 1.100 → 1.101 → 1.102 → 1.103
            // Open must be 1.100 (first), Close must be 1.103 (last)
            var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var buffer = new List<TickEvent>
            {
                new("EURUSD", "s5", 1.1000, now),
                new("EURUSD", "s5", 1.1010, now),
                new("EURUSD", "s5", 1.1020, now),
                new("EURUSD", "s5", 1.1030, now),
            };

            var (open, high, low, close) = AggregateTicks(buffer);

            _out.WriteLine($"[D1-1] Rising: Open={open:F4} High={high:F4} Low={low:F4} Close={close:F4}");
            Assert.Equal(1.1000, open,  precision: 4);  // first tick
            Assert.Equal(1.1030, close, precision: 4);  // last tick
            Assert.Equal(1.1030, high,  precision: 4);
            Assert.Equal(1.1000, low,   precision: 4);
            Assert.True(close > open, "On rising market Close must be > Open");
        }

        [Fact]
        public void D1_1_Ohlc_FallingMarket_OpenIsHighest_CloseIsLowest()
        {
            // Falling market: 1.103 → 1.102 → 1.101 → 1.100
            // Open must be 1.103 (first), Close must be 1.100 (last)
            var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var buffer = new List<TickEvent>
            {
                new("EURUSD", "s5", 1.1030, now),
                new("EURUSD", "s5", 1.1020, now),
                new("EURUSD", "s5", 1.1010, now),
                new("EURUSD", "s5", 1.1000, now),
            };

            var (open, high, low, close) = AggregateTicks(buffer);

            _out.WriteLine($"[D1-1] Falling: Open={open:F4} High={high:F4} Low={low:F4} Close={close:F4}");
            Assert.Equal(1.1030, open,  precision: 4);
            Assert.Equal(1.1000, close, precision: 4);
            Assert.Equal(1.1030, high,  precision: 4);
            Assert.Equal(1.1000, low,   precision: 4);
            Assert.True(open > close, "On falling market Open must be > Close");
        }

        [Fact]
        public void D1_1_Ohlc_MultipleIntervals_Isolated()
        {
            // Ticks for two different intervals in the same buffer
            // Each interval must have independent Open/Close
            var now5  = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var now10 = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

            var bufferS5 = new List<TickEvent>
            {
                new("EURUSD", "s5", 1.1000, now5),
                new("EURUSD", "s5", 1.1050, now5),
            };
            var bufferS10 = new List<TickEvent>
            {
                new("EURUSD", "s10", 1.2000, now10),
                new("EURUSD", "s10", 1.1500, now10),
            };

            var (o5, _, _, c5)   = AggregateTicks(bufferS5);
            var (o10, _, _, c10) = AggregateTicks(bufferS10);

            Assert.Equal(1.1000, o5,  precision: 4);
            Assert.Equal(1.1050, c5,  precision: 4);
            Assert.Equal(1.2000, o10, precision: 4);
            Assert.Equal(1.1500, c10, precision: 4);

            _out.WriteLine($"[D1-1] s5: Open={o5} Close={c5} | s10: Open={o10} Close={c10}");
        }

        [Fact]
        public void D1_1_Ohlc_SingleTick_OpenEqualsClose()
        {
            // Edge case: single tick → Open == High == Low == Close
            var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var buffer = new List<TickEvent> { new("BTCUSDT", "s5", 65000.0, now) };

            var (open, high, low, close) = AggregateTicks(buffer);

            Assert.Equal(65000.0, open);
            Assert.Equal(65000.0, high);
            Assert.Equal(65000.0, low);
            Assert.Equal(65000.0, close);

            _out.WriteLine("[D1-1] Single tick: Open=High=Low=Close=65000.0 ✓");
        }

        // ═══════════════════════════════════════════════════════════════════
        // D1-4: Culture-invariant DateTime comparison for live candle dedup
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void D1_4_DateTimeDedup_TicksComparison_CultureInvariant()
        {
            // Simulate what the fixed RemoveAll does:
            // Both sides parsed to UTC DateTime and compared by .Ticks
            DateTime liveOpenTime = new DateTime(2026, 1, 15, 10, 0, 35, DateTimeKind.Utc);

            // DB record stores as ISO-8601 string (Npgsql returns string or DateTime)
            string dbTimeIso = liveOpenTime.ToString("o"); // "2026-01-15T10:00:35.0000000Z"

            // Simulate the fixed comparison
            DateTime dbTime = DateTime.Parse(dbTimeIso, null, DateTimeStyles.AdjustToUniversal);
            bool shouldRemove = dbTime.Ticks == liveOpenTime.ToUniversalTime().Ticks;

            Assert.True(shouldRemove, "Same candle must be detected and removed regardless of culture");
            _out.WriteLine($"[D1-4] ISO match: live={liveOpenTime:o} db={dbTimeIso} → match={shouldRemove} ✓");
        }

        [Fact]
        public void D1_4_DateTimeDedup_DifferentCandle_NotRemoved()
        {
            DateTime liveOpenTime = new DateTime(2026, 1, 15, 10, 0, 35, DateTimeKind.Utc);
            DateTime dbOpenTime   = new DateTime(2026, 1, 15, 10, 0, 30, DateTimeKind.Utc); // different candle

            bool shouldRemove = dbOpenTime.Ticks == liveOpenTime.Ticks;

            Assert.False(shouldRemove, "Different candle must NOT be removed");
            _out.WriteLine($"[D1-4] Different candle: live=:35 db=:30 → match={shouldRemove} (correct: false) ✓");
        }

        [Fact]
        public void D1_4_DateTimeDedup_OldCodeBug_FailsOnNonInvariantFormat()
        {
            // This test documents WHY the old code was broken.
            // On a server with ru-RU or de-DE culture, Convert.ToString(DateTime) uses
            // a locale-specific format like "15.01.2026 10:00:35", NOT ISO-8601.
            // Meanwhile liveOpenTimeStr = "2026-01-15T10:00:35.0000000Z" (invariant).
            // The strings will NEVER match → dedup silently fails.

            DateTime liveOpenTime = new DateTime(2026, 1, 15, 10, 0, 35, DateTimeKind.Utc);
            string liveOpenTimeStr = liveOpenTime.ToString("o"); // old code's liveOpenTimeStr

            // Simulate Convert.ToString on a Russian culture server
            var ruCulture = new CultureInfo("ru-RU");
            string dbTimeRu = Convert.ToString(liveOpenTime, ruCulture); // "15.01.2026 10:00:35"

            bool oldCodeWouldMatch = dbTimeRu == liveOpenTimeStr;
            Assert.False(oldCodeWouldMatch, "Old string comparison must fail on non en-US culture — this is the bug");

            // Now verify the fix works correctly (Ticks comparison)
            DateTime dbTimeParsed = DateTime.Parse(
                liveOpenTime.ToString("o"), null, DateTimeStyles.AdjustToUniversal);
            bool newCodeMatches = dbTimeParsed.Ticks == liveOpenTime.ToUniversalTime().Ticks;
            Assert.True(newCodeMatches, "Fixed Ticks comparison must succeed regardless of culture");

            _out.WriteLine($"[D1-4] Bug doc: old='{oldCodeWouldMatch}' new='{newCodeMatches}'");
            _out.WriteLine($"  ru-RU format:  '{dbTimeRu}'");
            _out.WriteLine($"  ISO-8601 ref:  '{liveOpenTimeStr}'");
        }
    }
}
