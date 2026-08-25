import sqlite3
import pandas as pd
import warnings
import lightgbm as lgb
import numpy as np

warnings.filterwarnings("ignore")

conn = sqlite3.connect("ml_service/data/ValutaTicks.db")
query = """
SELECT OpenTime, Open, High, Low, Close, Volume 
FROM HistoricalCandles 
WHERE Interval = '1m' AND Asset = 'EURUSD' 
ORDER BY OpenTime ASC 
LIMIT 100000
"""
df = pd.read_sql_query(query, conn)
conn.close()

import sys
sys.path.insert(0, "ml_service")
import features

df = df.rename(columns={"OpenTime":"opentime", "Open":"open", "High":"high", "Low":"low", "Close":"close", "Volume":"volume"})

H = 5
df["target"] = (df["close"].shift(-H) > df["close"]).astype(int)
df_f = features.build_features(df.to_dict('records'))

df_f["target"] = df["target"]
df_f.dropna(inplace=True)

X = df_f.drop(columns=["target"]).values
y = df_f["target"].values

split_idx = int(len(X) * 0.8)
X_train, X_test = X[:split_idx], X[split_idx:]
y_train, y_test = y[:split_idx], y[split_idx:]

model = lgb.LGBMClassifier(objective='binary', n_estimators=300, random_state=42)
model.fit(X_train, y_train)

probs = model.predict_proba(X_test)[:, 1]

print("\n=== РАСПРЕДЕЛЕНИЕ СИГНАЛОВ ПО НАПРАВЛЕНИЯМ (BUY vs PUT) ===")
print("Тестовая выборка: 20,000 свечей (out-of-sample).")
print("-" * 65)

for threshold in [0.5, 0.55, 0.60, 0.65, 0.70, 0.75]:
    buys = probs > threshold
    puts = probs < (1 - threshold)
    
    buy_count = buys.sum()
    put_count = puts.sum()
    total = buy_count + put_count
    
    if total > 0:
        buy_pct = (buy_count / total) * 100
        put_pct = (put_count / total) * 100
        print(f"Порог: {threshold*100:.0f}% | Всего: {total:5d} | ВВЕРХ (BUY): {buy_count:4d} ({buy_pct:5.1f}%) | ВНИЗ (PUT): {put_count:4d} ({put_pct:5.1f}%)")
    else:
        print(f"Порог: {threshold*100:.0f}% | Всего: 0")

print("-" * 65)
