import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_else = '''        else
        {
            dbSw.Stop();'''

new_else = '''        else if (consensus.Probability >= 45 && consensus.Probability < 53)
        {
            var mlFeatures = new {
                Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                Smc = smcResult,
                Of = ofResult
            };
            string featuresJson = System.Text.Json.JsonSerializer.Serialize(mlFeatures);

            _ = SignalTracker.RecordPredictionAsync("SHADOW_" + consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson);
            dbSw.Stop();
            traceLines.Add($"[8. DB Write:]     SHADOW TRADE: {currentLivePrice} (Prob: {consensus.Probability}%, Horizon: {targetHorizon}) -> {dbSw.ElapsedMilliseconds}ms");
        }
        else
        {
            dbSw.Stop();'''

content = content.replace(old_else, new_else)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
