using System;

namespace ValutaBot.App.MiniApp.Models
{
    public record AnalysisResponseDto
    {
        public bool tfConflict { get; init; }
        public string uiMarketSession { get; init; } = "";
        public string uiMarketPhase { get; init; } = "";
        public string uiMarketEntropy { get; init; } = "";
        public string direction { get; init; } = "";
        public int probability { get; init; }
        public string duration { get; init; } = "";
        public int expiryCandles { get; init; }
        public string adaptiveReasoning { get; init; } = "";
        
        public string taDirection { get; init; } = "";
        public int taConfidence { get; init; }
        
        public string ofDirection { get; init; } = "";
        public int ofConfidence { get; init; }
        
        public string smcDirection { get; init; } = "";
        public int smcConfidence { get; init; }
        
        public string? lgbmDirection { get; init; }
        public int lgbmConfidence { get; init; }
        
        public double? winRateOverall { get; init; }
        public double? winRateAsset { get; init; }
        public int signalsVerifiedAsset { get; init; }
        
        public double rsi { get; init; }
        public double ema { get; init; }
        public double volumeStrength { get; init; }
        public double atr { get; init; }
        
        public double[] chartData { get; init; } = Array.Empty<double>();
        public object[] chartOhlc { get; init; } = Array.Empty<object>();
        
        public bool goldenSetup { get; init; }
        public string confluenceLabel { get; init; } = "";
        public double confluenceRatio { get; init; }
        
        // These fields are optionally present when ML responds with reasoning
        public string? llmReport { get; init; }
        public string? lgbmModelVersion { get; init; }
    }
}
