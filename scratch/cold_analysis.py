import sqlite3
import pandas as pd
import requests
import time
from datetime import datetime

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

if len(df) == 0:
    print("No candles found in DB!")
    exit(1)

print(f"Loaded {len(df)} candles.")

# Simulate ML predictions (we will use the live API to be authentic)
# But since calling API 100k times is slow, we will evaluate directly using the model if possible
import sys
sys.path.insert(0, "ml_service")
from model import ForexPredictor
import features

print("Initializing ML model...")
model = ForexPredictor("EURUSD", "1m")
model._try_load()

print("Extracting features (this might take a few seconds)...")
df = df.rename(columns={"OpenTime":"openTime", "Open":"open", "High":"high", "Low":"low", "Close":"close", "Volume":"volume"})
# Build features
f_df = features.build_features(df)
f_df = features.add_time_features(f_df, "openTime", is_forex=True)
f_df.dropna(inplace=True)
f_df.reset_index(drop=True, inplace=True)

print(f"Feature matrix size: {f_df.shape}")

# True labels for H=5
H = 5
f_df["target_close"] = f_df["close"].shift(-H)
f_df = f_df.dropna()

print("Calculating accuracy...")
# LightGBM predicts BUY if prob > 0.5, else PUT.
# The user wants true accuracy.
X = f_df[features.FEATURES].values

try:
    probs = model._model.predict_proba(X)[:, 1]
    
    # We will simulate bot's confidence threshold
    # Bot only enters if confidence > 0.65 or so, let's see for different thresholds
    for threshold in [0.5, 0.60, 0.70, 0.80]:
        buys = probs > threshold
        puts = probs < (1 - threshold)
        
        # Win logic:
        buy_wins = (f_df["target_close"] > f_df["close"])[buys].sum()
        put_wins = (f_df["target_close"] < f_df["close"])[puts].sum()
        
        total_trades = buys.sum() + puts.sum()
        if total_trades > 0:
            win_rate = (buy_wins + put_wins) / total_trades
            print(f"Threshold: {threshold:.2f} | Trades: {total_trades:5d} | Win Rate: {win_rate*100:.2f}%")
        else:
            print(f"Threshold: {threshold:.2f} | Trades: 0")

except Exception as e:
    print(f"Error evaluating model: {e}")
