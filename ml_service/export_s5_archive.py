"""
Incremental Exporter for High-Frequency s5 Candles -> Local Parquet Archive
===========================================================================
Archives `subminute_candles (interval='s5')` from PostgreSQL into a compressed
local Parquet file (`ml_service/data/s5_archive.parquet`) before database retention
prunes older days. All other subminute intervals (s10, s15, s30, 1min) can be
deterministically reconstructed from this s5 archive during training.

Usage:
    set DATABASE_URL=postgresql://...
    py ml_service/export_s5_archive.py
"""

from __future__ import annotations

import argparse
import os
import sys
from datetime import datetime, timedelta, timezone
from typing import Optional

import pandas as pd
import psycopg2

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ARCHIVE_PATH = os.path.join(SCRIPT_DIR, "data", "s5_archive.parquet")

CANDLE_COLUMNS = [
    "asset",
    "open_time",
    "open_price",
    "high_price",
    "low_price",
    "close_price",
    "volume",
]


def normalize_candle_frame(df: pd.DataFrame) -> pd.DataFrame:
    """Normalizes candle schema and dtypes cleanly without mutating caller frames."""
    if df is None or df.empty:
        return pd.DataFrame(columns=CANDLE_COLUMNS)

    out = df.copy()
    rename_map = {
        "open": "open_price",
        "high": "high_price",
        "low": "low_price",
        "close": "close_price",
    }
    out = out.rename(columns={k: v for k, v in rename_map.items() if k in out.columns and v not in out.columns})
    for col in CANDLE_COLUMNS:
        if col not in out.columns:
            out[col] = 0.0 if col == "volume" else ""
    out = out[CANDLE_COLUMNS]
    out["asset"] = out["asset"].astype(str).str.strip()
    out["open_time"] = out["open_time"].astype(str).str.strip()
    for col in ("open_price", "high_price", "low_price", "close_price", "volume"):
        out[col] = pd.to_numeric(out[col], errors="coerce").astype("float64")
    out = out.dropna(subset=["open_price", "high_price", "low_price", "close_price"])
    out = out[(out["asset"] != "") & (out["open_time"] != "")]
    return out


def _parse_iso_utc(series: pd.Series) -> pd.Series:
    try:
        return pd.to_datetime(series, format="ISO8601", utc=True, errors="coerce")
    except ValueError:
        return pd.to_datetime(series, utc=True, errors="coerce")


def merge_candle_frames(existing_df: Optional[pd.DataFrame], fresh_df: Optional[pd.DataFrame]) -> pd.DataFrame:
    """
    Merges existing archived candles with freshly queried DB candles.
    Deduplicates on `(asset, open_time)` keeping the fresh DB version (`keep='last'`).
    """
    parts = [
        df for df in (normalize_candle_frame(existing_df), normalize_candle_frame(fresh_df))
        if not df.empty
    ]
    if not parts:
        return pd.DataFrame(columns=CANDLE_COLUMNS)

    merged = pd.concat(parts, ignore_index=True)
    # Canonical timestamp ensures exact deduplication even across mixed ISO8601 sub-second formatting
    merged["_ts_key"] = _parse_iso_utc(merged["open_time"])
    merged = merged.dropna(subset=["_ts_key"])
    merged = merged.drop_duplicates(subset=["asset", "_ts_key"], keep="last")
    merged = merged.sort_values(["asset", "_ts_key"], kind="mergesort").drop(columns=["_ts_key"]).reset_index(drop=True)
    return merged


def _compute_incremental_cutoff(existing_df: pd.DataFrame, overlap_minutes: int) -> Optional[str]:
    if existing_df.empty:
        return None
    ts_max = _parse_iso_utc(existing_df["open_time"]).max()
    if pd.isna(ts_max):
        return None
    cutoff = ts_max.to_pydatetime().astimezone(timezone.utc) - timedelta(minutes=max(1, overlap_minutes))
    return cutoff.strftime("%Y-%m-%dT%H:%M:%S")


def export_s5_archive(
    db_url: str,
    out_path: str = DEFAULT_ARCHIVE_PATH,
    overlap_minutes: int = 5,
    compression: str = "zstd",
) -> pd.DataFrame:
    if not db_url:
        raise ValueError("DATABASE_URL / --db-url is required.")

    existing_df = pd.read_parquet(out_path) if os.path.exists(out_path) else pd.DataFrame(columns=CANDLE_COLUMNS)
    existing_norm = normalize_candle_frame(existing_df)
    since_iso = _compute_incremental_cutoff(existing_norm, overlap_minutes)

    conn = psycopg2.connect(db_url, options="-c default_transaction_read_only=on", connect_timeout=20)
    try:
        cur = conn.cursor()
        if since_iso:
            sql = """
                SELECT asset, open_time, open_price, high_price, low_price, close_price, volume
                FROM subminute_candles
                WHERE interval = 's5' AND open_time >= %s
                ORDER BY asset, open_time ASC
            """
            cur.execute(sql, (since_iso,))
        else:
            sql = """
                SELECT asset, open_time, open_price, high_price, low_price, close_price, volume
                FROM subminute_candles
                WHERE interval = 's5'
                ORDER BY asset, open_time ASC
            """
            cur.execute(sql)
        rows = cur.fetchall()
        cols = [d[0] for d in cur.description] if cur.description else CANDLE_COLUMNS
        fresh_df = pd.DataFrame(rows, columns=cols)
    finally:
        conn.close()

    merged = merge_candle_frames(existing_norm, fresh_df)
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    tmp_path = f"{out_path}.tmp.parquet"
    merged.to_parquet(tmp_path, index=False, compression=compression)
    os.replace(tmp_path, out_path)

    size_mb = os.path.getsize(out_path) / (1024.0 * 1024.0)
    min_t = merged["open_time"].min() if not merged.empty else "-"
    max_t = merged["open_time"].max() if not merged.empty else "-"
    mode = f"incremental >= {since_iso}" if since_iso else "full"
    print(
        f"[export_s5_archive] Mode={mode} | fetched={len(fresh_df):,} | total={len(merged):,} | "
        f"span={min_t} .. {max_t} | size={size_mb:.2f} MB -> {out_path}",
        flush=True,
    )
    return merged


def main() -> None:
    parser = argparse.ArgumentParser(description="Export & incrementally update local s5 Parquet archive.")
    parser.add_argument("--db-url", default=os.environ.get("DATABASE_URL", ""), help="PostgreSQL DSN")
    parser.add_argument("--out", default=DEFAULT_ARCHIVE_PATH, help="Output Parquet path")
    parser.add_argument("--overlap-minutes", type=int, default=5, help="Overlap minutes for idempotent upserts")
    args = parser.parse_args()

    try:
        export_s5_archive(args.db_url, out_path=args.out, overlap_minutes=args.overlap_minutes)
    except Exception as exc:
        print(f"[export_s5_archive] Error: {exc}", file=sys.stderr)
        raise SystemExit(1)


if __name__ == "__main__":
    main()
