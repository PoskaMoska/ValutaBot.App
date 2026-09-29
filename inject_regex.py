import re

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# DI
di_pattern = r"public MarketAnalysisOrchestrator\([\s\S]*?ILogger<MarketAnalysisOrchestrator> logger\s*\)\s*\{\s*_fetcher = fetcher;"
new_di = '''private readonly ValutaBot.MiniApp.Services.INewsCalendarService _newsCalendar;

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
content = re.sub(di_pattern, new_di, content)

# Logic
logic_pattern = r"(var consensus = await _cmEngine\.EvaluateMatrixAsync\([\s\S]*?CalculateVolatilityRatio\(mainPrices\)\);\s*)(matrixSw\.Stop\(\);)"
new_logic = r'''\1
        // --- NEWS CALENDAR INTEGRATION ---
        int? minutesToNews = _newsCalendar?.GetMinutesToNextHighImpactNews(cleanAsset);
        if (minutesToNews.HasValue && minutesToNews.Value >= 0 && minutesToNews.Value <= 15)
        {
            var nextNews = _newsCalendar?.GetNextHighImpactNews(cleanAsset);
            string newsWarning = $"?? ВНИМАНИЕ: Через {minutesToNews.Value} мин выходит важная новость ({nextNews?.Title}). Рынок нестабилен!";
            consensus = consensus with {
                Probability = 50,
                FinalDirection = "NEUTRAL",
                CombinedReasoningText = consensus.CombinedReasoningText + "\n" + newsWarning
            };
            traceLines.Add($"[6.5 NEWS] {newsWarning}");
        }
        else if (minutesToNews.HasValue)
        {
            traceLines.Add($"[6.5 NEWS] Next high-impact news in {minutesToNews.Value} minutes");
        }
        \2'''
content = re.sub(logic_pattern, new_logic, content)

# Feat
feat_pattern = r"(MarketRegime = state\.VelocityRegime)(\s*\};)"
new_feat = r"\1,\n                  MinutesToNews = minutesToNews\2"
content = re.sub(feat_pattern, new_feat, content)

with open('MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs', 'w', encoding='utf-8') as f:
    f.write(content)
