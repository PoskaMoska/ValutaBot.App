import psycopg2

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    
    cur.execute("""
        SELECT column_name, data_type 
        FROM information_schema.columns 
        WHERE table_name = 'calibration_state';
    """)
    print("calibration_state schema:", cur.fetchall())
    
    cur.execute("""
        SELECT column_name, data_type 
        FROM information_schema.columns 
        WHERE table_name = 'trade_outcomes';
    """)
    print("trade_outcomes schema:", cur.fetchall())

    cur.execute("""
        SELECT column_name, data_type 
        FROM information_schema.columns 
        WHERE table_name = 'signal_votes';
    """)
    print("signal_votes schema:", cur.fetchall())

    cur.execute("""
        SELECT asset, interval, open_time, open, high, low, close, volume 
        FROM subminute_candles 
        WHERE asset='EURUSD' AND interval='s15' 
        ORDER BY open_time DESC LIMIT 10;
    """)
    print("\nRecent s15:")
    for row in cur.fetchall():
        print(row)
    
    conn.close()
except Exception as e:
    print('Error:', e)
