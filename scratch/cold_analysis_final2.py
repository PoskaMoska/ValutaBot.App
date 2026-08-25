import sqlite3
import pandas as pd
import numpy as np
import warnings
import lightgbm as lgb
from sklearn.metrics import accuracy_score

warnings.filterwarnings("ignore")

print("1. Loading 100k candles from ValutaTicks.db...")
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

print(f"   Loaded {len(df)} candles.")

import sys
sys.path.insert(0, "ml_service")
import features

print("2. Extracting features and targets...")
# features.py expects lower case keys: opentime, open, high, low, close, volume
df = df.rename(columns={"OpenTime":"opentime", "Open":"open", "High":"high", "Low":"low", "Close":"close", "Volume":"volume"})
df_f = features.build_features(df.to_dict('records'))
df_f.dropna(inplace=True)
df_f.reset_index(drop=True, inplace=True)

# We predict 5 candles ahead
H = 5
df_f["target"] = (df_f["close"].shift(-H) > df_f["close"]).astype(int)
df_f = df_f.iloc[:-H]

X = df_f[features.FEATURES].values
y = df_f["target"].values

print(f"   Total valid samples: {len(X)}")

print("3. Training LightGBM on 80% (80,000 candles) and testing on 20% (20,000 candles)...")
split_idx = int(len(X) * 0.8)
X_train, X_test = X[:split_idx], X[split_idx:]
y_train, y_test = y[:split_idx], y[split_idx:]

model = lgb.LGBMClassifier(objective='binary', n_estimators=300, random_state=42)
model.fit(X_train, y_train)

probs = model.predict_proba(X_test)[:, 1]

print("\n=== ВАЛЮТНЫЙ БОТ: ХОЛОДНЫЙ АНАЛИЗ ПРОЦЕНТА ПОБЕД ===")
print("Рынок: EURUSD, Таймфрейм: 1m, Горизонт экспирации: 5 минут")
print("Тестовая выборка: 20,000 свечей (out-of-sample).")
print("-" * 50)

for threshold in [0.5, 0.55, 0.60, 0.65, 0.70]:
    buys = probs > threshold
    puts = probs < (1 - threshold)
    
    buy_wins = (y_test[buys] == 1).sum()
    put_wins = (y_test[puts] == 0).sum()
    
    total_trades = buys.sum() + puts.sum()
    if total_trades > 0:
        win_rate = (buy_wins + put_wins) / total_trades
        print(f"Порог уверенности: {threshold*100:.0f}%  |  Сделок: {total_trades:5d}  |  Win Rate: {win_rate*100:.2f}%")
    else:
        print(f"Порог уверенности: {threshold*100:.0f}%  |  Сделок: 0")

print("-" * 50)
