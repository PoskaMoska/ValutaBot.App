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
import bisect
import numpy as np
import psycopg2
from datetime import datetime

try:
    import orjson
    _loads = orjson.loads
except ImportError:
    _loads = json.loads

DEFAULT_DB_URL = os.environ.get("DATABASE_URL") or os.environ.get("DB_URL", "")

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
    "asset_id",
    "ta_score",
    "micro_vel",
    "hma_slope",
    "vol_ratio",
    "bb_squeeze",
    "price_position_pct",
    "trend_maturity",
    "tick_count_norm"
]


def normalize_candles(candles_list, seq_len=160, channels=8, tf_sec=5):
    """
    Normalizes a sequence of OHLCV candles into a stationary [seq_len, channels] array.
    Uses zero-padding on the left when len(candles_list) < seq_len.
    Features per candle (channels=8):
      0: (Close - Open) / Open * 10000 (candle body)
      1: (High - Open) / Open * 10000 (upper excursion)
      2: (Low - Open) / Open * 10000 (lower excursion)
      3: (Close - PrevClose) / PrevClose * 10000 (inter-candle return)
      4: log1p(Volume) / 10.0 (volume/tick density)
      5: (High - max(Open, Close)) / Open * 10000 (upper wick / seller rejection)
      6: (min(Open, Close) - Low) / Open * 10000 (lower wick / buyer rejection)
      7: (High - Low) / Open * 10000 (full candle range / local volatility)
    """
    arr = np.zeros((seq_len, channels), dtype=np.float32)
    if not candles_list:
        return arr

    tail = candles_list[-seq_len:] if len(candles_list) >= seq_len else candles_list
    offset = seq_len - len(tail)

    prev_close = None
    prev_ts = None
    max_allowed_gap_sec = max(60, tf_sec * 12)

    for i, c in enumerate(tail):
        idx = offset + i
        op = float(c.get("Open", c.get("open", 1.0)))
        hi = float(c.get("High", c.get("high", op)))
        lo = float(c.get("Low", c.get("low", op)))
        cl = float(c.get("Close", c.get("close", op)))

        if op <= 0:
            op = 1e-5

        arr[idx, 0] = ((cl - op) / op) * 10000.0
        arr[idx, 1] = ((hi - op) / op) * 10000.0
        arr[idx, 2] = ((lo - op) / op) * 10000.0

        # Check timestamp continuity if Timestamp is present
        cur_ts = None
        ts_raw = c.get("Timestamp") or c.get("timestamp") or c.get("openTime")
        if isinstance(ts_raw, (int, float)) and ts_raw > 0:
            cur_ts = float(ts_raw)
        elif isinstance(ts_raw, str) and len(ts_raw) >= 19:
            try:
                cur_ts = datetime.fromisoformat(ts_raw.replace("Z", "+00:00")).timestamp()
            except Exception:
                cur_ts = None

        is_time_gap = (
            prev_ts is not None
            and cur_ts is not None
            and (cur_ts - prev_ts) > max_allowed_gap_sec
        )

        if prev_close is not None and prev_close > 0 and not is_time_gap:
            arr[idx, 3] = ((cl - prev_close) / prev_close) * 10000.0
        else:
            arr[idx, 3] = 0.0

        if channels >= 5:
            vol = float(c.get("Volume", c.get("volume", 0.0)) or 0.0)
            arr[idx, 4] = math.log1p(max(0.0, vol)) / 10.0

        if channels >= 8:
            arr[idx, 5] = ((hi - max(op, cl)) / op) * 10000.0
            arr[idx, 6] = ((min(op, cl) - lo) / op) * 10000.0
            arr[idx, 7] = ((hi - lo) / op) * 10000.0

        prev_close = cl
        if cur_ts is not None:
            prev_ts = cur_ts

    return arr


