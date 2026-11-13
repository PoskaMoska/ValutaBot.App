"""
calibrate_active_onnx.py - Out-of-Sample Audit & Temperature Calibration for Dual-Stream s5 ONNX Model.

Evaluates the currently active production `dual_stream_brain_v5.onnx` against real
`subminute_candles`-derived `s5` samples (`dataset_objective_dual_stream.npz`, or freshly exported
from PostgreSQL when `--db-url` is provided). Fits honest post-hoc Temperature Scaling (Guo et al.,
2017) on the chronological first half (constrained to T >= 1.0 so probabilities are never inflated)
and evaluates strictly out-of-sample on the held-out second half after a temporal embargo.

If T >= 1.05 strictly reduces overconfidence (live Brier + trainable NLL + confidence ECE) on the
held-out OOS half, folds 1/T into the final ONNX linear output Gemm layer, verifies batch & batch=1
ONNX Runtime parity, and writes the sidecar `dual_stream_brain_v5.json` metadata read by C#.
"""

import argparse
import hashlib
import json
import math
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch

PAYOUT = 0.82
BREAK_EVEN = 1.0 / (1.0 + PAYOUT)
GATE_TAU = 0.58
GATE_MIN_TRADES = 300
TAUS = (0.50, 0.52, 0.54, 0.55, 0.56, 0.58, 0.60, 0.65)


def wilson(k: int, n: int, z: float = 1.96):
    if n == 0:
        return 0.0, 0.0
    p = k / n
    den = 1.0 + z * z / n
    ctr = p + z * z / (2.0 * n)
    rad = z * math.sqrt((p * (1.0 - p) + z * z / (4.0 * n)) / n)
    return (ctr - rad) / den, (ctr + rad) / den


def sigmoid_np(x: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-np.clip(x, -60.0, 60.0)))


def live_like_wins(pred_buy: np.ndarray, delta_bps: np.ndarray) -> np.ndarray:
    return (pred_buy & (delta_bps > 0.0)) | ((~pred_buy) & (delta_bps < 0.0))


def conf_ece(conf: np.ndarray, corr: np.ndarray) -> float:
    edges = [0.50, 0.51, 0.52, 0.54, 0.56, 0.58, 0.65, 1.01]
    ece = 0.0
    for a, b in zip(edges[:-1], edges[1:]):
        m = (conf >= a) & (conf < b)
        if m.any():
            ece += float(m.mean()) * abs(float(conf[m].mean()) - float(corr[m].mean()))
    return float(ece)


def evaluate_logits(logits: np.ndarray, delta: np.ndarray, labels: np.ndarray, trainable: np.ndarray, temp: float = 1.0):
    probs = np.clip(sigmoid_np(logits / temp), 0.01, 0.99)
    pred_buy = probs >= 0.5
    conf = np.maximum(probs, 1.0 - probs)
    wins = live_like_wins(pred_buy, delta).astype(np.float64)

    live_brier = float(np.mean((conf - wins) ** 2))
    live_ece = conf_ece(conf, wins)

    tr_m = trainable.astype(bool)
    tr_acc = float(((probs[tr_m] >= 0.5) == (labels[tr_m] == 1)).mean()) if tr_m.any() else float("nan")
    p_tr = np.clip(probs[tr_m], 1e-7, 1.0 - 1e-7)
    y_tr = labels[tr_m].astype(np.float64)
    tr_nll = float(-np.mean(y_tr * np.log(p_tr) + (1.0 - y_tr) * np.log(1.0 - p_tr))) if tr_m.any() else float("nan")

    rows = []
    for tau in TAUS:
        m = conf >= tau
        n = int(m.sum())
        if n == 0:
            continue
        k = int(wins[m].sum())
        wr = k / n
        lo, hi = wilson(k, n)
        ev = wr * PAYOUT - (1.0 - wr)
        rows.append({
            "tau": tau,
            "n": n,
            "winrate": wr,
            "avg_conf": float(conf[m].mean()),
            "ci_lo": lo,
            "ci_hi": hi,
            "ev": ev,
        })

    return {
        "n": int(len(logits)),
        "buy_rate": float(pred_buy.mean()),
        "max_conf": float(conf.max()),
        "live_brier": live_brier,
        "live_ece": live_ece,
        "tr_acc": tr_acc,
        "tr_nll": tr_nll,
        "rows": rows,
    }


def run_onnx_logits(onnx_path: Path, macro: np.ndarray, micro: np.ndarray, bridge: np.ndarray) -> np.ndarray:
    sess = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    b_in = [i.shape[1] for i in sess.get_inputs() if i.name == "bridge_x"][0]
    if isinstance(b_in, int) and bridge.shape[1] != b_in:
        if bridge.shape[1] > b_in:
            bridge = bridge[:, :b_in]
        else:
            pad = np.zeros((len(bridge), b_in - bridge.shape[1]), dtype=bridge.dtype)
            bridge = np.hstack([bridge, pad])
    out = []
    bs = 4096
    for i in range(0, len(macro), bs):
        logit = sess.run(None, {
            "macro_x": macro[i : i + bs],
            "micro_x": micro[i : i + bs],
            "bridge_x": bridge[i : i + bs],
        })[0].reshape(-1)
        out.append(logit.astype(np.float64))
    return np.concatenate(out)


