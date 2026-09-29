import psycopg2

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query_raw(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        with conn.cursor() as cur:
            cur.execute(query, params)
            return cur.fetchall()

print("=== PENDING TRADES SUMMARY ===")
pending = run_query_raw("SELECT asset, timeframe, direction, COUNT(*) as count, MIN(created_at) as first_created, MAX(created_at) as last_created FROM pending_trades GROUP BY asset, timeframe, direction ORDER BY count DESC;")
for row in pending:
    print(f"Asset: {row[0]}, Timeframe: {row[1]}, Direction: {row[2]}, Count: {row[3]}")

print("\n=== COMPLETED TRADES TODAY ===")
today_trades = run_query_raw("SELECT asset, timeframe, direction, was_win, pnl_bps FROM trade_outcomes WHERE DATE(created_at) = '2026-09-29';")
print(f"Total completed trades today: {len(today_trades)}")
if len(today_trades) > 0:
    wins = sum(1 for t in today_trades if t[3])
    losses = len(today_trades) - wins
    total_pnl = sum(t[4] for t in today_trades if t[4] is not None)
    print(f"Wins: {wins}, Losses: {losses}, Total PnL: {total_pnl}")

print("\n=== COMPLETED TRADES OVERALL ===")
all_trades = run_query_raw("SELECT COUNT(*), SUM(CASE WHEN was_win THEN 1 ELSE 0 END), SUM(pnl_bps) FROM trade_outcomes;")
print(f"Total: {all_trades[0][0]}, Wins: {all_trades[0][1]}, Total PnL: {all_trades[0][2]}")

