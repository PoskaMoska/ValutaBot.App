import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Replace the mlFeatures object to include smcResult and ofResult
old_features = '''var mlFeatures = new {
                Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray()
            };'''

new_features = '''var mlFeatures = new {
                Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                Smc = smcResult,
                Of = ofResult
            };'''

content = content.replace(old_features, new_features)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
