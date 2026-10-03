import psycopg2
import json
import datetime
from collections import defaultdict

conn_str = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

try:
    conn = psycopg2.connect(conn_str)
    cur = conn.cursor()
    
    # Analyze today's data (Oct 3, 2026) vs older data
    cur.execute("""
        SELECT timeframe, direction, was_win, ml_features_json, entry_time 
        FROM trade_outcomes 
        WHERE entry_time >= '2026-10-03 00:00:00'
    """)
    rows = cur.fetchall()
    
    print(f"Total trades today (Oct 3): {len(rows)}")
    
    tf_stats = defaultdict(lambda: {"wins": 0, "total": 0})
    ml_conf_stats = {"high": {"wins": 0, "total": 0}, "medium": {"wins": 0, "total": 0}, "low": {"wins": 0, "total": 0}}
    
    for row in rows:
        tf, direction, was_win, ml_json_str, entry_time = row
        tf_stats[tf]["total"] += 1
        if was_win:
            tf_stats[tf]["wins"] += 1
            
        try:
            if ml_json_str:
                data = json.loads(ml_json_str)
                # Check if confidence is recorded directly, or we can look at the fact that ML was used
        except:
            pass

    for tf, stats in tf_stats.items():
        wr = (stats["wins"] / stats["total"]) * 100 if stats["total"] > 0 else 0
        print(f"TF: {tf} -> Wins: {stats['wins']}/{stats['total']} ({wr:.1f}%)")

    # Let's get more detailed stats about ML confidence if we can parse it from trade_outcomes or signal_votes
    
    cur.execute("""
        SELECT timeframe, AVG(CAST(was_win AS int)) as win_rate, COUNT(*) 
        FROM trade_outcomes 
        WHERE entry_time < '2026-10-03 00:00:00'
        GROUP BY timeframe
    """)
    old_rows = cur.fetchall()
    print("\n--- PREVIOUS DAYS ---")
    for r in old_rows:
        print(f"TF: {r[0]} -> WR: {r[1]*100:.1f}% (Total: {r[2]})")
        
    conn.close()
except Exception as e:
    print(f"Error: {e}")