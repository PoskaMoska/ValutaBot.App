"""
export_objective_dataset.py - Fast Vectorized Objective Ground-Truth Dataset Exporter
Builds training samples directly from closed candles in subminute_candles (PostgreSQL).

Label is derived from objective forward price change over H s5-bars (default H=1 -> 5s expiry,
matching the live TradeTimeoutEngine which always expires after exactly 1 candle of the signal timeframe):
  delta_bps = (Close(T + H) - Close(T)) / Close(T) * 10,000
  delta_bps > 0 -> Label 1 (BUY wins), delta_bps <= 0 -> Label 0 (PUT wins; an exact tie is a loss for both).
  |delta_bps| < noise_bps (default 0.6) -> flagged trainable=False (excluded from fitting, but KEPT in the
  file so evaluation can be done on the FULL live-like stream where such trades are still placed).

Inputs:
  - macro_x: (N, 30, 5) - 30 closed M1 bars prior to T
  - micro_x: (N, 30, 5) - 30 closed s5 bars up to T
  - bridge_x: (N, 4) - [dist_to_m1_ema, atr_ratio, micro_zscore, wick_balance]
  - labels: (N,) - 1 for BUY, 0 for PUT
  - epochs: (N,) - entry epoch seconds (globally sorted ascending)
  - delta_bps: (N,) - signed forward move in bps (0.0 == exact tie)
  - trainable: (N,) - bool, False for |delta_bps| < noise_bps (noise zone)
  - asset_ids: (N,) - index into ASSETS (for per-pair evaluation)
  - meta: JSON string with horizon / step / noise threshold used
"""

import argparse
import contextlib
import json
import os
import sys
import numpy as np
import pandas as pd
import psycopg2

DB_URL = os.environ.get("DATABASE_URL") or os.environ.get("DB_URL", "postgresql://valutabot:password@localhost:5432/valutabot")

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
        SELECT open_time, open_price, high_price, low_price, close_price, volume,
               COALESCE(crowd_sentiment, 50.0) AS crowd_sentiment
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
    for col in ("open_price", "high_price", "low_price", "close_price", "volume", "crowd_sentiment"):
        df[col] = df[col].astype(float)
    df['open_time'] = pd.to_datetime(df['open_time'], utc=True)
    df = df.sort_values('open_time').reset_index(drop=True)
    df['epoch_sec'] = df['open_time'].astype('datetime64[s, UTC]').astype('int64').values
    return df

def normalize_candle_window(df_window: pd.DataFrame) -> np.ndarray:
    """Normalizes OHLCV into stationary invariant features matching C# BuildOnnxTensors."""
    close = df_window['close_price'].values.astype(np.float64)
    high = df_window['high_price'].values.astype(np.float64)
    low = df_window['low_price'].values.astype(np.float64)
    open_ = df_window['open_price'].values.astype(np.float64)
    vol = df_window['volume'].values.astype(np.float64)

    ref_price = close[-1] if close[-1] > 0 else 1.0

    # 1. Normalized Close relative to last close (per-mille / 10 bps units) [-50.0, 50.0]
    norm_close = np.clip((close - ref_price) / ref_price * 1000.0, -50.0, 50.0)
    # 2. Spread / High-Low range [0.0, 50.0]
    norm_spread = np.clip((high - low) / ref_price * 1000.0, 0.0, 50.0)
    # 3. Upper wick [0.0, 50.0]
    upper_wick = np.clip((high - np.maximum(open_, close)) / ref_price * 1000.0, 0.0, 50.0)
    # 4. Lower wick [0.0, 50.0]
    lower_wick = np.clip((np.minimum(open_, close) - low) / ref_price * 1000.0, 0.0, 50.0)
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

    # 3. Micro Z-Score with Huber MAD standard deviation matching C# BuildOnnxTensors
    s5_closes = s5_win['close_price'].values.astype(np.float64)
    micro_mean = np.mean(s5_closes)
    abs_dev = np.abs(s5_closes - micro_mean)
    max_dev = max(4.0 * np.mean(abs_dev), 1e-5)
    clipped_dev = np.clip(s5_closes - micro_mean, -max_dev, max_dev)
    s5_std = np.sqrt(np.mean(clipped_dev ** 2)) + 1e-7
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

    # 5. Pocket Option Crowd Sentiment (chafor % BUY, normalized from [0..100] to [-1..1])
    raw_crowd = float(s5_win['crowd_sentiment'].iloc[-1]) if 'crowd_sentiment' in s5_win.columns else 50.0
    if not np.isfinite(raw_crowd):
        raw_crowd = 50.0
    norm_crowd = float(np.clip((raw_crowd - 50.0) / 50.0, -1.0, 1.0))

    return np.array([dist_m1_ema, atr_ratio, z_score, wick_bal, norm_crowd], dtype=np.float32)

