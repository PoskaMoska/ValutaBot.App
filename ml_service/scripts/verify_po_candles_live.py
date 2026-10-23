import asyncio
import json
import psycopg2
import websockets
import pandas as pd
import numpy as np

async def verify_po_vs_bot():
    db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()
    cur.execute('SELECT state FROM po_collector_state WHERE id = 1;')
    state = json.loads(cur.fetchone()[0])
    auth_payload = state.get('authPayload')
    trading_url = state.get('tradingUrl')

    print('[1/4] Connecting to Pocket Option WebSocket...')
    headers = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/131.0.0.0 Safari/537.36'}
    
    po_histories = {}
    
    async with websockets.connect(trading_url, origin='https://pocketoption.com', additional_headers=headers) as ws:
        h1 = await ws.recv()
        print('Handshake:', h1[:60])
        await ws.send('40')
        ack = await ws.recv()
        print('ACK:', ack[:60] if isinstance(ack, str) else len(ack))
        auth_str = '42' + json.dumps(['auth', auth_payload])
        await ws.send(auth_str)
        print('Auth sent.')
        
        pending_event = None
        for i in range(80):
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=12)
            except asyncio.TimeoutError:
                print(f'Timeout waiting for msg {i}')
                break
            if isinstance(msg, str):
                if msg == '2':
                    await ws.send('3')
                    continue
                if msg.startswith('451-'):
                    ev_data = json.loads(msg[4:])
                    pending_event = ev_data[0]
                else:
                    pending_event = None

                if pending_event in ('updateAssets', 'successauth') or 'updateAssets' in msg or 'successauth' in msg:
                    for s in ['EURUSD_otc', 'GBPUSD_otc', 'USDJPY_otc']:
                        await ws.send('42' + json.dumps(['changeSymbol', {'asset': s, 'period': 60}]))
                        await ws.send('42' + json.dumps(['subfor', s]))
            elif isinstance(msg, bytes) and pending_event:
                if pending_event == 'updateHistoryNewFast':
                    try:
                        data = json.loads(msg.decode('utf-8'))
                        asset = data['asset']
                        if asset not in po_histories and 'history' in data:
                            po_histories[asset] = data['history']
                            print(f"   -> Received history for {asset}: {len(data['history'])} ticks")
                    except Exception as e:
                        pass
                pending_event = None
            if len(po_histories) >= 3:
                break
                
    print('\n[2/4] Processing server ticks and building candles...')
    for asset, ticks in po_histories.items():
        base = asset.replace('_otc', '').replace('_OTC', '')
        bot_asset = base + '_PO'
        
        df_ticks = pd.DataFrame(ticks, columns=['srv_ts', 'price'])
        # Convert server time to UTC seconds by subtracting 7200
        df_ticks['utc_sec'] = df_ticks['srv_ts'] - 7200.0
        
        for interval_sec in [5, 10, 15, 30, 60]:
            int_name = '1min' if interval_sec == 60 else f's{interval_sec}'
            df_ticks['candle_ts'] = (df_ticks['utc_sec'] // interval_sec) * interval_sec
            
            # Group into candles
            grouped = df_ticks.groupby('candle_ts')
            po_candles = grouped['price'].agg(
                open_po='first',
                high_po='max',
                low_po='min',
                close_po='last',
                ticks_po='count'
            ).reset_index()
            
            # Drop the earliest and latest partial candles
            if len(po_candles) > 4:
                po_candles = po_candles.iloc[2:-2].copy()
                
            t_min = int(po_candles['candle_ts'].min())
            t_max = int(po_candles['candle_ts'].max())
            
            dt_min = pd.to_datetime(t_min, unit='s', utc=True).isoformat()
            dt_max = pd.to_datetime(t_max, unit='s', utc=True).isoformat()
            
            # Query Railway DB for bot candles
            cur.execute(f"""
                SELECT open_time, open_price, high_price, low_price, close_price, volume
                FROM subminute_candles
                WHERE asset = '{bot_asset}' AND interval = '{int_name}'
                  AND open_time >= '{dt_min}' AND open_time <= '{dt_max}'
                ORDER BY open_time ASC;
            """)
            db_rows = cur.fetchall()
            if not db_rows:
                print(f"   [!] No DB rows for {bot_asset} {int_name} in range {dt_min}..{dt_max}")
                continue
                
            db_df = pd.DataFrame(db_rows, columns=['open_time', 'open_bot', 'high_bot', 'low_bot', 'close_bot', 'vol_bot'])
            db_df['candle_ts'] = pd.to_datetime(db_df['open_time'], utc=True).astype('int64') // 10**9
            
            merged = pd.merge(po_candles, db_df, on='candle_ts')
            if merged.empty:
                print(f"   [!] Zero matching timestamps for {bot_asset} {int_name}")
                continue
                
            diff_close = np.abs(merged['close_po'] - merged['close_bot'])
            diff_open = np.abs(merged['open_po'] - merged['open_bot'])
            diff_high = np.abs(merged['high_po'] - merged['high_bot'])
            diff_low = np.abs(merged['low_po'] - merged['low_bot'])
            
            exact_close = (diff_close < 1e-6).mean() * 100
            exact_open = (diff_open < 1e-6).mean() * 100
            exact_all = ((diff_close < 1e-6) & (diff_open < 1e-6) & (diff_high < 1e-6) & (diff_low < 1e-6)).mean() * 100
            
            print(f"\n======================================================")
            print(f"ASSET: {bot_asset} | INTERVAL: {int_name}")
            print(f"Matching candles:   {len(merged)} (out of {len(po_candles)} PO server candles)")
            print(f"Exact Open match:   {exact_open:.2f}%")
            print(f"Exact Close match:  {exact_close:.2f}%")
            print(f"Exact OHLC (100%):  {exact_all:.2f}%")
            print(f"Max Close diff:     {diff_close.max():.6f}")
            print(f"Mean Close diff:    {diff_close.mean():.6f}")
            
            if exact_all < 100:
                mismatch = merged[((diff_close >= 1e-6) | (diff_open >= 1e-6) | (diff_high >= 1e-6) | (diff_low >= 1e-6))].head(5)
                print('\nSample mismatches:')
                for _, row in mismatch.iterrows():
                    ts_str = pd.to_datetime(row['candle_ts'], unit='s', utc=True).strftime('%H:%M:%S')
                    print(f"  {ts_str} -> PO:  O={row['open_po']} H={row['high_po']} L={row['low_po']} C={row['close_po']} (ticks={row['ticks_po']})")
                    print(f"           BOT: O={row['open_bot']} H={row['high_bot']} L={row['low_bot']} C={row['close_bot']} (vol={row['vol_bot']})")

    conn.close()

if __name__ == '__main__':
    asyncio.run(verify_po_vs_bot())
