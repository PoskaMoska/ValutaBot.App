import psycopg2

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query_raw(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        with conn.cursor() as cur:
            cur.execute(query, params)
            return cur.fetchall()

print("=== SIGNAL VOTES TODAY ===")
votes_today = run_query_raw("SELECT COUNT(*) FROM signal_votes WHERE DATE(created_at) = '2026-09-29';")
print(f"Signal votes today: {votes_today[0][0]}")

print("=== NEUTRAL SIGNALS TODAY ===")
neutral_today = run_query_raw("SELECT COUNT(*) FROM neutral_signals WHERE DATE(created_at) = '2026-09-29';")
print(f"Neutral signals today: {neutral_today[0][0]}")

print("=== LATEST TRADE OUTCOMES DATE ===")
latest_trade = run_query_raw("SELECT MAX(created_at) FROM trade_outcomes;")
print(f"Latest trade outcome: {latest_trade[0][0]}")

print("=== ALL PENDING TRADES DATE RANGE ===")
pending_range = run_query_raw("SELECT MIN(created_at), MAX(created_at) FROM pending_trades;")
print(f"Pending trades from {pending_range[0][0]} to {pending_range[0][1]}")

