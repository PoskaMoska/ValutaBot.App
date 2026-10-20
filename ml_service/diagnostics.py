import os
import json
import logging
import pandas as pd
import numpy as np
from datetime import datetime, timedelta
from sklearn.tree import DecisionTreeClassifier

logging.basicConfig(level=logging.INFO, format='%(asctime)s [%(levelname)s] %(message)s')
log = logging.getLogger("Diagnostics")

def get_db_connection():
    db_url = os.getenv("DATABASE_URL")
    if not db_url:
        raise ValueError("DATABASE_URL environment variable is not set.")
    import psycopg2
    return psycopg2.connect(db_url)

def init_db():
    conn = get_db_connection()
    cur = conn.cursor()
    cur.execute("""
        CREATE TABLE IF NOT EXISTS module_diagnostics (
            id SERIAL PRIMARY KEY,
            created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
            module_name VARCHAR(50) NOT NULL,
            timeframe VARCHAR(20) NOT NULL,
            failed_trades_count INTEGER NOT NULL,
            insight_text TEXT NOT NULL,
            suggested_filter JSONB
        )
    """)
    conn.commit()
    cur.close()
    conn.close()
    log.info("Initialized module_diagnostics table.")

def extract_failure_rules(X: pd.DataFrame, y: np.ndarray):
    """
    Uses a shallow Decision Tree to find conditions where failure rate is exceptionally high.
    y=1 means FAILURE (we want to isolate failures).
    y=0 means WIN.
    """
    if len(np.unique(y)) < 2 or len(y) < 20:
        return None, 0.0, 0
        
    # Prevent creating rules that apply to too few samples
    min_samples = max(5, int(len(y) * 0.1))
    clf = DecisionTreeClassifier(max_depth=2, min_samples_leaf=min_samples, random_state=42)
    clf.fit(X, y)
    
    tree_ = clf.tree_
    feature_names = X.columns.tolist()
    
    best_fail_rate = 0.0
    best_rule = None
    best_samples = 0
    
    def recurse(node, rule_path):
        nonlocal best_fail_rate, best_rule, best_samples
        if tree_.feature[node] != -2: # Not a leaf
            name = feature_names[tree_.feature[node]]
            threshold = tree_.threshold[node]
            recurse(tree_.children_left[node], rule_path + [(name, "<", threshold)])
            recurse(tree_.children_right[node], rule_path + [(name, ">=", threshold)])
        else:
            samples = tree_.n_node_samples[node]
            failures = tree_.value[node][0][1] # Count of y=1 (fails)
            if samples > 0:
                fail_rate = failures / samples
                # We only care if the fail rate is significantly worse than a coin flip
                if fail_rate > 0.70 and samples >= min_samples and fail_rate > best_fail_rate:
                    best_fail_rate = fail_rate
                    best_rule = rule_path
                    best_samples = samples
                    
    recurse(0, [])
    return best_rule, best_fail_rate, best_samples

def analyze_module(df: pd.DataFrame, module_name: str, score_col: str, telemetry_col: str):
    """
    Analyzes a specific module's confident predictions that resulted in failure.
    """
    # 1. Filter for confident predictions from this module (|score| > 0.6)
    df_conf = df[df[score_col].abs() > 0.6].copy()
    if df_conf.empty:
        return
        
    # 2. Extract telemetry into separate columns
    telemetry_list = []
    valid_indices = []
    
    for idx, row in df_conf.iterrows():
        tel = row[telemetry_col]
        if isinstance(tel, str):
            try:
                tel = json.loads(tel)
            except:
                continue
        if isinstance(tel, dict) and len(tel) > 0:
            telemetry_list.append(tel)
            valid_indices.append(idx)
            
    if not telemetry_list:
        log.info(f"Not enough {module_name} JSON telemetry yet. Let the bot trade more.")
        return
        
    df_tel = pd.DataFrame(telemetry_list)
    df_tel = df_tel.select_dtypes(include=[np.number]).dropna(axis=1) # Only numeric features
    
    if df_tel.empty:
        return
        
    # y=1 if trade failed, y=0 if won
    y = (~df_conf.loc[valid_indices, 'was_win'].astype(bool)).astype(int).values
    
    rule, fail_rate, samples = extract_failure_rules(df_tel, y)
    
    if rule:
        # Format the insight
        conditions = " AND ".join([f"{f} {op} {val:.4f}" for f, op, val in rule])
        insight = f"Analysis of {len(y)} confident trades showed {fail_rate*100:.1f}% failure rate when [{conditions}]. (Based on {samples} cases)."
        log.warning(f"[{module_name} WEAKNESS FOUND] {insight}")
        
        # Save to DB
        conn = get_db_connection()
        cur = conn.cursor()
        suggested_filter = {"rules": [{"feature": f, "op": op, "value": val} for f, op, val in rule]}
        
        cur.execute("""
            INSERT INTO module_diagnostics (module_name, timeframe, failed_trades_count, insight_text, suggested_filter)
            VALUES (%s, %s, %s, %s, %s)
        """, (module_name, "ALL", int(samples), insight, json.dumps(suggested_filter)))
        conn.commit()
        cur.close()
        conn.close()
    else:
        log.info(f"[{module_name}] No glaring statistical weaknesses found in recent trades.")