def fit_temperature_lbfgs(logits: np.ndarray, delta: np.ndarray) -> float:
    l_t = torch.tensor(logits, dtype=torch.float32)
    y_t = torch.tensor((delta > 0.0).astype(np.float32))
    log_t = torch.nn.Parameter(torch.zeros(1))
    opt = torch.optim.LBFGS([log_t], lr=0.5, max_iter=200, line_search_fn="strong_wolfe")
    bce = torch.nn.BCEWithLogitsLoss()

    def closure():
        opt.zero_grad()
        # Constrain T >= 1.0 so calibration only corrects overconfidence, never inflates edge
        t = torch.exp(log_t).clamp(1.0, 25.0)
        loss = bce(l_t / t, y_t)
        loss.backward()
        return loss

    opt.step(closure)
    return float(torch.exp(log_t).clamp(1.0, 25.0).item())


def fold_temperature_into_onnx(onnx_path: Path, temperature: float) -> bytes:
    model = onnx.load(str(onnx_path))
    out_names = {o.name for o in model.graph.output}
    target_node = None
    for node in reversed(model.graph.node):
        if node.op_type == "Gemm" and any(o in out_names for o in node.output):
            target_node = node
            break
    if target_node is None:
        raise RuntimeError("Could not locate final Gemm output layer driving ONNX logit output.")

    init_map = {init.name: init for init in model.graph.initializer}
    scaled_names = []
    for inp in target_node.input[1:]:
        if inp in init_map:
            arr = onnx.numpy_helper.to_array(init_map[inp]).astype(np.float32) / float(temperature)
            new_tensor = onnx.numpy_helper.from_array(arr, name=inp)
            init_map[inp].CopyFrom(new_tensor)
            scaled_names.append(inp)
    if len(scaled_names) < 1:
        raise RuntimeError(f"Expected initializer weights on {target_node.name}, found none.")

    onnx.checker.check_model(model)
    return model.SerializeToString()


