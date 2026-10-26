"""
auto_retrain.py - Production 1-Click Pipeline for ValutaBot Neural Brain (V4)

Executes the full retraining cycle:
1. Extracts clean dataset from PostgreSQL (100% s10/s15/s30/m1, 1/10 s5, no anomalies).
2. Overwrites temporary rolling dataset: ml_service/data/dataset_temp.npz.
3. Trains DualStreamPatchBrain (PatchTST) with OneCycleLR and temperature scaling calibration.
4. Persists new production checkpoint: ml_service/models/patch_tst_brain_v4.pt.
5. Automatically purges the heavy dataset_temp.npz to keep disk clean.
6. Verifies model load and prints validation metrics.
"""

import os
import sys
import argparse
import subprocess
import time
from pathlib import Path

DEFAULT_DB_URL = os.environ.get(
    "DATABASE_URL",
    "postgresql://postgres:MaEHyMeUBqdeBJdrTWodZJEKoQcldCEN@centerbeam.proxy.rlwy.net:47825/railway"
)

def run_step(cmd_args, desc):
    print("\n" + "=" * 70)
    print(f"[*] STEP: {desc}")
    print("=" * 70)
    t0 = time.time()
    res = subprocess.run([sys.executable] + cmd_args)
    dt = time.time() - t0
    if res.returncode != 0:
        print(f"\n[-] ERROR: Step '{desc}' failed with exit code {res.returncode}")
        sys.exit(res.returncode)
    print(f"[+] Completed '{desc}' in {dt:.1f}s")

def main():
    parser = argparse.ArgumentParser(description="1-Click Auto-Retrain Pipeline for ValutaBot Neural Brain")
    parser.add_argument("--db-url", default=DEFAULT_DB_URL, help="PostgreSQL connection string")
    parser.add_argument("--epochs", type=int, default=20, help="Max training epochs (default: 20)")
    parser.add_argument("--batch-size", type=int, default=128, help="Batch size (default: 128)")
    parser.add_argument("--keep-dataset", action="store_true", help="Keep temporary .npz dataset file instead of purging")
    args = parser.parse_args()

    ml_dir = Path(__file__).resolve().parent
    temp_dataset = ml_dir / "data" / "dataset_temp.npz"
    checkpoint_out = ml_dir / "models" / "patch_tst_brain_v4.pt"

    # Step 1: Export clean dataset from PostgreSQL
    export_args = [
        str(ml_dir / "export_dataset.py"),
        "--db-url", args.db_url,
        "--out", str(temp_dataset),
        "--min-date", "2026-10-21 20:15:00+00",
        "--min-pnl-bps", "0.30"
    ]
    run_step(export_args, "Exporting clean dataset from PostgreSQL (V4 cadence)")

    # Step 2: Train DualStreamPatchBrain
    train_args = [
        str(ml_dir / "train_brain.py"),
        "--data", str(temp_dataset),
        "--model", "patch_tst",
        "--epochs", str(args.epochs),
        "--batch-size", str(args.batch_size),
        "--lr", "1e-3",
        "--val-split", "0.20",
        "--save", str(checkpoint_out),
        "--patience", "4",
        "--num-threads", "8"
    ]
    run_step(train_args, "Training PatchTST DualStream Neural Brain")

    # Step 3: Purge heavy temporary dataset to save disk
    if not args.keep_dataset and temp_dataset.exists():
        try:
            temp_dataset.unlink()
            print(f"[+] Purged temporary dataset file {temp_dataset.name} to preserve disk storage.")
        except Exception as e:
            print(f"[-] Note: could not delete temporary dataset: {e}")

    # Step 4: Verify predictor loads new checkpoint
    print("\n" + "=" * 70)
    print("[*] STEP: Verifying loaded model checkpoint")
    print("=" * 70)
    if str(ml_dir) not in sys.path:
        sys.path.insert(0, str(ml_dir))
    from models.patch_tst_predictor import PatchTSTPredictor
    pred = PatchTSTPredictor.get_instance()
    pred._load_model()
    if pred.is_available():
        print(f"[+] SUCCESS! Model {pred._arch_tag} is online and verified.")
        print(f"    Architecture: {pred._model_name}")
        print(f"    Metrics:      {pred.metrics}")
    else:
        print("[-] WARNING: Model loaded but reported unavailable.")

    print("\n" + "=" * 70)
    print(f"[+] Pipeline complete! Checkpoint saved to: {checkpoint_out}")
    print("=" * 70)

if __name__ == "__main__":
    main()
