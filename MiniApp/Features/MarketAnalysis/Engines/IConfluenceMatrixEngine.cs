using System.Threading.Tasks;

namespace ValutaBot.MiniApp;

// Signal records for unified confluence scoring
public record TaSignal(double Score, double Confidence, double Rsi, double Ema, double Volatility, double Atr, double Adx = 20.0);
public record SmcSignal(string BosDirection, string SweepDirection, string OrderBlockType, string FvgType, string Reasoning);
public record OrderflowSignal(double ScoreContribution, string Description);
public record MlSignal(string Direction, double Confidence, double? Accuracy, string ModelVersion);
public record StateSignal(string Regime, double VelocityBpsPerSec, double MomentumContribution);

public interface IConfluenceMatrixEngine
{
    // The previous 4D Matrix method is still useful for internal use, but we expose a unified Eval.
    Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null);

    // The new unified Confluence hub method
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
    string RecommendedExpiryText = ""
);


