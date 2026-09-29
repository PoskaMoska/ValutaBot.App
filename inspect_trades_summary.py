import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        df = pd.read_sql_query(query, conn, params=params)
        return df

print("=== PENDING TRADES SUMMARY ===")
pending = run_query("SELECT asset, timeframe, direction, COUNT(*) as count, MIN(created_at) as first_created, MAX(created_at) as last_created FROM pending_trades GROUP BY asset, timeframe, direction ORDER BY count DESC;")
if not pending.empty:
    print(pending)
else:
    print("No pending trades.")

print("\n=== RECENT PENDING TRADES DETAILS ===")
pending_details = run_query("SELECT id, asset, timeframe, direction, created_at, verify_at FROM pending_trades ORDER BY created_at DESC;")
if not pending_details.empty:
    print(pending_details)
