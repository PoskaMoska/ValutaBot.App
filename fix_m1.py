import re

with open('MiniApp/Services/AutoTradingScannerService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old_logic = '''
                string pair = _targetPairs[currentPairIndex];
                string tf = _targetTimeframes[currentTfIndex];

                _logger.LogInformation($"[AutoScanner] Scanning {pair} on {tf}...");
                var recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 160);
                if (recentCandles.Length < 160)
'''

new_logic = '''
                string pair = _targetPairs[currentPairIndex];
                string tf = _targetTimeframes[currentTfIndex];

                _logger.LogInformation($"[AutoScanner] Scanning {pair} on {tf}...");
                
                if (tf.StartsWith("s"))
                {
                    var recentCandles = await RealtimeTickCollector.GetRecentCandles(pair, tf, 160);
                    if (recentCandles.Length < 160)
                    {
                        MoveToNextCycle(ref currentTfIndex, ref currentPairIndex);
                        continue;
                    }
                    var lastCandleTime = recentCandles[^1].Timestamp;
                    if ((DateTime.UtcNow - lastCandleTime).TotalSeconds > 30)
                    {
                        MoveToNextCycle(ref currentTfIndex, ref currentPairIndex);
                        continue;
                    }
                }

                await orchestrator.ExecuteAnalysisAsync(pair, tf, userSettings);

                // Move to next TF/Pair
                MoveToNextCycle(ref currentTfIndex, ref currentPairIndex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[AutoScanner] Exception during cycle: {ex.Message}");
            }

            // Drip-feed: 1 scan every 20 seconds. 
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private void MoveToNextCycle(ref int currentTfIndex, ref int currentPairIndex)
    {
        currentTfIndex++;
        if (currentTfIndex >= _targetTimeframes.Length)
        {
            currentTfIndex = 0;
            currentPairIndex++;
            if (currentPairIndex >= _targetPairs.Length)
            {
                currentPairIndex = 0;
            }
        }
    }
}
'''

# We need to replace everything from "string pair" down to the end of the class.
pattern = r"string pair = _targetPairs\[currentPairIndex\];.*?^\}$"
content = re.sub(pattern, new_logic.strip(), content, flags=re.DOTALL | re.MULTILINE)

with open('MiniApp/Services/AutoTradingScannerService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
