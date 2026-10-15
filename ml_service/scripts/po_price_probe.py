r"""
po_price_probe.py - READ-ONLY Pocket Option price probe (never places trades).

Connects with the session string stored in ~/.env as PO_SSID (never printed), reports account type and payouts,
then records raw price updates for N seconds into data/po_ticks/<date>.jsonl together with the local
receive time (ns). Use it to compare the broker's price stream against the Tiingo feed (latency / divergence).

Setup (PowerShell, input hidden):
  $s = Read-Host "PO_SSID" -AsSecureString; $p=[Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)); Add-Content "$HOME\.env" "PO_SSID=$p"; Remove-Variable p,s

Run:
  py -3 po_price_probe.py --assets EURUSD EURUSD_otc --seconds 60
"""
import os
import sys
import json
import time
import asyncio
import argparse
import datetime as dt
from pathlib import Path

from BinaryOptionsToolsV2.pocketoption import PocketOptionAsync

OUT_DIR = Path(__file__).resolve().parent.parent / "data" / "po_ticks"


def load_ssid():
    val = os.environ.get("PO_SSID")
    if val:
        return val
    envf = Path.home() / ".env"
    if envf.exists():
        for line in envf.read_text(encoding="utf-8").splitlines():
            if line.startswith("PO_SSID="):
                return line[len("PO_SSID="):].strip()
    return None


async def record(client, asset, seconds, fh, preview):
    sub = await client.subscribe_symbol(asset)
    end = time.monotonic() + seconds
    n = 0
    async for upd in sub:
        recv_ns = time.time_ns()
        fh.write(json.dumps({"recv_ns": recv_ns, "asset": asset, "data": upd}, default=str) + "\n")
        n += 1
        if n <= preview:
            print(f"  [{asset}] {upd}")
        if time.monotonic() >= end:
            break
    return n


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--assets", nargs="+", default=["EURUSD", "EURUSD_otc"])
    ap.add_argument("--seconds", type=int, default=60)
    ap.add_argument("--preview", type=int, default=5, help="how many updates to echo per asset")
    args = ap.parse_args()

    ssid = load_ssid()
    if not ssid:
        print("PO_SSID not found in environment or ~/.env - see setup in the file header.")
        sys.exit(2)

    async with PocketOptionAsync(ssid) as client:
        await client.wait_for_assets(timeout=30)
        print(f"Connected. Account: {'DEMO' if client.is_demo() else 'REAL'}")
        pay = await client.payout(args.assets)
        for a, p in zip(args.assets, pay):
            print(f"  payout {a}: {p}%")
        OUT_DIR.mkdir(parents=True, exist_ok=True)
        path = OUT_DIR / f"{dt.date.today().isoformat()}.jsonl"
        with open(path, "a", encoding="utf-8") as fh:
            counts = await asyncio.gather(*[record(client, a, args.seconds, fh, args.preview) for a in args.assets])
        for a, c in zip(args.assets, counts):
            print(f"  {a}: {c} updates in {args.seconds}s ({c / max(args.seconds, 1):.1f}/s)")
        print(f"Saved to {path}")


if __name__ == "__main__":
    asyncio.run(main())
