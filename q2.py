import psycopg2
import pandas as pd
import warnings
warnings.filterwarnings('ignore')

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    df = pd.read_sql_query('''
    SELECT table_name 
    FROM information_schema.tables 
    WHERE table_schema = 'public';
    ''', conn)
    print("Tables:")
    print(df['table_name'].tolist())

    df2 = pd.read_sql_query("SELECT count(*) FROM model_feedback;", conn)
    print("model_feedback count:", df2.iloc[0,0])
except Exception as e:
    print("Error:", e)
