"""
export_dataset.py - High-Performance Dataset Exporter for Neural Brain (V3 Architecture)

Extracts historical trade sequences, macro context, and market outcomes from PostgreSQL,
normalizes them into stationary tensor format, and saves them into compressed .npz.

Output format:
  - X_candles: [N, 160, 5] (Relative returns & normalized volume per candle)
  - X_macro:   [N, D_macro] (Macro context, time anchors, SMC tags, cyclical time)
  - y:         [N] (3-class target: 0=HOLD/Chop, 1=BUY, 2=PUT)
  - y_pnl:     [N] (pnl_bps continuous return)
  - y_mfe:     [N] (max favorable excursion bps)
  - y_mae:     [N] (max adverse excursion bps)
  - metadata:  trade IDs, assets, timeframes, timestamps
"""

import os
import sys
import argparse
import json
import math
import numpy as np
import psycopg2
from datetime import datetime

DEFAULT_DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

ASSET_MAP = {
    "EURUSD": 0, "EUR/USD": 0, "EURUSD_OTC": 0,
    "GBPUSD": 1, "GBP/USD": 1, "GBPUSD_OTC": 1,
    "AUDUSD": 2, "AUD/USD": 2, "AUDUSD_OTC": 2,
    "USDJPY": 3, "USD/JPY": 3, "USDJPY_OTC": 3,
    "USDCAD": 4, "USD/CAD": 4, "USDCAD_OTC": 4,
    "USDCHF": 5, "USD/CHF": 5, "USDCHF_OTC": 5,
}

TF_SECONDS = {
    "s5": 5,
    "s10": 10,
    "s15": 15,
    "s30": 30,
    "m1": 60, "1m": 60,
    "m5": 300, "5m": 300,
}

MACRO_FEATURE_NAMES = [
    "day_range_pos",
    "dist_day_high_bps_norm",
    "dist_day_low_bps_norm",
    "dxy_mom_1m_bps_norm",
    "dxy_mom_5m_bps_norm",
    "basket_sync_score",
    "spread_bps_norm",
    "dist_asian_high_bps_norm",
    "dist_asian_low_bps_norm",
    "price_entropy",
    "adx_norm",
    "rsi_norm",
    "minutes_to_news_norm",
    "hour_utc_sin",
    "hour_utc_cos",
    "day_of_week_sin",
    "day_of_week_cos",
    "timeframe_sec_norm",
    "smc_has_ob",
    "smc_has_fvg",
    "smc_has_sweep",
    "smc_has_bos",
    "asset_id"
]


def normalize_candles(candles_list, seq_len=160):
    """
    Normalizes a sequence of OHLCV candles into a stationary [seq_len, 5] array.
    Features per candle:
      0: (Close - Open) / Open
      1: (High - Open) / Open
      2: (Low - Open) / Open
      3: (Close - PrevClose) / PrevClose (or 0 for first)
      4: log1p(Volume) / 10.0
    """
    if len(candles_list) < seq_len:
        # Pad with first candle
        padding = [candles_list[0]] * (seq_len - len(candles_list))
        candles_list = padding + candles_list
    elif len(candles_list) > seq_len:
        # Take the most recent seq_len candles
        candles_list = candles_list[-seq_len:]

    arr = np.zeros((seq_len, 5), dtype=np.float32)
    prev_close = None

    for i, c in enumerate(candles_list):
        op = float(c.get("Open", 1.0))
        hi = float(c.get("High", op))
        lo = float(c.get("Low", op))
        cl = float(c.get("Close", op))
        vol = float(c.get("Volume", 0.0))

        if op <= 0:
            op = 1e-5

        arr[i, 0] = (cl - op) / op
        arr[i, 1] = (hi - op) / op
        arr[i, 2] = (lo - op) / op

        if prev_close is not None and prev_close > 0:
            arr[i, 3] = (cl - prev_close) / prev_close
        else:
            arr[i, 3] = 0.0

        arr[i, 4] = math.log1p(max(0.0, vol)) / 10.0
        prev_close = cl

    return arr


