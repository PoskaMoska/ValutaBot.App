import os
file = 'MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs'
with open(file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# Fix boost lines
lines[74] = '                >= 0.99 => 15,\n'
lines[75] = '                >= 0.65 => 7,\n'
lines[76] = '                _       => 0\n'

lines[264] = '                >= 0.99 => 15,\n'
lines[265] = '                >= 0.65 => 7,\n'
lines[266] = '                _       => 0\n'

with open(file, 'w', encoding='utf-8') as f:
    f.writelines(lines)