def run_dataset_sanity_audit(sample_limit: int = 500, notify_telegram: bool = True) -> dict:
    """
    Autonomous Dataset & Pipeline Sanity Auditor.
    1. Runs a fast aggregate SQL scan across 100% of live BUY/PUT trades in the PO era
       for directional skew and Zero-MFE (instant adverse entry) metrics.
    2. Scans the latest `sample_limit` full features_json snapshots (160-candle sequences + MacroContext)
       for silent data corruption, candle time gaps, stuck/zero-variance features, and flat windows.
    Persists results to module_diagnostics and optionally sends a digest to Telegram admins.
    """
    init_db()
    conn = get_db_connection()
    cur = conn.cursor()

    # Part A: Fast 100%-coverage SQL scan over all live BUY/PUT trades in PO era
    cur.execute("""
        SELECT
            REPLACE(REPLACE(REPLACE(UPPER(asset), '_OTC', ''), ' OTC', ''), '/', '') AS clean_asset,
            COUNT(*) FILTER (WHERE direction = 'BUY') AS buy_cnt,
            COUNT(*) FILTER (WHERE direction = 'PUT') AS put_cnt,
            COUNT(*) FILTER (WHERE was_win = false) AS loss_cnt,
            COUNT(*) FILTER (WHERE was_win = false AND COALESCE(max_favorable_bps, 0) <= 1e-4) AS zero_mfe_loss_cnt,
            COUNT(*) FILTER (WHERE pnl_bps > 1e-4) AS gt_up_cnt,
            COUNT(*) FILTER (WHERE pnl_bps < -1e-4) AS gt_down_cnt
        FROM trade_outcomes
        WHERE created_at::timestamptz >= '2026-10-15 21:55:00+00'::timestamptz
          AND direction IN ('BUY', 'PUT')
          AND was_win IS NOT NULL
        GROUP BY clean_asset
        ORDER BY clean_asset
    """)
    agg_rows = cur.fetchall()

    # Part B: Deep JSON sequence & sensor inspection on recent snapshots
    cur.execute("""
        SELECT
            id, asset, timeframe, direction, was_win, pnl_bps,
            max_favorable_bps, max_adverse_bps,
            price_entropy, adx_at_signal, rsi_at_signal, minutes_to_news,
            created_at, features_json
        FROM trade_outcomes
        WHERE created_at::timestamptz >= '2026-10-15 21:55:00+00'::timestamptz
          AND features_json IS NOT NULL
        ORDER BY created_at::timestamptz DESC
        LIMIT %s
    """, (sample_limit,))
    rows = cur.fetchall()
    cols = [d[0] for d in cur.description]
    cur.close()
    conn.close()

    total = len(rows)
    if total == 0:
        return {"status": "empty", "total_checked": 0}

    tf_sec_map = {"s5": 5, "s10": 10, "s15": 15, "s30": 30, "m1": 60, "1m": 60, "m5": 300, "5m": 300}

    corrupted_json = 0
    short_candle_windows = 0
    gapped_candle_windows = 0
    flat_frozen_windows = 0
    zero_vol_candles_total = 0
    total_candles_checked = 0

    zero_mfe_live_losses = 0
    live_losses_total = 0
    skewed_pairs = []
    pair_summary = {}

    for clean_asset, buy_cnt, put_cnt, loss_cnt, zero_mfe_cnt, gt_up, gt_down in agg_rows:
        live_tot = int(buy_cnt or 0) + int(put_cnt or 0)
        live_losses_total += int(loss_cnt or 0)
        zero_mfe_live_losses += int(zero_mfe_cnt or 0)
        if live_tot > 0:
            buy_pct = int(buy_cnt or 0) / live_tot * 100.0
            gt_tot = max(1, int(gt_up or 0) + int(gt_down or 0))
            gt_up_pct = int(gt_up or 0) / gt_tot * 100.0
            pair_summary[clean_asset] = {
                "live_trades": live_tot,
                "buy_pct": round(buy_pct, 1),
                "gt_up_pct": round(gt_up_pct, 1),
            }
            if live_tot >= 20 and (buy_pct >= 75.0 or buy_pct <= 25.0):
                dominant = "BUY" if buy_pct >= 50 else "PUT"
                dom_pct = buy_pct if buy_pct >= 50 else (100.0 - buy_pct)
                skewed_pairs.append(f"{clean_asset}: {dom_pct:.0f}% {dominant} ({live_tot} сделок, рынок UP {gt_up_pct:.0f}%)")

    macro_vals = {
        "DayRangePositionPct": [],
        "DxyMomentum1mBps": [],
        "BasketSyncScore": [],
        "PriceEntropy": [],
        "Adx": [],
        "Rsi": [],
    }

    for r in rows:
        rd = dict(zip(cols, r))
        tf = str(rd.get("timeframe") or "s5").lower().strip()
        tf_sec = tf_sec_map.get(tf, 5)

        try:
            feat = json.loads(rd["features_json"])
        except Exception:
            corrupted_json += 1
            continue

        candles = feat.get("Candles") or feat.get("candles") or []
        if len(candles) < 40:
            short_candle_windows += 1
            continue

        total_candles_checked += len(candles)
        flat_bars = 0
        has_time_gap = False
        prev_ts = None
        max_gap = max(60, tf_sec * 12)

        for c in candles:
            op = float(c.get("Open", c.get("open", 0.0)) or 0.0)
            hi = float(c.get("High", c.get("high", op)) or op)
            lo = float(c.get("Low", c.get("low", op)) or op)
            cl = float(c.get("Close", c.get("close", op)) or op)
            vol = float(c.get("Volume", c.get("volume", 0.0)) or 0.0)

            if abs(hi - lo) <= 1e-9 and abs(cl - op) <= 1e-9:
                flat_bars += 1
            if vol <= 0.0:
                zero_vol_candles_total += 1

            ts_raw = c.get("Timestamp") or c.get("timestamp")
            if isinstance(ts_raw, str) and len(ts_raw) >= 19:
                try:
                    cur_ts = datetime.fromisoformat(ts_raw.replace("Z", "+00:00")).timestamp()
                    if prev_ts is not None and (cur_ts - prev_ts) > max_gap:
                        has_time_gap = True
                    prev_ts = cur_ts
                except Exception:
                    pass

        if has_time_gap:
            gapped_candle_windows += 1
        if flat_bars > len(candles) * 0.5:
            flat_frozen_windows += 1

        macro = feat.get("MacroContext") or {}
        for k in macro_vals:
            v = macro.get(k)
            if v is not None:
                try:
                    macro_vals[k].append(float(v))
                except Exception:
                    pass

    dead_sensors = []
    for k, arr in macro_vals.items():
        if len(arr) >= 20 and float(np.std(arr)) < 1e-6:
            dead_sensors.append(k)

    zero_vol_pct = (zero_vol_candles_total / max(1, total_candles_checked)) * 100.0
    zero_mfe_pct = (zero_mfe_live_losses / max(1, live_losses_total)) * 100.0

    report = {
        "status": "ok",
        "total_checked": total,
        "corrupted_json": corrupted_json,
        "short_candle_windows": short_candle_windows,
        "gapped_candle_windows": gapped_candle_windows,
        "flat_frozen_windows": flat_frozen_windows,
        "zero_vol_candle_pct": round(zero_vol_pct, 2),
        "zero_mfe_loss_pct": round(zero_mfe_pct, 1),
        "dead_sensors": dead_sensors,
        "skewed_pairs": skewed_pairs,
        "pair_summary": pair_summary,
    }

    insight_summary = (
        f"Checked {total} V3 samples: gaps={gapped_candle_windows}, flat={flat_frozen_windows}, "
        f"short={short_candle_windows}, zero_vol={zero_vol_pct:.2f}%, zero_mfe_losses={zero_mfe_pct:.1f}%, "
        f"dead_sensors={dead_sensors}, skewed_pairs={skewed_pairs}"
    )
    log.info(f"[DatasetAudit] {insight_summary}")

    try:
        conn = get_db_connection()
        cur = conn.cursor()
        cur.execute("""
            INSERT INTO module_diagnostics (module_name, timeframe, failed_trades_count, insight_text, suggested_filter)
            VALUES (%s, %s, %s, %s, %s)
        """, ("DATASET_AUDIT", "ALL", gapped_candle_windows + flat_frozen_windows, insight_summary, json.dumps(report)))
        conn.commit()
        cur.close()
        conn.close()
    except Exception as db_err:
        log.warning(f"[DatasetAudit] Failed to persist audit row: {db_err}")

    if notify_telegram:
        bot_url = os.getenv("BOT_BASE_URL", "http://localhost:5000").rstrip("/")
        api_secret = os.getenv("INTERNAL_API_SECRET", "")
        if api_secret:
            try:
                import requests as req_lib
                health_icon = "🟢" if (corrupted_json == 0 and len(dead_sensors) == 0 and flat_frozen_windows == 0) else "🟡"
                lines = [
                    f"{health_icon} <b>Авто-Аудит чистоты датасета (За 24ч)</b>",
                    f"Проверено слепков: <b>{total}</b>",
                    f"• Битый JSON / Короткие окна: <b>{corrupted_json} / {short_candle_windows}</b>",
                    f"• Разрывы времени внутри 160 свечей: <b>{gapped_candle_windows}</b> ({gapped_candle_windows/max(1,total)*100:.1f}%)",
                    f"• Замороженные свечи (>50% flat): <b>{flat_frozen_windows}</b>",
                    f"• Нулевой тиковый объём: <b>{zero_vol_pct:.2f}%</b>",
                    f"• Убытки с мгновенным уходом (MFE=0): <b>{zero_mfe_pct:.1f}%</b>",
                ]
                if dead_sensors:
                    lines.append(f"⚠️ Залипшие датчики (std=0): <code>{', '.join(dead_sensors)}</code>")
                if skewed_pairs:
                    lines.append(f"⚖️ Перекос направления: <code>{'; '.join(skewed_pairs[:4])}</code>")
                req_lib.post(
                    f"{bot_url}/internal/notify-admins",
                    json={"message": "\n".join(lines), "parse_mode": "HTML"},
                    headers={"X-Internal-Secret": api_secret},
                    timeout=10,
                )
            except Exception as notify_err:
                log.warning(f"[DatasetAudit] Telegram notify skipped: {notify_err}")

    return report


