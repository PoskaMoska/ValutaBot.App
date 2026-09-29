import codecs

lines = []
with codecs.open('MiniApp/wwwroot/js/api.js', 'r', 'utf-8') as f:
    lines = f.readlines()

new_lines = []
skip = False
for line in lines:
    if "let sessionLow = data.uiMarketSession.toLowerCase();" in line:
        new_lines.append(line)
        new_lines.append('                if (sessionLow.includes("\\u0435\\u0432\\u0440\\u043e\\u043f\\u0430") || sessionLow.includes("\\u0430\\u043c\\u0435\\u0440\\u0438\\u043a\\u0430") || sessionLow.includes("\\u043d\\u044c\\u044e-\\u0439\\u043e\\u0440\\u043a") || sessionLow.includes("\\u043b\\u043e\\u043d\\u0434\\u043e\\u043d")) {\n')
        new_lines.append('                    sColor = "#10b981"; // green\n')
        new_lines.append('                } else if (sessionLow.includes("otc")) {\n')
        new_lines.append('                    sColor = "#8b5cf6"; // purple\n')
        new_lines.append('                } else if (sessionLow.includes("\\u043c\\u0435\\u0440\\u0442\\u0432") || sessionLow.includes("\\u043f\\u0438\\u043b") || sessionLow.includes("\\u0430\\u0437\\u0438")) {\n')
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
        new_lines.append('                if (phaseLow.includes("\\u0444\\u043b\\u044d\\u0442") || phaseLow.includes("\\u0437\\u0430\\u043c\\u0435\\u0434\\u043b\\u0435\\u043d\\u0438\\u0435") || phaseLow.includes("\\u043a\\u043e\\u043d\\u0441\\u043e\\u043b\\u0438\\u0434\\u0430\\u0446\\u0438\\u044f") || phaseLow.includes("\\u0431\\u043e\\u043a\\u043e\\u0432\\u0438\\u043a")) {\n')
        new_lines.append('                    phaseColor = "#f59e0b"; // Yellow\n')
        new_lines.append('                } else if (phaseLow.includes("\\u043c\\u0435\\u0434\\u0432\\u0435\\u0436") || phaseLow.includes("\\u043f\\u0435\\u0440\\u0435\\u043f\\u0440\\u043e\\u0434\\u0430\\u043d") || phaseLow.includes("\\u043f\\u0430\\u0434\\u0435\\u043d\\u0438") || phaseLow.includes("\\u0441\\u0431\\u0440\\u043e\\u0441")) {\n')
        new_lines.append('                    phaseColor = "#ef4444"; // Red\n')
        new_lines.append('                } else if (phaseLow.includes("\\u0431\\u044b\\u0447") || phaseLow.includes("\\u043f\\u0435\\u0440\\u0435\\u043a\\u0443\\u043f\\u043b\\u0435\\u043d") || phaseLow.includes("\\u0440\\u043e\\u0441\\u0442")) {\n')
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
        new_lines.append('                if (entLow.includes("\\u043d\\u043e\\u0440\\u043c") || entLow.includes("\\u0431\\u0435\\u0437\\u043e\\u043f\\u0430\\u0441\\u043d")) {\n')
        new_lines.append('                    entColor = "#10b981"; // Green\n')
        new_lines.append('                } else if (entLow.includes("\\u0445\\u0430\\u043e\\u0441") || entLow.includes("\\u043e\\u043f\\u0430\\u0441\\u043d") || entLow.includes("\\u0448\\u0442\\u043e\\u0440\\u043c")) {\n')
        new_lines.append('                    entColor = "#ef4444"; // Red\n')
        new_lines.append('                } else if (entLow.includes("\\u043c\\u0435\\u0440\\u0442\\u0432") || entLow.includes("\\u0441\\u043e\\u043d\\u043d") || entLow.includes("\\u0448\\u0442\\u0438\\u043b\\u044c")) {\n')
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

with codecs.open('MiniApp/wwwroot/js/api.js', 'w', 'utf-8') as f:
    f.writelines(new_lines)
