import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute("SELECT COUNT(*) FROM subminute_candles WHERE interval = 'm1'")
    print(f'm1 candles in subminute: {cur.fetchone()[0]}')

    conn.close()
except Exception as e:
    print('Error:', e)
