import psycopg2

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    
    print('--- CALIBRATION STATE ---')
    cur.execute("SELECT asset, timeframe, module_name, win_rate, total_trades FROM calibration_state ORDER BY total_trades DESC LIMIT 20;")
    for row in cur.fetchall():
        print(f"{row[0]} {row[1]} | {row[2]}: WR={row[3]}% (Trades: {row[4]})")
        
    print('\n--- SUBMINUTE CANDLES (Recent 15 for EURUSD s15) ---')
    cur.execute("""
        SELECT asset, interval, open_time, open, high, low, close, volume 
        FROM subminute_candles 
        WHERE asset='EURUSD' AND interval='s15' 
        ORDER BY open_time DESC LIMIT 15;
    """)
    for row in cur.fetchall():
        print(row)
        
    print('\n--- SUBMINUTE CANDLES (Recent 15 for EURUSD s5) ---')
    cur.execute("""
        SELECT asset, interval, open_time, open, high, low, close, volume 
        FROM subminute_candles 
        WHERE asset='EURUSD' AND interval='s5' 
        ORDER BY open_time DESC LIMIT 15;
    """)
    for row in cur.fetchall():
        print(row)
        
    conn.close()
except Exception as e:
    print('Error:', e)
