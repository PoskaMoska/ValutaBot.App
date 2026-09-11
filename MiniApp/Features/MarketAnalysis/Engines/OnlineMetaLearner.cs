using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// Sub-millisecond Online Logistic Regression for dynamic signal weighting.
/// Learns in real-time which signals (TA, SMC, OF, ML) are currently working best.
/// </summary>
public static class OnlineMetaLearner
{
    private static readonly ConcurrentDictionary<string, double[]> _weights = new();
    private static readonly string _savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "meta_weights.json");
    private const double LearningRate = 0.05;

    static OnlineMetaLearner()
    {
        LoadWeights();
    }

    private static string GetKey(string asset, string timeframe) => $"{asset}_{timeframe}";

    private static double[] GetOrCreateWeights(string key)
    {
        return _weights.GetOrAdd(key, _ => new double[] { 0.0, 1.0, 1.0, 1.0, 1.0 }); // [Bias, TA, OF, SMC, ML]
    }

    /// <summary>
    /// Predicts the probability of the market going UP (BUY).
    /// </summary>
    public static double Predict(string asset, string timeframe, double ta, double of, double smc, double ml)
    {
        var w = GetOrCreateWeights(GetKey(asset, timeframe));
        
        // z = Bias + W1*TA + W2*OF + W3*SMC + W4*ML
        double z = w[0] + (w[1] * ta) + (w[2] * of) + (w[3] * smc) + (w[4] * ml);
        
        // Sigmoid
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    /// <summary>
    /// Performs a single stochastic gradient descent (SGD) step using log-loss.
    /// </summary>
    public static void PartialFit(string asset, string timeframe, double ta, double of, double smc, double ml, bool wasWin, string direction)
    {
        if (direction == "NEUTRAL") return;

        // Ground truth: 1.0 if market went UP, 0.0 if market went DOWN.
        double y = (direction == "BUY" && wasWin) || (direction == "PUT" && !wasWin) ? 1.0 : 0.0;

        var w = GetOrCreateWeights(GetKey(asset, timeframe));
        double p = Predict(asset, timeframe, ta, of, smc, ml);
        double error = y - p;

        // Update weights: W = W + LR * Error * X
        w[0] += LearningRate * error * 1.0; // Bias
        w[1] = Math.Max(0.0, w[1] + LearningRate * error * ta);  // Restrict to positive correlation
        w[2] = Math.Max(0.0, w[2] + LearningRate * error * of);
        w[3] = Math.Max(0.0, w[3] + LearningRate * error * smc);
        w[4] = Math.Max(0.0, w[4] + LearningRate * error * ml);

        // Normalize weights to prevent explosive growth (L1 norm = 4.0)
        double sum = w[1] + w[2] + w[3] + w[4];
        if (sum > 4.0)
        {
            w[1] = (w[1] / sum) * 4.0;
            w[2] = (w[2] / sum) * 4.0;
            w[3] = (w[3] / sum) * 4.0;
            w[4] = (w[4] / sum) * 4.0;
        }

        _ = SaveWeightsAsync();
    }

    private static void LoadWeights()
    {
        try
        {
            if (File.Exists(_savePath))
            {
                var json = File.ReadAllText(_savePath);
                var dict = JsonSerializer.Deserialize<ConcurrentDictionary<string, double[]>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict) _weights[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error("[MetaLearner] Failed to load weights", ex);
        }
    }

    private static async Task SaveWeightsAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            var json = JsonSerializer.Serialize(_weights);
            await File.WriteAllTextAsync(_savePath, json);
        }
        catch (Exception ex)
        {
            BotLogger.Error("[MetaLearner] Failed to save weights", ex);
        }
    }
}

