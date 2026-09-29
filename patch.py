import re

with open('MiniApp/Services/SignalTracker.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('double mlProb = 0.0, double mlScore = 0.0)', 'double mlProb = 0.0, double mlScore = 0.0, string featuresJson = \"\")')
content = re.sub(r'MlScore = mlScore\s*};', 'MlScore = mlScore,\n            FeaturesJson = featuresJson\n        };', content)
content = content.replace('public double MlScore { get; set; }', 'public double MlScore { get; set; }\n        public string FeaturesJson { get; set; } = \"\";')

with open('MiniApp/Services/SignalTracker.cs', 'w', encoding='utf-8') as f:
    f.write(content)
