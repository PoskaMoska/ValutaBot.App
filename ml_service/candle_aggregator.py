"""
candle_aggregator.py - Multi-horizon OHLCV + Microstructure candle aggregator.
Builds s5, s10, s15, s30, s60 candles from Pocket Option OTC ticks.
Calculates microstructure features:
  - tick_volume (ticks per bar)
  - zero_ticks (number of zero-change ticks / micro-pauses)
  - tick_delta (sum of signed tick movements)
  - realized_vol (sum of abs tick price changes)
  - spread_drift (close - open)
"""
import numpy as np
import pandas as pd
import psycopg2

TIMEFRAMES = {
    "s5": 5,
    "s10": 10,
    "s15": 15,
    "s30": 30,
    "s60": 60,
    "1m": 60
}

def load_ticks_from_db(db_url, asset=None, limit=None):
    conn = psycopg2.connect(db_url)
    query = "SELECT asset, ts, price, recv_ms FROM po_ticks"
    params = []
    if asset:
        query += " WHERE asset = %s"
        params.append(asset)
    query += " ORDER BY ts ASC"
    if limit:
        query += f" LIMIT {int(limit)}"
    
    df = pd.read_sql_query(query, conn, params=params)
    conn.close()
    return df

def aggregate_candles(ticks_df, timeframe_sec=5):
    """
    Takes a DataFrame of ticks (must have 'ts' and 'price') and produces
    complete OHLCV candles with microstructural metrics.
    """
    if ticks_df.empty:
        return pd.DataFrame()

    df = ticks_df.copy()
    # Bucket timestamps
    df["bucket"] = (df["ts"] // timeframe_sec) * timeframe_sec
    df["d_price"] = df.groupby("asset")["price"].diff().fillna(0.0)
    df["is_zero"] = (df["d_price"] == 0.0).astype(int)
    df["sign_delta"] = np.sign(df["d_price"])
    df["abs_delta"] = df["d_price"].abs()

    # Aggregate by asset and bucket
    grouped = df.groupby(["asset", "bucket"])
    candles = grouped.agg(
        open=("price", "first"),
        high=("price", "max"),
        low=("price", "min"),
        close=("price", "last"),
        ticks=("price", "count"),
        zero_ticks=("is_zero", "sum"),
        tick_delta=("sign_delta", "sum"),
        realized_vol=("abs_delta", "sum"),
        first_ts=("ts", "min"),
        last_ts=("ts", "max")
    ).reset_index()

    # Derived bar metrics
    candles["return"] = (candles["close"] - candles["open"])
    candles["range"] = (candles["high"] - candles["low"])
    candles["direction"] = np.where(candles["close"] > candles["open"], 1,
                           np.where(candles["close"] < candles["open"], -1, 0))
    candles["zero_ratio"] = candles["zero_ticks"] / np.maximum(candles["ticks"], 1)

    # Next-bar target (for classification / binary option outcome)
    candles["next_close"] = candles.groupby("asset")["close"].shift(-1)
    candles["target_direction"] = np.where(candles["next_close"] > candles["close"], 1,
                                 np.where(candles["next_close"] < candles["close"], -1, 0))
    candles["target_return"] = candles["next_close"] - candles["close"]

    candles.rename(columns={"bucket": "time"}, inplace=True)
    candles.sort_values(by=["asset", "time"], inplace=True)
    return candles

if __name__ == "__main__":
    import os
    db_url = os.environ.get("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")
    print("Loading ticks from DB...")
    ticks = load_ticks_from_db(db_url)
    print(f"Loaded {len(ticks)} ticks across assets: {ticks['asset'].unique().tolist()}")
    
    for tf_name, tf_sec in [("s5", 5), ("s10", 10), ("s15", 15), ("s30", 30), ("s60", 60)]:
        c = aggregate_candles(ticks, timeframe_sec=tf_sec)
        print(f"\n--- Timeframe: {tf_name} ({tf_sec}s) ---")
        for asset, sub in c.groupby("asset"):
            valid = sub.dropna(subset=["next_close"])
            ups = (valid["target_direction"] == 1).sum()
            downs = (valid["target_direction"] == -1).sum()
            ties = (valid["target_direction"] == 0).sum()
            total = len(valid)
            print(f"  {asset}: {len(sub)} candles | Target: UP={ups} ({ups/total*100:.1f}%), DOWN={downs} ({downs/total*100:.1f}%), TIE={ties} ({ties/total*100:.1f}%)")
