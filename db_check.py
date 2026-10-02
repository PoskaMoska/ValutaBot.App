import sys
import subprocess

def install_and_run():
    try:
        import psycopg2
    except ImportError:
        subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'psycopg2-binary'])
        import psycopg2
        
    try:
        conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
        cur = conn.cursor()
        
        cur.execute('''SELECT table_name FROM information_schema.tables WHERE table_schema='public' ''')
        tables = cur.fetchall()
        print('--- TABLES ---')
        for t in tables:
            print(t[0])
            
        if ('trade_outcomes',) in tables:
            cur.execute('SELECT COUNT(*) FROM trade_outcomes')
            print(f'Total outcomes: {cur.fetchone()[0]}')
            
            cur.execute('''
                SELECT direction, COUNT(*), SUM(CASE WHEN was_win THEN 1 ELSE 0 END) as wins 
                FROM trade_outcomes 
                GROUP BY direction
            ''')
            print('--- DISTRIBUTION ---')
            for row in cur.fetchall():
                print(row)
                
            cur.execute('SELECT MIN(entry_time), MAX(entry_time) FROM trade_outcomes')
            dates = cur.fetchone()
            print(f'Date range: {dates[0]} to {dates[1]}')
            
            cur.execute('SELECT ml_features_json IS NOT NULL FROM trade_outcomes LIMIT 1')
            row = cur.fetchone()
            if row:
                print(f'Has ML Features JSON: {row[0]}')
            else:
                print('Table trade_outcomes is empty')

            # Let's see some actual data from trade_outcomes to check for anomalies
            cur.execute('SELECT ml_features_json FROM trade_outcomes WHERE ml_features_json IS NOT NULL LIMIT 1')
            feat_row = cur.fetchone()
            if feat_row:
                feat = str(feat_row[0])
                print(f'Sample features length: {len(feat)} chars')
                print(f'Sample features snippet: {feat[:200]}...')
        
        conn.close()
    except Exception as e:
        print(f'Error: {e}')

install_and_run()
