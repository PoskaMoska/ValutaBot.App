"""
po_capture_server.py - local collector for po_capture.user.js (listens on 127.0.0.1 only).

Appends every received frame to ml_service/data/po_ticks/browser_<date>.jsonl with the local receive time.
GET /status shows counters.

Usage:  py -3 po_capture_server.py [--port 8765]
"""
import os
import json
import time
import argparse
import datetime as dt
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

OUT_DIR = Path(__file__).resolve().parent.parent / "data" / "po_ticks"
STATS = {"batches": 0, "msgs": 0, "last": None, "by_event": {}, "ticks": {}}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _cors(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Access-Control-Allow-Private-Network", "true")

    def do_OPTIONS(self):
        self.send_response(204); self._cors(); self.end_headers()

    def do_GET(self):
        body = json.dumps(STATS).encode()
        self.send_response(200); self._cors()
        self.send_header("Content-Type", "application/json"); self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        recv_ns = time.time_ns()
        
        # Endpoint to save browser session to PostgreSQL on Railway
        if self.path == "/save_session":
            try:
                body = json.loads(self.rfile.read(n))
                db_url = os.environ.get("DATABASE_URL", "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway")
                import psycopg2
                conn = psycopg2.connect(db_url)
                conn.autocommit = True
                with conn.cursor() as cur:
                    cur.execute("""CREATE TABLE IF NOT EXISTS po_collector_state (
                                    id int PRIMARY KEY, state text, updated_at timestamptz,
                                    last_tick_at timestamptz, status text)""")
                    cur.execute("""INSERT INTO po_collector_state (id, state, updated_at, status)
                                 VALUES (1, %s, now(), 'READY')
                                 ON CONFLICT (id) DO UPDATE SET state=EXCLUDED.state, updated_at=now(), status='READY'""",
                                 (json.dumps(body),))
                conn.close()
                STATS["last_session_saved"] = dt.datetime.now().isoformat(timespec="seconds")
                self.send_response(200); self._cors(); self.end_headers()
                self.wfile.write(b'{"status":"saved_to_railway_db"}')
                return
            except Exception as e:
                self.send_response(500); self._cors(); self.end_headers()
                self.wfile.write(str(e).encode())
                return

        try:
            msgs = json.loads(self.rfile.read(n)).get("msgs", [])
        except Exception:
            self.send_response(400); self._cors(); self.end_headers(); return
        OUT_DIR.mkdir(parents=True, exist_ok=True)
        day = dt.date.today().isoformat()
        path = OUT_DIR / f"browser_{day}.jsonl"
        tpath = OUT_DIR / f"ticks_{day}.csv"
        with open(path, "a", encoding="utf-8") as fh, open(tpath, "a", encoding="utf-8") as tf:
            for m in msgs:
                m["batch_recv_ns"] = recv_ns
                ev = m.get("ev") or "?"
                STATS["by_event"][ev] = STATS["by_event"].get(ev, 0) + 1
                if ev == "updateStream" and m.get("dir") == "in":
                    try:
                        for asset, srv_ts, price in json.loads(m["d"]):
                            tf.write(f"{m['t']:.3f},{asset},{srv_ts},{price}\n")
                            STATS["ticks"][asset] = STATS["ticks"].get(asset, 0) + 1
                        continue
                    except Exception:
                        pass
                fh.write(json.dumps(m, ensure_ascii=False) + "\n")
        STATS["batches"] += 1; STATS["msgs"] += len(msgs); STATS["last"] = dt.datetime.now().isoformat(timespec="seconds")
        self.send_response(204); self._cors(); self.end_headers()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()
    print(f"collector listening on http://127.0.0.1:{args.port}  ->  {OUT_DIR}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