def extract_macro_vector(row_dict, feat_obj):
    """
    Extracts a fixed-size 1D vector of macro and market context features.
    """
    macro = feat_obj.get("MacroContext") or {}
    smc = feat_obj.get("Smc") or {}
    
    # 1. Day Range Position (0..1)
    day_pos = float(macro.get("DayRangePositionPct", 0.5))
    
    # 2. Distance to Day High (bps / 100)
    dist_high = float(macro.get("DistToDayHighBps", 0.0)) / 100.0
    
    # 3. Distance to Day Low (bps / 100)
    dist_low = float(macro.get("DistToDayLowBps", 0.0)) / 100.0
    
    # 4. DXY Momentum 1m (bps / 10)
    dxy_1m = float(macro.get("DxyMomentum1mBps", 0.0)) / 10.0
    
    # 5. DXY Momentum 5m (bps / 10)
    dxy_5m = float(macro.get("DxyMomentum5mBps", 0.0)) / 10.0
    
    # 6. Basket Sync Score (-1..1)
    basket_sync = float(macro.get("BasketSyncScore", 0.0))
    
    # 7. Spread (bps / 10)
    spread = float(macro.get("SpreadBps", 1.0)) / 10.0
    
    # 8-9. Asian Session anchors
    asian_high = float(macro.get("DistToAsianHighBps", 0.0)) / 100.0
    asian_low = float(macro.get("DistToAsianLowBps", 0.0)) / 100.0
    
    # 10. Price Entropy (0..1)
    pe = float(row_dict.get("price_entropy") or 0.5)
    
    # 11. ADX (0..1)
    adx = float(row_dict.get("adx_at_signal") or 25.0) / 100.0
    
    # 12. RSI (-1..1 normalized around 50)
    rsi = (float(row_dict.get("rsi_at_signal") or 50.0) - 50.0) / 50.0
    
    # 13. News proximity (0..1, where 1 = news far away >= 120m, 0 = imminent news)
    news_m = float(row_dict.get("minutes_to_news") if row_dict.get("minutes_to_news") is not None else 120.0)
    news_norm = min(120.0, max(0.0, news_m)) / 120.0
    
    # 14-17. Cyclical time encoding
    hr = float(row_dict.get("hour_utc") or 12.0)
    dow = float(row_dict.get("day_of_week") or 2.0)
    hr_sin = math.sin(2 * math.pi * hr / 24.0)
    hr_cos = math.cos(2 * math.pi * hr / 24.0)
    dow_sin = math.sin(2 * math.pi * dow / 7.0)
    dow_cos = math.cos(2 * math.pi * dow / 7.0)
    
    # 18. Timeframe seconds normalized (s5 -> 5/60, m1 -> 1.0)
    tf_str = (row_dict.get("timeframe") or "s5").lower()
    tf_sec = float(TF_SECONDS.get(tf_str, 5)) / 60.0
    
    # 19-22. SMC Tags
    has_ob = 1.0 if (row_dict.get("smc_has_ob") or smc.get("HasOrderBlock")) else 0.0
    has_fvg = 1.0 if (row_dict.get("smc_has_fvg") or smc.get("HasFvg")) else 0.0
    has_swp = 1.0 if smc.get("HasLiquiditySweep") else 0.0
    has_bos = 1.0 if smc.get("HasBos") else 0.0
    
    # 23. Asset ID
    asset_str = (row_dict.get("asset") or "").upper().replace("/", "")
    asset_id = float(ASSET_MAP.get(asset_str, 6.0))
    
    vec = np.array([
        day_pos, dist_high, dist_low,
        dxy_1m, dxy_5m, basket_sync, spread,
        asian_high, asian_low,
        pe, adx, rsi, news_norm,
        hr_sin, hr_cos, dow_sin, dow_cos,
        tf_sec,
        has_ob, has_fvg, has_swp, has_bos,
        asset_id
    ], dtype=np.float32)
    
    return vec


def determine_label(direction, was_win, pnl_bps, min_pnl_threshold=0.5):
    """
    Determines 3-class target:
      0: HOLD (Flat market, chop, zero or negligible movement)
      1: BUY (Market went up strongly)
      2: PUT (Market went down strongly)
    """
    if direction in ("HOLD", "SHADOW_HOLD") or was_win is None:
        return 0
    
    if abs(pnl_bps) < min_pnl_threshold:
        return 0  # Market didn't produce decisive expansion
    
    d_upper = direction.upper()
    is_call = "BUY" in d_upper or "CALL" in d_upper
    is_put = "PUT" in d_upper
    
    if is_call:
        if was_win and pnl_bps >= min_pnl_threshold:
            return 1  # Correct BUY
        elif not was_win and pnl_bps <= -min_pnl_threshold:
            return 2  # Price dropped: PUT was the right move
        else:
            return 0
    elif is_put:
        if was_win and pnl_bps >= min_pnl_threshold:
            return 2  # Correct PUT
        elif not was_win and pnl_bps <= -min_pnl_threshold:
            return 1  # Price rose: BUY was the right move
        else:
            return 0
    
    return 0


import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

