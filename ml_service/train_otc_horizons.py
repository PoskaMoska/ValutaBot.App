"""
train_otc_horizons.py - Comprehensive Multi-Horizon ML & Feature Forensics on Native Pocket Option OTC Data.
Evaluates 5 horizons: 5s, 10s, 15s, 30s, 60s.
Uses strict Walk-Forward out-of-sample validation (no future lookahead).
Tests microstructural features: tick speed, zero-tick ratio, order-flow delta, streak exhaustion, volatility bursts.
"""
import os
import sys
import math
import numpy as np
import pandas as pd
import psycopg2
from sklearn.metrics import accuracy_score, precision_score, recall_score, f1_score
import lightgbm as lgb
import warnings
warnings.filterwarnings("ignore")

DEFAULT_DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def load_ticks():
    db_url = os.environ.get("DATABASE_URL", DEFAULT_DB_URL)
    conn = psycopg2.connect(db_url)
    df = pd.read_sql_query("SELECT asset, ts, price, recv_ms FROM po_ticks ORDER BY ts ASC;", conn)
    conn.close()
    return df

def build_candle_dataset(ticks_df, timeframe_sec=5):
    df = ticks_df.copy()
    df["bucket"] = (df["ts"] // timeframe_sec) * timeframe_sec
    df["d_price"] = df.groupby("asset")["price"].diff().fillna(0.0)
    df["is_zero"] = (df["d_price"] == 0.0).astype(int)
    df["sign_delta"] = np.sign(df["d_price"])
    df["abs_delta"] = df["d_price"].abs()

    grouped = df.groupby(["asset", "bucket"])
    candles = grouped.agg(
        open=("price", "first"),
        high=("price", "max"),
        low=("price", "min"),
        close=("price", "last"),
        ticks=("price", "count"),
        zero_ticks=("is_zero", "sum"),
        tick_delta=("sign_delta", "sum"),
        realized_vol=("abs_delta", "sum")
    ).reset_index()

    candles.rename(columns={"bucket": "time"}, inplace=True)
    candles.sort_values(by=["asset", "time"], inplace=True)

    # Engineering features per asset
    dfs = []
    for asset, group in candles.groupby("asset"):
        g = group.copy().reset_index(drop=True)
        # Returns
        g["ret_1"] = (g["close"] - g["open"]) / g["open"]
        g["ret_prev1"] = (g["close"] - g["close"].shift(1)) / g["close"].shift(1)
        g["ret_prev2"] = (g["close"] - g["close"].shift(2)) / g["close"].shift(2)
        g["ret_prev3"] = (g["close"] - g["close"].shift(3)) / g["close"].shift(3)
        g["ret_prev5"] = (g["close"] - g["close"].shift(5)) / g["close"].shift(5)

        # Microstructure features
        g["zero_ratio"] = g["zero_ticks"] / np.maximum(g["ticks"], 1)
        g["tick_delta_norm"] = g["tick_delta"] / np.maximum(g["ticks"], 1)
        g["tick_density"] = g["ticks"] / timeframe_sec
        g["realized_vol_norm"] = g["realized_vol"] / np.maximum(g["high"] - g["low"], 1e-6)

        # Bar morphology
        g["hl_range"] = (g["high"] - g["low"]) / g["open"]
        g["upper_wick"] = (g["high"] - np.maximum(g["open"], g["close"])) / g["open"]
        g["lower_wick"] = (np.minimum(g["open"], g["close"]) - g["low"]) / g["open"]

        # Streaks (consecutive up or down candles)
        directions = np.sign(g["close"] - g["open"])
        streaks = []
        cur_streak = 0
        for d in directions:
            if d > 0:
                cur_streak = cur_streak + 1 if cur_streak > 0 else 1
            elif d < 0:
                cur_streak = cur_streak - 1 if cur_streak < 0 else -1
            else:
                cur_streak = 0
            streaks.append(cur_streak)
        g["streak"] = streaks

        # Fast RSI (5 periods and 10 periods)
        delta = g["close"].diff()
        gain = (delta.where(delta > 0, 0)).rolling(5).mean()
        loss = (-delta.where(delta < 0, 0)).rolling(5).mean()
        rs = gain / np.maximum(loss, 1e-8)
        g["rsi_5"] = 100 - (100 / (1 + rs))

        # Bollinger position (10 periods)
        rolling_mean = g["close"].rolling(10).mean()
        rolling_std = g["close"].rolling(10).std()
        g["bb_pos"] = (g["close"] - rolling_mean) / np.maximum(2 * rolling_std, 1e-8)

        # Rolling volatility of returns
        g["vol_5"] = g["ret_1"].rolling(5).std()

        # TARGET: next candle direction (1=UP, 0=DOWN, drop ties)
        g["next_close"] = g["close"].shift(-1)
        g["target_return"] = g["next_close"] - g["close"]
        g["target"] = np.where(g["target_return"] > 0, 1,
                      np.where(g["target_return"] < 0, 0, np.nan))
        dfs.append(g)

    final_df = pd.concat(dfs, ignore_index=True)
    final_df.sort_values(by="time", inplace=True)
    # Drop rows without enough history or without target
    final_df.dropna(subset=["target", "bb_pos", "rsi_5", "ret_prev5"], inplace=True)
    final_df["target"] = final_df["target"].astype(int)
    return final_df

FEATURE_COLS = [
    "ret_1", "ret_prev1", "ret_prev2", "ret_prev3", "ret_prev5",
    "zero_ratio", "tick_delta_norm", "tick_density", "realized_vol_norm",
    "hl_range", "upper_wick", "lower_wick", "streak", "rsi_5", "bb_pos", "vol_5"
]

def evaluate_horizon(df, tf_name):
    # Chronological Walk-Forward split: 70% Train, 30% Test (Out-of-sample)
    n = len(df)
    if n < 100:
        print(f"Skipping {tf_name}: insufficient samples ({n})")
        return None

    split_idx = int(n * 0.70)
    train_df = df.iloc[:split_idx]
    test_df = df.iloc[split_idx:]

    X_train = train_df[FEATURE_COLS]
    y_train = train_df["target"]
    X_test = test_df[FEATURE_COLS]
    y_test = test_df["target"]

    clf = lgb.LGBMClassifier(
        n_estimators=100,
        max_depth=3,
        learning_rate=0.03,
        num_leaves=7,
        min_child_samples=15,
        subsample=0.8,
        colsample_bytree=0.8,
        random_state=42,
        verbose=-1
    )
    clf.fit(X_train, y_train)

    probs = clf.predict_proba(X_test)
    preds = clf.predict(X_test)

    # Base Out-of-sample accuracy
    base_acc = accuracy_score(y_test, preds)
    n_test = len(y_test)
    se = math.sqrt(base_acc * (1 - base_acc) / n_test) if n_test > 0 else 0
    ci_low = base_acc - 1.96 * se
    ci_high = base_acc + 1.96 * se

    # High-confidence trading threshold filter (e.g. predicted prob > 0.55 or < 0.45)
    high_conf_mask = (probs[:, 1] >= 0.55) | (probs[:, 1] <= 0.45)
    n_conf = np.sum(high_conf_mask)
    if n_conf > 10:
        conf_preds = np.where(probs[high_conf_mask, 1] >= 0.55, 1, 0)
        conf_acc = accuracy_score(y_test.iloc[high_conf_mask], conf_preds)
        se_conf = math.sqrt(conf_acc * (1 - conf_acc) / n_conf)
        ci_c_low = conf_acc - 1.96 * se_conf
        ci_c_high = conf_acc + 1.96 * se_conf
    else:
        conf_acc, ci_c_low, ci_c_high = None, None, None

    # Feature importances
    importances = dict(zip(FEATURE_COLS, clf.feature_importances_))
    top_features = sorted(importances.items(), key=lambda x: x[1], reverse=True)[:5]

    return {
        "tf": tf_name,
        "total_candles": n,
        "n_train": len(train_df),
        "n_test": n_test,
        "base_winrate": base_acc * 100,
        "ci_low": ci_low * 100,
        "ci_high": ci_high * 100,
        "n_conf": int(n_conf),
        "conf_winrate": conf_acc * 100 if conf_acc is not None else None,
        "conf_ci_low": ci_c_low * 100 if ci_c_low is not None else None,
        "conf_ci_high": ci_c_high * 100 if ci_c_high is not None else None,
        "top_features": top_features
    }

def main():
    print("=" * 70)
    print("POCKET OPTION OTC: MULTI-HORIZON MACHINE LEARNING FORENSICS")
    print("=" * 70)
    print("Loading ticks from DB...")
    ticks = load_ticks()
    print(f"Total ticks: {len(ticks):,} across {ticks['asset'].nunique()} pairs.")

    results = []
    timeframes = [
        ("s5", 5),
        ("s10", 10),
        ("s15", 15),
        ("s30", 30),
        ("s60 (1m)", 60)
    ]

    for tf_name, tf_sec in timeframes:
        print(f"\nProcessing horizon: {tf_name} ({tf_sec} seconds)...")
        c_df = build_candle_dataset(ticks, timeframe_sec=tf_sec)
        res = evaluate_horizon(c_df, tf_name)
        if res:
            results.append(res)
            print(f"  Total bars: {res['total_candles']:,} (Train: {res['n_train']:,}, Test: {res['n_test']:,})")
            print(f"  All-Signals Winrate: {res['base_winrate']:.2f}%  [95% CI: {res['ci_low']:.1f}% - {res['ci_high']:.1f}%]")
            if res['conf_winrate'] is not None:
                print(f"  High-Confidence Filter (prob >= 55%): {res['conf_winrate']:.2f}% on {res['n_conf']} trades [95% CI: {res['conf_ci_low']:.1f}% - {res['conf_ci_high']:.1f}%]")
            top_str = ", ".join(f"{k} ({v})" for k, v in res['top_features'])
            print(f"  Top Features: {top_str}")

if __name__ == "__main__":
    main()
