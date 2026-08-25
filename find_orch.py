with open("MiniApp/Features/MarketAnalysis/MarketAnalysisOrchestrator.cs", "r", encoding="utf-8") as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if "ML Telemetry" in line:
        print(f"FOUND at {i}")
        for j in range(i, min(i+10, len(lines))):
            print(repr(lines[j]))
        break
