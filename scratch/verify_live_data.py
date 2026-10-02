import psycopg2
import pandas as pd
import warnings
warnings.filterwarnings('ignore')

DB_URL = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

print('=== LIVE DATABASE METRICS & FORENSIC VERIFICATION ===')
try:
    conn = psycopg2.connect(DB_URL)
    
    print('\n1. Calibration State (Current WinRates by Timeframe)')
    df_cal = pd.read_sql('''
        SELECT timeframe, source_name, COUNT(*) as pairs_count, 
               AVG(total_trades) as avg_trades, 
               AVG(ema_win_rate) * 100 as avg_winrate 
        FROM calibration_state 
        GROUP BY timeframe, source_name 
        ORDER BY timeframe, source_name
    ''', conn)
    print(df_cal.to_string(index=False))
    
    print('\n2. Trade Outcomes by Timeframe (Historical)')
    df_trades = pd.read_sql('''
        SELECT timeframe, 
               COUNT(*) as trades, 
               SUM(CASE WHEN was_win THEN 1 ELSE 0 END) * 100.0 / COUNT(*) as win_rate,
               SUM(CASE WHEN ml_score > 0 THEN 1 ELSE 0 END) as ml_driven
        FROM trade_outcomes
        GROUP BY timeframe
        ORDER BY timeframe
    ''', conn)
    print(df_trades.to_string(index=False))
    
    print('\n3. Data Quality (Subminute Ghost Pricing Check)')
    df_wicks = pd.read_sql('''
        SELECT interval, 
               AVG(high_price - GREATEST(open_price, close_price)) as avg_upper_wick,
               AVG(LEAST(open_price, close_price) - low_price) as avg_lower_wick,
               COUNT(*) as sample_size
        FROM subminute_candles
        WHERE open_time > NOW() - INTERVAL '1 hour'
        GROUP BY interval
        ORDER BY interval
    ''', conn)
    print(df_wicks.to_string(index=False))

    conn.close()
except Exception as e:
    print(f'Error connecting to DB: {e}')
