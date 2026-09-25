import os
import glob
import pandas as pd
import psycopg2
from scipy.stats import pearsonr
import warnings
warnings.filterwarnings('ignore')

def process_file(po_file, symbol='EURUSD', interval='1m'):
    print(f"\n--- Analyzing {po_file} ---")
    try:
        if po_file.endswith('.xlsx'):
            po_df = pd.read_excel(po_file)
        else:
            po_df = pd.read_csv(po_file, delimiter=';')
            if len(po_df.columns) < 2:
                po_df = pd.read_csv(po_file, delimiter=',')
    except Exception as e:
        print('Error reading file:', e)
        return
        
    time_col = None
    close_col = None
    
    if len(po_df.columns) >= 8:
        sample_time = str(po_df.iloc[0, 4])
        sample_price = str(po_df.iloc[0, 7])
        if '202' in sample_time and '.' in sample_price:
            time_col = po_df.columns[4]
            close_col = po_df.columns[6]
            
    if not time_col or not close_col:
        time_col = next((c for c in po_df.columns if 'time' in c.lower() or 'date' in c.lower() or 'время' in c.lower()), None)
        close_col = next((c for c in po_df.columns if 'close' in c.lower() or 'bid' in c.lower() or 'price' in c.lower() or 'цена' in c.lower()), None)
    
    if not time_col or not close_col:
        print(f'Could not auto-detect columns for {po_file}. Skipping.')
        return

    po_df['timestamp'] = pd.to_datetime(po_df[time_col])
    if interval == '1m':
        po_df['timestamp'] = po_df['timestamp'].dt.floor('Min')
        
    po_df = po_df.sort_values('timestamp').set_index('timestamp')
    po_close = po_df[close_col].astype(float).groupby('timestamp').mean()

    db_url = os.getenv('DATABASE_URL', 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    conn = psycopg2.connect(db_url)
    
    query = f"""
        SELECT open_time, close 
        FROM historical_candles 
        WHERE asset = '{symbol}' AND interval = '{interval}'
        ORDER BY open_time ASC
    """
    td_df = pd.read_sql_query(query, conn)
    conn.close()

    if td_df.empty:
        print(f'No TwelveData candles found in DB for {symbol} {interval}.')
        return

    td_df['timestamp'] = pd.to_datetime(td_df['open_time'], format='ISO8601', utc=True)
    po_close.index = po_close.index.tz_localize(None)
    td_df['timestamp'] = td_df['timestamp'].dt.tz_localize(None)
    
    td_df = td_df.set_index('timestamp')
    td_close = td_df['close'].astype(float)

    aligned = pd.merge(po_close, td_close, left_index=True, right_index=True, suffixes=('_po', '_td'))
    
    if aligned.empty:
        print('No matching timestamps found! PO timestamps might not overlap with TwelveData.')
        return
        
    corr, _ = pearsonr(aligned.iloc[:, 0], aligned.iloc[:, 1])
    mae = (aligned.iloc[:, 0] - aligned.iloc[:, 1]).abs().mean()
    max_err = (aligned.iloc[:, 0] - aligned.iloc[:, 1]).abs().max()

    print(f"Found {len(aligned)} matching points for {symbol} {interval}.")
    print(f"Pearson Correlation: {corr*100:.2f}%")
    print(f"Mean Absolute Error: {mae:.5f}")
    if corr > 0.95:
        print("[OK] EXCELLENT MATCH! PO and TwelveData are identical.")
    elif corr > 0.80:
        print("[WARN] ACCEPTABLE MATCH. Minor discrepancies.")
    else:
        print("[FAIL] POOR MATCH. Do not trade this live on PO with TwelveData models!")

def main():
    files = glob.glob('*.xlsx') + glob.glob('*.csv')
    po_files = [f for f in files if ('1' in f or 'П' in f) and 'compare' not in f]
    
    if not po_files:
        print("No Excel/CSV files found in the folder.")
        return
        
    print(f"Auto-detected {len(po_files)} files. Running analysis...")
    for f in po_files[:3]:  # Test first 3 files to save time
        process_file(f, 'EURUSD', '1m')

if __name__ == '__main__':
    main()
