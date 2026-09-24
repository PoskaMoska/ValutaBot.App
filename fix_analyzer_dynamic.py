import re

with open(r'c:\Users\bural\source\repos\ValutaBot.App\MiniApp\Backtesting\CombinedColdAnalyzer.cs', 'r', encoding='utf-8') as f:
    code = f.read()

# Replace horizon = 5 with the timeout engine init
code = code.replace(
    ""var taEngine = new TechnicalAnalysisEngine();\n                int horizon = 5;"",
    ""var taEngine = new TechnicalAnalysisEngine();\n                var timeoutEngine = new TradeTimeoutEngine();""
)

# Update for loop to use hardcoded max horizon of 5
code = code.replace(
    ""for (int i = startIdx; i < candles.Length - horizon; i++)"",
    ""for (int i = startIdx; i < candles.Length - 5; i++)""
)

# Update the inside of the loop
replacement_inside = """"\"
                    var slice = candles.AsSpan(0, i + 1);
                    var currentPrice = candles[i].Close;

                    double[] closes = new double[60], vols = new double[60];
                    int start = Math.Max(0, i - 59);
                    for (int j = 0; j < 60 && start + j <= i; j++)
                    {
                        closes[j] = candles[start + j].Close;
                        vols[j] = candles[start + j].Volume;
                    }
                    var taScoreResult = taEngine.ScoreTimeframe(asset, "1m", closes, vols, slice);
                    double taScore = taScoreResult.score;

                    var smcResult = SmcEngine.AnalyzeSmcStructure(asset, "1m", slice, currentPrice);
                    double smcScore = 0;
                    if (smcResult.BosDirection == "BULLISH" || smcResult.SweepDirection == "BULLISH_SWEEP") smcScore += 1;
                    else if (smcResult.BosDirection == "BEARISH" || smcResult.SweepDirection == "BEARISH_SWEEP") smcScore -= 1;
                    
                    var state = ContinuousStateEngine.EvaluateContinuousState(closes, asset, "1m");
                    var timeout = timeoutEngine.CalculateTimeout(asset, "1m", taScoreResult.atrVal, 1.0, smcResult, currentPrice, state, isForex: true);
                    int dynamicHorizon = timeout.TimeoutCandles;
                    
                    var futurePrice = candles[i + dynamicHorizon].Close;
                    bool actualUp = futurePrice > currentPrice;
""""\"

# We need to match the original inside to replace it.
original_inside = """"\"
                    var slice = candles.AsSpan(0, i + 1);
                    var currentPrice = candles[i].Close;
                    var futurePrice = candles[i + horizon].Close;
                    bool actualUp = futurePrice > currentPrice;

                    double[] closes = new double[60], vols = new double[60];
                    int start = Math.Max(0, i - 59);
                    for (int j = 0; j < 60 && start + j <= i; j++)
                    {
                        closes[j] = candles[start + j].Close;
                        vols[j] = candles[start + j].Volume;
                    }
                    var taScoreResult = taEngine.ScoreTimeframe(asset, "1m", closes, vols, slice);
                    double taScore = taScoreResult.score;

                    var smcResult = SmcEngine.AnalyzeSmcStructure(asset, "1m", slice, currentPrice);
                    double smcScore = 0;
                    if (smcResult.BosDirection == "BULLISH" || smcResult.SweepDirection == "BULLISH_SWEEP") smcScore += 1;
                    else if (smcResult.BosDirection == "BEARISH" || smcResult.SweepDirection == "BEARISH_SWEEP") smcScore -= 1;
""""\"

code = code.replace(original_inside.strip(), replacement_inside.strip())

# Also replace VerifiedAt = candles[i + horizon] to VerifiedAt = candles[i + dynamicHorizon]
code = code.replace('VerifiedAt = candles[i + horizon].Timestamp.ToString("O")', 'VerifiedAt = candles[i + dynamicHorizon].Timestamp.ToString("O")')

with open(r'c:\Users\bural\source\repos\ValutaBot.App\MiniApp\Backtesting\CombinedColdAnalyzer.cs', 'w', encoding='utf-8') as f:
    f.write(code)

print("Replaced successfully!")
