import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Add serialization logic before RecordPredictionAsync
record_code = '''
        int targetHorizon = timeout.TimeoutCandles;
        if (consensus.Probability >= 53)
        {
            var mlFeatures = new {
                Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray()
            };
            string featuresJson = System.Text.Json.JsonSerializer.Serialize(mlFeatures);

            _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson);
'''

content = re.sub(
    r'int targetHorizon = timeout\.TimeoutCandles;\s*if \(consensus\.Probability >= 53\)\s*\{\s*_ = SignalTracker\.RecordPredictionAsync\(consensus\.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher\.TimeframeSeconds\(timeframe\), isForex, sourceDirections, consensus\.TaScore, consensus\.OfScore, consensus\.SmcScore, consensus\.MlProb, consensus\.MlScoreRaw\);',
    record_code.strip(),
    content
)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
