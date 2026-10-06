import psycopg2
import json
conn = psycopg2.connect('postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway')
cur = conn.cursor()
cur.execute('SELECT features_json FROM trade_outcomes ORDER BY created_at DESC LIMIT 1')
raw = cur.fetchone()[0]
parsed = json.loads(raw)
print('Keys in features_json:', list(parsed.keys()))
if 'Smc' in parsed: print('Smc keys:', list(parsed['Smc'].keys()))
if 'Ta' in parsed: print('Ta keys:', list(parsed['Ta'].keys()))
