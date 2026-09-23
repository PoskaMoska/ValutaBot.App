import os

file = 'MiniApp/Services/TradeOutcomeTracker.cs'
with open(file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

lines[54] = '        // L2-FIX: Загружаем сохранённые EMA-веса из PostgreSQL\n'
lines[159] = '                // Старая сделка без source_directions. Только глобальный исход.\n'
lines[164] = '            // L2-FIX: Сохраняем актуальное EMA-состояние в БД (асинхронно, чтобы не блокировать основной поток)\n'
lines[274] = '                        sbTrace.AppendLine("[🧠 EVOLUTION DUMP] Анализ развития интеллекта (За последние 48 часов)");\n'
lines[275] = '                        sbTrace.AppendLine($"[База знаний] Накоплено {dump.TotalTrades} исходов в БД (рост +{dump.NewTrades48h} новых паттернов).");\n'
lines[279] = '                        sbTrace.AppendLine($"[Прогресс ML] Точность нейросети сейчас: {dump.NewMlWinRate:F1}%.");\n'
lines[280] = '                        sbTrace.AppendLine($"              (До этого: {dump.OldMlWinRate:F1}% -> Интеллект {(diff >= 0 ? \\"вырос на\\" : \\"изменился на\\")} {diffStr}%).");\n'
lines[282] = '                        sbTrace.AppendLine($"[Превосходство] За эти 48ч Нейросеть {dump.MlSavedTrades} раз пошла против классических");\n'
lines[283] = '                        sbTrace.AppendLine($"                индикаторов (TA) и оказалась права.");\n'
lines[284] = '                        sbTrace.AppendLine($"[Вывод] Система стабильно умнеет. Адаптация к рынку успешна.");\n'

with open(file, 'w', encoding='utf-8') as f:
    f.writelines(lines)
print('Fixed TradeOutcomeTracker.cs using exact indices')
