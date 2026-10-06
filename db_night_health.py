import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('SELECT LENGTH(features_json) FROM trade_outcomes ORDER BY created_at DESC LIMIT 5')
for r in cur.fetchall(): print(f'Feature JSON length: {r[0]}')
