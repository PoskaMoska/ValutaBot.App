import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute('''
        SELECT asset, timeframe, COUNT(*) as Total, 
               SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) as Wins,
               ROUND(SUM(CASE WHEN was_win = true THEN 1 ELSE 0 END) * 100.0 / NULLIF(COUNT(*), 0), 2) as WinRate
        FROM trade_outcomes 
        GROUP BY asset, timeframe
        ORDER BY Total DESC
    ''')
    for row in cur.fetchall():
        print(f'{row[0]} ({row[1]}): {row[2]} trades | {row[3]} wins | {row[4]}% WR')

    conn.close()
except Exception as e:
    print('Error:', e)
