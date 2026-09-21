using Xunit;
using ValutaBot.MiniApp;

namespace ValutaBot.Tests.Engines
{
    public class AssetSanitizerTests
    {
        [Fact]
        public void Sanitize_EnglishOTC_ReturnsCleanSymbol()
        {
            string clean = AssetSanitizer.Sanitize("EUR/USD OTC");
            Assert.Equal("EURUSD", clean);
        }

        [Fact]
        public void Sanitize_CyrillicOTC_ReturnsCleanSymbol()
        {
            string clean = AssetSanitizer.Sanitize("EUR/USD Р С›Р СћР РЋ");
            Assert.Equal("EURUSD", clean);
        }

        [Fact]
        public void Sanitize_FormattedPair_ReturnsCleanSymbol()
        {
            string clean = AssetSanitizer.Sanitize("  GBP-USD  ");
            Assert.Equal("GBPUSD", clean);
        }
    }
}
