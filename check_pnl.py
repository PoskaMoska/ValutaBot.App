import psycopg2
import pandas as pd

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def get_samples():
    query = """
    SELECT id, asset, direction, entry_price, exit_price, was_win, pnl_bps
    FROM trade_outcomes
    WHERE DATE(created_at) = '2026-09-28'
    LIMIT 20
    """
    with psycopg2.connect(DB_URL) as conn:
        df = pd.read_sql_query(query, conn)
    print(df.to_string())

get_samples()
