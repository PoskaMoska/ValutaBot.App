import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_features = '''var mlFeatures = new {
                    Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                    MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                    Smc = smcResult,
                    Of = ofResult
                };'''

new_features = '''var mlFeatures = new {
                    Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                    MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                    Smc = smcResult,
                    Of = ofResult,
                    Ta = new { Rsi = taResult.rsiVal, Hma = taResult.hmaVal, Atr = mainAtr, Adx = mainAdx, Score = taResult.score }
                };'''

old_features2 = '''var mlFeatures = new {
                Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                Smc = smcResult,
                Of = ofResult
            };'''
new_features2 = '''var mlFeatures = new {
                Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                Smc = smcResult,
                Of = ofResult,
                Ta = new { Rsi = taResult.rsiVal, Hma = taResult.hmaVal, Atr = mainAtr, Adx = mainAdx, Score = taResult.score }
            };'''

content = content.replace(old_features, new_features)
content = content.replace(old_features2, new_features2)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
