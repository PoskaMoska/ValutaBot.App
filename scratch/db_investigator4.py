import psycopg2

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    
    cur.execute("""
        SELECT column_name, data_type 
        FROM information_schema.columns 
        WHERE table_name = 'subminute_candles';
    """)
    print("subminute_candles schema:", cur.fetchall())

    cur.execute("""
        SELECT source_name, asset, timeframe, ema_win_rate, total_trades 
        FROM calibration_state 
        ORDER BY source_name, total_trades DESC LIMIT 20;
    """)
    print("\nCalibration State (Win Rates):")
    for row in cur.fetchall():
        print(f"{row[0]:<15} | {row[1]:<8} {row[2]:<4} | WR: {row[3]:.4f} (Trades: {row[4]})")
        
    cur.execute("""
        SELECT id, asset, timeframe, direction, was_win, probability, ta_score, smc_score, ml_prob 
        FROM trade_outcomes 
        ORDER BY created_at DESC LIMIT 5;
    """)
    print("\nRecent Outcomes:")
    for row in cur.fetchall():
        print(row)
        
    conn.close()
except Exception as e:
    print('Error:', e)
