import os

file = 'MiniApp/Features/MarketAnalysis/Engines/ConfluenceMatrixEngine.cs'
with open(file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

lines[372] = '        // Пользовательское требование: Бот должен всегда давать сигнал (без NEUTRAL зоны)\n'
lines[375] = '        // Определение маржи уверенности (от 0.0 до 0.5)\n'
lines[389] = '            sb.AppendLine("[Feedback Loop / Рейтинг модулей]");\n'
lines[390] = '            sb.AppendLine($" - ML (Нейросеть): WinRate {mlWr:F1}% -> {(mlWr > 52 ? \\"Доверие УВЕЛИЧЕНО\\" : (mlWr < 48 ? \\"Доверие СНИЖЕНО\\" : \\"Норма\\"))}");\n'
lines[391] = '            sb.AppendLine($" - Tech Analysis: WinRate {taWr:F1}% -> {(taWr > 52 ? \\"Доверие УВЕЛИЧЕНО\\" : (taWr < 48 ? \\"Доверие СНИЖЕНО\\" : \\"Норма\\"))}");\n'
lines[392] = '            sb.AppendLine($" - Smart Money: WinRate {smcWr:F1}% -> {(smcWr > 52 ? \\"Доверие УВЕЛИЧЕНО\\" : (smcWr < 48 ? \\"Доверие СНИЖЕНО\\" : \\"Норма\\"))}");\n'
lines[393] = '            sb.AppendLine($" - OrderFlow: WinRate {ofWr:F1}% -> {(ofWr > 52 ? \\"Доверие УВЕЛИЧЕНО\\" : (ofWr < 48 ? \\"Доверие СНИЖЕНО\\" : \\"Норма\\"))}");\n'

lines[403] = '        sb.AppendLine("[Динамические фильтры]");\n'
lines[404] = '        sb.AppendLine($"- Базовая уверенность: {(0.5 + margin)*100:F1}% {finalDir}");\n'
lines[406] = '        // 1. Штраф конфликта таймфреймов\n'
lines[410] = '            sb.AppendLine("- Конфликт таймфреймов: Снижение уверенности");\n'
lines[413] = '        // 2. Критический конфликт ТехАнализа (Исправление бага с 25%)\n'
lines[416] = '            margin *= 0.5; // Срезаем только маржу, а не базовые 50%\n'
lines[417] = '            sb.AppendLine("- Критический разворот Теханализа: Сильное снижение уверенности");\n'
lines[420] = '        // 3. Фаза рынка (RSI)\n'
lines[424] = '            sb.AppendLine("- Фаза рынка: Перекупленность (риск лонга на пике)");\n'
lines[429] = '            sb.AppendLine("- Фаза рынка: Перепроданность (риск шорта на дне)");\n'
lines[434] = '            sb.AppendLine("- Фаза рынка: Подтверждение отката вниз (Перекупленность)");\n'
lines[439] = '            sb.AppendLine("- Фаза рынка: Подтверждение отката вверх (Перепроданность)");\n'
lines[442] = '        // 4. Энтропия / Скорость рынка\n'
lines[448] = '            sb.AppendLine("- Энтропия: Экстремальная волатильность (Хаос), занижение уверенности");\n'

with open(file, 'w', encoding='utf-8') as f:
    f.writelines(lines)
print('Fixed ConfluenceMatrixEngine.cs part 2')
