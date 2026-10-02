import psycopg2
import sys

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    cur.execute("""
        SELECT table_name 
        FROM information_schema.tables 
        WHERE table_schema='public' AND table_type='BASE TABLE';
    """)
    tables = cur.fetchall()
    print('--- TABLES ---')
    for t in tables:
        cur.execute(f'SELECT count(*) FROM "{t[0]}";')
        count = cur.fetchone()[0]
        print(f'{t[0]}: {count} rows')
    
    # Also grab 5 recent predictions to see the ML / Confluence behavior
    if any(t[0] == 'predictions' for t in tables):
        print('\n--- RECENT PREDICTIONS ---')
        cur.execute("SELECT asset, timeframe, direction, probability, ta_score, smc_score, ml_score_raw, was_win FROM predictions ORDER BY timestamp DESC LIMIT 5;")
        for row in cur.fetchall():
            print(row)
            
    conn.close()
except Exception as e:
    print('Error:', e)
