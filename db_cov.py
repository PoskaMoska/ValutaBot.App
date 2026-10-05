import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('SELECT asset, interval, COUNT(*), MIN(open_time), MAX(open_time) FROM historical_candles GROUP BY 1,2 ORDER BY 1,2')
for r in cur.fetchall(): print('hist', r)
cur.execute('SELECT asset, interval, COUNT(*), MIN(open_time), MAX(open_time) FROM subminute_candles GROUP BY 1,2 ORDER BY 1,2')
for r in cur.fetchall(): print('sub', r)
cur.execute('SELECT column_name FROM information_schema.columns WHERE table_name=\'subminute_candles\'')
print([r[0] for r in cur.fetchall()])
