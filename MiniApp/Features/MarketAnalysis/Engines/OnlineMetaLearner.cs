using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ValutaBot.App.MiniApp.Data.Repositories;

namespace ValutaBot.MiniApp.Features.MarketAnalysis.Engines;

public interface IOnlineMetaLearner
{
    double Predict(string asset, string timeframe, double ta, double of, double smc, double ml, bool tfConflict);
    void PartialFit(string asset, string timeframe, double ta, double of, double smc, double ml, bool wasWin, string direction);
    double[] GetWeights(string asset, string timeframe);
    Task InitializeFromDbAsync();
    void ResetWeights(string asset, string timeframe);
    void ResetAllWeights();
    Task ResetAllWeightsAndDbAsync();
}

public class OnlineMetaLearner : IOnlineMetaLearner
{
    private readonly ConcurrentDictionary<string, double[]> _weights = new();
    private readonly ConcurrentDictionary<string, int> _updateCounts = new();
    private readonly string _savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "meta_weights_v4.json");
    private const double InitialLearningRate = 0.08;
    private const double LrDecay             = 0.001;
    private const double WeightDecay         = 0.001;

    private static readonly double[] DefaultPriors = { 0.0, 1.10, 0.20, 0.90, 1.35 };

    public OnlineMetaLearner()
    {
        LoadWeightsFromFile();
    }

    private string GetKey(string asset, string timeframe) => $"{asset}_{timeframe}";

    private double[] GetOrCreateWeights(string key)
    {
        // Empirical priors from real trade data:
        //   ML (LightGBM): weight 1.35
        //   TA (Skender/Velocity): weight 1.10
        //   SMC:           weight 0.90
        //   OF (OrderFlow):weight 0.20 (low weight)
        // Order: [Bias, TA, OF, SMC, ML]
        return _weights.GetOrAdd(key, _ => (double[])DefaultPriors.Clone());
    }

    public double[] GetWeights(string asset, string timeframe)
    {
        var w = GetOrCreateWeights(GetKey(asset, timeframe));
        lock (w)
        {
            return (double[])w.Clone();
        }
    }

    public System.Collections.Generic.Dictionary<string, double[]> GetCurrentWeights()
    {
        return new System.Collections.Generic.Dictionary<string, double[]>(_weights);
    }

    public void ResetWeights(string asset, string timeframe)
    {
        string key = GetKey(asset, timeframe);
        _weights[key] = (double[])DefaultPriors.Clone();
        _updateCounts[key] = 0;
        _ = TradeRepository.SaveMetaWeightAsync(key, (double[])DefaultPriors.Clone(), 0);
        BotLogger.Info($"[OnlineMetaLearner] Reset weights for {key} to default priors (ML dominant: {DefaultPriors[4]}).");
    }

    public void ResetAllWeights()
    {
        _weights.Clear();
        _updateCounts.Clear();
        try
        {
            if (File.Exists(_savePath)) File.Delete(_savePath);
        }
        catch { }
        BotLogger.Info("[OnlineMetaLearner] All in-memory and file weights reset to default priors.");
    }

    public async Task ResetAllWeightsAndDbAsync()
    {
        ResetAllWeights();
        await TradeRepository.ClearMetaWeightsAsync();
        BotLogger.Info("[OnlineMetaLearner] All weights reset and DB table meta_learner_weights truncated.");
    }

    private double GetLearningRate(string key)
    {
        int t = _updateCounts.GetOrAdd(key, 0);
        return InitialLearningRate / (1.0 + LrDecay * t);
    }

    public double Predict(string asset, string timeframe, double ta, double of, double smc, double ml, bool tfConflict)
    {
        if (!double.IsFinite(ta)) ta = 0.0;
        if (!double.IsFinite(of)) of = 0.0;
        if (!double.IsFinite(smc)) smc = 0.0;
        if (!double.IsFinite(ml)) ml = 0.0;

        ta = Math.Clamp(ta, -1.0, 1.0);
        of = Math.Clamp(of, -1.0, 1.0);
        smc = Math.Clamp(smc, -1.0, 1.0);
        ml = Math.Clamp(ml, -1.0, 1.0);

        // Bayesian Log-Odds Transformation
        double LogOdds(double val) 
        {
            double p = (val + 1.0) / 2.0;
            p = Math.Clamp(p, 0.15, 0.85); 
            return Math.Log(p / (1.0 - p));
        }

        double lo_ta = LogOdds(ta);
        double lo_of = LogOdds(of);
        double lo_smc = LogOdds(smc);
        double lo_ml = LogOdds(ml);

        var w = GetOrCreateWeights(GetKey(asset, timeframe));
        double z;
        lock (w)
        {
            z = w[0] + (w[1] * lo_ta) + (w[2] * lo_of) + (w[3] * lo_smc) + (w[4] * lo_ml);
        }
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    public void PartialFit(string asset, string timeframe, double ta, double of, double smc, double ml, bool wasWin, string direction)
    {
        if (direction == "NEUTRAL") return;

        if (!double.IsFinite(ta)) ta = 0.0;
        if (!double.IsFinite(of)) of = 0.0;
        if (!double.IsFinite(smc)) smc = 0.0;
        if (!double.IsFinite(ml)) ml = 0.0;

        ta = Math.Clamp(ta, -1.0, 1.0);
        of = Math.Clamp(of, -1.0, 1.0);
        smc = Math.Clamp(smc, -1.0, 1.0);
        ml = Math.Clamp(ml, -1.0, 1.0);

        double LogOdds(double val) 
        {
            double prob = (val + 1.0) / 2.0;
            prob = Math.Clamp(prob, 0.15, 0.85);
            return Math.Log(prob / (1.0 - prob));
        }

        double lo_ta = LogOdds(ta);
        double lo_of = LogOdds(of);
        double lo_smc = LogOdds(smc);
        double lo_ml = LogOdds(ml);

        double y = (direction == "BUY" && wasWin) || (direction == "PUT" && !wasWin) ? 1.0 : 0.0;

        string key = GetKey(asset, timeframe);
        var w = GetOrCreateWeights(key);
        int newCount;

        lock (w)
        {
            double z = w[0] + (w[1] * lo_ta) + (w[2] * lo_of) + (w[3] * lo_smc) + (w[4] * lo_ml);
            double p = 1.0 / (1.0 + Math.Exp(-z));
            double error = y - p;

            double lr = GetLearningRate(key);

            // Symmetric balanced SGD: no asymmetric penalty destroying weights
            w[0] += lr * error;
            w[1] += lr * error * lo_ta;
            w[2] += lr * error * lo_of;
            w[3] += lr * error * lo_smc;
            w[4] += lr * error * lo_ml;

            // Soft L2 Regularization pulling towards empirical priors (ML dominant 1.35)
            for (int i = 1; i < w.Length; i++) 
            {
                w[i] += (DefaultPriors[i] - w[i]) * 0.005;
                w[i] = Math.Clamp(w[i], 0.35, 3.5);
            }

            // Bias shrinkage towards 0.0 to strictly prevent phantom directional lock-in
            w[0] += (0.0 - w[0]) * 0.02;
            w[0] = Math.Clamp(w[0], -0.15, 0.15);
        }

        newCount = _updateCounts.AddOrUpdate(key, 1, (_, v) => v + 1);

        // Persist to PostgreSQL asynchronously
        double[] snapshot;
        lock (w) { snapshot = (double[])w.Clone(); }
        _ = TradeRepository.SaveMetaWeightAsync(key, snapshot, newCount);

        // Also save to file
        _ = SaveWeightsToFileAsync();
    }

    public async Task InitializeFromDbAsync()
    {
        try
        {
            var dbWeights = await TradeRepository.LoadMetaWeightsAsync();
            if (dbWeights.Count > 0)
            {
                foreach (var item in dbWeights)
                {
                    if (item.weights != null && item.weights.Length == 5)
                    {
                        item.weights[0] = Math.Clamp(item.weights[0], -0.15, 0.15);
                        _weights[item.key] = item.weights;
                        _updateCounts[item.key] = item.updateCount;
                    }
                }
                BotLogger.Info($"[OnlineMetaLearner] Successfully restored {dbWeights.Count} model weight vectors from PostgreSQL.");
            }
            else
            {
                BotLogger.Info("[OnlineMetaLearner] DB has 0 saved weights. Using clean empirical priors with ML dominant (weight 1.35).");
            }
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[OnlineMetaLearner] DB weight load warning: {ex.Message}");
        }
    }

    private void LoadWeightsFromFile()
    {
        try
        {
            if (File.Exists(_savePath))
            {
                var json = File.ReadAllText(_savePath);
                var dict = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, double[]>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict) 
                    { 
                        var w = kvp.Value; 
                        w[0] = Math.Clamp(w[0], -0.15, 0.15);
                        for (int i = 1; i < w.Length; i++) 
                        { 
                            if (w[i] < 0.10) w[i] = 0.10; 
                            if (w[i] > 3.5) w[i] = 3.5; 
                        } 
                        _weights[kvp.Key] = w; 
                    }
                }
            }
        }
        catch { }
    }

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private Task SaveWeightsToFileAsync()
    {
        return Task.Run(async () =>
        {
            if (!_saveLock.Wait(0)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
                var dict = new System.Collections.Generic.Dictionary<string, double[]>();
                foreach (var kvp in _weights) 
                {
                    lock (kvp.Value) { dict[kvp.Key] = (double[])kvp.Value.Clone(); }
                }
                
                string json = JsonSerializer.Serialize(dict);
                string tmpPath = _savePath + $".tmp.{Guid.NewGuid():N}";
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
