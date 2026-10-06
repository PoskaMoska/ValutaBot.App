import psycopg2
import pandas as pd
import warnings
warnings.filterwarnings('ignore')

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    df = pd.read_sql_query("SELECT * FROM trade_outcomes LIMIT 5;", conn)
    print("trade_outcomes columns:")
    print(df.columns.tolist())
    
    df_count = pd.read_sql_query("SELECT count(*) FROM trade_outcomes;", conn)
    print("trade_outcomes total rows:", df_count.iloc[0,0])

    df_acc = pd.read_sql_query("SELECT avg(CASE WHEN was_win THEN 1.0 ELSE 0.0 END) as win_rate, count(*) as n FROM trade_outcomes GROUP BY timeframe;", conn)
    print("Win rates by timeframe:")
    print(df_acc)
except Exception as e:
    print("Error:", e)
