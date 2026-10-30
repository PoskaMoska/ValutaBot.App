"""
export_objective_dataset.py - Fast Vectorized Objective Ground-Truth Dataset Exporter
Builds training samples directly from closed candles in subminute_candles (PostgreSQL).

Label is derived from objective forward price change:
  delta_bps = (Close(T + H) - Close(T)) / Close(T) * 10,000
  If delta_bps >= +0.6 bps -> Label 1 (BUY)
  If delta_bps <= -0.6 bps -> Label 0 (PUT)
  If |delta_bps| < 0.6 bps -> Discarded as untradeable noise (Two-Stage Hurdle)

Inputs:
  - macro_x: (N, 30, 5) - 30 closed M1 bars prior to T
  - micro_x: (N, 30, 5) - 30 closed s5 bars up to T
  - bridge_x: (N, 4) - [dist_to_m1_ema, atr_ratio, micro_zscore, wick_balance]
  - labels: (N,) - 1 for BUY, 0 for PUT
  - epochs: (N,) - entry epoch seconds (globally sorted ascending)
"""

import contextlib
import os
import sys
import numpy as np
import pandas as pd
import psycopg2

DB_URL = os.environ["DATABASE_URL"]

def get_connection():
    conn = psycopg2.connect(
        DB_URL,
        keepalives=1,
        keepalives_idle=10,
        keepalives_interval=5,
        keepalives_count=3,
        options="-c statement_timeout=300000 -c lock_timeout=5000 "
                "-c idle_in_transaction_session_timeout=60000 "
                "-c default_transaction_read_only=on"
    )
    conn.set_session(readonly=True, autocommit=True)
    return conn

ASSETS = [
    "EURUSD_PO",
    "GBPUSD_PO",
    "USDJPY_PO",
    "AUDUSD_PO",
    "USDCAD_PO",
    "USDCHF_PO"
]

