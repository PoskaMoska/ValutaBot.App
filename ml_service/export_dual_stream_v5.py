"""
export_dual_stream_v5.py - Pure Dual-Stream MTF Dataset Exporter
Strict Point-in-Time Alignment without Data Leakage.

Extracts:
- Macro Stream: 30 M1 candles (strictly closed >= 60s before entry)
- Micro Stream: 30 s5 candles (strictly closed >= 5s before entry)
- Bridge Vector: 4 physical features (displacement bps, second/60, tick velocity, micro volatility)
- Binary Target: 1 (Win) vs 0 (Loss)
"""

import os
import sys
import math
import bisect
import numpy as np
import psycopg2
from datetime import datetime, timezone, timedelta

DEFAULT_DSN = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def _safe_float(val, default=0.0):
    if val is None:
        return default
    try:
        f = float(val)
        return default if math.isnan(f) or math.isinf(f) else f
    except (ValueError, TypeError):
        return default

def clean_asset_name(raw: str) -> str:
    s = (raw or "").upper()
    for drop in ["_PO", "_OTC", " OTC", "/", "-", "_", " "]:
        s = s.replace(drop, "")
    return s.strip()

class FastCandleIndex:
    """Preloads subminute_candles into RAM for sub-millisecond bisect lookups."""
    def __init__(self, conn):
        self.series = {}  # (asset_clean, interval) -> (list_of_iso_strings, list_of_ohlcv)
        print("[*] Pre-loading candles into high-speed in-memory index...")
        cur = conn.cursor()
        cur.execute("""
            SELECT asset, interval, open_time, open_price, high_price, low_price, close_price, volume
            FROM subminute_candles
            WHERE interval IN ('s5', '1min')
            ORDER BY open_time ASC;
        """)
        
        count = 0
        while True:
            batch = cur.fetchmany(25000)
            if not batch:
                break
            for asset, interval, ot, op, hi, lo, cl, vol in batch:
                clean_a = clean_asset_name(asset)
                iv = "m1" if interval in ("1min", "1m", "m1") else "s5"
                key = (clean_a, iv)
                if key not in self.series:
                    self.series[key] = ([], [])
                
                # Normalize timestamp representation
                ts_str = str(ot)[:19].replace("T", " ")
                c_dict = {
                    "o": _safe_float(op, 1.0),
                    "h": _safe_float(hi, 1.0),
                    "l": _safe_float(lo, 1.0),
                    "c": _safe_float(cl, 1.0),
                    "v": _safe_float(vol, 0.0)
                }
                
                ts_list, c_list = self.series[key]
                if not ts_list or ts_list[-1] != ts_str:
                    ts_list.append(ts_str)
                    c_list.append(c_dict)
                    count += 1
                    
        cur.close()
        print(f"[+] Loaded {count:,} candles across {len(self.series)} series into RAM index.")

    def get_closed_bars(self, asset: str, interval: str, cutoff_dt: datetime, count: int = 30):
        key = (clean_asset_name(asset), interval)
        bucket = self.series.get(key)
        if not bucket or not bucket[0]:
            return []
            
        iso_cut = cutoff_dt.strftime("%Y-%m-%d %H:%M:%S")
        idx = bisect.bisect_right(bucket[0], iso_cut)
        start = max(0, idx - count)
        candles = bucket[1][start:idx]
        return candles if len(candles) == count else []


