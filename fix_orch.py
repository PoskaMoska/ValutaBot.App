with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "r", encoding="utf-8") as f:
    text = f.read()

import re
# Find the ML Telemetry block and remove it
new_text = re.sub(
    r'// \?\?\? ML Telemetry: Global Retraining \?\?\?.*?_lastSeenModelVersions\[cacheKey\] = currentVer;.*?\}[\s\n]*\}[\s\n]*\}',
    '// --- ML Telemetry Disabled ---',
    text,
    flags=re.DOTALL
)

with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "w", encoding="utf-8") as f:
    f.write(new_text)
