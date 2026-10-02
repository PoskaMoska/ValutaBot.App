import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('''SELECT column_name, data_type FROM information_schema.columns WHERE table_name='circuit_breaker_state' ''')
    print('--- circuit_breaker_state ---')
    for c in cur.fetchall():
        print(c)
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
