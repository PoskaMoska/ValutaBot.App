import psycopg2
import pandas as pd
import numpy as np

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    df = pd.read_sql_query(\"\"\"
    SELECT 
        was_win, direction, ta_score, of_score, smc_score, ml_prob, 
        smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio, 
        dynamic_horizon, atr_at_signal, adx_at_signal, rsi_at_signal
    FROM trade_outcomes
    WHERE was_win IS NOT NULL
    \"\"\", conn)
    
    df['was_win'] = df['was_win'].astype(float)
    df['direction'] = df['direction'].apply(lambda x: 1.0 if str(x).upper() == 'BUY' else -1.0)
    
    for col in df.columns:
        df[col] = pd.to_numeric(df[col], errors='coerce').fillna(0)
        
    print("Correlation with was_win:")
    corr = df.corr()['was_win'].sort_values(ascending=False)
    print(corr)
    
except Exception as e:
    print("Error:", e)
