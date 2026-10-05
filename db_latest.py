import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('SELECT COUNT(*), MAX(created_at) FROM trade_outcomes')
print('outcomes:', cur.fetchone())
cur.execute('SELECT direction, features_json FROM trade_outcomes ORDER BY created_at DESC LIMIT 5')
print('latest:', cur.fetchall())
