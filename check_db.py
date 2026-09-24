import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()
    
    cur.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='public'")
    tables = [r[0] for r in cur.fetchall()]
    print('TABLES:', tables)
    
    if 'trade_outcomes' in tables:
        cur.execute("SELECT COUNT(*), SUM(CASE WHEN was_win THEN 1 ELSE 0 END) FROM trade_outcomes")
        row = cur.fetchone()
        tot = row[0] or 0
        wins = int(row[1] or 0)
        print(f'OUTCOMES: {tot} trades, WR: {wins/tot*100 if tot > 0 else 0:.2f}%')
        
    if 'historical_candles' in tables:
        cur.execute("SELECT asset, interval, COUNT(*), MIN(open_time), MAX(open_time) FROM historical_candles GROUP BY asset, interval")
        print(f'HISTORICAL_CANDLES:')
        for r in cur.fetchall():
            print("  ", r)
            
    if 'subminute_candles' in tables:
        cur.execute("SELECT asset, interval, COUNT(*), MIN(open_time), MAX(open_time) FROM subminute_candles GROUP BY asset, interval")
        print(f'SUBMINUTE_CANDLES:')
        for r in cur.fetchall():
            print("  ", r)
            
    conn.close()
except Exception as e:
    print('DB ERROR:', e)
