import psycopg2

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    
    print("--- EUR/USD s30 CANDLES ---")
    cur.execute("""
        SELECT open_time, open_price, close_price, volume 
        FROM subminute_candles 
        WHERE asset='EUR/USD' AND interval='s30' 
        ORDER BY open_time DESC LIMIT 15;
    """)
    for row in cur.fetchall():
        print(row)

    print("\n--- EUR/USD s5 CANDLES ---")
    cur.execute("""
        SELECT open_time, open_price, close_price, volume 
        FROM subminute_candles 
        WHERE asset='EUR/USD' AND interval='s5' 
        ORDER BY open_time DESC LIMIT 15;
    """)
    for row in cur.fetchall():
        print(row)
    
    conn.close()
except Exception as e:
    print('Error:', e)
