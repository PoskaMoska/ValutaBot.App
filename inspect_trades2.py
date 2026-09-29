import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        df = pd.read_sql_query(query, conn, params=params)
        return df

print("=== LATEST TRADE OUTCOMES ===")
outcomes = run_query("SELECT id, asset, direction, was_win, pnl_bps, created_at FROM trade_outcomes ORDER BY created_at DESC LIMIT 10;")
print(outcomes)

print("\n=== PENDING TRADES SUMMARY ===")
pending = run_query("SELECT id, asset, direction, created_at, verify_at FROM pending_trades ORDER BY created_at DESC;")
if not pending.empty:
    print(pending)
else:
    print("No pending trades.")
