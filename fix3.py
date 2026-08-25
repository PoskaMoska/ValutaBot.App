with open("MiniApp/Services/MLPythonService.cs", "r", encoding="utf-8") as f:
    lines = f.readlines()

for i in range(len(lines)):
    if "if (response.IsSuccessStatusCode)" in lines[i] and "[MLPython] Online RL feedback registered" in lines[i+2]:
        lines[i+2] = '                var responseBody = await response.Content.ReadAsStringAsync();\n                string winStr = wasWin ? "WIN" : "LOSS";\n                BotLogger.Info($"[AI Feedback Detector] Feedback sent for {asset}/{timeframe} -> {winStr}. Python Response: {responseBody}");\n'
        break

with open("MiniApp/Services/MLPythonService.cs", "w", encoding="utf-8") as f:
    f.writelines(lines)
