import asyncio
import json
import psycopg2
import websockets

async def test_direct_ws():
    db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()
    cur.execute('SELECT state FROM po_collector_state WHERE id = 1;')
    state_raw = cur.fetchone()[0]
    state = json.loads(state_raw) if isinstance(state_raw, str) else state_raw
    auth_payload = state.get('authPayload')
    trading_url = state.get('tradingUrl')
    
    print('Connecting to:', trading_url)
    headers = {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36'
    }
    
    async with websockets.connect(trading_url, origin='https://pocketoption.com', additional_headers=headers) as ws:
        # Step 1: wait for server handshake (EIO 4 -> '0{"sid":...}')
        msg = await asyncio.wait_for(ws.recv(), timeout=10)
        print('Recv 1:', msg[:100])
        
        # Step 2: send Socket.IO connection packet '40'
        await ws.send('40')
        print('Sent: 40')
        
        # Step 2b: wait for server ACK '40{"sid":...}'
        ack = await asyncio.wait_for(ws.recv(), timeout=10)
        print('Recv ACK:', ack[:100] if isinstance(ack, str) else str(ack)[:100])
        
        # Step 3: send auth
        auth_msg = '42' + json.dumps(['auth', auth_payload])
        await ws.send(auth_msg)
        print('Sent auth packet')
        
        # Step 4: listen to incoming messages
        for i in range(40):
            msg = await asyncio.wait_for(ws.recv(), timeout=10)
            if isinstance(msg, bytes):
                try:
                    text_preview = msg[:80].decode('utf-8', errors='replace')
                except Exception:
                    text_preview = str(msg[:80])
                print(f'Msg {i} (BYTES): len={len(msg)}, preview={text_preview}')
                continue
            
            if msg == '2':
                await ws.send('3')
                print('Sent: 3 (pong)')
                continue
            
            # Print brief
            if len(msg) > 120:
                print(f'Msg {i}: {msg[:120]}... [len={len(msg)}]')
            else:
                print(f'Msg {i}: {msg}')
            
            # If authenticated or after asset list, subscribe to symbols
            if 'successauth' in msg or 'updateAssets' in msg or 'successAuth' in msg:
                print('>>> AUTH/ASSETS RECEIVED! Subscribing to OTC symbols...')
                symbols = ["EURUSD_otc", "GBPUSD_otc", "USDJPY_otc", "AUDUSD_otc", "USDCAD_otc", "USDCHF_otc"]
                for s in symbols:
                    await ws.send('42' + json.dumps(['changeSymbol', {'asset': s, 'period': 60}]))
                    await ws.send('42' + json.dumps(['subfor', s]))
                print('Sent subscriptions!')

if __name__ == '__main__':
    asyncio.run(test_direct_ws())
