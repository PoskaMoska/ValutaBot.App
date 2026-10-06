import psycopg2
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()

cur.execute('SELECT COUNT(*) FROM trade_outcomes')
outcomes_count = cur.fetchone()[0]

cur.execute('SELECT COUNT(*) FROM pending_trades')
pending_count = cur.fetchone()[0]

cur.execute('SELECT direction, COUNT(*) FROM trade_outcomes GROUP BY direction')
directions = cur.fetchall()

cur.execute('SELECT MAX(created_at), MIN(created_at) FROM trade_outcomes')
dates = cur.fetchone()

print(f'Outcomes total: {outcomes_count}')
print(f'Pending total: {pending_count}')
print(f'By direction: {directions}')
print(f'Date range: {dates[1]} to {dates[0]}')
