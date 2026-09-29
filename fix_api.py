with open('MiniApp/wwwroot/js/api.js', 'r', encoding='utf-8') as f:
    lines = f.readlines()

new_lines = []
skip = False
for line in lines:
    if "let sessionLow = data.uiMarketSession.toLowerCase();" in line:
        new_lines.append(line)
        new_lines.append('                if (sessionLow.includes("европа") || sessionLow.includes("америка") || sessionLow.includes("нью-йорк") || sessionLow.includes("лондон")) {\n')
        new_lines.append('                    sColor = "#10b981"; // green\n')
        new_lines.append('                } else if (sessionLow.includes("otc")) {\n')
        new_lines.append('                    sColor = "#8b5cf6"; // purple\n')
        new_lines.append('                } else if (sessionLow.includes("мертв") || sessionLow.includes("пил") || sessionLow.includes("ази")) {\n')
        new_lines.append('                    sColor = "#f59e0b"; // yellow\n')
        new_lines.append('                }\n')
        skip = True
        continue
    
    if skip and "wSession.style.color" in line:
        skip = False
        new_lines.append(line)
        continue
    
    if not skip:
        new_lines.append(line)

lines = new_lines
new_lines = []
skip = False
for line in lines:
    if "let phaseLow = data.uiMarketPhase.toLowerCase();" in line:
        new_lines.append(line)
        new_lines.append('                if (phaseLow.includes("флэт") || phaseLow.includes("замедление") || phaseLow.includes("консолидация") || phaseLow.includes("боковик")) {\n')
        new_lines.append('                    phaseColor = "#f59e0b"; // Yellow\n')
        new_lines.append('                } else if (phaseLow.includes("медвеж") || phaseLow.includes("перепродан") || phaseLow.includes("падени") || phaseLow.includes("сброс")) {\n')
        new_lines.append('                    phaseColor = "#ef4444"; // Red\n')
        new_lines.append('                } else if (phaseLow.includes("быч") || phaseLow.includes("перекуплен") || phaseLow.includes("рост")) {\n')
        new_lines.append('                    phaseColor = "#10b981"; // Green\n')
        new_lines.append('                }\n')
        skip = True
        continue
    
    if skip and "wPhase.style.color" in line:
        skip = False
        new_lines.append(line)
        continue
    
    if not skip:
        new_lines.append(line)
        
lines = new_lines
new_lines = []
skip = False
for line in lines:
    if "let entLow = data.uiMarketEntropy.toLowerCase();" in line:
        new_lines.append(line)
        new_lines.append('                if (entLow.includes("норм") || entLow.includes("безопасн")) {\n')
        new_lines.append('                    entColor = "#10b981"; // Green\n')
        new_lines.append('                } else if (entLow.includes("хаос") || entLow.includes("опасн") || entLow.includes("шторм")) {\n')
        new_lines.append('                    entColor = "#ef4444"; // Red\n')
        new_lines.append('                } else if (entLow.includes("мертв") || entLow.includes("сонн") || entLow.includes("штиль")) {\n')
        new_lines.append('                    entColor = "#f59e0b"; // Yellow\n')
        new_lines.append('                }\n')
        skip = True
        continue
    
    if skip and "wEntropy.style.color" in line:
        skip = False
        new_lines.append(line)
        continue
    
    if not skip:
        new_lines.append(line)

with open('MiniApp/wwwroot/js/api.js', 'w', encoding='utf-8') as f:
    f.writelines(new_lines)
