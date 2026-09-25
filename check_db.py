import psycopg2
import sys

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()
    
    print('--- DB Tables ---')
    cur.execute("""
        SELECT table_name 
        FROM information_schema.tables 
        WHERE table_schema = 'public';
    """)
    tables = cur.fetchall()
    
    print('\n--- Row Counts ---')
    for t in tables:
        table_name = t[0]
        try:
            cur.execute(f'SELECT COUNT(*) FROM "{table_name}"')
            count = cur.fetchone()[0]
            print(f'{table_name}: {count}')
        except Exception as e:
            print(f'{table_name}: Error {e}')
            conn.rollback()
            
except Exception as e:
    print('Error:', e)
