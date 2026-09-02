
import psycopg2
conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
with psycopg2.connect(conn_str) as conn:
    with conn.cursor() as cur:
        cur.execute('''SELECT asset, interval, open_time, open, high, low, close, volume FROM historical_candles LIMIT 1;''')
        row = cur.fetchone()
        print('=== FIRST ROW ===')
        print(row)
        
        cur.execute('''SELECT DISTINCT asset FROM historical_candles;''')
        print('\n=== DISTINCT ASSETS ===')
        for r in cur.fetchall():
            print(r[0])

