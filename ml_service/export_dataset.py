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
    "EURJPY": 6, "EUR/JPY": 6, "EURJPY_OTC": 6,
    "GBPJPY": 7, "GBP/JPY": 7, "GBPJPY_OTC": 7,
    "BTCUSD": 8, "ETHUSD": 9
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


def _safe_float(val, default=0.0):
    if val is None:
        return default
    try:
        f = float(val)
        return default if math.isnan(f) or math.isinf(f) else f
    except (ValueError, TypeError):
        return default


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
        op = _safe_float(c.get("Open", c.get("open")), default=1.0)
        if op <= 0.0:
            op = 1e-5
        hi = _safe_float(c.get("High", c.get("high")), default=op)
        lo = _safe_float(c.get("Low", c.get("low")), default=op)
        cl = _safe_float(c.get("Close", c.get("close")), default=op)

        arr[idx, 0] = ((cl - op) / op) * 10000.0
        arr[idx, 1] = ((hi - op) / op) * 10000.0
        arr[idx, 2] = ((lo - op) / op) * 10000.0

        # Check timestamp continuity if Timestamp is present
        cur_ts = None
        ts_raw = c.get("Timestamp") or c.get("timestamp") or c.get("openTime")
        if isinstance(ts_raw, (int, float)) and ts_raw > 0:
            cur_ts = float(ts_raw) / 1000.0 if float(ts_raw) > 1e11 else float(ts_raw)
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

        if prev_close is not None and prev_close > 0.0 and not is_time_gap:
            arr[idx, 3] = ((cl - prev_close) / prev_close) * 10000.0
        else:
            arr[idx, 3] = 0.0

        if channels >= 5:
            vol = _safe_float(c.get("Volume", c.get("volume")), default=0.0)
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
    pe = min(5.0, max(0.0, float(pe_raw if pe_raw is not None else macro.get("PriceEntropy", 0.5))))
    
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
    dow = float(dow_raw if dow_raw is not None else macro.get("DayOfWeek", 2.0))
    hr_sin = math.sin(2 * math.pi * hr / 24.0)
    hr_cos = math.cos(2 * math.pi * hr / 24.0)
    dow_sin = math.sin(2 * math.pi * dow / 7.0)
    dow_cos = math.cos(2 * math.pi * dow / 7.0)
    
    # 18. Timeframe seconds normalized (s5 -> 5/60, m1 -> 1.0)
    tf_str = (row_dict.get("timeframe") or "s5").lower()
    tf_sec = float(TF_SECONDS.get(tf_str, 5)) / 60.0
    
    # 19-22. Signed SMC Directional Sensors (+1.0 Bullish, -1.0 Bearish, 0.0 None/Unsigned)
    def _signed_smc(dir_str, has_flag):
        if not has_flag and not dir_str:
            return 0.0
        s = str(dir_str or "").upper()
        if "BULL" in s:
            return 1.0
        if "BEAR" in s:
            return -1.0
        return 0.0

    bos_raw = str(smc.get("BosDirection") or row_dict.get("smc_bos_dir") or "NONE").upper()
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
        bool(row_dict.get("smc_has_swp") or smc.get("HasLiquiditySweep"))
    )
    has_bos = _signed_smc(
        bos_raw,
        bool(bos_raw not in ("NONE", "", "NULL") or smc.get("HasBos"))
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

    # 24-31. Enriched TA and Micro-Dynamics (Fix Train-Serve Skew: C# serializes VolStrengthVal in DB and VolRatio in live payload)
    ta_score = _safe_float(ta.get("Score"), 0.0)
    micro_vel = _safe_float(ta.get("MicroVel"), 0.0) / 20.0
    hma_slope = _safe_float(ta.get("HmaSlope"), 0.0) / 10.0
    vol_raw = ta.get("VolRatio") if ta.get("VolRatio") is not None else ta.get("VolStrengthVal", 1.0)
    vol_ratio = min(3.0, max(0.0, _safe_float(vol_raw, 1.0))) / 3.0
    bb_sq = float(1.0 if (macro.get("BbSqueeze") or row_dict.get("bb_squeeze")) else 0.0)
    price_pos = min(1.0, max(0.0, float(macro.get("PricePositionPct", row_dict.get("price_position_pct", 0.5) or 0.5))))
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
    Strictly sorts and deduplicates timestamps across tables to guarantee monotonic indexation.
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
        print("[*] Loading subminute & historical candles into strictly monotonic index...")
        cur = self.conn.cursor()
        raw_buckets = {}
        for tbl, o_col, h_col, l_col, c_col in [
            ("subminute_candles", "open_price", "high_price", "low_price", "close_price"),
            ("historical_candles", "open", "high", "low", "close"),
        ]:
            try:
                cur.execute(f"""
                    SELECT asset, interval, open_time, {o_col}, {h_col}, {l_col}, {c_col}, volume
                    FROM {tbl}
                    WHERE asset LIKE '%%_PO'
                      AND open_time >= to_char((%s::timestamptz - INTERVAL '3 hours') AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS')
                    ORDER BY open_time ASC
                """, (self.min_date,))
                for asset, interval, ot, op, hi, lo, cl, vol in cur.fetchall():
                    clean_a = (
                        asset.upper()
                        .replace("_PO", "")
                        .replace("_OTC", "")
                        .replace(" OTC", "")
                        .replace("/", "")
                        .replace("-", "")
                        .replace("_", "")
                        .strip()
                    )
                    iv = interval.lower().strip()
                    if iv in ("1min", "1m", "m1"):
                        iv = "m1"
                    elif iv in ("5min", "5m", "m5"):
                        iv = "m5"
                    key = (clean_a, iv)
                    bucket = raw_buckets.setdefault(key, [])
                    ts_str = str(ot)[:19].replace("T", " ")
                    bucket.append((ts_str, {
                        "Timestamp": ot,
                        "Open": _safe_float(op, 1.0),
                        "High": _safe_float(hi, 1.0),
                        "Low": _safe_float(lo, 1.0),
                        "Close": _safe_float(cl, 1.0),
                        "Volume": _safe_float(vol, 0.0)
                    }))
            except Exception as tbl_err:
                print(f"[-] Warning: Failed to load candles from {tbl}: {tbl_err}")
        cur.close()

        for key, rows_list in raw_buckets.items():
            rows_list.sort(key=lambda x: x[0])
            dedup_ts = []
            dedup_c = []
            for ts, c in rows_list:
                if not dedup_ts or dedup_ts[-1] != ts:
                    dedup_ts.append(ts)
                    dedup_c.append(c)
            self.series[key] = (dedup_ts, dedup_c)

        print(f"[+] Monotonically indexed {len(self.series)} (asset, timeframe) series from subminute/historical tables.")

    def get_candles_before(self, asset: str, timeframe: str, created_at_dt, count: int):
        self._ensure_loaded()
        clean_a = (
            asset.upper()
            .replace("_PO", "")
            .replace("_OTC", "")
            .replace(" OTC", "")
            .replace("/", "")
            .replace("-", "")
            .replace("_", "")
            .strip()
        )
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
        from datetime import timedelta, timezone
        dt_obj = None
        if hasattr(created_at_dt, "timestamp"):
            dt_obj = created_at_dt
        elif isinstance(created_at_dt, str) and len(created_at_dt) >= 19:
            try:
                s = created_at_dt.strip().replace("Z", "+00:00")
                dt_obj = datetime.fromisoformat(s)
            except Exception:
                try:
                    dt_obj = datetime.strptime(created_at_dt[:19].replace("T", " "), "%Y-%m-%d %H:%M:%S")
                except Exception:
                    dt_obj = None

        # Strict Anti-Leakage: NEVER fall back to unshifted created_at_dt; drop sample if timestamp cannot be parsed
        if dt_obj is None:
            return []
        if dt_obj.tzinfo is not None:
            dt_obj = dt_obj.astimezone(timezone.utc).replace(tzinfo=None)
        cutoff_dt = dt_obj - timedelta(seconds=dur_sec)
        iso_cut = cutoff_dt.strftime("%Y-%m-%d %H:%M:%S")

        idx = bisect.bisect_right(bucket[0], iso_cut)
        start = max(0, idx - count)
        return bucket[1][start:idx]


def determine_label(entry_price, exit_price, direction, pnl_bps_recorded, min_pnl_threshold=0.30):
    """
    AUDIT 4 FIX: Accurately identifies true market direction irrespective of order direction.
    Class 0: HOLD / Chop (|raw_delta_bps| <= min_pnl_threshold)
    Class 1: BUY  (Market moved UP:   raw_delta_bps > min_pnl_threshold)
    Class 2: PUT  (Market moved DOWN: raw_delta_bps < -min_pnl_threshold)
    """
    en = _safe_float(entry_price)
    ex = _safe_float(exit_price)
    if en > 0.0 and ex > 0.0:
        raw_pnl_bps = ((ex - en) / en) * 10000.0
    else:
        is_put = str(direction or "").upper().endswith("PUT")
        pnl_val = _safe_float(pnl_bps_recorded)
        raw_pnl_bps = -pnl_val if is_put else pnl_val

    if abs(raw_pnl_bps) <= min_pnl_threshold:
        return 0, raw_pnl_bps
    elif raw_pnl_bps > min_pnl_threshold:
        return 1, raw_pnl_bps
    else:
        return 2, raw_pnl_bps


import sys

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

PO_CUTOFF_UTC = "2026-10-21 20:15:00+00"

def export_dataset(db_url, output_path, limit=None, seq_len=160, seq_len_mtf=30, channels=8, include_tiingo=False, min_date=PO_CUTOFF_UTC, stride_hf=True, min_pnl_bps=0.30):
    print("=" * 60)
    print("[*] Neural Brain Dataset Exporter (V4 Architecture)")
    print(f"Connecting to database...")
    print("=" * 60)
    
    conn = psycopg2.connect(
        db_url,
        keepalives=1,
        keepalives_idle=15,
        keepalives_interval=5,
        keepalives_count=3
    )
    conn.autocommit = True  # Release oldest_xmin immediately so autovacuum is not blocked during dataset export
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
              AND was_win IS NOT NULL
              AND entry_price > 0 AND exit_price > 0
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
              AND was_win IS NOT NULL
              AND entry_price > 0 AND exit_price > 0
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
              AND was_win IS NOT NULL
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

        # 2. Normalize MTF Candles Sequence (Fix Train-Serve Skew: mtf_tf_sec = 60 if tf_sec_row < 60 else 300)
        mtf_tf_sec = 60 if tf_sec_row < 60 else 300
        mtf_candles = feat_obj.get("MtfCandles") or feat_obj.get("mtf_candles") or []
        if not mtf_candles:
            htf = higher_tf_map.get(tf_str_row, "m1")
            mtf_candles = candle_store.get_candles_before(str(row_dict.get("asset") or ""), htf, row_dict.get("created_at"), seq_len_mtf)
        elif len(mtf_candles) > 1:
            # Strip trailing unclosed MTF candle if its close time exceeds created_at
            last_mtf_ts = mtf_candles[-1].get("Timestamp") or mtf_candles[-1].get("timestamp") or mtf_candles[-1].get("openTime")
            created_dt = row_dict.get("created_at")
            if isinstance(last_mtf_ts, str) and len(last_mtf_ts) >= 19 and hasattr(created_dt, "timestamp"):
                try:
                    mtf_open_ts = datetime.fromisoformat(last_mtf_ts.replace("Z", "+00:00")).timestamp()
                    if mtf_open_ts + mtf_tf_sec > created_dt.timestamp():
                        mtf_candles = mtf_candles[:-1]
                except Exception:
                    pass
        mtf_tensor = normalize_candles(mtf_candles, seq_len=seq_len_mtf, channels=channels, tf_sec=mtf_tf_sec) if len(mtf_candles) > 0 else np.zeros((seq_len_mtf, channels), dtype=np.float32)
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
        if was_win is None:
            skipped_count += 1
            continue
        pnl = _safe_float(row_dict.get("pnl_bps"))
        en_p = _safe_float(row_dict.get("entry_price"))
        ex_p = _safe_float(row_dict.get("exit_price"))
        # Scale HOLD noise threshold with sqrt(timeframe) so m1 chop is not mislabeled as directional trend
        effective_min_pnl = min_pnl_bps * math.sqrt(max(1.0, tf_sec_row / 5.0))
        label, raw_pnl = determine_label(en_p, ex_p, direction, pnl, min_pnl_threshold=effective_min_pnl)
        
        mfe = _safe_float(row_dict.get("max_favorable_bps"))
        mae = _safe_float(row_dict.get("max_adverse_bps"))
        
        x_candles_list.append(candle_tensor)
        x_mtf_candles_list.append(mtf_tensor)
        x_macro_list.append(macro_vec)
        y_list.append(label)
        y_pnl_list.append(raw_pnl)
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
    parser.add_argument("--min-pnl-bps", type=float, default=0.30, help="Minimum price delta in bps for non-HOLD classification")
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
        stride_hf=not args.no_stride_hf,
        min_pnl_bps=args.min_pnl_bps
    )
