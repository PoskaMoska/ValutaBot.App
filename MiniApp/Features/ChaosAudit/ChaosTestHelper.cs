using System;
using System.Collections.Generic;
using ValutaBot.MiniApp;

namespace ValutaBot.MiniApp.Features.ChaosAudit;

public static class ChaosTestHelper
{
    public static MiniAppController.OhlcCandle[] GenerateTrendingCandles(
        int count,
        double startPrice = 1.05000,
        double slopePerCandle = 0.00010,
        double wickVolatility = 0.00005,
        int intervalSec = 5)
    {
        var result = new MiniAppController.OhlcCandle[count];
        var baseTime = DateTime.UtcNow.AddSeconds(-count * intervalSec);
        double current = startPrice;

        for (int i = 0; i < count; i++)
        {
            double open = current;
            double close = open + slopePerCandle;
            double high = Math.Max(open, close) + wickVolatility;
            double low = Math.Min(open, close) - wickVolatility;
            double volume = 100.0 + (i % 10) * 5.0;
            DateTime time = baseTime.AddSeconds(i * intervalSec);

            result[i] = new MiniAppController.OhlcCandle(open, high, low, close, volume, time);
            current = close;
        }

        return result;
    }

    /// <summary>
    /// Flips a candlestick sequence vertically around the starting price:
    /// High and Low are swapped, Open and Close inverted.
    /// Perfect mirror for testing directional symmetry invariants.
    /// </summary>
    public static MiniAppController.OhlcCandle[] InvertCandles(MiniAppController.OhlcCandle[] candles)
    {
        if (candles == null || candles.Length == 0) return [];
        double pivot = candles[0].Open;
        var inverted = new MiniAppController.OhlcCandle[candles.Length];

        for (int i = 0; i < candles.Length; i++)
        {
            var c = candles[i];
            double invOpen = 2.0 * pivot - c.Open;
            double invClose = 2.0 * pivot - c.Close;
            // High becomes inverted Low; Low becomes inverted High
            double invHigh = 2.0 * pivot - c.Low;
            double invLow = 2.0 * pivot - c.High;

            inverted[i] = new MiniAppController.OhlcCandle(invOpen, invHigh, invLow, invClose, c.Volume, c.Timestamp);
        }

        return inverted;
    }

    public static MiniAppController.OhlcCandle[] GenerateFlatlineCandles(int count, double price = 1.05000, int intervalSec = 5)
    {
        var result = new MiniAppController.OhlcCandle[count];
        var baseTime = DateTime.UtcNow.AddSeconds(-count * intervalSec);
        for (int i = 0; i < count; i++)
        {
            result[i] = new MiniAppController.OhlcCandle(price, price, price, price, 0.0, baseTime.AddSeconds(i * intervalSec));
        }
        return result;
    }

    public static MiniAppController.OhlcCandle[] GenerateFlashSpikeCandles(int count, double basePrice = 1.05000, double spikeMultiplier = 50.0, int intervalSec = 5)
    {
        var candles = GenerateTrendingCandles(count, basePrice, 0.00001, 0.00002, intervalSec);
        int mid = count / 2;
        var m = candles[mid];
        double giantHigh = m.Close * spikeMultiplier;
        double giantLow = m.Close / spikeMultiplier;
        candles[mid] = new MiniAppController.OhlcCandle(m.Open, giantHigh, giantLow, m.Close, 1_000_000_000.0, m.Timestamp);
        return candles;
    }

    public static MiniAppController.OhlcCandle[] GenerateAlternatingCandles(int count, double low = 1.05000, double high = 1.05500, int intervalSec = 5)
    {
        var result = new MiniAppController.OhlcCandle[count];
        var baseTime = DateTime.UtcNow.AddSeconds(-count * intervalSec);
        for (int i = 0; i < count; i++)
        {
            bool isUp = (i % 2 == 0);
            double open = isUp ? low : high;
            double close = isUp ? high : low;
            result[i] = new MiniAppController.OhlcCandle(open, high + 0.0001, low - 0.0001, close, 50.0, baseTime.AddSeconds(i * intervalSec));
        }
        return result;
    }
}
