import os
import re

file = 'MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

# Fix int boost
text = re.sub(r'int boost = confluenceRatio switch\s*\{\s*>= 0\.99 => [^,]+,\s*>= 0\.65 => [^,]+,\s*_       => [^\}]+\s*\};',
    '''int boost = confluenceRatio switch
            {
                >= 0.99 => 15,
                >= 0.65 => 7,
                _       => 0
            };''', text)

# Fix string label
text = re.sub(r'string label = confluenceRatio switch\s*\{\s*>= 0\.99 => [^,]+,\s*>= 0\.65 => [^,]+,\s*_       => [^\}]+\s*\};',
    '''string label = confluenceRatio switch
            {
                >= 0.99 => "💎 ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)",
                >= 0.65 => "💎 СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)",
                _       => "💎 СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)"
            };''', text)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)
