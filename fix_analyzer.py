import sys

with open('MiniApp/Backtesting/CombinedColdAnalyzer.cs', 'r', encoding='utf-8') as f:
    c = f.read()

target = 'var mlPred = await MLPythonService.PredictAsync("EURUSD", "1m", slice.ToArray(), isForex: true);'
replacement = '''int mlStart = Math.Max(0, i + 1 - 200);
                var mlSlice = candles.AsSpan(mlStart, i + 1 - mlStart);
                var mlPred = await MLPythonService.PredictAsync("EURUSD", "1m", mlSlice.ToArray(), isForex: true);'''

c = c.replace(target, replacement)

with open('MiniApp/Backtesting/CombinedColdAnalyzer.cs', 'w', encoding='utf-8') as f:
    f.write(c)
