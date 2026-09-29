import psycopg2

db_url = 'postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway'

try:
    conn = psycopg2.connect(db_url)
    cur = conn.cursor()

    cur.execute("DELETE FROM trade_outcomes WHERE features_json IS NULL")
    deleted_outcomes = cur.rowcount
    
    cur.execute("DELETE FROM pending_trades WHERE features_json IS NULL")
    deleted_pending = cur.rowcount
    
    conn.commit()
    print(f'Deleted {deleted_outcomes} outcomes and {deleted_pending} pending trades lacking JSON.')

    conn.close()
except Exception as e:
    print('Error:', e)
