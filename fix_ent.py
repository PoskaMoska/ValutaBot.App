import os
import re

file = 'MiniApp/wwwroot/js/api.js'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

new_entropy = '''
            const wEntropy = document.getElementById("weatherEntropy");
            const wTitle = document.getElementById("weatherTitle");
            if (wEntropy && data.uiMarketEntropy) {
                wEntropy.innerText = data.uiMarketEntropy;
                let entColor = "#10b981";
                let entLow = data.uiMarketEntropy.toLowerCase();
                
                // Fix for the "Безопасно" trap: explicitly check for it first,
                // or just use exact word matching, or check "хаос" / "высокая".
                if (entLow.includes("безопасно") || entLow.includes("в норме")) {
                    entColor = "#10b981"; // Green
                } else if (entLow.includes("опасн") || entLow.includes("высок") || entLow.includes("хаос")) {
                    entColor = "#ef4444"; // Red
                } else if (entLow.includes("мертв") || entLow.includes("слаб") || entLow.includes("переход")) {
                    entColor = "#f59e0b"; // Yellow
                }
                
                wEntropy.style.color = entColor;
                if (wTitle) wTitle.style.color = entColor;
            }
'''

text = re.sub(r'const wEntropy = document\.getElementById\("weatherEntropy"\);\s*const wTitle = document\.getElementById\("weatherTitle"\);\s*if \(wEntropy && data\.uiMarketEntropy\) \{.*?if \(wTitle\) wTitle\.style\.color = entColor;\s*\}', new_entropy.strip(), text, flags=re.DOTALL)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