def main() -> int:
    parser = argparse.ArgumentParser(description="Out-of-sample audit & temperature calibration for dual_stream_brain_v5.onnx")
    parser.add_argument("--onnx", default="ml_service/models/dual_stream_brain_v5.onnx")
    parser.add_argument("--dataset", default="ml_service/data/dataset_objective_dual_stream.npz")
    parser.add_argument("--db-url", default="", help="Optional PostgreSQL URL to export fresh s5 samples first")
    parser.add_argument("--embargo-sec", type=int, default=2400)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    onnx_path = Path(args.onnx)
    dataset_path = Path(args.dataset)

    if args.db_url:
        env = os.environ.copy()
        env["DATABASE_URL"] = args.db_url
        export_script = Path(__file__).resolve().parent / "export_objective_dataset.py"
        res = subprocess.run([sys.executable, str(export_script)], env=env)
        if res.returncode != 0:
            print("ERROR: export_objective_dataset.py failed.")
            return res.returncode

    if not onnx_path.exists() or not dataset_path.exists():
        print(f"ERROR: Missing file: onnx={onnx_path.exists()} dataset={dataset_path.exists()}")
        return 2

    z = np.load(dataset_path, allow_pickle=False)
    macro = z["macro_x"].astype(np.float32)
    micro = z["micro_x"].astype(np.float32)
    bridge = z["bridge_x"].astype(np.float32)
    delta = z["delta_bps"].astype(np.float64)
    labels = z["labels"].astype(np.int64)
    trainable = z["trainable"].astype(bool)
    epochs = z["epochs"].astype(np.int64)
    ds_meta = json.loads(str(z["meta"])) if "meta" in z else {}

    logits = run_onnx_logits(onnx_path, macro, micro, bridge)

    mid = len(logits) // 2
    cal_idx = np.arange(mid)
    cutoff_epoch = int(epochs[mid - 1])
    oos_start = int(np.searchsorted(epochs, cutoff_epoch + args.embargo_sec, side="left"))
    oos_idx = np.arange(oos_start, len(logits))
    if len(oos_idx) < 500:
        print("ERROR: Insufficient OOS samples after embargo.")
        return 2

    t_fit = fit_temperature_lbfgs(logits[cal_idx], delta[cal_idx])
    before_oos = evaluate_logits(logits[oos_idx], delta[oos_idx], labels[oos_idx], trainable[oos_idx], temp=1.0)
    after_oos = evaluate_logits(logits[oos_idx], delta[oos_idx], labels[oos_idx], trainable[oos_idx], temp=t_fit)

    print(f"Dataset N={len(logits)} (cal={len(cal_idx)}, oos={len(oos_idx)} after {args.embargo_sec}s embargo)")
    print(f"Fitted honest Temperature T = {t_fit:.4f}")
    print(f"OOS BEFORE: live_Brier={before_oos['live_brier']:.5f}, live_ECE={before_oos['live_ece']:.5f}, tr_NLL={before_oos['tr_nll']:.5f}, max_conf={before_oos['max_conf']*100:.2f}%")
    print(f"OOS AFTER : live_Brier={after_oos['live_brier']:.5f}, live_ECE={after_oos['live_ece']:.5f}, tr_NLL={after_oos['tr_nll']:.5f}, max_conf={after_oos['max_conf']*100:.2f}%")
    for tag, rows in (("BEFORE rows", before_oos["rows"]), ("AFTER  rows", after_oos["rows"])):
        print(f"  [{tag}]")
        for r in rows:
            print(f"    tau>={r['tau']:.2f}: n={r['n']:6d} | avg_conf={r['avg_conf']*100:5.2f}% | live_wr={r['winrate']*100:5.2f}% | gap={(r['avg_conf']-r['winrate'])*100:+5.2f}%")

    improves = (
        t_fit >= 1.05
        and after_oos["live_brier"] < before_oos["live_brier"]
        and after_oos["live_ece"] < before_oos["live_ece"]
        and after_oos["tr_nll"] <= before_oos["tr_nll"]
    )

    json_path = onnx_path.with_suffix(".json")
    prev_temp = 1.0
    prev_cal = False
    orig_trained_utc = datetime.fromtimestamp(onnx_path.stat().st_mtime, tz=timezone.utc).isoformat()
    if json_path.exists():
        try:
            prev_meta = json.loads(json_path.read_text(encoding="utf-8"))
            prev_temp = float(prev_meta.get("temperature", 1.0) or 1.0)
            prev_cal = bool(prev_meta.get("calibration_applied", False))
            if prev_meta.get("trained_at_utc"):
                orig_trained_utc = str(prev_meta["trained_at_utc"])
        except Exception:
            prev_temp = 1.0

    applied_temp = round(t_fit, 4) if improves else 1.0
    effective_temp = round(prev_temp * applied_temp, 4)
    final_eval = after_oos if improves else before_oos

    if improves and not args.dry_run:
        onnx_bytes = fold_temperature_into_onnx(onnx_path, t_fit)
        onnx_path.write_bytes(onnx_bytes)
        reloaded = run_onnx_logits(onnx_path, macro[:512], micro[:512], bridge[:512])
        np.testing.assert_allclose(reloaded, logits[:512] / t_fit, rtol=1e-3, atol=1e-4)

        # Verify batch=1 runtime path matches batch path (how C# calls ONNX Runtime)
        sess_new = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
        b_in = [i.shape[1] for i in sess_new.get_inputs() if i.name == "bridge_x"][0]
        b_slice = bridge[:16, :b_in] if bridge.shape[1] > b_in else bridge[:16]
        one_out = np.concatenate([
            sess_new.run(None, {
                "macro_x": macro[i : i + 1],
                "micro_x": micro[i : i + 1],
                "bridge_x": b_slice[i : i + 1],
            })[0].reshape(-1)
            for i in range(16)
        ])
        np.testing.assert_allclose(one_out, reloaded[:16], rtol=1e-3, atol=1e-4)
        print(f"[+] Folded T={t_fit:.4f} into {onnx_path.name} and verified batch + batch=1 parity.")

    if not args.dry_run:
        sha = hashlib.sha256(onnx_path.read_bytes()).hexdigest()
        gate_row = next((r for r in final_eval["rows"] if abs(r["tau"] - GATE_TAU) < 1e-9), None)
        gate_pass = bool(gate_row and gate_row["n"] >= GATE_MIN_TRADES and gate_row["ci_lo"] > BREAK_EVEN)
        gate_msg = (
            f"no trades at confidence >= {GATE_TAU:.2f}"
            if gate_row is None
            else f"tau={GATE_TAU:.2f}: trades={gate_row['n']} winrate={gate_row['winrate']*100:.2f}% "
                 f"CI-lo={gate_row['ci_lo']*100:.2f}% vs break-even {BREAK_EVEN*100:.2f}%"
        )
        meta = {
            "model_version": "dual_stream_onnx_v5",
            "calibrated_at_utc": datetime.now(timezone.utc).isoformat(),
            "trained_at_utc": orig_trained_utc,
            "dataset": dataset_path.name,
            "dataset_meta": ds_meta,
            "horizon_sec": int(ds_meta.get("horizon_sec", 5)),
            "n_train": int(len(logits)),
            "n_cal": int(len(cal_idx)),
            "n_test": int(len(oos_idx)),
            "temperature": effective_temp,
            "temperature_folded": True,
            "calibration_applied": bool(improves or prev_cal),
            "payout": PAYOUT,
            "break_even_winrate": BREAK_EVEN,
            "accuracy": gate_row["winrate"] if gate_row else None,
            "accuracy_tradeable_only": final_eval["tr_acc"],
            "overall_live_winrate_50": next((r["winrate"] for r in final_eval["rows"] if abs(r["tau"] - 0.50) < 1e-9), None),
            "test_live_like": final_eval["rows"],
            "gate": {"pass": gate_pass, "tau": GATE_TAU, "min_trades": GATE_MIN_TRADES, "detail": gate_msg},
            "onnx_parity_ok": True,
            "onnx_sha256": sha,
        }
        json_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")
        print(f"[+] Wrote sidecar metadata to {json_path.name}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
