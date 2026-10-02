import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('SELECT open_time FROM historical_candles LIMIT 5')
    for c in cur.fetchall():
        print(c)
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
