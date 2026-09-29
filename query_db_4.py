import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute("SELECT COUNT(*), MIN(created_at), MAX(created_at) FROM trade_outcomes WHERE created_at LIKE '2026-09-29%'")
    print(f'Completed Trades today: {cur.fetchone()}')

    cur.execute("SELECT COUNT(*), MIN(created_at), MAX(created_at) FROM pending_trades WHERE created_at LIKE '2026-09-29%'")
    print(f'Pending Trades today: {cur.fetchone()}')

    conn.close()
except Exception as e:
    print('Error:', e)
