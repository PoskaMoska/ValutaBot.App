import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute("SELECT COUNT(*) FROM pending_trades")
    print(f'Total Pending Trades: {cur.fetchone()[0]}')

    cur.execute("SELECT MIN(created_at), MAX(created_at) FROM pending_trades")
    print(f'Pending Trades date range: {cur.fetchone()}')

    cur.execute("SELECT COUNT(*) FROM trade_outcomes WHERE features_json IS NOT NULL")
    print(f'Total Completed Trades with JSON: {cur.fetchone()[0]}')

    cur.execute("SELECT MIN(created_at), MAX(created_at) FROM trade_outcomes WHERE features_json IS NOT NULL")
    print(f'Completed Trades with JSON date range: {cur.fetchone()}')

    conn.close()
except Exception as e:
    print('Error:', e)
