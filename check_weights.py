import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

with psycopg2.connect(DB_URL) as conn:
    print("=== META LEARNER WEIGHTS ===")
    try:
        df = pd.read_sql_query("SELECT * FROM meta_learner_weights ORDER BY updated_at DESC LIMIT 1;", conn)
        print(df.to_string())
    except Exception as e:
        print(e)
    
    print("\n=== CIRCUIT BREAKER STATE ===")
    try:
        df2 = pd.read_sql_query("SELECT * FROM circuit_breaker_state ORDER BY updated_at DESC LIMIT 1;", conn)
        print(df2.to_string())
    except Exception as e:
        print(e)

