import sqlite3
import pandas as pd
import numpy as np

print("Loading 100k candles from local database...")
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
from model import ForexPredictor

print("Initializing ML model...")
model = ForexPredictor("EURUSD", "1m")
model._try_load()

print("Building features via model internal method...")
# Convert df to list of dicts that model._prepare_data expects
# The model uses df = pd.DataFrame(candles) but it expects is_forex etc.
# The easiest way is to let the model train on these to see what it can do!

records = df.rename(columns={"OpenTime":"openTime", "Open":"open", "High":"high", "Low":"low", "Close":"close", "Volume":"volume"}).to_dict('records')
X, y, df_feats = model._prepare_data(records, is_forex=True)

print(f"Prepared Data: X.shape={X.shape}, y.shape={y.shape}")

# True labels
probs = model._model.predict_proba(X)[:, 1]

for threshold in [0.5, 0.55, 0.60, 0.65, 0.70, 0.75, 0.80]:
    buys = probs > threshold
    puts = probs < (1 - threshold)
    
    buy_wins = (y[buys] == 1).sum()
    put_wins = (y[puts] == 0).sum()
    
    total_trades = buys.sum() + puts.sum()
    if total_trades > 0:
        win_rate = (buy_wins + put_wins) / total_trades
        print(f"Threshold: {threshold:.2f} | Trades: {total_trades:5d} | Win Rate: {win_rate*100:.2f}%")
    else:
        print(f"Threshold: {threshold:.2f} | Trades: 0")