def run_diagnostics():
    init_db()
    try:
        run_dataset_sanity_audit(sample_limit=500, notify_telegram=True)
    except Exception as audit_err:
        log.error(f"[DatasetAudit] Error in sanity audit: {audit_err}")
    
    conn = get_db_connection()
    # Fetch trades from the last 7 days that have telemetry
    query = """
        SELECT asset, timeframe, was_win, ta_score, ml_score, ta_telemetry, ml_telemetry
        FROM trade_outcomes
        WHERE created_at::timestamptz > NOW() - INTERVAL '7 days'
          AND was_win IS NOT NULL
          AND ta_telemetry IS NOT NULL
    """
    df = pd.read_sql_query(query, conn)
    conn.close()
    
    log.info(f"Loaded {len(df)} trades with telemetry for diagnostics.")
    if len(df) < 30:
        log.info("Not enough trades with telemetry yet. The bot needs to generate more signals first.")
        return

    log.info("--- Analyzing TA Module ---")
    analyze_module(df, "TA", "ta_score", "ta_telemetry")
    
    log.info("--- Analyzing ML Module ---")
    # For ML, score is ml_score
    analyze_module(df, "ML", "ml_score", "ml_telemetry")
    
    log.info("Diagnostics completed successfully.")

if __name__ == "__main__":
    run_diagnostics()