def load_candles_for_asset(asset: str, interval: str) -> pd.DataFrame:
    query = """
        SELECT open_time, open_price, high_price, low_price, close_price, volume
        FROM subminute_candles
        WHERE asset = %s AND interval = %s AND open_time > %s
        ORDER BY open_time ASC
        LIMIT 100000;
    """
    frames = []
    last_open_time = ""
    with contextlib.closing(get_connection()) as conn, conn.cursor() as cur:
        while True:
            cur.execute(query, (asset, interval, last_open_time))
            rows = cur.fetchall()
            if not rows:
                break
            cols = [desc[0] for desc in cur.description] if cur.description else []
            frames.append(pd.DataFrame(rows, columns=cols))
            last_open_time = rows[-1][0]

    if not frames:
        return pd.DataFrame()

    df = pd.concat(frames, ignore_index=True)
    for col in ("open_price", "high_price", "low_price", "close_price", "volume"):
        df[col] = df[col].astype(float)
    df['open_time'] = pd.to_datetime(df['open_time'], utc=True)
    df = df.sort_values('open_time').reset_index(drop=True)
    df['epoch_sec'] = (df['open_time'].astype('int64') // 10**9).values
    return df

def normalize_candle_window(df_window: pd.DataFrame) -> np.ndarray:
    """Normalizes OHLCV into stationary invariant features matching C# BuildOnnxTensors."""
    close = df_window['close_price'].values.astype(np.float64)
    high = df_window['high_price'].values.astype(np.float64)
    low = df_window['low_price'].values.astype(np.float64)
    open_ = df_window['open_price'].values.astype(np.float64)
    vol = df_window['volume'].values.astype(np.float64)

    ref_price = close[-1] if close[-1] > 0 else 1.0

    # 1. Normalized Close relative to last close (per-mille / 10 bps units)
    norm_close = (close - ref_price) / ref_price * 1000.0
    # 2. Spread / High-Low range
    norm_spread = (high - low) / ref_price * 1000.0
    # 3. Upper wick
    upper_wick = (high - np.maximum(open_, close)) / ref_price * 1000.0
    # 4. Lower wick
    lower_wick = (np.minimum(open_, close) - low) / ref_price * 1000.0
    # 5. Normalized Volume clamped to [0, 10] exactly as in C# BuildOnnxTensors
    mean_vol = np.mean(vol) + 1e-4
    norm_vol = np.clip(vol / mean_vol, 0.0, 10.0)

    feats = np.column_stack([norm_close, norm_spread, upper_wick, lower_wick, norm_vol])
    return feats.astype(np.float32)

def compute_bridge_vector(m1_win: pd.DataFrame, s5_win: pd.DataFrame) -> np.ndarray:
    """Computes cross-stream alignment features with clips matching C# BuildOnnxTensors."""
    cur_p = float(s5_win['close_price'].iloc[-1])
    if cur_p <= 0:
        cur_p = 1.0

    # 1. Distance to M1 EMA(9)
    m1_closes = m1_win['close_price'].values.astype(np.float64)
    m1_ema = pd.Series(m1_closes).ewm(span=9).mean().iloc[-1]
    dist_m1_ema = float(np.clip((cur_p - m1_ema) / cur_p * 1000.0, -50.0, 50.0))

    # 2. Volatility Ratio (s5 ATR vs M1 ATR)
    s5_tr = np.maximum(s5_win['high_price'].values - s5_win['low_price'].values, 1e-7).mean()
    m1_tr = np.maximum(m1_win['high_price'].values - m1_win['low_price'].values, 1e-7).mean()
    atr_ratio = float(np.clip(s5_tr / (m1_tr + 1e-7), 0.01, 20.0))

    # 3. Micro Z-Score
    s5_closes = s5_win['close_price'].values.astype(np.float64)
    s5_std = np.std(s5_closes) + 1e-7
    s5_ema = pd.Series(s5_closes).ewm(span=7).mean().iloc[-1]
    z_score = float(np.clip((cur_p - s5_ema) / s5_std, -5.0, 5.0))

    # 4. Last bar wick balance
    last_h = float(s5_win['high_price'].iloc[-1])
    last_l = float(s5_win['low_price'].iloc[-1])
    last_o = float(s5_win['open_price'].iloc[-1])
    last_c = float(s5_win['close_price'].iloc[-1])
    rng = max(last_h - last_l, 1e-7)
    l_wick = min(last_o, last_c) - last_l
    u_wick = last_h - max(last_o, last_c)
    wick_bal = float(np.clip((l_wick - u_wick) / rng, -1.0, 1.0))

    return np.array([dist_m1_ema, atr_ratio, z_score, wick_bal], dtype=np.float32)

def main():
    host_info = DB_URL.split('@')[1] if '@' in DB_URL else "configured DB"
    print(f"Connecting to DB: {host_info}...")

    all_macro = []
    all_micro = []
    all_bridge = []
    all_labels = []
    all_epochs = []

    noise_filtered_count = 0
    total_checked = 0

    h = 6  # 30-second primary expiry (6 bars of 5s)

    for asset in ASSETS:
        print(f"\n--- Loading candles for {asset} ---")
        m1_df = load_candles_for_asset(asset, "1min")
        s5_df = load_candles_for_asset(asset, "s5")

        print(f"  Loaded: {len(m1_df)} M1 bars, {len(s5_df)} s5 bars")
        if len(m1_df) < 35 or len(s5_df) < 100:
            print("  Not enough candles, skipping.")
            continue

        m1_epochs = m1_df['epoch_sec'].values
        s5_epochs = s5_df['epoch_sec'].values
        s5_closes = s5_df['close_price'].values

        step = 6
        min_idx = 30
        max_idx = len(s5_df) - h - 1

        asset_samples = 0
        for i in range(min_idx, max_idx, step):
            total_checked += 1
            entry_epoch = int(s5_epochs[i])
            entry_close = s5_closes[i]
            if entry_close <= 0:
                continue

            future_close = s5_closes[i + h]
            delta_bps = (future_close - entry_close) / entry_close * 10000.0

            # Hurdle Gatekeeper: Filter dead flat noise (|delta| < 0.6 bps)
            if abs(delta_bps) < 0.6:
                noise_filtered_count += 1
                continue

            label = 1 if delta_bps >= 0.6 else 0

            # Macro window: 30 closed M1 bars strictly prior to entry_epoch (>= 60s ago)
            m1_avail_count = np.searchsorted(m1_epochs, entry_epoch - 60, side='right')
            if m1_avail_count < 30:
                continue

            micro_win = s5_df.iloc[i - 29 : i + 1]
            macro_win = m1_df.iloc[m1_avail_count - 30 : m1_avail_count]

            macro_feats = normalize_candle_window(macro_win)
            micro_feats = normalize_candle_window(micro_win)
            bridge_feats = compute_bridge_vector(macro_win, micro_win)

            if not (np.all(np.isfinite(macro_feats)) and np.all(np.isfinite(micro_feats)) and np.all(np.isfinite(bridge_feats))):
                continue

            all_macro.append(macro_feats)
            all_micro.append(micro_feats)
            all_bridge.append(bridge_feats)
            all_labels.append(label)
            all_epochs.append(entry_epoch)
            asset_samples += 1

        print(f"  Extracted {asset_samples} high-conviction samples for {asset}")

    total_valid = len(all_labels)
    print(f"\n==========================================")
    print(f"Extraction Summary:")
    print(f"  Total points evaluated: {total_checked}")
    print(f"  Noise / Dead flat filtered: {noise_filtered_count} ({noise_filtered_count / (total_checked + 1e-7) * 100:.1f}%)")
    print(f"  High-conviction samples kept: {total_valid}")

    if total_valid < 100:
        print("ERROR: Not enough samples extracted.")
        return

    macro_arr = np.array(all_macro, dtype=np.float32)
    micro_arr = np.array(all_micro, dtype=np.float32)
    bridge_arr = np.array(all_bridge, dtype=np.float32)
    labels_arr = np.array(all_labels, dtype=np.int64)
    epochs_arr = np.array(all_epochs, dtype=np.int64)

    # Sort globally by timestamp so chronological train/val/test split never leaks or biases by asset order
    order = np.argsort(epochs_arr, kind="stable")
    macro_arr = macro_arr[order]
    micro_arr = micro_arr[order]
    bridge_arr = bridge_arr[order]
    labels_arr = labels_arr[order]
    epochs_arr = epochs_arr[order]

    buy_ratio = np.mean(labels_arr)
    print(f"  Class balance: BUY={buy_ratio * 100:.2f}%, PUT={(1 - buy_ratio) * 100:.2f}%")

    out_dir = os.path.join(os.path.dirname(__file__), "data")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "dataset_objective_dual_stream.npz")

    np.savez_compressed(
        out_path,
        macro_x=macro_arr,
        micro_x=micro_arr,
        bridge_x=bridge_arr,
        labels=labels_arr,
        epochs=epochs_arr
    )
    print(f"Saved dataset to {out_path} ({os.path.getsize(out_path) / 1024 / 1024:.2f} MB)")

if __name__ == "__main__":
    main()
