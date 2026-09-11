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
    // FIX PRIORITY-1: Перегрузка с уже загруженными свечами (избегает 3 лишних HTTP-запроса).
    // primaryCandles и macroCandles уже загружены Orchestrator'ом — передаём их напрямую.
    // Только microTF требует отдельного fetch (1 запрос вместо 3).
    Task<ConfluenceMatrixResult> Evaluate4DMatrixAsync(
        string asset,
        string primaryTimeframe,
        bool isForex = false,
        string? binanceSymbol = null,
        MiniAppController.OhlcCandle[]? primaryCandles = null,
        double[]? primaryPrices = null,
        double[]? primaryVolumes = null,
        MiniAppController.OhlcCandle[]? macroCandles = null,
        double[]? macroPrices = null,
        double[]? macroVolumes = null);

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
    string RecommendedExpiryText = "",
    double TaScore = 0.0,
    double OfScore = 0.0,
    double SmcScore = 0.0,
    double MlProb = 0.0
);