def export_dual_stream_dataset(db_url: str = DEFAULT_DSN, out_path: str = "ml_service/data/dataset_v5_dual_stream.npz"):
    print("=" * 70)
    print("[*] Pocket Option Dual-Stream Dataset Exporter (V5 Architecture)")
    print(f"Connecting to database...")
    print("=" * 70)

    conn = psycopg2.connect(db_url)
    conn.autocommit = True
    
    # 1. Preload candles into RAM
    candle_index = FastCandleIndex(conn)
    
    # 2. Fetch trade outcomes
    cur = conn.cursor()
    print("[*] Fetching verified trade outcomes...")
    cur.execute("""
        SELECT id, asset, timeframe, direction, created_at, entry_price, exit_price, was_win, pnl_bps
        FROM trade_outcomes
        WHERE was_win IS NOT NULL
        ORDER BY created_at ASC;
    """)
    trades = cur.fetchall()
    cur.close()
    conn.close()
    
    total_trades = len(trades)
    print(f"[+] Fetched {total_trades:,} trades with confirmed outcomes.")
    
    macro_list = []
    micro_list = []
    bridge_list = []
    labels_list = []
    dirs_list = []
    pnls_list = []
    assets_list = []
    tfs_list = []
    times_list = []
    
    skipped_incomplete = 0
    skipped_math = 0
    
    for row in trades:
        t_id, asset, tf, direction, created_at, entry_p, exit_p, was_win, pnl_bps = row
        en_price = _safe_float(entry_p, 0.0)
        if en_price <= 0:
            skipped_math += 1
            continue
            
        # Parse timestamp safely
        if hasattr(created_at, "astimezone"):
            dt_utc = created_at.astimezone(timezone.utc).replace(tzinfo=None)
        elif isinstance(created_at, str):
            try:
                dt_utc = datetime.fromisoformat(created_at.replace("Z", "+00:00")).astimezone(timezone.utc).replace(tzinfo=None)
            except Exception:
                dt_utc = datetime.strptime(created_at[:19].replace("T", " "), "%Y-%m-%d %H:%M:%S")
        else:
            skipped_math += 1
            continue
            
        # Point-in-Time Cutoff:
        # Macro M1: must be strictly closed before trade -> open_time <= dt_utc - 60s
        m1_cutoff = dt_utc - timedelta(seconds=60)
        # Micro s5: must be strictly closed before trade -> open_time <= dt_utc - 5s
        s5_cutoff = dt_utc - timedelta(seconds=5)
        
        m1_bars = candle_index.get_closed_bars(asset, "m1", m1_cutoff, count=30)
        s5_bars = candle_index.get_closed_bars(asset, "s5", s5_cutoff, count=30)
        
        if len(m1_bars) < 30 or len(s5_bars) < 30:
            skipped_incomplete += 1
            continue
            
        # --- 1. Macro Features [30, 5] (Relative returns in basis points & log-volume) ---
        m1_features = np.zeros((30, 5), dtype=np.float32)
        for i, c in enumerate(m1_bars):
            m1_features[i, 0] = (c["o"] / en_price - 1.0) * 10000.0
            m1_features[i, 1] = (c["h"] / en_price - 1.0) * 10000.0
            m1_features[i, 2] = (c["l"] / en_price - 1.0) * 10000.0
            m1_features[i, 3] = (c["c"] / en_price - 1.0) * 10000.0
            m1_features[i, 4] = np.log1p(max(0.0, c["v"]))

        # --- 2. Micro Features [30, 5] (Relative returns in basis points & log-volume) ---
        s5_features = np.zeros((30, 5), dtype=np.float32)
        for i, c in enumerate(s5_bars):
            s5_features[i, 0] = (c["o"] / en_price - 1.0) * 10000.0
            s5_features[i, 1] = (c["h"] / en_price - 1.0) * 10000.0
            s5_features[i, 2] = (c["l"] / en_price - 1.0) * 10000.0
            s5_features[i, 3] = (c["c"] / en_price - 1.0) * 10000.0
            s5_features[i, 4] = np.log1p(max(0.0, c["v"]))

        # --- 3. Bridge Vector [4] ---
        # Feature A: Price displacement from 30-bar micro-SMA (in bps)
        s5_closes = np.array([c["c"] for c in s5_bars], dtype=np.float64)
        last_close = s5_closes[-1]
        sma_30 = np.mean(s5_closes)
        displacement_bps = float(((last_close - sma_30) / sma_30) * 10000.0) if sma_30 > 0 else 0.0
        
        # Feature B: Normalized seconds from minute start [0.0 .. 1.0]
        sec_frac = float((dt_utc.second + dt_utc.microsecond / 1e6) / 60.0)
        
        # Feature C: Tick velocity (mean volume of last 5 bars)
        recent_vols = [c["v"] for c in s5_bars[-5:]]
        tick_vel = float(np.mean(recent_vols)) if recent_vols else 0.0
        
        # Feature D: Micro-volatility (std of last 30 bars in bps)
        micro_vol_bps = float((np.std(s5_closes) / en_price) * 10000.0)
        
        bridge_vec = np.array([displacement_bps, sec_frac, tick_vel, micro_vol_bps], dtype=np.float32)
        
        # Check for NaN / Inf
        if np.isnan(m1_features).any() or np.isnan(s5_features).any() or np.isnan(bridge_vec).any():
            skipped_math += 1
            continue
            
        # Binary target: 1 = Win, 0 = Loss
        lbl = 1 if was_win is True else 0
        dir_val = 1 if str(direction or "").upper() == "BUY" else 2
        
        macro_list.append(m1_features)
        micro_list.append(s5_features)
        bridge_list.append(bridge_vec)
        labels_list.append(lbl)
        dirs_list.append(dir_val)
        pnls_list.append(_safe_float(pnl_bps, 0.0))
        assets_list.append(str(asset))
        tfs_list.append(str(tf))
        times_list.append(str(created_at))

    print("-" * 70)
    print(f"[+] Total samples exported: {len(labels_list):,}")
    print(f"    Skipped incomplete (<30 bars): {skipped_incomplete:,}")
    print(f"    Skipped math / invalid price:  {skipped_math:,}")
    
    macro_arr = np.array(macro_list, dtype=np.float32)
    micro_arr = np.array(micro_list, dtype=np.float32)
    bridge_arr = np.array(bridge_list, dtype=np.float32)
    labels_arr = np.array(labels_list, dtype=np.int64)
    dirs_arr = np.array(dirs_list, dtype=np.int64)
    pnls_arr = np.array(pnls_list, dtype=np.float32)
    
    print(f"    Macro tensor:  {macro_arr.shape} (N, 30, 5)")
    print(f"    Micro tensor:  {micro_arr.shape} (N, 30, 5)")
    print(f"    Bridge vector: {bridge_arr.shape} (N, 4)")
    print(f"    Labels:        {labels_arr.shape} (1={np.sum(labels_arr==1):,}, 0={np.sum(labels_arr==0):,})")
    
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    np.savez_compressed(
        out_path,
        macro=macro_arr,
        micro=micro_arr,
        bridge=bridge_arr,
        labels=labels_arr,
        directions=dirs_arr,
        pnl=pnls_arr,
        assets=np.array(assets_list),
        timeframes=np.array(tfs_list),
        timestamps=np.array(times_list)
    )
    
    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    print(f"[+] Successfully saved clean V5 dataset to {out_path} ({size_mb:.2f} MB)")
    print("=" * 70)


if __name__ == "__main__":
    export_dual_stream_dataset()
