import os

file = 'MiniApp/Services/TradeOutcomeTracker.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

lines = text.split('\n')
for i, line in enumerate(lines):
    if 'EMA-' in line and 'PostgreSQL' in line:
        lines[i] = '        // L2-FIX: Загружаем сохранённые EMA-веса из PostgreSQL'
    elif 'source_directions.' in line:
        lines[i] = '                // Старая сделка без source_directions. Только глобальный исход.'
    elif 'EMA-' in line and 'L2-FIX' in line and not 'PostgreSQL' in line:
        lines[i] = '            // L2-FIX: Сохраняем актуальное EMA-состояние в БД (асинхронно, чтобы не блокировать основной поток)'
    elif 'EVOLUTION DUMP' in line:
        lines[i] = '                        sbTrace.AppendLine("[🧠 EVOLUTION DUMP] Анализ развития интеллекта (За последние 48 часов)");'
    elif 'dump.TotalTrades' in line:
        lines[i] = '                        sbTrace.AppendLine($"[База знаний] Накоплено {dump.TotalTrades} исходов в БД (рост +{dump.NewTrades48h} новых паттернов).");'
    elif 'dump.NewMlWinRate' in line:
        lines[i] = '                        sbTrace.AppendLine($"[Прогресс ML] Точность нейросети сейчас: {dump.NewMlWinRate:F1}%.");'
    elif 'dump.OldMlWinRate' in line:
        lines[i] = '                        sbTrace.AppendLine($"              (До этого: {dump.OldMlWinRate:F1}% -> Интеллект {(diff >= 0 ? \"вырос на\" : \"изменился на\")} {diffStr}%).");'
    elif 'dump.MlSavedTrades' in line:
        lines[i] = '                        sbTrace.AppendLine($"[Превосходство] За эти 48ч Нейросеть {dump.MlSavedTrades} раз пошла против классических");'
    elif '(TA)' in line and 'sbTrace.AppendLine' in line:
        lines[i] = '                        sbTrace.AppendLine($"                индикаторов (TA) и оказалась права.");'
    elif 'sbTrace.AppendLine' in line and '[' in line and ']' in line and not 'dump' in line and '' in line:
        lines[i] = '                        sbTrace.AppendLine($"[Вывод] Система стабильно умнеет. Адаптация к рынку успешна.");'
    elif 'sbTrace.AppendLine' in line and '[?' in line:
        lines[i] = '                        sbTrace.AppendLine($"[Вывод] Система стабильно умнеет. Адаптация к рынку успешна.");'

with open(file, 'w', encoding='utf-8') as f:
    f.write('\n'.join(lines))
print('Fixed TradeOutcomeTracker.cs')
