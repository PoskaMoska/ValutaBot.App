import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('SELECT COUNT(*) FROM pending_trades')
    print(f'Pending trades count: {cur.fetchone()[0]}')
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