def main():
    ap = argparse.ArgumentParser(description="Export objective dual-stream dataset")
    ap.add_argument("--horizon", type=int, default=1,
                    help="Label horizon in s5 bars (1 = 5s expiry, the live expiry for s5). Default: 1")
    ap.add_argument("--step", type=int, default=6, help="Stride between samples in s5 bars (default 6 = 30s)")
    ap.add_argument("--noise-bps", type=float, default=0.6, help="|delta| below this is flagged non-trainable")
    args = ap.parse_args()
    if args.horizon < 1 or args.step < 1:
        raise SystemExit("--horizon and --step must be >= 1")

    if not DB_URL or "@localhost" in DB_URL and not (os.environ.get("DATABASE_URL") or os.environ.get("DB_URL")):
        raise SystemExit("DATABASE_URL (or DB_URL) is not set. Export it before running the exporter.")
    host_info = DB_URL.split('@')[1] if '@' in DB_URL else "configured DB"
    print(f"Connecting to DB: {host_info}...")
    print(f"Horizon: {args.horizon} x s5 = {args.horizon * 5}s | step: {args.step} | noise threshold: {args.noise_bps} bps")

    all_macro = []
    all_micro = []
    all_bridge = []
    all_labels = []
    all_epochs = []
    all_delta = []
    all_trainable = []
    all_asset_ids = []

    noise_filtered_count = 0
    total_checked = 0

    h = args.horizon  # label horizon in s5 bars (5s per bar)

    for asset_idx, asset in enumerate(ASSETS):
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

        step = args.step
        min_idx = 30
        max_idx = len(s5_df) - h - 1

        asset_samples = 0
        for i in range(min_idx, max_idx, step):
            total_checked += 1
            entry_epoch = int(s5_epochs[i])
            entry_close = s5_closes[i]
            if entry_close <= 0:
                continue

            # Strict temporal continuity: reject samples crossing data gaps / broker disconnections
            if int(s5_epochs[i + h]) - entry_epoch != h * 5:
                continue
            if entry_epoch - int(s5_epochs[i - 29]) != 29 * 5:
                continue

            future_close = s5_closes[i + h]
            delta_bps = (future_close - entry_close) / entry_close * 10000.0

            # Noise zone is flagged, not dropped: live trading still takes these trades (ties are losses),
            # so honest evaluation needs them. Training/validation fit uses trainable == True only.
            is_trainable = abs(delta_bps) >= args.noise_bps
            if not is_trainable:
                noise_filtered_count += 1

            label = 1 if delta_bps > 0 else 0

            # Точный Point-in-Time расчет закрытия свечи s5:
            # Свеча s5 с open_time = entry_epoch закрывается ровно в entry_epoch + 5 сек.
            # M1 свеча с open_time = T_m1 закрывается в T_m1 + 60 сек.
            # Она закрыта к моменту s5_close_epoch тогда и только тогда, когда T_m1 <= s5_close_epoch - 60 (entry_epoch - 55).
            s5_close_epoch = entry_epoch + 5
            m1_avail_count = np.searchsorted(m1_epochs, s5_close_epoch - 60, side='right')
            if m1_avail_count < 30:
                continue

            # Macro window continuity: 30 closed M1 bars must not cross gaps (> 31 minutes)
            if int(m1_epochs[m1_avail_count - 1]) - int(m1_epochs[m1_avail_count - 30]) > 29 * 60 + 120:
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
            all_delta.append(delta_bps)
            all_trainable.append(is_trainable)
            all_asset_ids.append(asset_idx)
            asset_samples += 1

        print(f"  Extracted {asset_samples} samples for {asset}")

    total_valid = len(all_labels)
    print(f"\n==========================================")
    print(f"Extraction Summary:")
    print(f"  Total points evaluated: {total_checked}")
    print(f"  Noise-zone samples (flagged non-trainable): {noise_filtered_count} ({noise_filtered_count / (max(total_valid, 1)) * 100:.1f}% of kept)")
    print(f"  Total samples saved: {total_valid}")

    if total_valid < 100:
        print("ERROR: Not enough samples extracted.")
        return

    macro_arr = np.array(all_macro, dtype=np.float32)
    micro_arr = np.array(all_micro, dtype=np.float32)
    bridge_arr = np.array(all_bridge, dtype=np.float32)
    labels_arr = np.array(all_labels, dtype=np.int64)
    epochs_arr = np.array(all_epochs, dtype=np.int64)
    delta_arr = np.array(all_delta, dtype=np.float32)
    trainable_arr = np.array(all_trainable, dtype=bool)
    asset_arr = np.array(all_asset_ids, dtype=np.int16)

    # Sort globally by timestamp so chronological train/val/test split never leaks or biases by asset order
    order = np.argsort(epochs_arr, kind="stable")
    macro_arr = macro_arr[order]
    micro_arr = micro_arr[order]
    bridge_arr = bridge_arr[order]
    labels_arr = labels_arr[order]
    epochs_arr = epochs_arr[order]
    delta_arr = delta_arr[order]
    trainable_arr = trainable_arr[order]
    asset_arr = asset_arr[order]

    buy_ratio = np.mean(labels_arr[trainable_arr]) if trainable_arr.any() else float("nan")
    print(f"  Class balance (trainable): BUY={buy_ratio * 100:.2f}%, PUT={(1 - buy_ratio) * 100:.2f}%")
    print(f"  Exact ties (delta == 0): {int(np.sum(delta_arr == 0.0))} ({np.mean(delta_arr == 0.0) * 100:.1f}%)")
    print(f"  Span: {pd.to_datetime(int(epochs_arr[0]), unit='s', utc=True)} -> {pd.to_datetime(int(epochs_arr[-1]), unit='s', utc=True)}")

    out_dir = os.path.join(os.path.dirname(__file__), "data")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "dataset_objective_dual_stream.npz")

    np.savez_compressed(
        out_path,
        macro_x=macro_arr,
        micro_x=micro_arr,
        bridge_x=bridge_arr,
        labels=labels_arr,
        epochs=epochs_arr,
        delta_bps=delta_arr,
        trainable=trainable_arr,
        asset_ids=asset_arr,
        meta=np.array(json.dumps({
            "horizon_bars": args.horizon, "horizon_sec": args.horizon * 5, "step": args.step,
            "noise_bps": args.noise_bps, "assets": ASSETS, "n": int(total_valid),
        }))
    )
    print(f"Saved dataset to {out_path} ({os.path.getsize(out_path) / 1024 / 1024:.2f} MB)")

if __name__ == "__main__":
    main()