def export_dataset(db_url, output_path, limit=None, seq_len=160):
    print("=" * 60)
    print("[*] Neural Brain Dataset Exporter (V3 Architecture)")
    print(f"Connecting to database...")
    print("=" * 60)
    
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()
    
    query = """
        SELECT 
            id, asset, timeframe, direction, 
            entry_price, exit_price, pnl_bps, was_win,
            created_at, features_json,
            price_entropy, adx_at_signal, atr_at_signal, rsi_at_signal,
            minutes_to_news, hour_utc, day_of_week,
            smc_has_ob, smc_has_fvg,
            max_favorable_bps, max_adverse_bps
        FROM trade_outcomes
        WHERE features_json IS NOT NULL
        ORDER BY created_at ASC
    """
    if limit:
        query += f" LIMIT {limit}"
        
    print(f"Executing query...")
    cur.execute(query)
    
    cols = [desc[0] for desc in cur.description]
    rows = cur.fetchall()
    total_found = len(rows)
    print(f"Retrieved {total_found} rows from database.")
    
    x_candles_list = []
    x_macro_list = []
    y_list = []
    y_pnl_list = []
    y_mfe_list = []
    y_mae_list = []
    
    meta_ids = []
    meta_assets = []
    meta_timeframes = []
    meta_timestamps = []
    
    valid_count = 0
    skipped_count = 0
    
    for r in rows:
        row_dict = dict(zip(cols, r))
        feat_str = row_dict["features_json"]
        if not feat_str:
            skipped_count += 1
            continue
            
        try:
            feat_obj = json.loads(feat_str)
        except Exception:
            skipped_count += 1
            continue
            
        candles = feat_obj.get("Candles") or feat_obj.get("candles") or []
        if len(candles) < 10:
            skipped_count += 1
            continue
            
        # 1. Normalize Candles Sequence
        candle_tensor = normalize_candles(candles, seq_len=seq_len)
        if np.isnan(candle_tensor).any() or np.isinf(candle_tensor).any():
            skipped_count += 1
            continue
            
        # 2. Extract Macro Vector
        macro_vec = extract_macro_vector(row_dict, feat_obj)
        if np.isnan(macro_vec).any() or np.isinf(macro_vec).any():
            skipped_count += 1
            continue
            
        # 3. Target Label
        direction = str(row_dict.get("direction") or "")
        was_win = row_dict.get("was_win")
        pnl = float(row_dict.get("pnl_bps") or 0.0)
        label = determine_label(direction, was_win, pnl)
        
        mfe = float(row_dict.get("max_favorable_bps") or 0.0)
        mae = float(row_dict.get("max_adverse_bps") or 0.0)
        
        x_candles_list.append(candle_tensor)
        x_macro_list.append(macro_vec)
        y_list.append(label)
        y_pnl_list.append(pnl)
        y_mfe_list.append(mfe)
        y_mae_list.append(mae)
        
        meta_ids.append(str(row_dict.get("id")))
        meta_assets.append(str(row_dict.get("asset")))
        meta_timeframes.append(str(row_dict.get("timeframe")))
        meta_timestamps.append(str(row_dict.get("created_at")))
        
        valid_count += 1
        if valid_count % 2500 == 0:
            print(f"Processed {valid_count}/{total_found} samples...")
            
    conn.close()
    
    print("-" * 60)
    print(f"Valid samples parsed: {valid_count} (Skipped: {skipped_count})")
    
    X_candles = np.array(x_candles_list, dtype=np.float32)
    X_macro = np.array(x_macro_list, dtype=np.float32)
    y = np.array(y_list, dtype=np.int64)
    y_pnl = np.array(y_pnl_list, dtype=np.float32)
    y_mfe = np.array(y_mfe_list, dtype=np.float32)
    y_mae = np.array(y_mae_list, dtype=np.float32)
    
    print(f"X_candles shape: {X_candles.shape} (N, SeqLen, Feats)")
    print(f"X_macro shape:   {X_macro.shape} (N, Feats)")
    print(f"y shape:         {y.shape}")
    
    # Class breakdown
    c0 = np.sum(y == 0)
    c1 = np.sum(y == 1)
    c2 = np.sum(y == 2)
    tot = len(y) if len(y) > 0 else 1
    print("\nTarget Class Distribution:")
    print(f"  Class 0 (HOLD / Chop): {c0:6d} ({c0/tot*100:5.2f}%)")
    print(f"  Class 1 (BUY):         {c1:6d} ({c1/tot*100:5.2f}%)")
    print(f"  Class 2 (PUT):         {c2:6d} ({c2/tot*100:5.2f}%)")
    
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    
    print(f"\nSaving dataset to: {output_path} ...")
    np.savez_compressed(
        output_path,
        X_candles=X_candles,
        X_macro=X_macro,
        y=y,
        y_pnl=y_pnl,
        y_mfe=y_mfe,
        y_mae=y_mae,
        meta_ids=np.array(meta_ids),
        meta_assets=np.array(meta_assets),
        meta_timeframes=np.array(meta_timeframes),
        meta_timestamps=np.array(meta_timestamps),
        macro_feature_names=np.array(MACRO_FEATURE_NAMES)
    )
    
    file_size_mb = os.path.getsize(output_path) / (1024 * 1024)
    print(f"[+] Saved successfully! File size: {file_size_mb:.2f} MB")
    print("=" * 60)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Export V3 dataset from PostgreSQL")
    parser.add_argument("--db-url", default=os.getenv("DATABASE_URL", DEFAULT_DB_URL))
    parser.add_argument("--out", default="ml_service/data/dataset_v3.npz")
    parser.add_argument("--limit", type=int, default=None)
    parser.add_argument("--seq-len", type=int, default=160)
    args = parser.parse_args()
    
    export_dataset(args.db_url, args.out, limit=args.limit, seq_len=args.seq_len)