def extract_macro_vector(row_dict, feat_obj):
    """
    Extracts a fixed-size 31-element vector of enriched macro and market context features.
    """
    macro = feat_obj.get("MacroContext") or {}
    smc = feat_obj.get("Smc") or {}
    ta = feat_obj.get("Ta") or {}
    
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
    pe_raw = row_dict.get("price_entropy")
    pe = float(pe_raw if pe_raw is not None else macro.get("PriceEntropy", 0.5))
    
    # 11. ADX (0..1)
    adx_raw = row_dict.get("adx_at_signal")
    adx = float(adx_raw if (adx_raw is not None and adx_raw > 0) else macro.get("Adx", 25.0)) / 100.0
    
    # 12. RSI (-1..1 normalized around 50)
    rsi_raw = row_dict.get("rsi_at_signal")
    rsi = (float(rsi_raw if (rsi_raw is not None and rsi_raw > 0) else macro.get("Rsi", 50.0)) - 50.0) / 50.0
    
    # 13. News proximity (0..1, where 1 = news far away >= 120m or -1 no news, 0 = imminent news)
    news_raw = row_dict.get("minutes_to_news")
    if news_raw is None:
        news_raw = macro.get("MinutesToNews", 120.0)
    news_m = float(news_raw)
    if news_m < 0:
        news_m = 120.0  # -1 means no upcoming high-impact news -> treat as far away (1.0)
    news_norm = min(120.0, max(0.0, news_m)) / 120.0
    
    # 14-17. Cyclical time encoding (preserve hour_utc == 0 midnight!)
    hr_raw = row_dict.get("hour_utc")
    hr = float(hr_raw if hr_raw is not None else macro.get("HourUtc", 12.0))
    dow_raw = row_dict.get("day_of_week")
    dow = float(dow_raw if (dow_raw is not None and dow_raw > 0) else macro.get("DayOfWeek", 2.0))
    hr_sin = math.sin(2 * math.pi * hr / 24.0)
    hr_cos = math.cos(2 * math.pi * hr / 24.0)
    dow_sin = math.sin(2 * math.pi * dow / 7.0)
    dow_cos = math.cos(2 * math.pi * dow / 7.0)
    
    # 18. Timeframe seconds normalized (s5 -> 5/60, m1 -> 1.0)
    tf_str = (row_dict.get("timeframe") or "s5").lower()
    tf_sec = float(TF_SECONDS.get(tf_str, 5)) / 60.0
    
    # 19-22. Signed SMC Directional Sensors (+1.0 Bullish, -1.0 Bearish, 0.0 None)
    def _signed_smc(dir_str, has_flag):
        s = str(dir_str or "").upper()
        if "BULL" in s:
            return 1.0
        if "BEAR" in s:
            return -1.0
        return 1.0 if has_flag else 0.0

    has_ob = _signed_smc(
        smc.get("OrderBlockType") or row_dict.get("smc_ob_type"),
        bool(row_dict.get("smc_has_ob") or smc.get("HasOrderBlock"))
    )
    has_fvg = _signed_smc(
        smc.get("FvgType") or row_dict.get("smc_fvg_type"),
        bool(row_dict.get("smc_has_fvg") or smc.get("HasFvg"))
    )
    has_swp = _signed_smc(
        smc.get("SweepDirection") or row_dict.get("smc_swp_dir"),
        bool(smc.get("HasLiquiditySweep"))
    )
    has_bos = _signed_smc(
        smc.get("BosDirection") or row_dict.get("smc_bos_dir"),
        bool(smc.get("HasBos"))
    )
    
    # 23. Asset ID
    asset_str = (
        (row_dict.get("asset") or "")
        .upper()
        .replace("_OTC", "")
        .replace(" OTC", "")
        .replace("_PO", "")
        .replace("/", "")
        .replace("-", "")
        .replace("_", "")
        .strip()
    )
    asset_id = float(ASSET_MAP.get(asset_str, 6.0))

    # 24-31. Enriched TA and Micro-Dynamics
    ta_score = float(ta.get("Score", 0.0))
    micro_vel = float(ta.get("MicroVel", 0.0)) / 20.0
    hma_slope = float(ta.get("HmaSlope", 0.0)) / 10.0
    vol_ratio = min(3.0, float(ta.get("VolRatio", 1.0))) / 3.0
    bb_sq = float(1.0 if (macro.get("BbSqueeze") or row_dict.get("bb_squeeze")) else 0.0)
    price_pos = float(macro.get("PricePositionPct", row_dict.get("price_position_pct", 0.5) or 0.5))
    trend_mat = float(macro.get("TrendMaturity", row_dict.get("trend_maturity", 0.0) or 0.0)) / 100.0
    last_ticks = float(macro.get("TickCountLastCandle", 1.0))
    tick_density = math.log1p(max(0.0, last_ticks)) / 5.0
    
    vec = np.array([
        day_pos, dist_high, dist_low,
        dxy_1m, dxy_5m, basket_sync, spread,
        asian_high, asian_low,
        pe, adx, rsi, news_norm,
        hr_sin, hr_cos, dow_sin, dow_cos,
        tf_sec,
        has_ob, has_fvg, has_swp, has_bos,
        asset_id,
        ta_score, micro_vel, hma_slope, vol_ratio,
        bb_sq, price_pos, trend_mat, tick_density
    ], dtype=np.float32)
    
    return vec


