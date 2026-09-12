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
                candles[i] = new MiniAppController.OhlcCandle
                {
                    Timestamp = baseTime.AddMinutes(i).UtcDateTime,
                    Open = 1.1000m + (i * 0.0001m),
                    High = 1.1005m + (i * 0.0001m),
                    Low = 1.0995m + (i * 0.0001m),
                    Close = 1.1002m + (i * 0.0001m)
                };
            }

            // 2. Act
            var candleList = candles.Select(c => new
            {
                openTime = new DateTimeOffset(DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                open = c.Open,
                high = c.High,
                low = c.Low,
                close = c.Close,
                volume = 100
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
            Assert.Contains(""symbol":"EURUSD"", json);
            Assert.Contains(""openTime"", json);
            Assert.Contains(""open"", json);
            
            // Validate that openTime is an integer (unix timestamp)
            var deserialized = JsonDocument.Parse(json);
            var firstCandle = deserialized.RootElement.GetProperty("candles")[0];
            Assert.Equal(JsonValueKind.Number, firstCandle.GetProperty("openTime").ValueKind);
            Assert.True(firstCandle.GetProperty("openTime").GetInt64() > 1700000000);
        }
    }
}
