import psycopg2
try:
    conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
    cur = conn.cursor()
    
    insert_sql = '''
        INSERT INTO trade_outcomes (
            id, asset, timeframe, direction, entry_price, exit_price, pnl_bps, was_win,
            created_at, verified_at, probability, ta_score, of_score, smc_score,
            ml_prob, ml_score, smc_bos_dir, smc_has_ob, smc_has_fvg, of_delta_ratio,
            of_state, dynamic_horizon, features_json
        ) VALUES (
            %s, %s, %s, %s, %s, %s, %s, %s,
            %s, %s, %s, %s, %s, %s,
            %s, %s, %s, %s, %s, %s,
            %s, %s, %s
        )
    '''
    
    cur.execute(insert_sql, (
        'test_id', 'EURUSD', 'm1', 'BUY', 1.0, 1.1, 100.0, True,
        '2026-10-02T21:11:13.4265737Z', '2026-10-02T21:11:13.4265737Z', 80, 0.5, 0.5, 0.5,
        0.5, 0.5, 'NONE', False, False, 1.0,
        'NORMAL', 10, '{}'
    ))
    
    # Don't commit, just see if it errors
    conn.rollback()
    print('INSERT SUCCEEDED!')
        
    conn.close()
except Exception as e:
    print(f'Error: {e}')
