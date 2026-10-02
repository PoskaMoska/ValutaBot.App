import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('SELECT asset, timeframe, direction, created_at FROM trade_outcomes')
    print('--- OUTCOMES ---')
    for row in cur.fetchall():
        print(row)
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
