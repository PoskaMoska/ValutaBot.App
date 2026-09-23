import os, psycopg2
conn = psycopg2.connect(os.getenv('DATABASE_URL'))
c = conn.cursor()
c.execute("SELECT COUNT(*) FROM historical_candles WHERE asset='EURUSD' AND open_time > '2026-09-12 08:37:00' AND open_time < '2026-09-20 00:00:00'")
print('Gap Candles:', c.fetchone()[0])
