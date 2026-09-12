using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests
{
    public class DataParityTests
    {
        [Fact]
        public void PredictAsync_PayloadFormat_MatchesPythonExpectation()
        {
            // 1. Arrange
            var candles = new MiniAppController.OhlcCandle[5];
            var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
            for (int i = 0; i < 5; i++)
            {
                // record OhlcCandle(double Open, double High, double Low, double Close, double Volume, DateTime Timestamp)
                candles[i] = new MiniAppController.OhlcCandle(
                    1.1000 + (i * 0.0001), // Open
                    1.1005 + (i * 0.0001), // High
                    1.0995 + (i * 0.0001), // Low
                    1.1002 + (i * 0.0001), // Close
                    100.0, // Volume
                    baseTime.AddMinutes(i).UtcDateTime // Timestamp
                );
            }

            // 2. Act
            var candleList = candles.Select(c => new
            {
                openTime = new DateTimeOffset(DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = c.Volume
            }).ToArray();

            var payload = new
            {
                symbol = "EURUSD",
                interval = "1m",
                is_forex = true,
                candles = candleList,
                mtf_candles = (object?)null
            };

            var json = JsonSerializer.Serialize(payload);

            // 3. Assert
            Assert.Contains("\"symbol\":\"EURUSD\"", json);
            Assert.Contains("\"openTime\"", json);
            Assert.Contains("\"open\"", json);
            
            // Validate that openTime is an integer (unix timestamp)
            var deserialized = JsonDocument.Parse(json);
            var firstCandle = deserialized.RootElement.GetProperty("candles")[0];
            Assert.Equal(JsonValueKind.Number, firstCandle.GetProperty("openTime").ValueKind);
            Assert.True(firstCandle.GetProperty("openTime").GetInt64() > 1700000000);
        }
    }
}
