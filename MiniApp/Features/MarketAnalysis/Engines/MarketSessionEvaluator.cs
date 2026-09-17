using System;

namespace ValutaBot.MiniApp
{
    public static class MarketSessionEvaluator
    {
        public static (double Multiplier, string SessionName) GetSessionMultiplier(string asset)
        {
            if (asset.Contains("BTC") || asset.Contains("ETH") || asset.Contains("SOL"))
                return (1.0, "DEFAULT");

            int h = DateTime.UtcNow.Hour;
            if (h >= 21 || h < 2) return (0.75, "DEAD_ZONE");
            if (h >= 2 && h < 8) return (0.85, "ASIAN");
            if (h >= 8 && h < 13) return (1.0, "LONDON_MORNING");
            if (h >= 13 && h < 16) return (1.1, "LONDON_NY_OVERLAP");
            if (h >= 17 && h < 21) return (1.0, "NY_AFTERNOON");

            return (1.0, "DEFAULT");
        }
    }
}
