# -*- coding: utf-8 -*-
import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Inject into DI
old_di = '''    public MarketAnalysisOrchestrator(
        MarketDataFetcher fetcher,
        IRiskGatekeeper riskGatekeeper,
        IMathEngine mathEngine,
        IMarketAnalyzer marketAnalyzer,
        IConfluenceMatrixEngine cmEngine,
        ITradeTimeoutEngine timeoutEngine,
        Microsoft.Extensions.Options.IOptions<TradingBotSettings> settings,
        ILogger<MarketAnalysisOrchestrator> logger
    )
    {
        _fetcher = fetcher;'''

new_di = '''    private readonly ValutaBot.MiniApp.Services.INewsCalendarService _newsCalendar;

    public MarketAnalysisOrchestrator(
        MarketDataFetcher fetcher,
        IRiskGatekeeper riskGatekeeper,
        IMathEngine mathEngine,
        IMarketAnalyzer marketAnalyzer,
        IConfluenceMatrixEngine cmEngine,
        ITradeTimeoutEngine timeoutEngine,
        Microsoft.Extensions.Options.IOptions<TradingBotSettings> settings,
        ILogger<MarketAnalysisOrchestrator> logger,
        ValutaBot.MiniApp.Services.INewsCalendarService newsCalendar = null
    )
    {
        _newsCalendar = newsCalendar;
        _fetcher = fetcher;'''

content = content.replace(old_di, new_di)

# Add to logic
old_logic = '''        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));
        matrixSw.Stop();'''

new_logic = '''        var consensus = await _cmEngine.EvaluateMatrixAsync(cleanAsset, timeframe, tfLower.StartsWith("s"), conflictPenalty, taSignal, smcSignal, ofSignal, mlSignal, stateSignal, mtfResult, TradeOutcomeTracker.GetConsecutiveLosses(cleanAsset, timeframe), _marketAnalyzer.CalculateVolatilityRatio(mainPrices));
        
        // --- NEWS CALENDAR INTEGRATION ---
        int? minutesToNews = _newsCalendar?.GetMinutesToNextHighImpactNews(cleanAsset);
        if (minutesToNews.HasValue && minutesToNews.Value >= 0 && minutesToNews.Value <= 15)
        {
            var nextNews = _newsCalendar?.GetNextHighImpactNews(cleanAsset);
            string newsWarning = $"?? ВНИМАНИЕ: Через {minutesToNews.Value} мин выходит важная новость ({nextNews?.Title}). Рынок нестабилен!";
            consensus = consensus with {
                Probability = 50,
                FinalDirection = "NEUTRAL",
                CombinedReasoningText = consensus.CombinedReasoningText + "\\n" + newsWarning
            };
            traceLines.Add($"[6.5 NEWS] {newsWarning}");
        }
        else if (minutesToNews.HasValue)
        {
            traceLines.Add($"[6.5 NEWS] Next high-impact news in {minutesToNews.Value} minutes");
        }

        matrixSw.Stop();'''

content = content.replace(old_logic, new_logic)

# Append to features_json
old_feat = '''                var mlFeatures = new {
                  Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                  MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                  Smc = smcResult,
                  Ta = taResult,
                  MathState = state,
                  MtfConsensus = mtfResult.DominantDirection,
                  MarketRegime = state.VelocityRegime
              };'''

new_feat = '''                var mlFeatures = new {
                  Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                  MtfCandles = closedHigherCandles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume }).ToArray(),
                  Smc = smcResult,
                  Ta = taResult,
                  MathState = state,
                  MtfConsensus = mtfResult.DominantDirection,
                  MarketRegime = state.VelocityRegime,
                  MinutesToNews = minutesToNews
              };'''

content = content.replace(old_feat, new_feat)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
