import psycopg2
import pandas as pd

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    df = pd.read_sql_query(\"\"\"
    SELECT table_name 
    FROM information_schema.tables 
    WHERE table_schema = 'public';
    \"\"\", conn)
    print("Tables:")
    print(df)
except Exception as e:
    print(e)
