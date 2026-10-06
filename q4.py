import psycopg2
import pandas as pd
import json

conn_str = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
try:
    conn = psycopg2.connect(conn_str)
    df = pd.read_sql_query("SELECT features_json, ml_prob, was_win, direction FROM trade_outcomes WHERE features_json IS NOT NULL LIMIT 1;", conn)
    if not df.empty and df['features_json'].iloc[0]:
        feat_str = df['features_json'].iloc[0]
        try:
            feats = json.loads(feat_str)
            print("Number of features in DB JSON:", len(feats.keys()))
            print("Sample features:")
            for k in list(feats.keys())[:15]:
                print(f"  {k}: {feats[k]}")
        except:
            print("Could not parse json")
    else:
        print("No features_json")
except Exception as e:
    print("Error:", e)
