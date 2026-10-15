"""
download_dukascopy_ticks.py - Free historical bid/ask ticks -> 5s bars with real microstructure fields.

Dukascopy datafeed: https://datafeed.dukascopy.com/datafeed/{PAIR}/{YYYY}/{MM-1:02d}/{DD:02d}/{HH:02d}h_ticks.bi5
  (month is ZERO-based). File = LZMA stream of 20-byte big-endian records:
  uint32 ms_since_hour_start, uint32 ask*pt, uint32 bid*pt, float32 ask_vol, float32 bid_vol
  (pt = 1e5 for most pairs, 1e3 for JPY pairs).

Output per pair: ml_service/data/dukascopy/{PAIR}_s5.npz with arrays:
  t (epoch sec, bar start), o,h,l,c (mid), spread (mean ask-bid), ticks (count),
  ofi (sum of sign(d mid) over ticks), vol (ask+bid vol sum), bid, ask (last quotes)

Resumable: each downloaded day is cached in data/dukascopy/cache/{PAIR}_{date}.npy.

Usage:
  py -3 download_dukascopy_ticks.py --pairs EURUSD GBPUSD USDJPY --start 2025-06-01 --end 2025-09-30 --workers 12
"""
import os
import lzma
import argparse
import datetime as dt
from concurrent.futures import ThreadPoolExecutor, as_completed
import numpy as np
import requests

BASE = "https://datafeed.dukascopy.com/datafeed"
BAR = 5  # seconds
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "data", "dukascopy")
CACHE_DIR = os.path.join(OUT_DIR, "cache")
_session = requests.Session()
_session.headers.update({"User-Agent": "Mozilla/5.0"})

TICK_DTYPE = np.dtype([("ms", ">u4"), ("ask", ">u4"), ("bid", ">u4"), ("av", ">f4"), ("bv", ">f4")])
BAR_DTYPE = np.dtype([("t", "i8"), ("o", "f8"), ("h", "f8"), ("l", "f8"), ("c", "f8"), ("spread", "f8"),
                      ("ticks", "f8"), ("ofi", "f8"), ("vol", "f8"), ("bid", "f8"), ("ask", "f8")])


def point_value(pair):
    return 1e3 if pair.endswith("JPY") else 1e5


def fetch_hour(pair, day, hour, retries=2):
    url = f"{BASE}/{pair}/{day.year}/{day.month - 1:02d}/{day.day:02d}/{hour:02d}h_ticks.bi5"
    last_err = None
    for a in range(retries):
        try:
            r = _session.get(url, timeout=(5, 20))
            if r.status_code == 404:
                return None
            if r.status_code == 200:
                if not r.content:
                    return None
                raw = lzma.decompress(r.content)
                n = len(raw) // 20
                if n == 0:
                    return None
                return np.frombuffer(raw[: n * 20], dtype=TICK_DTYPE)
        except Exception as e:
            last_err = e
    raise ConnectionError(f"Dukascopy fetch failed: {url} ({last_err})")


def day_bars(pair, day):
    """Returns array of 5s bars for one UTC day (only bars that had ticks), or None."""
    pt = point_value(pair)
    parts = []
    base_ts = int(dt.datetime(day.year, day.month, day.day, tzinfo=dt.timezone.utc).timestamp())
    with ThreadPoolExecutor(max_workers=6) as hx:
        hours = list(hx.map(lambda h: fetch_hour(pair, day, h), range(24)))
    for h, arr in enumerate(hours):
        if arr is None:
            continue
        ts = base_ts + h * 3600 + arr["ms"].astype(np.int64) / 1000.0
        ask = arr["ask"].astype(np.float64) / pt
        bid = arr["bid"].astype(np.float64) / pt
        vol = arr["av"].astype(np.float64) + arr["bv"].astype(np.float64)
        parts.append((ts, ask, bid, vol))
    if not parts:
        return None
    ts = np.concatenate([p[0] for p in parts]); ask = np.concatenate([p[1] for p in parts])
    bid = np.concatenate([p[2] for p in parts]); vol = np.concatenate([p[3] for p in parts])
    order = np.argsort(ts, kind="stable")
    ts, ask, bid, vol = ts[order], ask[order], bid[order], vol[order]
    mid = (ask + bid) / 2.0
    bucket = (ts // BAR).astype(np.int64)
    ub, first = np.unique(bucket, return_index=True)
    last = np.r_[first[1:], len(bucket)] - 1
    sign = np.sign(np.r_[0.0, np.diff(mid)])
    idx = np.searchsorted(ub, bucket)
    nb = len(ub)
    high = np.full(nb, -np.inf); low = np.full(nb, np.inf)
    np.maximum.at(high, idx, mid); np.minimum.at(low, idx, mid)
    ticks = np.bincount(idx, minlength=nb).astype(np.float64)
    out = np.zeros(nb, dtype=BAR_DTYPE)
    out["t"] = ub * BAR
    out["o"] = mid[first]; out["h"] = high; out["l"] = low; out["c"] = mid[last]
    out["spread"] = np.bincount(idx, weights=ask - bid, minlength=nb) / ticks
    out["ticks"] = ticks
    out["ofi"] = np.bincount(idx, weights=sign, minlength=nb)
    out["vol"] = np.bincount(idx, weights=vol, minlength=nb)
    out["bid"] = bid[last]; out["ask"] = ask[last]
    return out


def cached_day(pair, day):
    os.makedirs(CACHE_DIR, exist_ok=True)
    f = os.path.join(CACHE_DIR, f"{pair}_{day.isoformat()}.npy")
    if os.path.exists(f):
        a = np.load(f, allow_pickle=False)
        return a if len(a) else None
    bars = day_bars(pair, day)
    np.save(f, bars if bars is not None else np.zeros(0, dtype=BAR_DTYPE))
    return bars


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pairs", nargs="+", default=["EURUSD", "GBPUSD", "USDJPY"])
    ap.add_argument("--start", required=True)
    ap.add_argument("--end", required=True)
    ap.add_argument("--workers", type=int, default=12)
    args = ap.parse_args()
    d0 = dt.date.fromisoformat(args.start); d1 = dt.date.fromisoformat(args.end)
    days = [d0 + dt.timedelta(days=i) for i in range((d1 - d0).days + 1)]
    days = [d for d in days if d.weekday() != 5]  # Saturday has no FX ticks
    os.makedirs(OUT_DIR, exist_ok=True)
    for pair in args.pairs:
        got, done, failed = [], 0, 0
        with ThreadPoolExecutor(max_workers=args.workers) as ex:
            futs = {ex.submit(cached_day, pair, d): d for d in days}
            for fu in as_completed(futs):
                done += 1
                try:
                    r = fu.result()
                except ConnectionError:
                    failed += 1
                    continue
                if r is not None and len(r):
                    got.append(r)
                if done % 20 == 0:
                    print(f"[{pair}] {done}/{len(days)} days (failed so far: {failed})", flush=True)
        if failed:
            print(f"[{pair}] WARNING: {failed} day(s) failed (network); rerun the same command to resume", flush=True)
        if not got:
            print(f"[{pair}] no data in range", flush=True)
            continue
        allb = np.concatenate(got)
        allb = allb[np.argsort(allb["t"], kind="stable")]
        path = os.path.join(OUT_DIR, f"{pair}_s5.npz")
        np.savez_compressed(path, **{k: allb[k] for k in allb.dtype.names})
        span = (allb["t"][-1] - allb["t"][0]) / 86400
        print(f"[{pair}] saved {len(allb):,} bars over {span:.0f} days -> {path}", flush=True)


if __name__ == "__main__":
    main()
