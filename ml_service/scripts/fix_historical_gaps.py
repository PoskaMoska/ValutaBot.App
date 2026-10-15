"""
Fix Historical Gaps Script for ValutaBot PostgreSQL Database.

Addresses data quality issues in `historical_candles` where `open_time` is stored as TEXT.
Due to differing timestamp formats ('YYYY-MM-DD HH:MM:SS' vs 'YYYY-MM-DDTHH:MM:SS.0000000Z')
from overlapping data ingestions, string sorting (`ORDER BY open_time ASC`) causes timestamps
to jump backwards in time (min_gap = -28860s).

This script:
1. Groups rows by (asset, interval).
2. Analyzes chronological sequencing ordered by open_time ASC.
3. Detects negative time gaps and identifies out-of-order duplicate rows.
4. Safely deletes out-of-order rows in a transaction.
5. Re-runs gap analysis to verify zero negative gaps remain.
6. Commits only if --auto is specified or upon interactive confirmation. Supports --dry-run.
"""

import os
import sys
import argparse
from datetime import datetime, timezone
from typing import List, Tuple, Dict, Any
import psycopg2

# Configure utf-8 stdout/stderr if possible on Windows
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

DEFAULT_CONN = "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"


def parse_open_time(s: str) -> datetime:
    """
    Parses open_time string into a UTC timezone-aware datetime.
    Handles 'YYYY-MM-DD HH:MM:SS' and ISO8601 'YYYY-MM-DDTHH:MM:SS.sssssssZ'.
    """
    dt = datetime.fromisoformat(s)
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def analyze_group_gaps(cur, asset: str, interval: str) -> Dict[str, Any]:
    """
    Selects all rows for (asset, interval) ORDER BY open_time ASC,
    detects negative time gaps, and identifies out-of-order rows.
    """
    cur.execute(
        """
        SELECT id, open_time 
        FROM historical_candles 
        WHERE asset = %s AND interval = %s 
        ORDER BY open_time ASC;
        """,
        (asset, interval),
    )
    rows = cur.fetchall()
    total_rows = len(rows)

    negative_gaps: List[Dict[str, Any]] = []
    out_of_order_ids: List[int] = []
    out_of_order_details: List[Dict[str, Any]] = []

    prev_dt = None
    prev_open_time = None
    prev_id = None
    max_dt_seen = None
    min_gap_sec = None

    for r_id, r_open_time in rows:
        curr_dt = parse_open_time(r_open_time)

        if prev_dt is not None:
            gap_sec = (curr_dt - prev_dt).total_seconds()
            if min_gap_sec is None or gap_sec < min_gap_sec:
                min_gap_sec = gap_sec

            if gap_sec < 0:
                negative_gaps.append({
                    "prev_id": prev_id,
                    "prev_open_time": prev_open_time,
                    "prev_dt": prev_dt,
                    "curr_id": r_id,
                    "curr_open_time": r_open_time,
                    "curr_dt": curr_dt,
                    "gap_sec": gap_sec,
                })

        # Check if row is out of chronological order relative to all preceding rows
        if max_dt_seen is not None and curr_dt < max_dt_seen:
            out_of_order_ids.append(r_id)
            out_of_order_details.append({
                "id": r_id,
                "open_time": r_open_time,
                "dt": curr_dt,
                "max_dt_seen": max_dt_seen,
            })
        else:
            max_dt_seen = curr_dt

        prev_dt = curr_dt
        prev_open_time = r_open_time
        prev_id = r_id

    return {
        "asset": asset,
        "interval": interval,
        "total_rows": total_rows,
        "negative_gaps": negative_gaps,
        "min_gap_sec": min_gap_sec,
        "out_of_order_ids": out_of_order_ids,
        "out_of_order_details": out_of_order_details,
    }


def delete_rows(cur, ids: List[int], batch_size: int = 1000) -> int:
    """
    Deletes rows by ID from historical_candles in batches.
    """
    if not ids:
        return 0
    total_deleted = 0
    for i in range(0, len(ids), batch_size):
        batch = ids[i : i + batch_size]
        cur.execute(
            "DELETE FROM historical_candles WHERE id = ANY(%s);",
            (batch,),
        )
        total_deleted += cur.rowcount
    return total_deleted


