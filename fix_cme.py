import os

file = 'MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

# Since the bytes are already messed up, let's just do a blanket regex for corrupted blocks!
import re
def fix_match(m):
    return m.group(0).encode('cp1251').decode('utf-8')

# A corrupted block usually looks like a sequence of cp1251 bytes mapped to latin-1, then interpreted as UTF-8
# We know the literal strings we want to replace.
# Let's just find the file lines, and if they contain '>= 0.99 =>', replace the whole line!
lines = text.split('\n')
for i, line in enumerate(lines):
    if '>= 0.99 =>' in line:
        lines[i] = '                >= 0.99 => "💎 ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",'
    elif '>= 0.65 =>' in line:
        lines[i] = '                >= 0.65 => "💎 СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",'
    elif '_       =>' in line and '(1 ' in line:
        lines[i] = '                _       => "💎 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"'
    elif 'ConfluenceLabel:' in line and 'Matrix Unavailable' in line:
        lines[i] = '                ConfluenceLabel: "💎 3D Matrix Unavailable",'
    elif 'ScoreDirection (' in line and 'avgDiff' in line:
        lines[i] = '    // В отличие от ScoreDirection (которому нужен только цены и дает avgDiff±0.5),'
    elif 'ATR/ADX' in line and 'High/Low' in line:
        lines[i] = '    // этот метод передает реальные High/Low свечи -> ATR/ADX корректны -> нет шума ±12%.'
    elif 'OhlcCandle' in line and 'ScoreTimeframe' in line:
        lines[i] = '    // Передаем реальные OhlcCandle[] (с настоящими High/Low) напрямую в ScoreTimeframe'
    elif '[-1, +1]' in line:
        lines[i] = '    // Порог ±0.20: при шкале [-1, +1] отсекает рыночный шум'
    elif 'FIX PRIORITY-1:' in line:
        lines[i] = '    // FIX PRIORITY-1: Перегрузка принимающая уже загруженные current+higher свечи из Orchestrator\'а.'
    elif 'micro/primary/macro' in line:
        lines[i] = '    // Умно маппит их на слоты (micro/primary/macro) и делает 1 HTTP-запрос для недостающего таймфрейма.'
    elif 'Doppelganger Bug' in line:
        lines[i] = '    // Это устраняет главную причину нестабильности: TwelveData rate limit (7 req/min) и Doppelganger Bug.'

with open(file, 'w', encoding='utf-8') as f:
    f.write('\n'.join(lines))
print('Fixed ConfluenceMatrixEngine.cs')
