import re

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Select Query
content = content.replace(
    'ml_prob as \"MlProb\", ml_score as \"MlScore\"', 
    'ml_prob as \"MlProb\", ml_score as \"MlScore\", features_json as \"FeaturesJson\"'
)

# Mapping
content = re.sub(
    r'MlScore = \(double\)\(r\.MlScore \?\? 0\.0\)', 
    'MlScore = (double)(r.MlScore ?? 0.0),\n                FeaturesJson = r.FeaturesJson ?? \"\"', 
    content
)

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'w', encoding='utf-8') as f:
    f.write(content)
