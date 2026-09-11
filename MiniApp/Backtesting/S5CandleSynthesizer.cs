using System;
using ValutaBot.MiniApp;

namespace ValutaBot.App.MiniApp.Backtesting
{
    /// <summary>
    /// Синтезирует S5-свечи из M1 методом OHLC-декомпозиции.
    /// Каждая M1-свеча → 12 S5-свечей с реалистичным внутренним движением.
    /// ВАЖНО: Результат — синтетика, пригодна для stress-test движков,
    /// но не отражает реальную рыночную структуру S5.
    /// </summary>
    public static class S5CandleSynthesizer
    {
        private const int SubCandlesPerMinute = 12; // 60s / 5s = 12
        private static readonly Random _rng = new(42); // seed для воспроизводимости

        public static MiniAppController.OhlcCandle[] SynthesizeFromM1(
            MiniAppController.OhlcCandle[] m1Candles)
        {
            var result = new MiniAppController.OhlcCandle[m1Candles.Length * SubCandlesPerMinute];
            int idx = 0;

            foreach (var m1 in m1Candles)
            {
                double open  = m1.Open;
                double close = m1.Close;
                double high  = m1.High;
                double low   = m1.Low;
                double range = high - low;
                double scale = range * 0.5; // Scale max bridge variance to half the candle range

                // 1. Generate Brownian increments (random walk)
                double[] dW = new double[SubCandlesPerMinute];
                double[] W = new double[SubCandlesPerMinute + 1];
                W[0] = 0.0;
                
                for (int i = 0; i < SubCandlesPerMinute; i++)
                {
                    // Generate Standard Normal (Box-Muller)
                    double u1 = 1.0 - _rng.NextDouble();
                    double u2 = 1.0 - _rng.NextDouble();
                    double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
                    
                    dW[i] = randStdNormal;
                    W[i + 1] = W[i] + dW[i];
                }
                
                // 2. Transform into Brownian Bridge (guarantees start at 0, end at 0)
                double[] bridge = new double[SubCandlesPerMinute];
                double maxB = 0, minB = 0;
                for (int i = 0; i < SubCandlesPerMinute; i++)
                {
                    bridge[i] = W[i + 1] - ((i + 1.0) / SubCandlesPerMinute) * W[SubCandlesPerMinute];
                    if (bridge[i] > maxB) maxB = bridge[i];
                    if (bridge[i] < minB) minB = bridge[i];
                }
                
                // Normalize bridge amplitude to strictly fit inside our scale
                double rangeB = maxB - minB + 1e-10;
                double bridgeScale = scale / rangeB;
                
                double prevClose = open;
                
                for (int i = 0; i < SubCandlesPerMinute; i++)
                {
                    double fracEnd = (i + 1.0) / SubCandlesPerMinute;
                    
                    // Linear drift + Stochastic Bridge
                    double c = open + (close - open) * fracEnd + (bridge[i] * bridgeScale);
                    c = Math.Max(Math.Min(c, high), low); // strict clamp
                    
                    double o = prevClose;
                    
                    double h = Math.Max(o, c) + (range * 0.1 * _rng.NextDouble());
                    double l = Math.Min(o, c) - (range * 0.1 * _rng.NextDouble());
                    
                    h = Math.Min(h, high);
                    l = Math.Max(l, low);
                    
                    if (i == SubCandlesPerMinute - 1) c = close;

                    DateTime subDt = m1.Timestamp.AddSeconds(i * 5);
                    result[idx++] = new MiniAppController.OhlcCandle(
                        Open: o,
                        High: h,
                        Low: l,
                        Close: c,
                        Volume: 0,
                        Timestamp: subDt);
                        
                    prevClose = c;
                }
            }

            return result;
        }
    }
}
