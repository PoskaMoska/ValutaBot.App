import re

with open('MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs', 'r', encoding='utf-8') as f:
    content = f.read()

# We have an extra "else { ... }" block after the Anti-Streak logic due to a regex accident.
# We will find the Anti-Streak block and remove the garbage after it.

pattern = r'(// Anti-Streak Filter.*?mlWeight \*= Math\.Max\(0\.5, 1\.0 - \(consecutiveSame - 2\) \* 0\.15\); \s*\}\s*)else\s*\{\s*// Macro: standard blend.*?mathWeight \*= 0\.85;\s*\}\s*\}'
content = re.sub(pattern, r'\1', content, flags=re.DOTALL)

with open('MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs', 'w', encoding='utf-8') as f:
    f.write(content)
print('Fixed duplicate block')
