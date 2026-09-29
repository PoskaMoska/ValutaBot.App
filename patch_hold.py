import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_else = '''        else
        {
            dbSw.Stop();'''

new_else = '''        else
        {
            dbSw.Stop();
            
            // --- СБОР TRUE NEGATIVE NOISE (Для перехода на 3-х системную архитектуру Трансформера) ---
            // Сканер делает ~288 проверок в минуту. Шанс 0.5% даст нам около 1.4 срезов 'скучного рынка' в минуту.
            if (Random.Shared.NextDouble() < 0.005)
            {
                var mlFeatures = new {
                    Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                    MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                    Smc = smcResult,
                    Of = ofResult
                };
                string featuresJson = System.Text.Json.JsonSerializer.Serialize(mlFeatures);

                _ = SignalTracker.RecordPredictionAsync("HOLD", cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson);
            }'''

content = content.replace(old_else, new_else)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
