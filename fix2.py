with open("MiniApp/Services/MLPythonService.cs", "r", encoding="utf-8") as f:
    text = f.read()

target = """            if (response.IsSuccessStatusCode)
            {
                BotLogger.Info($\"[MLPython] Online RL feedback registered for {asset}/{timeframe} \uFFFD\' {(wasWin ? \"WIN\" : \"LOSS\")}\");
            }"""

# The file might have mangled characters. Let's just find the start of the line:
import re
new_text = re.sub(
    r'if \(response\.IsSuccessStatusCode\)[\s\n]*\{[\s\n]*BotLogger\.Info[^\}]+;',
    r'''if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                string winStr = wasWin ? "WIN" : "LOSS";
                BotLogger.Info($"[AI Feedback Detector] Feedback sent for {asset}/{timeframe} -> {winStr}. Python Response: {responseBody}");''',
    text
)

with open("MiniApp/Services/MLPythonService.cs", "w", encoding="utf-8") as f:
    f.write(new_text)