class SubminuteCandleStore:
    """
    Lazily loads subminute_candles (*_PO) and historical_candles (*_PO) from PostgreSQL
    to reconstruct 160 micro-candles and 30 MTF-candles in O(log N) for compacted trade_outcomes rows (>24h old).
    """
    def __init__(self, conn, min_date):
        self.conn = conn
        self.min_date = min_date
        self.loaded = False
        self.series = {}  # (clean_asset, interval_key) -> (timestamps_list, candles_dict_list)

    def _ensure_loaded(self):
        if self.loaded:
            return
        self.loaded = True
        print("[*] Loading subminute_candles (*_PO) index for compacted >24h rows...")
        cur = self.conn.cursor()
        for tbl, o_col, h_col, l_col, c_col in [
            ("subminute_candles", "open_price", "high_price", "low_price", "close_price"),
            ("historical_candles", "open", "high", "low", "close"),
        ]:
            try:
                cur.execute(f"""
                    SELECT asset, interval, open_time, {o_col}, {h_col}, {l_col}, {c_col}, volume
                    FROM {tbl}
                    WHERE asset LIKE '%%_PO'
                    ORDER BY asset, interval, open_time ASC
                """)
                for asset, interval, ot, op, hi, lo, cl, vol in cur.fetchall():
                    clean_a = asset.upper().replace("_PO", "").replace("_OTC", "").replace(" OTC", "").replace("/", "").strip()
                    iv = interval.lower().strip()
                    if iv in ("1min", "1m", "m1"):
                        iv = "m1"
                    elif iv in ("5min", "5m", "m5"):
                        iv = "m5"
                    key = (clean_a, iv)
                    bucket = self.series.setdefault(key, ([], []))
                    ts_str = str(ot)[:19].replace("T", " ")
                    if not bucket[0] or bucket[0][-1] != ts_str:
                        bucket[0].append(ts_str)
                        bucket[1].append({
                            "Timestamp": ot,
                            "Open": float(op),
                            "High": float(hi),
                            "Low": float(lo),
                            "Close": float(cl),
                            "Volume": float(vol or 1.0)
                        })
            except Exception as tbl_err:
                print(f"[-] Warning: Failed to load candles from {tbl}: {tbl_err}")
        cur.close()
        print(f"[+] Indexed {len(self.series)} (asset, timeframe) series from subminute/historical tables.")

    def get_candles_before(self, asset: str, timeframe: str, created_at_dt, count: int):
        self._ensure_loaded()
        clean_a = asset.upper().replace("_PO", "").replace("_OTC", "").replace(" OTC", "").replace("/", "").strip()
        iv = timeframe.lower().strip()
        if iv in ("1min", "1m", "m1"):
            iv = "m1"
        elif iv in ("5min", "5m", "m5"):
            iv = "m5"
        bucket = self.series.get((clean_a, iv))
        if not bucket or not bucket[0]:
            return []

        # Strict Anti-Leakage: a candle is ONLY valid if it was 100% closed BEFORE created_at_dt.
        # open_time + candle_duration <= created_at_dt  ==>  open_time <= created_at_dt - candle_duration
        dur_sec = TF_SECONDS.get(iv, 60)
        from datetime import timedelta
        if hasattr(created_at_dt, "timestamp"):
            cutoff_dt = created_at_dt - timedelta(seconds=dur_sec)
            iso_cut = cutoff_dt.strftime("%Y-%m-%d %H:%M:%S")
        else:
            iso_cut = str(created_at_dt)[:19].replace("T", " ")

        idx = bisect.bisect_right(bucket[0], iso_cut)
        start = max(0, idx - count)
        return bucket[1][start:idx]


