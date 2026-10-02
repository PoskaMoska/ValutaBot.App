import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('''SELECT column_name, data_type FROM information_schema.columns WHERE table_name='trade_outcomes' ''')
    cols = cur.fetchall()
    print('--- COLUMNS trade_outcomes ---')
    for c in cols:
        print(c)
        
    cur.execute('SELECT COUNT(*) FROM historical_candles')
    print(f'Total historical_candles: {cur.fetchone()[0]}')
    
    conn.close()
except Exception as e:
    print(f'Error: {e}')
