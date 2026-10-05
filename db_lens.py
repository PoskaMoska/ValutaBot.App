import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('SELECT direction, LENGTH(features_json), created_at FROM trade_outcomes ORDER BY created_at DESC LIMIT 6')
for r in cur.fetchall(): print(r)
