import psycopg2

try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    conn.autocommit = True
    cur = conn.cursor()
    
    # 1. Update trade_outcomes
    cur.execute('''
        ALTER TABLE trade_outcomes 
        ALTER COLUMN created_at TYPE TIMESTAMPTZ USING created_at::timestamptz,
        ALTER COLUMN verified_at TYPE TIMESTAMPTZ USING verified_at::timestamptz;
    ''')
    print('trade_outcomes updated successfully.')
    
    # 2. Update circuit_breaker_state
    cur.execute('''
        ALTER TABLE circuit_breaker_state 
        ALTER COLUMN halted_until TYPE TIMESTAMPTZ USING halted_until::timestamptz,
        ALTER COLUMN created_at TYPE TIMESTAMPTZ USING created_at::timestamptz;
    ''')
    print('circuit_breaker_state updated successfully.')

    # Clean up EUR/USD to EURUSD in trade_outcomes for the existing 4 rows
    cur.execute('''
        UPDATE trade_outcomes 
        SET asset = REPLACE(asset, '/', '') 
        WHERE asset LIKE '%/%'
    ''')
    print('Existing assets sanitized in DB.')

    conn.close()
except Exception as e:
    print(f'Error: {e}')
