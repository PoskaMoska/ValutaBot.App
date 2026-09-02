
import psycopg2
conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
with psycopg2.connect(conn_str) as conn:
    with conn.cursor() as cur:
        cur.execute('''SELECT column_name, data_type FROM information_schema.columns WHERE table_name = ''historical_candles'';''')
        print('=== SCHEMA ===')
        for r in cur.fetchall():
            print(f'{r[0]}: {r[1]}')
        cur.execute('''SELECT symbol, COUNT(*) FROM historical_candles GROUP BY symbol;''')
        print('\n=== SYMBOLS ===')
        for r in cur.fetchall():
            print(f'{r[0]}: {r[1]}')

