import psycopg2
import pandas as pd
from tabulate import tabulate
import datetime

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

def run_query(query, params=None):
    with psycopg2.connect(DB_URL) as conn:
        df = pd.read_sql_query(query, conn, params=params)
        return df

print("--- TRADE OUTCOMES STRUCTURE ---")
schema = run_query("SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'trade_outcomes';")
print(tabulate(schema, headers='keys', tablefmt='psql'))

print("\n--- SAMPLE TRADE OUTCOMES ---")
trades = run_query("SELECT * FROM trade_outcomes ORDER BY created_at DESC LIMIT 5;")
print(tabulate(trades, headers='keys', tablefmt='psql'))

print("\n--- PENDING TRADES STRUCTURE ---")
schema_pending = run_query("SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'pending_trades';")
print(tabulate(schema_pending, headers='keys', tablefmt='psql'))

print("\n--- SAMPLE PENDING TRADES ---")
pending = run_query("SELECT * FROM pending_trades ORDER BY created_at DESC LIMIT 5;")
print(tabulate(pending, headers='keys', tablefmt='psql'))

