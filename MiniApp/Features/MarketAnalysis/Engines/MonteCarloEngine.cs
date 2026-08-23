using System;

namespace ValutaBot.MiniApp;

public record MonteCarloResult(
    int Iterations,
    int SuccessCount,
    double ExpectedValuePct,
    double KellyRiskPct,
    string EvLabel,
    string KellyLabel,
    string SummaryReasoning
);

public interface IMonteCarloEngine
{
    MonteCarloResult Simulate(
        double currentPrice,
        double winProbability,
        string direction,
        double atr,
        int timeInSeconds = 60,
        double payoutRatio = 0.85,
        int iterations = 1000);
}

public class MonteCarloEngine : IMonteCarloEngine
{
    private readonly double _defaultPayoutRatio = 0.85;
    private readonly double _expectedJumps = 0.05; // Reduced from 15% to 5% chance of a black swan
    private readonly double _jumpVolMultiplier = 2.0; // Jumps are 2x more volatile (was 3x)

    /// <summary>
    /// Runs algorithmic Monte Carlo stochastic price path simulations with ATR volatility and calculates
    /// Expected Value (EV) and Fractional Kelly Criterion risk management.
    /// </summary>
    public MonteCarloResult Simulate(
        double currentPrice,
        double winProbability,
        string direction,
        double atr,
        int timeInSeconds = 60,
        double payoutRatio = 0.85,
        int iterations = 1000)
    {
        if (currentPrice <= 0) currentPrice = 1.0;
        if (atr <= 0) atr = currentPrice * 0.0005; // Fallback volatility 0.05%
        
        if (winProbability > 1.0) winProbability /= 100.0;
        double prob = Math.Clamp(winProbability, 0.35, 0.95);
        bool isBuy = direction.Equals("BUY", StringComparison.OrdinalIgnoreCase);

        // FIX W-19: old code always divided by sqrt(60), as if every candle were 1 minute.
        // For a 5m ATR the correct divisor is sqrt(300); for s3 it's sqrt(3).
        // timeInSeconds already carries the actual TF duration — use it to derive correct vol.
        double secondsPerCandle = Math.Max(1.0, timeInSeconds);
        double volPerSec   = (atr / currentPrice) / Math.Sqrt(secondsPerCandle);
        double totalTimeStep = Math.Max(10, timeInSeconds);
        double totalVol    = volPerSec * Math.Sqrt(totalTimeStep);

        // Ito's drift correction for Geometric Brownian Motion
        double itoDrift = -0.5 * totalVol * totalVol;

        // Directional drift based on probability
        double driftSign = isBuy ? 1.0 : -1.0;
        double directionalDrift = (driftSign * (prob - 0.5) * 2.0 * totalVol) + itoDrift;

        double jumpVol = totalVol * _jumpVolMultiplier;

        int successCount = 0;
        var rand = Random.Shared;

        // Stochastic Monte Carlo iterations
        for (int i = 0; i < iterations; i++)
        {
            // Box-Muller transform for standard normal Gaussian random numbers
            // BUG-1 FIX: rand.NextDouble() can return exactly 1.0 → 1.0-1.0 = 0.0 → Math.Log(0) = -Infinity → NaN
            // Clamp to 1e-10 minimum to guarantee Log input is always positive.
            double u1 = Math.Max(1e-10, 1.0 - rand.NextDouble());
            double u2 = 1.0 - rand.NextDouble();
            double randNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

            // Simulate Poisson Jumps (Black Swan / Manipulation)
            double jumpReturn = 0;
            if (rand.NextDouble() < _expectedJumps) 
            {
                double j1 = Math.Max(1e-10, 1.0 - rand.NextDouble()); // same NaN guard
                double j2 = 1.0 - rand.NextDouble();
                double jumpNormal = Math.Sqrt(-2.0 * Math.Log(j1)) * Math.Sin(2.0 * Math.PI * j2);
                
                // Jumps usually happen against the obvious crowd direction in crypto (liquidation hunting)
                double jumpMean = -driftSign * totalVol * 1.0; // Reduced from 1.5x penalty to 1.0x
                jumpReturn = jumpMean + (jumpNormal * jumpVol);
            }

            // Merton Jump-Diffusion Geometric Brownian Motion step
            double simulatedReturn = directionalDrift + (totalVol * randNormal) + jumpReturn;
            double finalSimulatedPrice = currentPrice * Math.Exp(simulatedReturn);

            if (isBuy && finalSimulatedPrice > currentPrice)
            {
                successCount++;
            }
            else if (!isBuy && finalSimulatedPrice < currentPrice)
            {
                successCount++;
            }
        }

        double simulatedWinRate = (double)successCount / iterations;

        // Calculate Expected Value (EV): EV = (Win% * Payout) - (Loss% * 1.0)
        double evRatio = (simulatedWinRate * payoutRatio) - ((1.0 - simulatedWinRate) * 1.0);
        double evPct = Math.Round(evRatio * 100.0, 1);

        // Calculate Kelly Criterion Risk Percentage: K% = (p * b - q) / b
        double p = simulatedWinRate;
        double q = 1.0 - p;
        double b = payoutRatio > 0 ? payoutRatio : _defaultPayoutRatio;

        double fullKelly = (p * b - q) / b;
        // Fractional Kelly (Half-Kelly to Fractional 25% for conservative capital preservation)
        double fractionalKelly = Math.Clamp(fullKelly * 0.25, 0.0, 0.05);
        double kellyRiskPct = Math.Round(fractionalKelly * 100.0, 1);

        string evLabel = evPct > 0 
            ? $"+{evPct:F1}% EV (Positive Expectancy)" 
            : $" {evPct:F1}% EV (Negative Expectancy)";

        string kellyLabel = kellyRiskPct > 0 
            ? $"{kellyRiskPct:F1}% - {Math.Min(kellyRiskPct + 0.5, 5.0):F1}% of Capital"
            : "0% (Do not trade, low edge)";

        string summary = $"Monte-Carlo Model ({iterations} paths ATR): {successCount}/{iterations} won | EV: {(evPct > 0 ? "+" : " ")}{evPct:F1}% | Kelly Risk: {kellyRiskPct:F1}%";

        return new MonteCarloResult(
            iterations,
            successCount,
            evPct,
            kellyRiskPct,
            evLabel,
            kellyLabel,
            summary
        );
    }
}

