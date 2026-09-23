import os
import re

file = 'MiniApp/Features/MarketAnalysis/Engines/OnlineMetaLearner.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

# I want to fix the duplicate SGD blocks. I'll just write a clean function for PartialFit.

new_partial_fit = '''
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
'''

# Use regex to replace the entire PartialFit method
text = re.sub(r'public void PartialFit.*?_updateCounts\.AddOrUpdate.*?_ = SaveWeightsAsync\(\);\s*\}', new_partial_fit, text, flags=re.DOTALL)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

