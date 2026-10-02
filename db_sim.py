import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    cur.execute('SELECT id, asset, verify_at FROM pending_trades')
    for row in cur.fetchall():
        id_val, asset, verify_at = row
        print(f'Checking trade {id_val}, verify_at={verify_at}')
        
        cur.execute('SELECT open_time FROM subminute_candles LIMIT 1')
        print(f'subminute_candles open_time format: {cur.fetchone()[0]}')
        
        cur.execute('''
            SELECT close_price
            FROM subminute_candles
            WHERE asset = %s AND interval = %s
              AND open_time <= %s
            ORDER BY open_time DESC LIMIT 1
        ''', (asset, 's5', '2026-10-02T21:11:13.4265737Z'))
        res = cur.fetchone()
        print(f'Subminute res: {res}')
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
