"""Analyze trade_outcomes to check quality/corruption from broken ML."""
import sys
sys.stdout.reconfigure(encoding='utf-8')
import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
conn = psycopg2.connect(db_url)
cur = conn.cursor()

# 1. Win rate overall
cur.execute("SELECT COUNT(*), SUM(CASE WHEN was_win THEN 1 ELSE 0 END) FROM trade_outcomes")
total, wins = cur.fetchone()
print(f"Total trades: {total}")
print(f"Win rate: {wins}/{total} = {wins/total*100:.1f}%")

# 2. Win rate by direction
cur.execute("""
    SELECT direction, COUNT(*), SUM(CASE WHEN was_win THEN 1 ELSE 0 END)
    FROM trade_outcomes GROUP BY direction
""")
print("\nBy direction:")
for row in cur.fetchall():
    d, cnt, w = row
    print(f"  {d}: {w}/{cnt} = {w/cnt*100:.1f}% win")

# 3. ML score distribution — was it near-zero (random) the whole time?
cur.execute("""
    SELECT 
        AVG(ml_score), STDDEV(ml_score),
        MIN(ml_score), MAX(ml_score),
        AVG(ABS(ml_score)),
        SUM(CASE WHEN ABS(ml_score) < 0.05 THEN 1 ELSE 0 END) as near_zero_count
    FROM trade_outcomes WHERE ml_score IS NOT NULL
""")
row = cur.fetchone()
print(f"\nML Score stats:")
print(f"  avg={row[0]:.4f}  std={row[1]:.4f}  min={row[2]:.4f}  max={row[3]:.4f}")
print(f"  avg|score|={row[4]:.4f}  near-zero (<0.05): {row[5]} ({row[5]/total*100:.1f}%)")

# 4. Date range
cur.execute("SELECT MIN(created_at), MAX(created_at) FROM trade_outcomes")
mn, mx = cur.fetchone()
print(f"\nDate range: {mn} -> {mx}")

# 5. By asset
cur.execute("""
    SELECT asset, COUNT(*), SUM(CASE WHEN was_win THEN 1 ELSE 0 END)
    FROM trade_outcomes GROUP BY asset ORDER BY COUNT(*) DESC LIMIT 10
""")
print("\nBy asset:")
for row in cur.fetchall():
    a, cnt, w = row
    print(f"  {a}: {w}/{cnt} = {w/cnt*100:.1f}%")

# 6. signal_votes win rate per source
cur.execute("""
    SELECT source_name, total_votes, win_votes, 
           ROUND(win_votes::numeric/NULLIF(total_votes,0)*100, 1) as wr
    FROM signal_votes ORDER BY total_votes DESC
""")
print("\nSignal votes (AutoCalib sources):")
for row in cur.fetchall():
    print(f"  {row[0]}: {row[2]}/{row[1]} = {row[3]}%")

conn.close()
