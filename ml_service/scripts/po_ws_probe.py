r"""
po_ws_probe.py - READ-ONLY raw Socket.IO probe for Pocket Option (never sends trade orders).

The unofficial BinaryOptionsToolsV2 library sends the old auth key `session`, which the server now rejects
(it sends Socket.IO "41" disconnect). The browser sends `sessionToken`; this script speaks the protocol directly
with exactly the browser's auth payload, subscribes to an asset and records every message with the local receive time.

Reads PO_SSID from env or ~/.env (either the full `42["auth",{...}]` string or JSON); the token is never printed.

Usage:
  py -3 po_ws_probe.py --asset EURUSD_otc --period 5 --seconds 30 [--host demo-api-eu.po.market]
"""
import re
import os
import sys
import json
import time
import asyncio
import argparse
import datetime as dt
from pathlib import Path

from websockets.asyncio.client import connect

OUT_DIR = Path(__file__).resolve().parent.parent / "data" / "po_ticks"


def load_auth():
    raw = os.environ.get("PO_SSID")
    if not raw:
        envf = Path.home() / ".env"
        if envf.exists():
            for line in envf.read_text(encoding="utf-8").splitlines():
                if line.startswith("PO_SSID="):
                    raw = line[len("PO_SSID="):].strip()
    if not raw:
        sys.exit("PO_SSID not found")
    m = re.search(r"\{.*\}", raw, re.S)
    payload = json.loads(m.group(0))
    token = payload.get("sessionToken") or payload.get("session")
    return {"sessionToken": token, "uid": str(payload.get("uid")), "lang": payload.get("lang", "ru"),
            "currentUrl": payload.get("currentUrl", "cabinet/demo-quick-high-low"), "isChart": 1}


def short(x, n=220):
    s = x if isinstance(x, str) else repr(x)
    return s if len(s) <= n else s[:n] + "..."


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--asset", default="EURUSD_otc")
    ap.add_argument("--period", type=int, default=5)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--host", default="demo-api-eu.po.market")
    ap.add_argument("--preview", type=int, default=25)
    ap.add_argument("--origin", default="https://m.pocketoption.com")
    ap.add_argument("--url", default=None, help="full wss:// URL (overrides --host)")
    args = ap.parse_args()

    auth = load_auth()
    uri = args.url or f"wss://{args.host}/socket.io/?EIO=4&transport=websocket"
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    path = OUT_DIR / f"raw_{dt.date.today().isoformat()}.jsonl"
    headers = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                             "Chrome/155.0.0.0 Safari/537.36", "Accept-Language": "ru-RU,ru;q=0.9,en-US;q=0.8"}
    shown = 0
    total = 0
    authed = False
    subscribed = False
    t_end = None
    fh = open(path, "a", encoding="utf-8")
    hard_deadline = time.monotonic() + args.seconds + 40

    async def send(ws, text):
        fh.write(json.dumps({"sent_ns": time.time_ns(), "msg": text.replace(auth["sessionToken"], "<TOKEN>")}) + "\n")
        fh.flush()
        print("-> " + short(text.replace(auth["sessionToken"], "<TOKEN>"), 160))
        await ws.send(text)

    async with connect(uri, origin=args.origin, additional_headers=headers, max_size=None) as ws:
        print(f"connected to {args.host}")
        asyncio.get_running_loop().call_later(args.seconds + 15, lambda: asyncio.ensure_future(ws.close()))
        async for msg in ws:
            recv_ns = time.time_ns()
            is_bin = isinstance(msg, (bytes, bytearray))
            text = msg.decode("utf-8", "replace") if is_bin else msg
            total += 1
            fh.write(json.dumps({"recv_ns": recv_ns, "bin": is_bin, "msg": text}) + "\n")
            fh.flush()
            if shown < args.preview:
                shown += 1
                print(f"<- {'BIN ' if is_bin else ''}{short(text)}")
            if not is_bin:
                if text.startswith("0{"):
                    await send(ws, "40")
                elif text.startswith("40"):
                    await send(ws, "42" + json.dumps(["auth", auth], separators=(",", ":")))
                elif text == "2":
                    await ws.send("3")
                elif text.startswith("41"):
                    print("!! server sent 41 (disconnect): auth rejected / session expired")
                    break
                elif ("auth/success" in text or "successauth" in text) and not authed:
                    authed = True
                    print("== authenticated")
                    await send(ws, "42" + json.dumps(["changeSymbol", {"asset": args.asset, "period": args.period}],
                                                     separators=(",", ":")))
                    await send(ws, "42" + json.dumps(["subfor", args.asset], separators=(",", ":")))
                    subscribed = True
                    t_end = time.monotonic() + args.seconds
            if (t_end and time.monotonic() >= t_end) or time.monotonic() >= hard_deadline:
                break
    fh.close()
    print(f"done: {total} messages, authed={authed}, subscribed={subscribed}, saved to {path}")


if __name__ == "__main__":
    asyncio.run(main())
