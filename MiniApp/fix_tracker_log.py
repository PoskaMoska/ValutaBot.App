import sys

with open("MiniApp/Services/TradeOutcomeTracker.cs", "r", encoding="utf-8") as f:
    code = f.read()

find_str = """BotLogger.Warn($"[TradeOutcomeTracker] Training Doji (Noise) for {record.Asset} (pctDiff={pctDiff:F4}% < 0.025%). Skipping ML/WF feedback to prevent weight poisoning.");"""
replace_str = """BotLogger.Warn($"[TradeOutcomeTracker] Training Doji (Noise) for {record.Asset} (pctDiff={pctDiff:F4}% < {dojiThreshold:F3}%). Skipping ML/WF feedback to prevent weight poisoning.");"""

if find_str in code:
    code = code.replace(find_str, replace_str)
    with open("MiniApp/Services/TradeOutcomeTracker.cs", "w", encoding="utf-8") as f:
        f.write(code)
    print("Replaced TradeOutcomeTracker log successfully")
else:
    print("Could not find log target string in TradeOutcomeTracker.cs")
