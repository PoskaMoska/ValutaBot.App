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

def run_diagnostics():
    init_db()
    
    conn = get_db_connection()
    # Fetch trades from the last 7 days that have telemetry
    query = """
        SELECT asset, timeframe, was_win, ta_score, ml_score, ta_telemetry, ml_telemetry
        FROM trade_outcomes
        WHERE created_at > NOW() - INTERVAL '7 days'
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
