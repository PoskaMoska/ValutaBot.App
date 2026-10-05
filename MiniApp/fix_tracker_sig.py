import sys

with open("MiniApp/Services/SignalTracker.cs", "r", encoding="utf-8") as f:
    code = f.read()

find_str = """        double taScore = 0.0,
        double ofScore = 0.0,
        double smcScore = 0.0,
        double mlProb = 0.0, double mlScore = 0.0, string featuresJson = "")"""
replace_str = """        double taScore = 0.0,
        double ofScore = 0.0,
        double smcScore = 0.0,
        double mlProb = 0.0, double mlScore = 0.0, string featuresJson = "",
        string smcBosDir = "NONE", bool smcHasOb = false, bool smcHasFvg = false,
        double ofDeltaRatio = 1.0, string ofState = "NEUTRAL")"""

if find_str in code:
    code = code.replace(find_str, replace_str)
    
    find2 = """            FeaturesJson = featuresJson
        };"""
    replace2 = """            FeaturesJson = featuresJson,
            SmcBosDir = smcBosDir,
            SmcHasOb = smcHasOb,
            SmcHasFvg = smcHasFvg,
            OfDeltaRatio = ofDeltaRatio,
            OfState = ofState,
            DynamicHorizon = expiryCandles
        };"""
    code = code.replace(find2, replace2)
    
    with open("MiniApp/Services/SignalTracker.cs", "w", encoding="utf-8") as f:
        f.write(code)
    print("Replaced SignalTracker signature successfully")
else:
    print("Could not find signature target string in SignalTracker.cs")
