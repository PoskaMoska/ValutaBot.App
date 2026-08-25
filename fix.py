import re

with open('MiniApp/Services/MLPythonService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

pattern = r'if \(response\.IsSuccessStatusCode\)[\s\n\r]*\{[\s\n\r]*BotLogger\.Info\([^;]+;\[\s\n\r]*\}'
# Actually let us just replace it via index. It's safer.

old_str = '''            if (response.IsSuccessStatusCode)
            {
                BotLogger.Info($\"[MLPython] Online RL feedback registered for {asset}/{timeframe} \' {(wasWin ? \"WIN\" : \"LOSS\")}\");
            }'''

new_str = '''            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                string winStr = wasWin ? \"WIN\" : \"LOSS\";
                BotLogger.Info($\"[AI Feedback Detector] Feedback sent for {asset}/{timeframe} -> {winStr}. Python Response: {responseBody}\");
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                BotLogger.Warn($\"[AI Feedback Detector] ERROR! Python rejected feedback: {(int)response.StatusCode} - {errorBody}\");
            }'''

content = content.replace(old_str, new_str)

with open('MiniApp/Services/MLPythonService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
