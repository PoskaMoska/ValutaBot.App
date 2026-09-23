import sqlite3
import pandas as pd
from datetime import datetime
import os
import psycopg2

def verify_data():
    db_url = os.getenv("DATABASE_URL")
    
    if db_url:
        print(f"🔍 Проверка базы данных: PostgreSQL\n" + "="*40)
        try:
            conn = psycopg2.connect(db_url)
            df_counts = pd.read_sql_query("SELECT asset as Asset, interval as Interval, COUNT(*) as Count, MIN(open_time) as Oldest, MAX(open_time) as Newest FROM historical_candles GROUP BY asset, interval", conn)
        except Exception as e:
            print(f"❌ Ошибка PostgreSQL: {e}")
            return
    else:
        db_path = "/app/data/models/ValutaTicks.db"
        print(f"🔍 Проверка базы данных: SQLite ({db_path})\n" + "="*40)
        try:
            conn = sqlite3.connect(db_path)
            df_counts = pd.read_sql_query("SELECT Asset, Interval, COUNT(*) as Count, MIN(OpenTime) as Oldest, MAX(OpenTime) as Newest FROM HistoricalCandles GROUP BY Asset, Interval", conn)
        except Exception as e:
            print(f"❌ Ошибка SQLite: {e}")
            return

    if df_counts.empty:
        print("❌ База данных пуста!")
        return

    print(f"✅ Найдено {len(df_counts)} таблиц(пар).")
    
    for _, row in df_counts.iterrows():
        # Postgres returns lowercased column names for unquoted aliases
        asset = row.get('Asset', row.get('asset'))
        interval = row.get('Interval', row.get('interval'))
        count = row.get('Count', row.get('count'))
        oldest = row.get('Oldest', row.get('oldest'))
        newest = row.get('Newest', row.get('newest'))
        
        if str(oldest).isdigit():
            oldest = datetime.utcfromtimestamp(int(oldest)).strftime('%Y-%m-%d %H:%M:%S')
        if str(newest).isdigit():
            newest = datetime.utcfromtimestamp(int(newest)).strftime('%Y-%m-%d %H:%M:%S')
            
        print(f"📊 {asset} [{interval}]: {count} свечей | С {oldest} по {newest}")
            
    conn.close()
    print("\n" + "="*40 + "\n✅ Диагностика завершена.")

if __name__ == "__main__":
    verify_data()
