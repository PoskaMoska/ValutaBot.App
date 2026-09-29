import re

with open('MiniApp/Services/AutoTradingScannerService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# We want to replace everything inside the while(!stoppingToken.IsCancellationRequested) loop
pattern = r"while \(!stoppingToken\.IsCancellationRequested\)\s*\{.*?await Task\.Delay\(delay, stoppingToken\);\s*\}\s*\n\s*\}"

new_loop = '''
        int currentPairIndex = 0;
        int currentTfIndex = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var dayOfWeek = DateTime.UtcNow.DayOfWeek;
            bool isWeekend = dayOfWeek == DayOfWeek.Saturday ||
                             (dayOfWeek == DayOfWeek.Sunday && DateTime.UtcNow.Hour < 21) ||
                             (dayOfWeek == DayOfWeek.Friday && DateTime.UtcNow.Hour >= 21);

            if (isWeekend)
            {
                _logger.LogDebug("[AutoScanner] Weekend - skipping scan.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IMarketAnalysisOrchestrator>();
                var userSettings = await UserRepository.GetSettingsAsync(0);

                string pair = _targetPairs[currentPairIndex];
                string tf = _targetTimeframes[currentTfIndex];

                _logger.LogInformation($"[AutoScanner] Scanning {pair} on {tf}...");
                await orchestrator.ExecuteAnalysisAsync(pair, tf, userSettings);

                // Move to next TF/Pair
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
            catch (Exception ex)
            {
                _logger.LogError($"[AutoScanner] Exception during cycle: {ex.Message}");
            }

            // Drip-feed: 1 scan every 20 seconds. 
            // 3 requests a minute maximum, leaving 5 requests/minute for the user.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }'''

content = re.sub(pattern, new_loop, content, flags=re.DOTALL)

with open('MiniApp/Services/AutoTradingScannerService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
