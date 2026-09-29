import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        df = pd.read_sql_query(query, conn, params=params)
        return df

print("=== TODAY'S TRADE OUTCOMES ===")
# Fetch columns
cols = run_query("SELECT column_name FROM information_schema.columns WHERE table_name = 'trade_outcomes';")
print("Columns in trade_outcomes:", cols['column_name'].tolist())

print("\n=== TOTAL OUTCOMES TODAY (2026-09-29) ===")
today_trades = run_query("SELECT * FROM trade_outcomes WHERE DATE(created_at) = '2026-09-29' ORDER BY created_at ASC;")

if not today_trades.empty:
    print(today_trades)
    if 'pnl' in today_trades.columns:
        print("\nTotal PnL:", today_trades['pnl'].sum())
    if 'outcome' in today_trades.columns:
        print("\nOutcomes Count:")
        print(today_trades['outcome'].value_counts())
    if 'is_win' in today_trades.columns:
        print("\nWins vs Losses:")
        print(today_trades['is_win'].value_counts())
else:
    print("No closed trades found for today.")

print("\n=== PENDING TRADES TODAY ===")
# Fetch columns
cols_p = run_query("SELECT column_name FROM information_schema.columns WHERE table_name = 'pending_trades';")
print("Columns in pending_trades:", cols_p['column_name'].tolist())

pending = run_query("SELECT * FROM pending_trades;")
if not pending.empty:
    print(pending)
else:
    print("No pending trades.")
