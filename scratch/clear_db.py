import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('DELETE FROM calibration_state;')
conn.commit()
print('Cleared calibration_state table!')
