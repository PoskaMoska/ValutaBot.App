# -*- coding: utf-8 -*-
with open('MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs', 'r', encoding='utf-8') as f:
    content = f.read()

old = '''        return new ConfluenceMatrixResult(
            finalDir,
            finalProb,
            sb.ToString().TrimEnd()
        );'''

new = '''        if (consecutiveLosses >= 3)
        {
            finalDir = "NEUTRAL";
            finalProb = 50.0;
            sb.AppendLine($"\n?? Сработал Anti-Tilt: {consecutiveLosses} минуса подряд на этой паре. Сигналы заблокированы до победы или смены тренда.");
        }

        return new ConfluenceMatrixResult(
            finalDir,
            finalProb,
            sb.ToString().TrimEnd()
        );'''

content = content.replace(old, new)

with open('MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs', 'w', encoding='utf-8') as f:
    f.write(content)
