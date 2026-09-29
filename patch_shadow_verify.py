import re

with open('MiniApp/Services/PendingTradeVerificationService.cs', 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace(
    'bool isCorrect = (record.Direction == \"BUY\" && exitPrice.Value > record.EntryPrice)\n                      || (record.Direction == \"PUT\" && exitPrice.Value < record.EntryPrice);',
    'bool isCorrect = (record.Direction.EndsWith(\"BUY\") && exitPrice.Value > record.EntryPrice)\n                      || (record.Direction.EndsWith(\"PUT\") && exitPrice.Value < record.EntryPrice);'
)

with open('MiniApp/Services/PendingTradeVerificationService.cs', 'w', encoding='utf-8') as f:
    f.write(content)
