"""
Synthesize subminute candles v3 — uses PostgreSQL COPY for bulk insert.
COPY is 50-100x faster than INSERT...ON CONFLICT for large datasets.
"""
import psycopg2, io, math, random, datetime, time

DB_URL = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"

PAIRS     = ["GBPUSD", "USDJPY", "USDCAD", "USDCHF", "AUDUSD"]
INTERVALS = [("s5", 1), ("s10", 2), ("s15", 3), ("s30", 6)]
MAX_M1    = 20000   # last 20k 1m candles -> ~240k s5 per pair
rng       = random.Random(42)

def get_conn():
    return psycopg2.connect(DB_URL, connect_timeout=30)

def synthesize_s5(o, h, l, c, vol, m1_time):
    N, scale = 12, (h - l) * 0.5
    W = [0.0] * (N + 1)
    for i in range(N):
        u1 = max(1e-10, 1.0 - rng.random())
        u2 = max(1e-10, 1.0 - rng.random())
        W[i+1] = W[i] + math.sqrt(-2*math.log(u1)) * math.sin(2*math.pi*u2)
    bridge = [W[i+1] - ((i+1)/N)*W[N] for i in range(N)]
    rb = max(max(bridge) - min(bridge), 1e-10)
    bs = scale / rb
    prev, out = o, []
    for i in range(N):
        cv = o + (c-o)*((i+1)/N) + bridge[i]*bs
        cv = max(min(cv, h), l)
        ov = prev
        hv = min(max(ov,cv) + (h-l)*0.1*rng.random(), h)
        lv = max(min(ov,cv) - (h-l)*0.1*rng.random(), l)
        if i == N-1: cv = c
        out.append((ov, hv, lv, cv, vol/N, m1_time + datetime.timedelta(seconds=i*5)))
        prev = cv
    return out

def aggregate(s5_candles, gs):
    out = []
    for i in range(0, len(s5_candles), gs):
        g = s5_candles[i:i+gs]
        if not g: continue
        out.append((g[0][0], max(x[1] for x in g), min(x[2] for x in g),
                    g[-1][3], sum(x[4] for x in g), g[0][5]))
    return out

def existing_count(cur, pair, interval):
    cur.execute("SELECT COUNT(*) FROM subminute_candles WHERE asset=%s AND interval=%s", (pair, interval))
    return cur.fetchone()[0]

def copy_insert(conn, pair, ivname, candles):
    """Fast bulk insert using COPY + temp table then INSERT...ON CONFLICT."""
    cur = conn.cursor()

    # Create temp table
    cur.execute("""
        CREATE TEMP TABLE IF NOT EXISTS tmp_candles (
            asset TEXT, interval TEXT, open_time TEXT,
            open_price DOUBLE PRECISION, high_price DOUBLE PRECISION,
            low_price DOUBLE PRECISION, close_price DOUBLE PRECISION,
            volume DOUBLE PRECISION
        ) ON COMMIT DELETE ROWS
    """)

    # Build CSV in memory
    buf = io.StringIO()
    for (o, h, l, c, vol, ts) in candles:
        ts_str = ts.strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
        buf.write(f"{pair}\t{ivname}\t{ts_str}\t{o}\t{h}\t{l}\t{c}\t{vol}\n")
    buf.seek(0)

    # COPY into temp
    cur.copy_from(buf, "tmp_candles",
                  columns=("asset","interval","open_time","open_price",
                            "high_price","low_price","close_price","volume"))

    # INSERT from temp with conflict handling
    cur.execute("""
        INSERT INTO subminute_candles
            (asset, interval, open_time, open_price, high_price, low_price, close_price, volume)
        SELECT asset, interval, open_time, open_price, high_price, low_price, close_price, volume
        FROM tmp_candles
        ON CONFLICT (asset, interval, open_time) DO NOTHING
    """)
    inserted = cur.rowcount
    conn.commit()
    cur.close()
    return inserted

def main():
    print("=== Synthesize subminute candles v3 (COPY method) ===")
    t0 = time.time()
    conn = get_conn()
    cur  = conn.cursor()

    for pair in PAIRS:
        print(f"\n--- {pair} ---")
        cur.execute("""
            SELECT open, high, low, close, volume, open_time
            FROM historical_candles
            WHERE asset=%s AND interval='1m'
            ORDER BY open_time DESC LIMIT %s
        """, (pair, MAX_M1))
        rows = list(reversed(cur.fetchall()))
        print(f"  1m candles loaded: {len(rows)}")
        if len(rows) < 100:
            print("  Not enough data, skipping")
            continue

        # Synthesize all s5
        all_s5 = []
        for row in rows:
            o,h,l,c,vol,ts = float(row[0]),float(row[1]),float(row[2]),float(row[3]),float(row[4] or 1),row[5]
            if isinstance(ts, str):
                ts = datetime.datetime.fromisoformat(ts.replace('Z','+00:00'))
            if ts.tzinfo is None:
                ts = ts.replace(tzinfo=datetime.timezone.utc)
            all_s5.extend(synthesize_s5(o,h,l,c,vol,ts))

        for ivname, gs in INTERVALS:
            candles = all_s5 if gs == 1 else aggregate(all_s5, gs)
            already = existing_count(cur, pair, ivname)
            print(f"  {ivname}: {len(candles):,} candles (already in DB: {already:,})...", end=" ", flush=True)

            if already >= len(candles) * 0.9:
                print("skip (already complete)")
                continue

            t1 = time.time()
            try:
                n = copy_insert(conn, pair, ivname, candles)
                elapsed = time.time() - t1
                print(f"OK — {n:,} inserted in {elapsed:.1f}s")
            except Exception as e:
                conn.rollback()
                print(f"FAILED: {e}")
                conn = get_conn()
                cur  = conn.cursor()

    conn.close()
    print(f"\n=== DONE in {(time.time()-t0)/60:.1f} min ===")

if __name__ == "__main__":
    main()
