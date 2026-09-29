import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute('''
        SELECT open_time, close_price 
        FROM subminute_candles 
        WHERE asset = 'EURUSD' AND interval = 's5' 
          AND open_time <= '2026-09-29T18:14:46.5295032Z' 
        ORDER BY open_time DESC LIMIT 1
    ''')
    row = cur.fetchone()
    print(f'Query returned: {row}')

    conn.close()
except Exception as e:
    print('Error:', e)
