import psycopg2

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query_raw(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        with conn.cursor() as cur:
            cur.execute(query, params)
            return cur.fetchall()

print("=== TRADE OUTCOMES BY DATE ===")
outcomes = run_query_raw("SELECT DATE(created_at), COUNT(*) FROM trade_outcomes GROUP BY DATE(created_at) ORDER BY DATE(created_at) DESC LIMIT 5;")
for row in outcomes:
    print(f"Date: {row[0]}, Count: {row[1]}")

print("\n=== PENDING TRADES BY DATE ===")
pending = run_query_raw("SELECT DATE(created_at), COUNT(*) FROM pending_trades GROUP BY DATE(created_at) ORDER BY DATE(created_at) DESC LIMIT 5;")
for row in pending:
    print(f"Date: {row[0]}, Count: {row[1]}")

print("\n=== SIGNAL VOTES BY DATE ===")
votes = run_query_raw("SELECT DATE(created_at), COUNT(*) FROM signal_votes GROUP BY DATE(created_at) ORDER BY DATE(created_at) DESC LIMIT 5;")
for row in votes:
    print(f"Date: {row[0]}, Count: {row[1]}")
