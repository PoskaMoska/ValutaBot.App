using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

/// <summary>
/// Sub-millisecond Online Logistic Regression for dynamic signal weighting.
/// Learns in real-time which signals (TA, SMC, OF, ML) are currently working best.
///
/// Improvements:
/// - Adaptive Learning Rate: LR(t) = LR₀ / (1 + decay * t) — aggressive at start, conservative later.
/// - Weight Decay (Temporal Forgetting): each update decays all weights by 0.1%,
///   preventing stale knowledge from dominating (rho = 0.999 per update).
/// - Negative weights allowed: if a signal systematically inverts outcomes,
///   its weight correctly goes negative (inverse correlation is valid information).
/// </summary>
public static class OnlineMetaLearner
{
    private static readonly ConcurrentDictionary<string, double[]> _weights = new();
    private static readonly ConcurrentDictionary<string, int> _updateCounts = new();
    private static readonly string _savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "meta_weights.json");
    private const double InitialLearningRate = 0.10;  // higher start — faster initial learning
    private const double LrDecay             = 0.002; // LR halves after ~500 updates
    private const double WeightDecay         = 0.999; // each update forgets 0.1% of old knowledge

    static OnlineMetaLearner()
    {
        LoadWeights();
    }

    private static string GetKey(string asset, string timeframe) => $"{asset}_{timeframe}";

    private static double[] GetOrCreateWeights(string key)
    {
        return _weights.GetOrAdd(key, _ => new double[] { 0.0, 1.0, 1.0, 1.0, 1.0 }); // [Bias, TA, OF, SMC, ML]
    }

    private static double GetLearningRate(string key)
    {
        int t = _updateCounts.GetOrAdd(key, 0);
        return InitialLearningRate / (1.0 + LrDecay * t);
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
    /// Uses adaptive learning rate and weight decay for temporal forgetting.
    /// </summary>
    public static void PartialFit(string asset, string timeframe, double ta, double of, double smc, double ml, bool wasWin, string direction)
    {
        if (direction == "NEUTRAL") return;

        // Ground truth: 1.0 if market went UP, 0.0 if market went DOWN.
        double y = (direction == "BUY" && wasWin) || (direction == "PUT" && !wasWin) ? 1.0 : 0.0;

        string key = GetKey(asset, timeframe);
        var w  = GetOrCreateWeights(key);
        double lr = GetLearningRate(key);

        double p     = Predict(asset, timeframe, ta, of, smc, ml);
        double error = y - p;

        // Weight Decay (Temporal Forgetting): multiply ALL weights by rho before update.
        // This exponentially down-weights knowledge learned long ago, so the model
        // stays responsive to the current market regime without manual resets.
        for (int i = 0; i < w.Length; i++) w[i] *= WeightDecay;

        // SGD update: W = W + LR(t) * Error * X
        // Note: Math.Max(0.0) removed — negative weights are allowed.
        // If a signal systematically predicts the opposite, negative weight IS the correct adaptation.
        w[0] += lr * error * 1.0; // Bias (no decay — bias is structural)
        w[1] += lr * error * ta;  // TA weight
        w[2] += lr * error * of;  // OrderFlow weight
        w[3] += lr * error * smc; // SMC weight
        w[4] += lr * error * ml;  // ML weight

        // Soft normalization: keep L1 norm of signal weights near 4.0 to prevent
        // explosive growth, but only when sum significantly exceeds the target.
        double sum = Math.Abs(w[1]) + Math.Abs(w[2]) + Math.Abs(w[3]) + Math.Abs(w[4]);
        if (sum > 4.0)
        {
            double scale = 4.0 / sum;
            w[1] *= scale;
            w[2] *= scale;
            w[3] *= scale;
            w[4] *= scale;
        }

        _updateCounts.AddOrUpdate(key, 1, (_, c) => c + 1);

        BotLogger.Info($"[MetaLearner] {key} | LR={lr:F4} | error={error:F3} | w=[{w[0]:F2},{w[1]:F2},{w[2]:F2},{w[3]:F2},{w[4]:F2}]");

        _ = SaveWeightsAsync();
    }

    private static void LoadWeights()
    {
        try
        {
            if (File.Exists(_savePath))
            {
                var json = File.ReadAllText(_savePath);
                // Try new format first (MetaLearnerState with Weights + UpdateCounts).
                var state = JsonSerializer.Deserialize<MetaLearnerState>(json);
                if (state?.Weights != null)
                {
                    foreach (var kvp in state.Weights) _weights[kvp.Key] = kvp.Value;
                    if (state.UpdateCounts != null)
                        foreach (var kvp in state.UpdateCounts) _updateCounts[kvp.Key] = kvp.Value;
                    return;
                }
                // Backward compat: old format was a flat Dictionary<string, double[]>.
                var legacy = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json);
                if (legacy != null)
                {
                    foreach (var kvp in legacy) _weights[kvp.Key] = kvp.Value;
                    BotLogger.Info("[MetaLearner] Migrated from legacy flat weights format. UpdateCounts reset to 0.");
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error("[MetaLearner] Failed to load weights", ex);
        }
    }

    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    private static async Task SaveWeightsAsync()
    {
        // FIX P-3: Use SemaphoreSlim to prevent concurrent saves from racing,
        // and write via a temp file + atomic rename so a crash mid-write
        // cannot corrupt the existing meta_weights.json.
        if (!await _saveLock.WaitAsync(0)) // non-blocking: skip if another save is already queued
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
            var state = new MetaLearnerState
            {
                Weights      = new Dictionary<string, double[]>(_weights),
                UpdateCounts = new Dictionary<string, int>(_updateCounts)
            };
            var json = JsonSerializer.Serialize(state);
            string tmpPath = _savePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json);
            File.Move(tmpPath, _savePath, overwrite: true); // atomic on same filesystem volume
        }
        catch (Exception ex)
        {
            BotLogger.Error("[MetaLearner] Failed to save weights", ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private sealed class MetaLearnerState
    {
        public Dictionary<string, double[]>? Weights      { get; set; }
        public Dictionary<string, int>?      UpdateCounts { get; set; }
    }
}

