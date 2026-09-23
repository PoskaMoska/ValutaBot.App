import os

file = 'MiniApp/Services/TradeOutcomeTracker.cs'
with open(file, 'r', encoding='utf-8') as f:
    text = f.read()

# I will just replace the exact lines that got corrupted!
# Let's find those lines using regex to match the structure.

import re

text = re.sub(r'// L2-FIX:[^\n]*EMA-[^\n]*PostgreSQL', r'// L2-FIX: Загружаем сохранённые EMA-веса из PostgreSQL', text)
text = re.sub(r'//[^\n]*source_directions\.[^\n]*', r'// Старая сделка без source_directions. Только глобальный исход.', text)
text = re.sub(r'// L2-FIX:[^\n]*EMA-[^\n]*\([^\n]*\)', r'// L2-FIX: Сохраняем актуальное EMA-состояние в БД (асинхронно, чтобы не блокировать основной поток)', text)

text = re.sub(r'sbTrace\.AppendLine\("\[[^\]]*EVOLUTION DUMP\][^\n]*', r'sbTrace.AppendLine("[🧠 EVOLUTION DUMP] Анализ развития интеллекта (За последние 48 часов)");', text)
text = re.sub(r'sbTrace\.AppendLine\(\$"\[[^\]]*\][^\n]*dump\.TotalTrades[^\n]*', r'sbTrace.AppendLine($"[База знаний] Накоплено {dump.TotalTrades} исходов в БД (рост +{dump.NewTrades48h} новых паттернов).");', text)
text = re.sub(r'sbTrace\.AppendLine\(\$"\[[^\]]*ML\][^\n]*dump\.NewMlWinRate:F1[^\n]*', r'sbTrace.AppendLine($"[Прогресс ML] Точность нейросети сейчас: {dump.NewMlWinRate:F1}%.");', text)
text = re.sub(r'sbTrace\.AppendLine\(\$"\s*\([^:]*:[^\n]*dump\.OldMlWinRate:F1[^\n]*', r'sbTrace.AppendLine($"              (До этого: {dump.OldMlWinRate:F1}% -> Интеллект {(diff >= 0 ? \"вырос на\" : \"изменился на\")} {diffStr}%).");', text)
text = re.sub(r'sbTrace\.AppendLine\(\$"\[[^\]]*\][^\n]*dump\.MlSavedTrades[^\n]*', r'sbTrace.AppendLine($"[Превосходство] За эти 48ч Нейросеть {dump.MlSavedTrades} раз пошла против классических");', text)
text = re.sub(r'sbTrace\.AppendLine\(\$"\s*[^\n]*\(TA\)[^\n]*', r'sbTrace.AppendLine($"                индикаторов (TA) и оказалась права.");', text)
text = re.sub(r'sbTrace\.AppendLine\(\$"\[[^\]]*\][^\n]*\.[^\n]*\."\);', r'sbTrace.AppendLine($"[Вывод] Система стабильно умнеет. Адаптация к рынку успешна.");', text)

with open(file, 'w', encoding='utf-8') as f:
    f.write(text)

