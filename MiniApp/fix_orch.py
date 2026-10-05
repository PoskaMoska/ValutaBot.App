import sys

with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "r", encoding="utf-8") as f:
    code = f.read()

find1 = """            _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson);"""
replace1 = """            _ = SignalTracker.RecordPredictionAsync(consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson,
                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", ofResult.DeltaRatio, ofResult.OrderFlowState);"""

find2 = """             _ = SignalTracker.RecordPredictionAsync("SHADOW_" + consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson);"""
replace2 = """             _ = SignalTracker.RecordPredictionAsync("SHADOW_" + consensus.FinalDirection, cleanAsset, timeframe, currentLivePrice, targetHorizon, _fetcher.TimeframeSeconds(timeframe), isForex, sourceDirections, consensus.Probability, consensus.TaScore, consensus.OfScore, consensus.SmcScore, consensus.MlProb, consensus.MlScoreRaw, featuresJson,
                smcResult.BosDirection ?? "NONE", smcResult.OrderBlockType != "NONE", smcResult.FvgType != "NONE", ofResult.DeltaRatio, ofResult.OrderFlowState);"""

code = code.replace(find1, replace1)
code = code.replace(find2, replace2)

with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "w", encoding="utf-8") as f:
    f.write(code)
print("Replaced Orchestrator successfully")
