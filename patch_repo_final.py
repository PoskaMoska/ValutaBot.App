import re

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('public double MlScore { get; set; }', 'public double MlScore { get; set; }\n        public string FeaturesJson { get; set; } = \"\";')

content = content.replace('ml_score, created_at', 'ml_score, features_json, created_at')
content = content.replace('@MlScore, @CreatedAt', '@MlScore, @FeaturesJson, @CreatedAt')
content = content.replace('ml_score = EXCLUDED.ml_score', 'ml_score = EXCLUDED.ml_score,\n                        features_json = EXCLUDED.features_json')

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'w', encoding='utf-8') as f:
    f.write(content)
