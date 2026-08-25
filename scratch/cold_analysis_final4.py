import sqlite3
import pandas as pd
import warnings
import lightgbm as lgb

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

# Some features might be missing in df_f if features.py drops them.
# I will just use all columns from df_f as features.
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

print("\n=== ВАЛЮТНЫЙ БОТ: ХОЛОДНЫЙ АНАЛИЗ ПРОЦЕНТА ПОБЕД ===")
print("Рынок: EURUSD, Таймфрейм: 1m, Горизонт экспирации: 5 минут")
print(f"Тестовая выборка: {len(X_test)} свечей (out-of-sample).")
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
