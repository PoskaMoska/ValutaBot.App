# -*- coding: utf-8 -*-
import re

with open('MiniApp/Features/MarketAnalysis/Engines/OnlineMetaLearner.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Add log to PartialFit
log_pattern = r"(for \(int i = 1; i < w\.Length; i\+\+\) w\[i\] = 1\.0 - \(\(1\.0 - w\[i\]\) \* WeightDecay\);\n\s*\})"
new_log = r"\1\n            BotLogger.Info($\"[MetaLearner] {key} Online RL Shift: Error={error:F3}, New Weights: Bias={w[0]:F2}, TA={w[1]:F2}, OF={w[2]:F2}, SMC={w[3]:F2}, ML={w[4]:F2}\");"
content = re.sub(log_pattern, new_log, content)

# 2. Add GetWeights property/method
if "public System.Collections.Generic.Dictionary<string, double[]> GetCurrentWeights()" not in content:
    get_weights = r"""    public System.Collections.Generic.Dictionary<string, double[]> GetCurrentWeights()
    {
        return new System.Collections.Generic.Dictionary<string, double[]>(_weights);
    }
    
    public void PartialFit"""
    content = content.replace("public void PartialFit", get_weights)

with open('MiniApp/Features/MarketAnalysis/Engines/OnlineMetaLearner.cs', 'w', encoding='utf-8') as f:
    f.write(content)

with open('MiniApp/Controllers/MiniAppController.Routes.cs', 'r', encoding='utf-8') as f:
    routes = f.read()

# 3. Add endpoints in Routes
if "/api/stats/weights" not in routes:
    endpoints = r"""        app.MapGet("/api/stats/weights", (HttpContext context) =>
        {
            var ml = ValutaBot.MiniApp.TradeOutcomeTracker.MetaLearner as ValutaBot.MiniApp.Features.MarketAnalysis.Engines.OnlineMetaLearner;
            if (ml == null) return Results.Json(new { status = "offline", message = "MetaLearner is currently offline waiting for calibration data." });
            return Results.Json(new { status = "online", weights = ml.GetCurrentWeights() });
        });
        
        app.MapGet("/api/stats/ml","""
    routes = routes.replace('app.MapGet("/api/stats/ml",', endpoints)

with open('MiniApp/Controllers/MiniAppController.Routes.cs', 'w', encoding='utf-8') as f:
    f.write(routes)
