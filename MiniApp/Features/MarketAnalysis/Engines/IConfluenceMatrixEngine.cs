using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

// Signal records for unified confluence scoring
public record TaSignal(double Score, double Confidence, double Rsi, double Ema, double Volatility, double Atr, double Adx = 20.0);

// D2-3 FIX: SmcSignal strings are guaranteed non-null at construction via Orchestrator sanitization.
// All comparison sites use == against known literals — null-safe by C# spec (null != "BULLISH_BOS").
// However canonical empty value is "" (not null, not "NONE") — enforced at Orchestrator call site.
public record SmcSignal(string BosDirection, string SweepDirection, string OrderBlockType, string FvgType, string Reasoning);

public record OrderflowSignal(double ScoreContribution, string Description);

// D2-2 FIX: MlSignal.Confidence is ALWAYS in [0..1] (Python confidence, not a logit).
// Enforcement happens at Orchestrator level before construction. This comment is the contract.
public record MlSignal(string Direction, double Confidence, double? Accuracy, string ModelVersion, int? HorizonCandles = null, double? RawConfidence = null);

public record StateSignal(string Regime, double VelocityBpsPerSec, double MomentumContribution);

public interface IConfluenceMatrixEngine
{
    // D2-1 FIX: Parameter names now exactly match ConfluenceMatrixEngine implementation.
    // Old names 'primaryCandles/macroCandles' renamed to 'currentCandles/higherCandles'.
    // This prevents CS1739 errors if callers use named arguments, and removes the
    // semantic confusion between 'macro' (long-term TF) vs 'higher' (one step up).
    Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null,
        MiniAppController.OhlcCandle[]? currentCandles = null,
        double[]? currentPrices = null,
        double[]? currentVolumes = null,
        MiniAppController.OhlcCandle[]? higherCandles = null,
        double[]? higherPrices = null,
        double[]? higherVolumes = null);

    // The unified Confluence hub method
    Task<ConsensusDecision> EvaluateMatrixAsync(
        string asset,
        string timeframe,
        bool isSubMinute,
        double conflictPenalty,
        TaSignal taSignal,
        SmcSignal smcSignal,
        OrderflowSignal ofSignal,
        MlSignal mlSignal,
        StateSignal stateSignal,
        ConfluenceMatrixResult mtfResult, int consecutiveLosses = 0, double volRatio = 1.0);
}

// Replaces ConsensusEngine.DecisionResult
public record ConsensusDecision(
    string CandidateDirection,
    string FinalDirection,
    int Probability,
    string CombinedReasoningText,
    double FinalTotalScore,
    string RecommendedExpiryText = "",
    double TaScore = 0.0,
    double OfScore = 0.0,
    double SmcScore = 0.0,
    double MlProb = 0.0,
    double MlScoreRaw = 0.0
);
