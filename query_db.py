import psycopg2
import json

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    print('--- DATABASE STATS ---')
    
    # Total dataset rows
    cur.execute("SELECT COUNT(*) FROM trade_outcomes WHERE features_json IS NOT NULL")
    completed = cur.fetchone()[0]
    
    cur.execute("SELECT COUNT(*) FROM pending_trades WHERE features_json IS NOT NULL")
    pending = cur.fetchone()[0]
    
    print(f'Total Completed Trades with JSON: {completed}')
    print(f'Total Pending Trades with JSON: {pending}')
    print(f'TOTAL DATASET SIZE: {completed + pending}')
    print('----------------------')

    # Breakdown by direction (CALL, PUT, HOLD, SHADOW_CALL, SHADOW_PUT)
    cur.execute('''
        SELECT direction, COUNT(*) 
        FROM trade_outcomes 
        WHERE features_json IS NOT NULL 
        GROUP BY direction 
        ORDER BY direction
    ''')
    print('\n[Completed Trades by Direction]')
    for row in cur.fetchall():
        print(f'{row[0]}: {row[1]}')

    # Breakdown by Pair & Timeframe for real trades (not HOLD)
    cur.execute('''
        SELECT asset, timeframe, COUNT(*) as Total, 
               SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) as Wins,
               ROUND(SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) as WinRate
        FROM trade_outcomes 
        WHERE features_json IS NOT NULL AND direction NOT LIKE 'HOLD%'
        GROUP BY asset, timeframe
        ORDER BY Total DESC
    ''')
    print('\n[WinRate Breakdown by Pair and Timeframe (Real + Shadow)]')
    for row in cur.fetchall():
        print(f'{row[0]} ({row[1]}): {row[2]} trades | {row[3]} wins | {row[4]}% WR')

    # Breakdown for Real Trades vs Shadow Trades
    cur.execute('''
        SELECT 
            CASE WHEN direction LIKE 'SHADOW%' THEN 'SHADOW' ELSE 'REAL' END as Type,
            COUNT(*) as Total,
            ROUND(SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) as WinRate
        FROM trade_outcomes 
        WHERE features_json IS NOT NULL AND direction NOT LIKE 'HOLD%'
        GROUP BY CASE WHEN direction LIKE 'SHADOW%' THEN 'SHADOW' ELSE 'REAL' END
    ''')
    print('\n[Real vs Shadow WinRate]')
    for row in cur.fetchall():
        print(f'{row[0]}: {row[1]} trades | {row[2]}% WR')

    conn.close()
except Exception as e:
    print('Error:', e)
