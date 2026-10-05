import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('DELETE FROM circuit_breaker_state WHERE id = 1;')
conn.commit()
cur.execute('SELECT COUNT(*) FROM trade_outcomes')
print('Circuit Breaker RESET. Outcomes so far:', cur.fetchone()[0])
