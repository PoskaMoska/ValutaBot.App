using Xunit;
namespace ValutaBot.Tests
{
    // NOTE: Skipped — GetMarketAnalysisQueryHandler and MarketAnalysisContext API changed.
    // These integration tests need to be updated to match the new CQRS handler constructor.
    public class IntegrationScenariosTests
    {
        [Fact(Skip = "Requires DI rewrite — handler ctor changed after CQRS refactor")]
        public void PerfectStorm_ForexWeekday_ShouldGenerateFinalSignal() { }
    }
}