def main():
    parser = argparse.ArgumentParser(
        description="Detect and fix negative time gaps in historical_candles."
    )
    parser.add_argument(
        "--conn",
        type=str,
        default=os.getenv("DATABASE_URL", DEFAULT_CONN),
        help="PostgreSQL connection string.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Perform detection and report what would be deleted without making any database modifications.",
    )
    parser.add_argument(
        "--auto",
        action="store_true",
        help="Automatically delete out-of-order rows and commit transaction without prompting.",
    )
    args = parser.parse_args()

    conn_str = args.conn
    dry_run = args.dry_run
    auto_commit = args.auto

    print("=" * 80)
    print(" ValutaBot - Historical Candles Negative Gap Fixer")
    print("=" * 80)
    print(f" Mode: {'DRY RUN (no changes)' if dry_run else ('AUTOMATIC COMMIT' if auto_commit else 'INTERACTIVE CONFIRMATION')}")
    print(f" Target Database: {conn_str.split('@')[-1] if '@' in conn_str else conn_str}")
    print("-" * 80)

    try:
        conn = psycopg2.connect(conn_str)
        conn.autocommit = False
    except Exception as e:
        print(f"[ERROR] Failed to connect to PostgreSQL: {e}")
        sys.exit(1)

    cur = conn.cursor()

    try:
        # Step 1: Discover all (asset, interval) groups
        cur.execute(
            """
            SELECT DISTINCT asset, interval 
            FROM historical_candles 
            ORDER BY asset, interval;
            """
        )
        groups = cur.fetchall()

        if not groups:
            print("[WARN] No data found in historical_candles table.")
            cur.close()
            conn.close()
            return

        print(f"Found {len(groups)} (asset, interval) groups to analyze:\n")

        all_analysis: List[Dict[str, Any]] = []
        total_negative_gaps = 0
        total_out_of_order = 0

        # Step 2: Analyze each group
        for asset, interval in groups:
            analysis = analyze_group_gaps(cur, asset, interval)
            all_analysis.append(analysis)

            neg_count = len(analysis["negative_gaps"])
            ooo_count = len(analysis["out_of_order_ids"])
            total_negative_gaps += neg_count
            total_out_of_order += ooo_count

            min_gap_str = f"{analysis['min_gap_sec']:.1f}s" if analysis["min_gap_sec"] is not None else "N/A"
            print(f"[DATA] [{asset} | {interval}] Total rows: {analysis['total_rows']}")
            print(f"       Negative gap transitions: {neg_count} | Out-of-order rows: {ooo_count} | Min gap: {min_gap_str}")

            if neg_count > 0:
                print("       Negative gap transitions detected:")
                for gap in analysis["negative_gaps"]:
                    print(
                        f"         -> Jump from [id={gap['prev_id']}] '{gap['prev_open_time']}' "
                        f"to [id={gap['curr_id']}] '{gap['curr_open_time']}' "
                        f"(Gap: {gap['gap_sec']:.1f}s)"
                    )
            if ooo_count > 0:
                first_ooo = analysis["out_of_order_details"][0]
                last_ooo = analysis["out_of_order_details"][-1]
                print(
                    f"       Out-of-order range: '{first_ooo['open_time']}' [id={first_ooo['id']}] "
                    f"to '{last_ooo['open_time']}' [id={last_ooo['id']}]"
                )
            print("-" * 80)

        # Summary of detection
        print("\n" + "=" * 80)
        print(" INITIAL DETECTION SUMMARY")
        print("=" * 80)
        print(f" Total groups analyzed:           {len(groups)}")
        print(f" Total negative gap transitions:  {total_negative_gaps}")
        print(f" Total out-of-order rows to drop: {total_out_of_order}")
        print("=" * 80)

        if total_out_of_order == 0:
            print("[OK] No out-of-order rows found! Database is already consistent.")
            cur.close()
            conn.close()
            return

        if dry_run:
            print("\n[DRY RUN] No rows will be deleted. Exiting without database modifications.")
            cur.close()
            conn.rollback()
            conn.close()
            return

        # Interactive confirmation if not --auto
        if not auto_commit:
            confirm = input(
                f"\n[CONFIRM] Proceed with deleting {total_out_of_order} out-of-order rows across {len(groups)} groups? [y/N]: "
            ).strip().lower()
            if confirm not in ("y", "yes"):
                print("[ABORT] Operation cancelled by user. Rolling back transaction.")
                cur.close()
                conn.rollback()
                conn.close()
                return

        # Step 3: Deletion
        print("\n[DELETE] Deleting out-of-order rows...")
        total_deleted = 0
        for analysis in all_analysis:
            asset = analysis["asset"]
            interval = analysis["interval"]
            ids = analysis["out_of_order_ids"]
            if ids:
                deleted = delete_rows(cur, ids)
                total_deleted += deleted
                print(f"   Deleted {deleted} rows for [{asset} | {interval}].")

        print(f"\n[OK] Total rows deleted from database: {total_deleted}")

        # Step 4: Verification re-run
        print("\n" + "=" * 80)
        print(" POST-CLEANUP VERIFICATION")
        print("=" * 80)

        verification_passed = True
        post_summaries = []

        for asset, interval in groups:
            post_analysis = analyze_group_gaps(cur, asset, interval)
            post_neg_count = len(post_analysis["negative_gaps"])
            post_ooo_count = len(post_analysis["out_of_order_ids"])
            min_gap_str = f"{post_analysis['min_gap_sec']:.1f}s" if post_analysis["min_gap_sec"] is not None else "N/A"

            status = "PASS" if (post_neg_count == 0 and post_ooo_count == 0) else "FAIL"
            if post_neg_count > 0 or post_ooo_count > 0:
                verification_passed = False

            post_summaries.append((asset, interval, post_analysis["total_rows"], min_gap_str, status))
            print(f" [{status}] [{asset} | {interval}] Remaining rows: {post_analysis['total_rows']} | Min gap: {min_gap_str} | Negative gaps: {post_neg_count}")

        print("-" * 80)
        if verification_passed:
            print("[OK] All groups verified clean: ZERO negative gaps remain!")
            conn.commit()
            print("[COMMIT] Transaction committed successfully.")
        else:
            print("[FAIL] Verification failed! Rolling back transaction to prevent corruption.")
            conn.rollback()
            sys.exit(1)

        print("\n" + "=" * 80)
        print(" FINAL SUMMARY TABLE")
        print("=" * 80)
        print(f"{'Asset':<10} {'Interval':<10} {'Remaining Rows':<16} {'Min Gap':<12} {'Status':<10}")
        print("-" * 80)
        for asset, interval, count, min_gap_str, status in post_summaries:
            print(f"{asset:<10} {interval:<10} {count:<16} {min_gap_str:<12} {status:<10}")
        print("=" * 80)

    except Exception as e:
        print(f"\n[ERROR] Exception during processing: {e}")
        conn.rollback()
        raise
    finally:
        cur.close()
        conn.close()


if __name__ == "__main__":
    main()
