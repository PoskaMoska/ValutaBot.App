import re

with open('MiniApp/Services/TradeOutcomeTracker.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = re.sub(
    r'MlScore = record.MlScore,\s*CreatedAt = record.CreatedAt.ToString\(\"o\"\)', 
    'MlScore = record.MlScore,\n                FeaturesJson = record.FeaturesJson,\n                CreatedAt = record.CreatedAt.ToString(\"o\")', 
    content
)

with open('MiniApp/Services/TradeOutcomeTracker.cs', 'w', encoding='utf-8') as f:
    f.write(content)