def determine_label(direction, was_win, pnl_bps, min_pnl_threshold=1e-4):
    """
    Determines 3-class target for true binary options payouts:
      0: HOLD / Tie / Exact Flat (|pnl_bps| <= 1e-4, entry_price == exit_price)
      1: BUY (Market went UP: pnl_bps > 1e-4 -> BUY win)
      2: PUT (Market went DOWN: pnl_bps < -1e-4 -> PUT win)
    """
    if pnl_bps is None or abs(pnl_bps) <= min_pnl_threshold:
        return 0
    elif pnl_bps > min_pnl_threshold:
        return 1
    else:
        return 2


import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

PO_CUTOFF_UTC = "2026-10-21 20:15:00+00"

def export_dataset(db_url, output_path, limit=None, seq_len=160, seq_len_mtf=30, channels=8, include_tiingo=False, min_date=PO_CUTOFF_UTC, stride_hf=True):
    print("=" * 60)
    print("[*] Neural Brain Dataset Exporter (V4 Architecture)")
    print(f"Connecting to database...")
    print("=" * 60)
    
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()
    
    if include_tiingo:
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
            UNION ALL
            SELECT 
                id, asset, timeframe, direction, 
                entry_price, exit_price, pnl_bps, was_win,
                created_at, features_json,
                price_entropy, adx_at_signal, atr_at_signal, rsi_at_signal,
                minutes_to_news, hour_utc, day_of_week,
                smc_has_ob, smc_has_fvg,
                max_favorable_bps, max_adverse_bps
            FROM trade_outcomes_archive_20261008
            WHERE features_json IS NOT NULL
            ORDER BY created_at ASC
        """
        params = ()
    else:
        hf_filter = ""
        if stride_hf:
            hf_filter = """
              AND (
                  (LOWER(timeframe) = 's5' AND MOD(ABS(hashtext(id)), 10) = 0)
                  OR LOWER(timeframe) != 's5'
              )
              AND ABS(pnl_bps) <= 500
            """
            print(f"[*] Filtering Golden PO OTC V4 era (created_at >= {min_date}) with high-fidelity cadence (s5: 1/10, s10: 100%, s15: 100%, s30: 100%, m1: 100%)")
        else:
            hf_filter = "AND ABS(pnl_bps) <= 500"
            print(f"[*] Filtering strictly Pocket Option OTC era (created_at >= {min_date}, no Tiingo archive)")
        query = f"""
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
              AND entry_price > 0 AND exit_price > 0
              AND created_at >= %s::timestamptz
              {hf_filter}
            ORDER BY created_at ASC
        """
        params = (min_date,)

    if limit:
        query += f" LIMIT {int(limit)}"
        
    print(f"Executing query...")
    cur.execute(query, params)
    
    cols = [desc[0] for desc in cur.description]
    rows = cur.fetchall()
    total_found = len(rows)
    print(f"Retrieved {total_found} rows from database.")

    # Temporal sort validation
    created_idx = cols.index("created_at")
    neg_gaps = 0
    prev_dt = None
    for idx, r in enumerate(rows):
        dt = r[created_idx]
        if prev_dt is not None and dt is not None and dt < prev_dt:
            neg_gaps += 1
            if neg_gaps <= 5:
                print(f"[-] WARNING: Negative time gap detected at row {idx}: {prev_dt} -> {dt} (gap: {dt - prev_dt})")
        if dt is not None:
            prev_dt = dt

    if neg_gaps > 0:
        print(f"[-] WARNING: Total {neg_gaps} negative time gap(s) found in temporal sequence!")
    else:
        print(f"[+] Temporal sort validated: strictly non-decreasing timestamps (0 negative gaps).")
    
    x_candles_list = []
    x_mtf_candles_list = []
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
    candle_store = SubminuteCandleStore(conn, min_date)
    higher_tf_map = {"s5": "m1", "s10": "m1", "s15": "m1", "s30": "m1", "m1": "m5"}
    
    for r in rows:
        row_dict = dict(zip(cols, r))
        feat_str = row_dict["features_json"]
        if not feat_str:
            skipped_count += 1
            continue
            
        try:
            feat_obj = _loads(feat_str)
        except Exception:
            skipped_count += 1
            continue
            
        tf_str_row = str(row_dict.get("timeframe") or "s5").lower()
        candles = feat_obj.get("Candles") or feat_obj.get("candles") or []
        if not candles:
            candles = candle_store.get_candles_before(str(row_dict.get("asset") or ""), tf_str_row, row_dict.get("created_at"), seq_len)

        # Strip trailing unclosed 1-tick candle if present from legacy orchestrator serialization
        if len(candles) > 10:
            last_c = candles[-1]
            c_o = last_c.get("Open", last_c.get("open"))
            c_h = last_c.get("High", last_c.get("high"))
            c_l = last_c.get("Low", last_c.get("low"))
            c_c = last_c.get("Close", last_c.get("close"))
            c_v = float(last_c.get("Volume", last_c.get("volume", 0)) or 0)
            if c_v <= 1.0 and c_o is not None and c_o == c_h == c_l == c_c:
                candles = candles[:-1]

        if len(candles) < 10:
            skipped_count += 1
            continue
            
        # 1. Normalize Candles Sequence
        tf_sec_row = TF_SECONDS.get(tf_str_row, 5)
        candle_tensor = normalize_candles(candles, seq_len=seq_len, channels=channels, tf_sec=tf_sec_row)
        if np.isnan(candle_tensor).any() or np.isinf(candle_tensor).any():
            skipped_count += 1
            continue

        # 2. Normalize MTF Candles Sequence
        mtf_candles = feat_obj.get("MtfCandles") or feat_obj.get("mtf_candles") or []
        if not mtf_candles:
            htf = higher_tf_map.get(tf_str_row, "m1")
            mtf_candles = candle_store.get_candles_before(str(row_dict.get("asset") or ""), htf, row_dict.get("created_at"), seq_len_mtf)
        mtf_tensor = normalize_candles(mtf_candles, seq_len=seq_len_mtf, channels=channels, tf_sec=max(60, tf_sec_row * 4)) if len(mtf_candles) > 0 else np.zeros((seq_len_mtf, channels), dtype=np.float32)
        if np.isnan(mtf_tensor).any() or np.isinf(mtf_tensor).any():
            mtf_tensor = np.zeros((seq_len_mtf, channels), dtype=np.float32)
            
        # 3. Extract Macro Vector
        macro_vec = extract_macro_vector(row_dict, feat_obj)
        if np.isnan(macro_vec).any() or np.isinf(macro_vec).any():
            skipped_count += 1
            continue
            
        # 4. Target Label
        direction = str(row_dict.get("direction") or "")
        was_win = row_dict.get("was_win")
        pnl = float(row_dict.get("pnl_bps") or 0.0)
        label = determine_label(direction, was_win, pnl)
        
        mfe = float(row_dict.get("max_favorable_bps") or 0.0)
        mae = float(row_dict.get("max_adverse_bps") or 0.0)
        
        x_candles_list.append(candle_tensor)
        x_mtf_candles_list.append(mtf_tensor)
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
    X_mtf_candles = np.array(x_mtf_candles_list, dtype=np.float32)
    X_macro = np.array(x_macro_list, dtype=np.float32)
    y = np.array(y_list, dtype=np.int64)
    y_pnl = np.array(y_pnl_list, dtype=np.float32)
    y_mfe = np.array(y_mfe_list, dtype=np.float32)
    y_mae = np.array(y_mae_list, dtype=np.float32)
    
    print(f"X_candles shape:     {X_candles.shape} (N, SeqLen, Feats)")
    print(f"X_mtf_candles shape: {X_mtf_candles.shape} (N, SeqLenMtf, Feats)")
    print(f"X_macro shape:       {X_macro.shape} (N, Feats)")
    print(f"y shape:             {y.shape}")
    
    # Class breakdown
    c0 = int(np.sum(y == 0))
    c1 = int(np.sum(y == 1))
    c2 = int(np.sum(y == 2))
    tot = len(y) if len(y) > 0 else 1

    # Data Quality Report
    nan_candles = int(np.isnan(X_candles).sum())
    inf_candles = int(np.isinf(X_candles).sum())
    nan_mtf = int(np.isnan(X_mtf_candles).sum())
    inf_mtf = int(np.isinf(X_mtf_candles).sum())
    nan_macro = int(np.isnan(X_macro).sum())
    inf_macro = int(np.isinf(X_macro).sum())
    nan_y = int(np.isnan(y).sum())
    nan_pnl = int(np.isnan(y_pnl).sum())
    total_nans = nan_candles + nan_mtf + nan_macro + nan_y + nan_pnl
    total_infs = inf_candles + inf_mtf + inf_macro

    total_candle_timesteps = int(X_candles.shape[0] * X_candles.shape[1])
    total_mtf_timesteps = int(X_mtf_candles.shape[0] * X_mtf_candles.shape[1])
    if channels >= 5:
        vol_zeros_c = int(np.sum(X_candles[:, :, 4] == 0.0))
        vol_zero_pct = (vol_zeros_c / max(1, total_candle_timesteps)) * 100.0
        vol_zeros_mtf = int(np.sum(X_mtf_candles[:, :, 4] == 0.0))
        vol_zero_mtf_pct = (vol_zeros_mtf / max(1, total_mtf_timesteps)) * 100.0
    else:
        vol_zeros_c = 0
        vol_zero_pct = 0.0
        vol_zeros_mtf = 0
        vol_zero_mtf_pct = 0.0

    print("\n" + "=" * 60)
    print("[+] DATA QUALITY REPORT")
    print(f"  Valid samples parsed:     {valid_count} (Skipped: {skipped_count})")
    print(f"  Candle shape:             {X_candles.shape} (channels={channels})")
    print(f"  MTF candle shape:         {X_mtf_candles.shape}")
    print(f"  Macro vector shape:       {X_macro.shape}")
    print(f"  NaN counts:               Total={total_nans} (Candles={nan_candles}, MTF={nan_mtf}, Macro={nan_macro}, Target={nan_y+nan_pnl})")
    print(f"  Inf counts:               Total={total_infs} (Candles={inf_candles}, MTF={inf_mtf}, Macro={inf_macro})")
    if channels >= 5:
        print(f"  Micro Volume zeros:       {vol_zeros_c:,} / {total_candle_timesteps:,} ({vol_zero_pct:.2f}%)")
        print(f"  MTF Volume zeros:         {vol_zeros_mtf:,} / {total_mtf_timesteps:,} ({vol_zero_mtf_pct:.2f}%)")
    else:
        print(f"  Volume channel:           Excluded (4-channel mode: OHLC relative returns)")
    print(f"  Class 0 (HOLD / Chop):    {c0:6d} ({c0/tot*100:5.2f}%)")
    print(f"  Class 1 (BUY):            {c1:6d} ({c1/tot*100:5.2f}%)")
    print(f"  Class 2 (PUT):            {c2:6d} ({c2/tot*100:5.2f}%)")
    print("=" * 60)
    
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    
    print(f"\nSaving dataset to: {output_path} ...")
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    np.savez_compressed(
        output_path,
        X_candles=X_candles,
        X_mtf_candles=X_mtf_candles,
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
    default_out = "ml_service/data/dataset_v4.npz" if os.path.exists("ml_service") else "data/dataset_v4.npz"
    parser = argparse.ArgumentParser(description="Export V4 dataset from PostgreSQL")
    parser.add_argument("--db-url", default=os.getenv("DATABASE_URL", DEFAULT_DB_URL))
    parser.add_argument("--out", default=default_out)
    parser.add_argument("--limit", type=int, default=None)
    parser.add_argument("--seq-len", type=int, default=160)
    parser.add_argument("--seq-len-mtf", type=int, default=30)
    parser.add_argument("--channels", type=int, choices=[4, 5, 8], default=8, help="Candle feature channels: 4 (OHLC), 5 (OHLCV) or 8 (OHLCV+Wicks+Range)")
    parser.add_argument("--include-tiingo", action="store_true", help="Include legacy Tiingo era rows (not recommended for PO OTC)")
    parser.add_argument("--min-date", default=PO_CUTOFF_UTC, help="Minimum created_at UTC timestamp for PO era")
    parser.add_argument("--no-stride-hf", action="store_true", help="Disable SQL striding of high-frequency s5/s10 rows")
    args = parser.parse_args()
    
    export_dataset(
        args.db_url,
        args.out,
        limit=args.limit,
        seq_len=args.seq_len,
        seq_len_mtf=args.seq_len_mtf,
        channels=args.channels,
        include_tiingo=args.include_tiingo,
        min_date=args.min_date,
        stride_hf=not args.no_stride_hf
    )
