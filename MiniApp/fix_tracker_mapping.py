import sys

with open("MiniApp/Services/TradeOutcomeTracker.cs", "r", encoding="utf-8") as f:
    code = f.read()

find_str = """                MlScore = record.MlScore,
                FeaturesJson = record.FeaturesJson,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };"""
replace_str = """                MlScore = record.MlScore,
                FeaturesJson = record.FeaturesJson,
                SmcBosDir = record.SmcBosDir,
                SmcHasOb = record.SmcHasOb,
                SmcHasFvg = record.SmcHasFvg,
                OfDeltaRatio = record.OfDeltaRatio,
                OfState = record.OfState,
                DynamicHorizon = record.DynamicHorizon,
                CreatedAt = record.CreatedAt.ToString("o"),
                VerifiedAt = DateTime.UtcNow.ToString("o")
            };"""

if find_str in code:
    code = code.replace(find_str, replace_str)
    with open("MiniApp/Services/TradeOutcomeTracker.cs", "w", encoding="utf-8") as f:
        f.write(code)
    print("Replaced TradeOutcomeTracker mapping successfully")
else:
    print("Could not find mapping target string in TradeOutcomeTracker.cs")
