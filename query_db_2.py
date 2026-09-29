import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    print('--- OVERALL DATABASE STATS ---')
    cur.execute("SELECT COUNT(*) FROM trade_outcomes")
    print(f'Total Completed: {cur.fetchone()[0]}')
    
    cur.execute("SELECT COUNT(*) FROM pending_trades")
    print(f'Total Pending: {cur.fetchone()[0]}')

    cur.execute("SELECT created_at, features_json IS NOT NULL as has_json, direction FROM pending_trades ORDER BY created_at DESC LIMIT 10")
    print('\n[Latest 10 Pending Trades]')
    for row in cur.fetchall():
        print(row)

    cur.execute("SELECT created_at, features_json IS NOT NULL as has_json, direction, was_win FROM trade_outcomes ORDER BY created_at DESC LIMIT 10")
    print('\n[Latest 10 Completed Trades]')
    for row in cur.fetchall():
        print(row)

    conn.close()
except Exception as e:
    print('Error:', e)
