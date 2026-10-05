import sys

with open("MiniApp/Services/TradeOutcomeTracker.cs", "r", encoding="utf-8") as f:
    code = f.read()

find_str = """            double pctDiff = record.EntryPrice > 1e-8 ? Math.Abs(exitPriceVal - record.EntryPrice) / record.EntryPrice * 100.0 : 0;
            bool isTrainingDoji = pctDiff < 0.025;"""
replace_str = """            double pctDiff = record.EntryPrice > 1e-8 ? Math.Abs(exitPriceVal - record.EntryPrice) / record.EntryPrice * 100.0 : 0;
            double dojiThreshold = record.Timeframe switch
            {
                "s5" => 0.005,
                "s10" => 0.010,
                "s15" => 0.015,
                _ => 0.025
            };
            bool isTrainingDoji = pctDiff < dojiThreshold;"""

if find_str in code:
    code = code.replace(find_str, replace_str)
    with open("MiniApp/Services/TradeOutcomeTracker.cs", "w", encoding="utf-8") as f:
        f.write(code)
    print("Replaced TradeOutcomeTracker.cs successfully")
else:
    print("Could not find target string in TradeOutcomeTracker.cs")
