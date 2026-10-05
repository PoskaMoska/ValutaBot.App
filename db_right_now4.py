import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('SELECT COUNT(*) FROM pending_trades')
print('pending:', cur.fetchone()[0])
