import psycopg2

try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    # Check pending trades
    cur.execute('SELECT COUNT(*) FROM pending_trades')
    pending_count = cur.fetchone()[0]
    
    # Check completed trade outcomes
    cur.execute('SELECT COUNT(*) FROM trade_outcomes')
    outcomes_count = cur.fetchone()[0]
    
    print(f'Total Pending Trades: {pending_count}')
    print(f'Total Trade Outcomes: {outcomes_count}')
    
    # Get the latest 5 entries from trade_outcomes to see what was recorded
    if outcomes_count > 0:
        cur.execute('''
            SELECT direction, asset, timeframe, created_at 
            FROM trade_outcomes 
            ORDER BY created_at DESC 
            LIMIT 5
        ''')
        print('\n--- Latest 5 Trade Outcomes ---')
        for row in cur.fetchall():
            print(row)
            
    # Get the latest 5 entries from pending_trades
    if pending_count > 0:
        cur.execute('''
            SELECT direction, asset, timeframe, created_at 
            FROM pending_trades 
            ORDER BY created_at DESC 
            LIMIT 5
        ''')
        print('\n--- Latest 5 Pending Trades ---')
        for row in cur.fetchall():
            print(row)
            
    conn.close()
except Exception as e:
    print(f'Error: {e}')
