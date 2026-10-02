import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('SELECT DISTINCT asset FROM historical_candles')
    for c in cur.fetchall():
        print(c)
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
