import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute("SELECT asset, timeframe, direction, created_at FROM pending_trades ORDER BY created_at DESC LIMIT 15")
    for row in cur.fetchall():
        print(row)

    conn.close()
except Exception as e:
    print('Error:', e)
