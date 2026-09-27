import psycopg2, os

conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()

cur.execute("SELECT COUNT(*) FROM trade_outcomes")
print("Total trade_outcomes:", cur.fetchone()[0])

cur.execute("""
    SELECT asset, timeframe, COUNT(*),
           ROUND(AVG(CASE WHEN was_win = true THEN 1.0 ELSE 0.0 END)*100, 1)
    FROM trade_outcomes
    WHERE created_at::timestamptz >= NOW() - INTERVAL '12 hours'
    GROUP BY asset, timeframe ORDER BY COUNT(*) DESC
""")
rows = cur.fetchall()
print("\nSdelki za noch:")
if rows:
    for r in rows:
        print(f"  {r[0]} {r[1]}: {r[2]} sdelok | WR: {r[3]}%")
else:
    print("  Net")

conn.close()

lf = r'C:\Users\bural\source\repos\ValutaBot.App\retrain_v3.log'
if os.path.exists(lf):
    lines = open(lf).readlines()
    print("\n=== retrain_v3.log (last 20 lines) ===")
    for l in lines[-20:]:
        print(l.rstrip())
else:
    print("\nretrain_v3.log not found")
