import re

with open('MiniApp/Data/Repositories/TradeRepository.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# Add FeaturesJson to TradeOutcomeRecord
content = content.replace('public double MlScore { get; set; }', 'public double MlScore { get; set; }\n        public string FeaturesJson { get; set; } = \"\";')

# Pending Trade Insert
content = content.replace('ml_score, created_at', 'ml_score, features_json, created_at')
content = content.replace('@MlScore, @CreatedAt', '@MlScore, @FeaturesJson, @CreatedAt')

# Pending Trade Select (SaveTradeOutcomeAsync is not selecting pending trade, it receives TradeOutcomeRecord)
# Let's check TradeOutcomeTracker.cs where it creates TradeOutcomeRecord
