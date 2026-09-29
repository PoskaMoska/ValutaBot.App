import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

query = """
SELECT direction, COUNT(*), AVG(ml_prob) as avg_prob, AVG(ml_score) as avg_score, AVG(ta_score) as ta_score
FROM trade_outcomes
WHERE DATE(created_at) = '2026-09-28'
GROUP BY direction
"""
with psycopg2.connect(DB_URL) as conn:
    df = pd.read_sql_query(query, conn)
print(df.to_string())

