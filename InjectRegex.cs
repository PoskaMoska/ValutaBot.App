using System;
using System.IO;
using System.Text.RegularExpressions;

class Program
{
    static void Main()
    {
        string path = @""MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs"";
        string content = File.ReadAllText(path);

        string diPattern = @""public MarketAnalysisOrchestrator\([\s\S]*?ILogger<MarketAnalysisOrchestrator> logger\s*\)\s*\{\s*_fetcher = fetcher;"";
        string newDi = @""private readonly ValutaBot.MiniApp.Services.INewsCalendarService _newsCalendar;

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
        _fetcher = fetcher;"";
        content = Regex.Replace(content, diPattern, newDi);

        string logicPattern = @"(var consensus = await _cmEngine\.EvaluateMatrixAsync\([\s\S]*?CalculateVolatilityRatio\(mainPrices\)\);\s*)(matrixSw\.Stop\(\);)";
        string newLogic = @"
        // --- NEWS CALENDAR INTEGRATION ---
        int? minutesToNews = _newsCalendar?.GetMinutesToNextHighImpactNews(cleanAsset);
        if (minutesToNews.HasValue && minutesToNews.Value >= 0 && minutesToNews.Value <= 15)
        {
            var nextNews = _newsCalendar?.GetNextHighImpactNews(cleanAsset);
            string newsWarning = $""?? ВНИМАНИЕ: Через {minutesToNews.Value} мин выходит важная новость ({nextNews?.Title}). Рынок нестабилен!"";
            consensus = consensus with {
                Probability = 50,
                FinalDirection = ""NEUTRAL"",
                CombinedReasoningText = consensus.CombinedReasoningText + ""\n"" + newsWarning
            };
            traceLines.Add($""[6.5 NEWS] {newsWarning}"");
        }
        else if (minutesToNews.HasValue)
        {
            traceLines.Add($""[6.5 NEWS] Next high-impact news in {minutesToNews.Value} minutes"");
        }
        ";
        content = Regex.Replace(content, logicPattern, newLogic);

        string featPattern = @"(MarketRegime = state\.VelocityRegime)(\s*\};)";
        string newFeat = @",
                  MinutesToNews = minutesToNews";
        content = Regex.Replace(content, featPattern, newFeat);

        File.WriteAllText(path, content);
        Console.WriteLine(""Done!"");
    }
}
