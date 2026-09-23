import os

file = 'MiniApp/Services/TradeOutcomeTracker.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

text = text.replace(r'\"вырос на\"', '\"вырос на\"')
text = text.replace(r'\"изменился на\"', '\"изменился на\"')

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)
