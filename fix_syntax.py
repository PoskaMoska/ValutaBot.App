import os

file = 'MiniApp/Services/PendingTradeVerificationService.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

text = text.replace('SELECT close_price as "Close"', 'SELECT close_price as ""Close""')

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

