import sqlite3
conn = sqlite3.connect("ml_service/data/ValutaTicks.db")
cur = conn.cursor()
cur.execute("SELECT Asset, Interval, COUNT(*) as cnt FROM HistoricalCandles GROUP BY Asset, Interval ORDER BY cnt DESC")
rows = cur.fetchall()
print("=== Количество свечей по каждой паре и таймфрейму ===")
for r in rows:
    print(f"  {r[0]:<22} {r[1]:<6} -> {r[2]:,} свечей")
conn.close()

# Проверяем модели которые существуют
import os
model_dir = "ml_service/models"
if os.path.exists(model_dir):
    print("\n=== Файлы моделей (ML) ===")
    for f in os.listdir(model_dir):
        size = os.path.getsize(os.path.join(model_dir, f))
        print(f"  {f:<50} {size/1024:.1f} KB")
else:
    print("\nПапка models не найдена")
