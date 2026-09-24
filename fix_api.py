import os
import re

file = 'MiniApp/wwwroot/js/api.js'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

new_session = '''
            const wSession = document.getElementById("weatherSession");
            if (wSession && data.uiMarketSession) {
                wSession.innerText = data.uiMarketSession;
                let sColor = "#f59e0b"; // default yellow for quiet/range
                let sessionLow = data.uiMarketSession.toLowerCase();
                if (sessionLow.includes("объемы") || sessionLow.includes("лондон") || sessionLow.includes("крипто")) {
                    sColor = "#10b981"; // green (good volume)
                } else if (sessionLow.includes("otc")) {
                    sColor = "#8b5cf6"; // purple (OTC)
                } else if (sessionLow.includes("тихий")) {
                    sColor = "#6b7280"; // gray (dead)
                }
                wSession.style.color = sColor;
            }
'''

text = re.sub(r'const wSession = document\.getElementById\("weatherSession"\);\s*if \(wSession && data\.uiMarketSession\) wSession\.innerText = data\.uiMarketSession;', new_session.strip(), text)


new_phase = '''
            const wPhase = document.getElementById("weatherPhase");
            if (wPhase && data.uiMarketPhase) {
                wPhase.innerText = data.uiMarketPhase;
                let phaseColor = "#10b981"; // Green default (upward/good)
                let phaseLow = data.uiMarketPhase.toLowerCase();
                
                if (phaseLow.includes("замедление") || phaseLow.includes("переход") || phaseLow.includes("флэт") || phaseLow.includes("боковик")) {
                    phaseColor = "#f59e0b"; // Yellow (Average / Sideways)
                } else if (phaseLow.includes("падени") || phaseLow.includes("медвеж") || phaseLow.includes("перепроданность") || phaseLow.includes("слабый") || phaseLow.includes("волатильный") || phaseLow.includes("резкий") || phaseLow.includes("шум")) {
                    phaseColor = "#ef4444"; // Red (Downward / Bad)
                }
                wPhase.style.color = phaseColor;
            }
'''

text = re.sub(r'const wPhase = document\.getElementById\("weatherPhase"\);\s*if \(wPhase && data\.uiMarketPhase\) \{.*?wPhase\.style\.color = phaseColor;\s*\}', new_phase.strip(), text, flags=re.DOTALL)

new_entropy = '''
            const wEntropy = document.getElementById("weatherEntropy");
            const wTitle = document.getElementById("weatherTitle");
            if (wEntropy && data.uiMarketEntropy) {
                wEntropy.innerText = data.uiMarketEntropy;
                let entColor = "#10b981";
                let entLow = data.uiMarketEntropy.toLowerCase();
                if (entLow.includes("опасн") || entLow.includes("высок") || entLow.includes("хаос")) {
                    entColor = "#ef4444";
                } else if (entLow.includes("мертв") || entLow.includes("слаб") || entLow.includes("переход")) {
                    entColor = "#f59e0b";
                }
                wEntropy.style.color = entColor;
                if (wTitle) wTitle.style.color = entColor;
            }
'''

text = re.sub(r'const wEntropy = document\.getElementById\("weatherEntropy"\);\s*const wTitle = document\.getElementById\("weatherTitle"\);\s*if \(wEntropy && data\.uiMarketEntropy\) \{.*?if \(wTitle\) wTitle\.style\.color = entColor;\s*\}', new_entropy.strip(), text, flags=re.DOTALL)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

