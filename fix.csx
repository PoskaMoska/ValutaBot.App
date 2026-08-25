using System.IO;
using System.Text.RegularExpressions;

string file = @"MiniApp\Services\MLPythonService.cs";
string content = File.ReadAllText(file);

string pattern = @"if\s*\(response\.IsSuccessStatusCode\)\s*\{[^}]+\}\s*else\s*\{[^}]+\}";

string replacement = @"
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                string winStr = wasWin ? ""WIN"" : ""LOSS"";
                BotLogger.Info($""[AI Feedback Detector] Feedback sent for {asset}/{timeframe} -> {winStr}. Python Response: {responseBody}"");
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                BotLogger.Warn($""[AI Feedback Detector] ERROR! Python rejected feedback: {(int)response.StatusCode} - {errorBody}"");
            }";

string newContent = Regex.Replace(content, pattern, replacement);
File.WriteAllText(file, newContent);
