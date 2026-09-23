using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

public interface IOnlineMetaLearner
{
    double Predict(string asset, string timeframe, double ta, double of, double smc, double ml, bool tfConflict);
    void PartialFit(string asset, string timeframe, double ta, double of, double smc, double ml, bool wasWin, string direction);
}

public class OnlineMetaLearner : IOnlineMetaLearner
{
    private readonly ConcurrentDictionary<string, double[]> _weights = new();
    private readonly ConcurrentDictionary<string, int> _updateCounts = new();
    private readonly string _savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "meta_weights.json");
    private const double InitialLearningRate = 0.10;
    private const double LrDecay             = 0.002;
    private const double WeightDecay         = 0.999;

    public OnlineMetaLearner()
    {
        LoadWeights();
    }

    private string GetKey(string asset, string timeframe) => $"{asset}_{timeframe}";

    private double[] GetOrCreateWeights(string key)
    {
        return _weights.GetOrAdd(key, _ => new double[] { 0.0, 1.0, 1.0, 1.0, 1.0 }); // [Bias, TA, OF, SMC, ML]
    }

    private double GetLearningRate(string key)
    {
        int t = _updateCounts.GetOrAdd(key, 0);
        return InitialLearningRate / (1.0 + LrDecay * t);
    }

    public double Predict(string asset, string timeframe, double ta, double of, double smc, double ml, bool tfConflict)
    {
        // STRICT SANITIZATION
        if (!double.IsFinite(ta)) ta = 0.0;
        if (!double.IsFinite(of)) of = 0.0;
        if (!double.IsFinite(smc)) smc = 0.0;
        if (!double.IsFinite(ml)) ml = 0.0;

        ta = Math.Clamp(ta, -1.0, 1.0);
        of = Math.Clamp(of, -1.0, 1.0);
        smc = Math.Clamp(smc, -1.0, 1.0);
        ml = Math.Clamp(ml, -1.0, 1.0);

        // Phase 3: Bayesian Log-Odds Transformation
        // Raw inputs [-1.0, 1.0] are mapped to probabilities, then to log-odds.
        double LogOdds(double val) 
        {
            double p = (Math.Clamp(val, -0.99, 0.99) + 1.0) / 2.0;
            return Math.Log(p / (1.0 - p));
        }

        double lo_ta = LogOdds(ta);
        double lo_of = LogOdds(of);
        double lo_smc = LogOdds(smc);
        double lo_ml = LogOdds(ml);

        var w = GetOrCreateWeights(GetKey(asset, timeframe));
        double z;
        lock (w) // DATA RACE FIX: Read weights safely
        {
            z = w[0] + (w[1] * lo_ta) + (w[2] * lo_of) + (w[3] * lo_smc) + (w[4] * lo_ml);
        }
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    
    public void PartialFit(string asset, string timeframe, double ta, double of, double smc, double ml, bool wasWin, string direction)
    {
        if (direction == "NEUTRAL") return;

        // STRICT SANITIZATION
        if (!double.IsFinite(ta)) ta = 0.0;
        if (!double.IsFinite(of)) of = 0.0;
        if (!double.IsFinite(smc)) smc = 0.0;
        if (!double.IsFinite(ml)) ml = 0.0;

        ta = Math.Clamp(ta, -1.0, 1.0);
        of = Math.Clamp(of, -1.0, 1.0);
        smc = Math.Clamp(smc, -1.0, 1.0);
        ml = Math.Clamp(ml, -1.0, 1.0);

        // Phase 3: Bayesian Log-Odds Transformation
        double LogOdds(double val) 
        {
            double prob = (Math.Clamp(val, -0.99, 0.99) + 1.0) / 2.0;
            return Math.Log(prob / (1.0 - prob));
        }

        double lo_ta = LogOdds(ta);
        double lo_of = LogOdds(of);
        double lo_smc = LogOdds(smc);
        double lo_ml = LogOdds(ml);

        double y = (direction == "BUY" && wasWin) || (direction == "PUT" && !wasWin) ? 1.0 : 0.0;

        string key = GetKey(asset, timeframe);
        var w  = GetOrCreateWeights(key);
        
        bool isSubMinute = timeframe.StartsWith("s", StringComparison.OrdinalIgnoreCase);
        double penaltyMultiplier = isSubMinute ? 2.0 : 4.0;
        double lossDecay         = isSubMinute ? 0.95 : 0.85;

        lock (w) // DATA RACE FIX: Mutate weights atomically
        {
            double z = w[0] + (w[1] * lo_ta) + (w[2] * lo_of) + (w[3] * lo_smc) + (w[4] * lo_ml);
            double p = 1.0 / (1.0 + Math.Exp(-z));
            double error = y - p;
            
            double lr = GetLearningRate(key);
            if (!wasWin)
            {
                lr *= penaltyMultiplier;
                for (int i = 1; i < w.Length; i++) w[i] *= lossDecay;
            }
            else
            {
                for (int i = 1; i < w.Length; i++) w[i] = 1.0 - ((1.0 - w[i]) * WeightDecay);
            }

            // Stochastic Gradient Descent step using Log-Odds gradients
            w[0] += lr * error;
            w[1] += lr * error * lo_ta;
            w[2] += lr * error * lo_of;
            w[3] += lr * error * lo_smc;
            w[4] += lr * error * lo_ml;

            // Cap L1 Norm
            for (int i = 1; i < w.Length; i++)
            {
                if (w[i] > 4.0) w[i] = 4.0;
                if (w[i] < -4.0) w[i] = -4.0;
            }
        }

        _updateCounts.AddOrUpdate(key, 1, (_, v) => v + 1);
        _ = SaveWeightsAsync();
    }


    private void LoadWeights()
    {
        try
        {
            if (File.Exists(_savePath))
            {
                var json = File.ReadAllText(_savePath);
                var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, double[]>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict) _weights[kvp.Key] = kvp.Value;
                }
            }
        }
        catch { }
    }

    private readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    private Task SaveWeightsAsync()
    {
        return Task.Run(async () =>
        {
            if (!_saveLock.Wait(0)) return; // I/O CRASH FIX: Debounce overlapping saves
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
                var dict = new System.Collections.Generic.Dictionary<string, double[]>();
                foreach (var kvp in _weights) 
                {
                    lock (kvp.Value) { dict[kvp.Key] = (double[])kvp.Value.Clone(); }
                }
                
                string json = JsonSerializer.Serialize(dict);
                string tmpPath = _savePath + $".tmp.{Guid.NewGuid():N}"; // I/O CRASH FIX: Unique temp file
                await File.WriteAllTextAsync(tmpPath, json);
                File.Move(tmpPath, _savePath, overwrite: true);
            }
            catch { }
            finally
            {
                _saveLock.Release();
            }
        });
    }
}
