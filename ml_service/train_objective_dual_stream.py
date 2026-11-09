"""
train_objective_dual_stream.py - Pure Multi-Timeframe Dual-Stream Neural Brain
Trained on objective forward-move labels exported by export_objective_dataset.py
(label horizon == live expiry, default 5s).

Features:
- Macro Stream (30, 5): 1D-CNN + LayerNorm -> Macro Context Vector
- Micro Stream (30, 5): 1D-CNN + LayerNorm -> Micro Dynamics Vector
- Bridge Vector (4): Dense Embedding
- Multi-Head Cross-Attention: Micro dynamics queries Macro context
- Exports to ONNX (dual_stream_brain_v5.onnx) for ~1ms in-process C# execution.

Safety: the new model is written as dual_stream_brain_v5.candidate.{onnx,json}. It replaces the production
dual_stream_brain_v5.onnx ONLY when the held-out quality gate passes (lower 95% Wilson bound of the live-like
win-rate at confidence >= 0.58 is above break-even) and ONNX parity is verified. Use --promote to force.
"""

import argparse
import copy
import hashlib
import json
import math
import os
import shutil
import sys
from datetime import datetime, timezone

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader


class DualStreamMTFBrain(nn.Module):
    def __init__(self, d_model=32, nhead=2, dropout=0.1, bridge_dim=5):
        super().__init__()
        self.d_model = d_model
        self.bridge_dim = bridge_dim

        # 1. Macro Stream (30, 5) -> 1D-CNN over time
        self.macro_conv1 = nn.Conv1d(5, d_model, kernel_size=3, padding=1)
        self.macro_conv2 = nn.Conv1d(d_model, d_model, kernel_size=5, padding=2)
        self.macro_norm = nn.LayerNorm(d_model)

        # 2. Micro Stream (30, 5) -> 1D-CNN over time
        self.micro_conv1 = nn.Conv1d(5, d_model, kernel_size=3, padding=1)
        self.micro_conv2 = nn.Conv1d(d_model, d_model, kernel_size=5, padding=2)
        self.micro_norm = nn.LayerNorm(d_model)

        # 3. Cross-Attention: Micro queries Macro
        self.cross_attn = nn.MultiheadAttention(embed_dim=d_model, num_heads=nhead, dropout=dropout, batch_first=True)
        self.attn_norm = nn.LayerNorm(d_model)

        # 4. Bridge Feature Embedding (5 features: dist_m1_ema, atr_ratio, z_score, wick_bal, norm_crowd_sentiment)
        self.bridge_dense = nn.Sequential(
            nn.Linear(bridge_dim, d_model // 2),
            nn.GELU(),
            nn.Linear(d_model // 2, d_model // 2)
        )

        # 5. Fusion & Classifier Head
        fusion_dim = d_model * 2 + d_model // 2
        self.classifier = nn.Sequential(
            nn.Linear(fusion_dim, d_model),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(d_model, 16),
            nn.GELU(),
            nn.Linear(16, 1)  # Output logit: P(BUY)
        )

    def forward(self, macro_x, micro_x, bridge_x):
        # macro_x: (B, 30, 5), micro_x: (B, 30, 5), bridge_x: (B, 4)
        macro_x = torch.nan_to_num(macro_x, nan=0.0, posinf=50.0, neginf=-50.0)
        micro_x = torch.nan_to_num(micro_x, nan=0.0, posinf=50.0, neginf=-50.0)
        bridge_x = torch.nan_to_num(bridge_x, nan=0.0, posinf=50.0, neginf=-50.0)

        m_in = macro_x.transpose(1, 2)
        m_h = F.gelu(self.macro_conv1(m_in))
        m_h = F.gelu(self.macro_conv2(m_h))
        m_h = self.macro_norm(m_h.transpose(1, 2))  # (B, 30, d_model)

        u_in = micro_x.transpose(1, 2)
        u_h = F.gelu(self.micro_conv1(u_in))
        u_h = F.gelu(self.micro_conv2(u_h))
        u_h = self.micro_norm(u_h.transpose(1, 2))  # (B, 30, d_model)

        attn_out, _ = self.cross_attn(query=u_h, key=m_h, value=m_h)
        u_fused = self.attn_norm(u_h + attn_out)

        m_pooled = m_h.mean(dim=1)
        u_pooled = u_fused.mean(dim=1)
        b_emb = self.bridge_dense(bridge_x)

        fused = torch.cat([m_pooled, u_pooled, b_emb], dim=-1)
        return self.classifier(fused)


PAYOUT = 0.82
BREAK_EVEN = 1.0 / (1.0 + PAYOUT)   # ~0.5495: win-rate required for EV = 0
GATE_TAU = 0.58                     # live entry threshold (orchestrator requiredProb)
GATE_MIN_TRADES = 300               # minimum trades at GATE_TAU for a meaningful verdict
TAUS = (0.50, 0.52, 0.54, 0.56, 0.58, 0.60, 0.62)


def wilson(k, n, z=1.96):
    """95% Wilson score interval for a binomial proportion."""
    if n <= 0:
        return (float("nan"), float("nan"))
    p_hat = k / n
    denom = 1.0 + z * z / n
    centre = (p_hat + z * z / (2.0 * n)) / denom
    half = z * math.sqrt(p_hat * (1.0 - p_hat) / n + z * z / (4.0 * n * n)) / denom
    return (centre - half, centre + half)


def ece_score(probs, y, bins=10):
    """Expected calibration error of P(BUY) against the realised direction label."""
    probs = np.asarray(probs, dtype=np.float64)
    y = np.asarray(y, dtype=np.float64)
    if len(probs) == 0:
        return float("nan")
    edges = np.linspace(0.0, 1.0, bins + 1)
    ece = 0.0
    for i in range(bins):
        hi_ok = probs <= edges[i + 1] if i == bins - 1 else probs < edges[i + 1]
        m = (probs >= edges[i]) & hi_ok
        if m.any():
            ece += m.mean() * abs(probs[m].mean() - y[m].mean())
    return float(ece)


def live_like_wins(pred_buy, delta_bps):
    """A live trade wins only on a strict move in the predicted direction. Exact tie == loss (ATM rule)."""
    return (pred_buy & (delta_bps > 0)) | ((~pred_buy) & (delta_bps < 0))


def summarize_stream(probs, delta_bps, label):
    """Win-rate / EV table over confidence thresholds for (calibrated P(BUY), forward delta)."""
    pred_buy = probs >= 0.5
    conf = np.maximum(probs, 1.0 - probs)
    wins = live_like_wins(pred_buy, delta_bps)
    rows = []
    print(f"\n  [{label}]  N={len(probs)}   break-even win-rate = {BREAK_EVEN * 100:.2f}% (payout {PAYOUT * 100:.0f}%)")
    print(f"  {'tau':>5} | {'trades':>7} | {'coverage':>8} | {'winrate':>8} | {'95% CI':>15} | {'EV/trade':>9}")
    for tau in TAUS:
        m = conf >= tau
        n = int(m.sum())
        if n == 0:
            continue
        k = int(wins[m].sum())
        wr = k / n
        lo, hi = wilson(k, n)
        ev = wr * PAYOUT - (1.0 - wr)
        rows.append({"tau": tau, "n": n, "winrate": wr, "ci_lo": lo, "ci_hi": hi, "ev": ev})
        print(f"  {tau:>5.2f} | {n:>7d} | {n / len(probs) * 100:>7.1f}% | {wr * 100:>7.2f}% | "
              f"[{lo * 100:5.2f}, {hi * 100:5.2f}] | {ev * 100:>+8.2f}%")
    return rows


def predict_logits(model, loader, device):
    model.eval()
    out = []
    with torch.no_grad():
        for m_x, u_x, b_x, _y in loader:
            out.append(model(m_x.to(device), u_x.to(device), b_x.to(device)).squeeze(-1).cpu())
    return torch.cat(out) if out else torch.zeros(0)


def chronological_split(epochs_arr, N, embargo_sec=2400):
    """Train 80% / Val 10% / Test 10% in time order with a temporal embargo between segments.
    Returns (train_end, val_start, val_end, test_start)."""
    train_n = int(0.80 * N)
    span_ok = epochs_arr is not None and N > 500 and (int(epochs_arr[-1]) - int(epochs_arr[0])) > embargo_sec * 4
    if span_ok:
        train_cutoff = int(epochs_arr[max(0, train_n - 1)])
        val_start = int(np.searchsorted(epochs_arr, train_cutoff + embargo_sec, side="left"))
        val_end = min(N, val_start + int(0.10 * N))
        val_cutoff = int(epochs_arr[max(0, min(N - 1, val_end - 1))])
        ts_start = int(np.searchsorted(epochs_arr, val_cutoff + embargo_sec, side="left"))
        if val_start < N and ts_start < N:
            return train_n, val_start, val_end, ts_start
    embargo = 30 if N > 500 else 0
    val_start = min(N, train_n + embargo)
    val_end = min(N, val_start + int(0.10 * N))
    ts_start = min(N, val_end + embargo)
    return train_n, val_start, val_end, ts_start


def onnx_probs(path, macro, micro, bridge):
    """P(BUY) from an ONNX graph whose output is a logit; None if it cannot be evaluated."""
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        b_inputs = [inp for inp in sess.get_inputs() if inp.name == "bridge_x"]
        if b_inputs and len(b_inputs[0].shape) >= 2 and isinstance(b_inputs[0].shape[1], int):
            expected_b_dim = b_inputs[0].shape[1]
            if bridge.shape[1] > expected_b_dim:
                bridge = bridge[:, :expected_b_dim]
            elif bridge.shape[1] < expected_b_dim:
                pad = np.zeros((len(bridge), expected_b_dim - bridge.shape[1]), dtype=bridge.dtype)
                bridge = np.hstack([bridge, pad])
        probs = []
        for i in range(0, len(macro), 1024):
            logit = sess.run(None, {"macro_x": macro[i:i + 1024], "micro_x": micro[i:i + 1024],
                                    "bridge_x": bridge[i:i + 1024]})[0].reshape(-1)
            probs.append(1.0 / (1.0 + np.exp(-logit.astype(np.float64))))
        return np.concatenate(probs)
    except Exception as e:  # noqa: BLE001
        print(f"  (could not evaluate ONNX model {os.path.basename(path)}: {e})")
        return None


def train_model():
    # Windows consoles (cp1251/cp866) cannot print the emoji torch.onnx emits; never let logging crash a run.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:  # noqa: BLE001
            pass
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description="Train objective dual-stream brain; export candidate ONNX; gated promote")
    ap.add_argument("--dataset", default=os.path.join(here, "data", "dataset_objective_dual_stream.npz"))
    ap.add_argument("--models-dir", default=os.path.join(here, "models"))
    ap.add_argument("--epochs", type=int, default=25)
    ap.add_argument("--promote", action="store_true",
                    help="Overwrite dual_stream_brain_v5.onnx even if the quality gate fails (NOT recommended)")
    args = ap.parse_args()

    torch.manual_seed(42)
    np.random.seed(42)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(42)

    if not os.path.exists(args.dataset):
        print(f"Dataset not found: {args.dataset}")
        return 2

    print(f"Loading objective dataset: {args.dataset}")
    data = np.load(args.dataset, allow_pickle=False)
    macro_x, micro_x, bridge_x, labels = data["macro_x"], data["micro_x"], data["bridge_x"], data["labels"]
    N = len(labels)
    epochs_arr = data["epochs"] if "epochs" in data else None
    has_delta = "delta_bps" in data
    delta_bps = data["delta_bps"].astype(np.float64) if has_delta else np.where(labels == 1, 1.0, -1.0)
    trainable = data["trainable"].astype(bool) if "trainable" in data else np.ones(N, dtype=bool)
    asset_ids = data["asset_ids"] if "asset_ids" in data else None
    ds_meta = json.loads(str(data["meta"])) if "meta" in data else {}
    if not has_delta:
        print("WARNING: old-format dataset (no delta_bps/trainable): live-like evaluation is NOT possible. "
              "Re-export with export_objective_dataset.py.")

    if epochs_arr is not None:
        order = np.argsort(epochs_arr, kind="stable")
        macro_x, micro_x, bridge_x, labels = macro_x[order], micro_x[order], bridge_x[order], labels[order]
        delta_bps, trainable = delta_bps[order], trainable[order]
        asset_ids = asset_ids[order] if asset_ids is not None else None
        epochs_arr = epochs_arr[order]

    horizon = ds_meta.get("horizon_sec")
    print(f"Total samples: {N} | trainable: {int(trainable.sum())} | label horizon: {horizon if horizon else '?'}s")
    if horizon not in (None, 5):
        print(f"WARNING: dataset horizon is {horizon}s but live s5 trades expire after 5s.")

    train_end, val_start, val_end, ts_start = chronological_split(epochs_arr, N)
    tr_idx = np.arange(0, train_end)
    val_idx = np.arange(val_start, val_end)
    ts_idx = np.arange(ts_start, N)
    tr_fit = tr_idx[trainable[tr_idx]]
    val_fit = val_idx[trainable[val_idx]]
    print(f"Split sizes (all):       Train={len(tr_idx)}, Val={len(val_idx)}, Test={len(ts_idx)}")
    print(f"Split sizes (trainable): Train={len(tr_fit)}, Val={len(val_fit)}")
    if len(tr_fit) < 200 or len(val_fit) < 50 or len(ts_idx) < 50:
        print("ERROR: not enough samples in one of the splits.")
        return 2

    def make_ds(idx):
        return TensorDataset(torch.from_numpy(macro_x[idx]), torch.from_numpy(micro_x[idx]),
                             torch.from_numpy(bridge_x[idx]), torch.from_numpy(labels[idx]).float())

    tr_loader = DataLoader(make_ds(tr_fit), batch_size=128, shuffle=True)
    val_loader = DataLoader(make_ds(val_fit), batch_size=128, shuffle=False)
    ts_loader = DataLoader(make_ds(ts_idx), batch_size=128, shuffle=False)
    ts_macro, ts_micro, ts_bridge = macro_x[ts_idx], micro_x[ts_idx], bridge_x[ts_idx]

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Training on device: {device}")

    bridge_dim = int(bridge_x.shape[1]) if bridge_x.ndim == 2 else 5
    model = DualStreamMTFBrain(d_model=32, nhead=2, dropout=0.15, bridge_dim=bridge_dim).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4)
    epochs = args.epochs
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs, eta_min=1e-5)

    y_tr = labels[tr_fit]
    pos_count, neg_count = float(np.sum(y_tr == 1)), float(np.sum(y_tr == 0))
    pos_weight_val = neg_count / max(pos_count, 1.0)
    print(f"Class Distribution (train, trainable): BUY={int(pos_count)}, PUT={int(neg_count)} (pos_weight: {pos_weight_val:.4f})")
    criterion = nn.BCEWithLogitsLoss(pos_weight=torch.tensor([pos_weight_val], dtype=torch.float32, device=device))
    val_criterion = nn.BCEWithLogitsLoss()
    val_y_t = torch.from_numpy(labels[val_fit]).float()

    best_val_loss, best_weights, best_epoch = float("inf"), None, 0
    print("\nStarting training loop (model selection by validation log-loss)...")
    for epoch in range(1, epochs + 1):
        model.train()
        total_loss, correct, total = 0.0, 0, 0
        for m_x, u_x, b_x, y in tr_loader:
            m_x, u_x, b_x, y = m_x.to(device), u_x.to(device), b_x.to(device), y.to(device)
            optimizer.zero_grad()
            logits = model(m_x, u_x, b_x).squeeze(-1)
            loss = criterion(logits, y)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
            optimizer.step()
            total_loss += loss.item() * len(y)
            correct += ((torch.sigmoid(logits) >= 0.5).float() == y).sum().item()
            total += len(y)
        scheduler.step()

        v_logits = predict_logits(model, val_loader, device)
        v_loss = float(val_criterion(v_logits, val_y_t).item())
        v_acc = float(((torch.sigmoid(v_logits) >= 0.5).float() == val_y_t).float().mean().item())
        print(f"Epoch {epoch:02d}/{epochs:02d} | Train Loss: {total_loss / total:.4f} | Train Acc: {correct / total * 100:.2f}% | "
              f"Val Loss: {v_loss:.4f} | Val Acc: {v_acc * 100:.2f}%")
        if v_loss < best_val_loss:
            best_val_loss, best_epoch = v_loss, epoch
            best_weights = copy.deepcopy(model.state_dict())

    print(f"\nBest epoch by validation log-loss: {best_epoch} (val loss {best_val_loss:.4f})")
    if best_weights is not None:
        model.load_state_dict(best_weights)

    # Temperature scaling on the validation set (Guo et al., 2017), parametrised as log T for stability
    val_logits_all = predict_logits(model, val_loader, device)
    log_t = nn.Parameter(torch.zeros(1))
    opt_t = torch.optim.LBFGS([log_t], lr=0.5, max_iter=200, line_search_fn="strong_wolfe")
    bce = nn.BCEWithLogitsLoss()

    def closure():
        opt_t.zero_grad()
        t = torch.exp(log_t).clamp(0.05, 20.0)
        loss = bce(val_logits_all / t, val_y_t)
        loss.backward()
        return loss

    opt_t.step(closure)
    T = float(torch.exp(log_t).clamp(0.05, 20.0).item())
    ece_raw = ece_score(torch.sigmoid(val_logits_all).numpy(), val_y_t.numpy())
    ece_cal = ece_score(torch.sigmoid(val_logits_all / T).numpy(), val_y_t.numpy())
    print(f"Temperature T = {T:.4f} | Val ECE raw -> calibrated: {ece_raw:.4f} -> {ece_cal:.4f}")

    # Test evaluation with CALIBRATED probabilities (what the C# runtime will see)
    p_test = torch.sigmoid(predict_logits(model, ts_loader, device) / T).numpy().astype(np.float64)
    d_test, y_test, tr_mask = delta_bps[ts_idx], labels[ts_idx], trainable[ts_idx]

    print("\n================ TEST EVALUATION (held-out, calibrated) ================")
    acc_tr = None
    if tr_mask.any():
        n_tr = int(tr_mask.sum())
        k_tr = int(np.sum((p_test[tr_mask] >= 0.5) == (y_test[tr_mask] == 1)))
        acc_tr = k_tr / n_tr
        lo, hi = wilson(k_tr, n_tr)
        print(f"Tradeable-move samples only: acc={acc_tr * 100:.2f}% [{lo * 100:.2f}, {hi * 100:.2f}] (N={n_tr})"
              f"  -- optimistic: live also trades the noise zone")
        print(f"Test ECE (trainable): {ece_score(p_test[tr_mask], y_test[tr_mask]):.4f}")
    full_rows = summarize_stream(p_test, d_test, "FULL live-like stream (noise-zone trades included, tie = loss)")

    print("\n  Baselines on the same full test stream (win-rate, tie = loss):")
    last_bar_up = micro_x[ts_idx][:, -2, 0] < 0   # previous s5 close below the reference (last) close
    for name, pred in (("always BUY", np.ones(len(ts_idx), dtype=bool)),
                       ("momentum (follow last bar)", last_bar_up),
                       ("mean-reversion (fade last bar)", ~last_bar_up)):
        w = live_like_wins(pred, d_test)
        lo, hi = wilson(int(w.sum()), len(w))
        print(f"    {name:<32} {w.mean() * 100:6.2f}%  [{lo * 100:5.2f}, {hi * 100:5.2f}]")

    if asset_ids is not None and ds_meta.get("assets"):
        print(f"\n  Per-asset live-like win-rate at confidence >= {GATE_TAU:.2f}:")
        conf = np.maximum(p_test, 1.0 - p_test)
        a_test = asset_ids[ts_idx]
        wins = live_like_wins(p_test >= 0.5, d_test)
        for a, name in enumerate(ds_meta["assets"]):
            m = (a_test == a) & (conf >= GATE_TAU)
            n = int(m.sum())
            if n:
                lo, hi = wilson(int(wins[m].sum()), n)
                print(f"    {name:<10} trades={n:>6d}  winrate={wins[m].mean() * 100:6.2f}%  [{lo * 100:5.2f}, {hi * 100:5.2f}]")

    prod_onnx = os.path.join(args.models_dir, "dual_stream_brain_v5.onnx")
    if os.path.exists(prod_onnx):
        p_cur = onnx_probs(prod_onnx, ts_macro, ts_micro, ts_bridge)
        if p_cur is not None:
            summarize_stream(p_cur, d_test, "CURRENT production ONNX on this test stream (informational; may have seen this period)")

    # Quality gate
    gate_row = next((r for r in full_rows if abs(r["tau"] - GATE_TAU) < 1e-9), None)
    gate_pass = bool(gate_row and gate_row["n"] >= GATE_MIN_TRADES and gate_row["ci_lo"] > BREAK_EVEN)
    gate_msg = (f"no trades at confidence >= {GATE_TAU:.2f}" if gate_row is None else
                f"tau={GATE_TAU:.2f}: trades={gate_row['n']} winrate={gate_row['winrate'] * 100:.2f}% "
                f"CI-lo={gate_row['ci_lo'] * 100:.2f}% vs break-even {BREAK_EVEN * 100:.2f}% (need >= {GATE_MIN_TRADES} trades)")
    print(f"\nQUALITY GATE: {'PASS' if gate_pass else 'FAIL'} -> {gate_msg}")

    # Save candidate weights + ONNX (temperature folded into the last linear layer)
    models_dir = args.models_dir
    os.makedirs(models_dir, exist_ok=True)
    pt_path = os.path.join(models_dir, "dual_stream_brain_v5.candidate.pt")
    torch.save(model.state_dict(), pt_path)
    print(f"Saved PyTorch candidate to: {pt_path}")

    cand_onnx = os.path.join(models_dir, "dual_stream_brain_v5.candidate.onnx")
    export_model = copy.deepcopy(model).to("cpu").eval()
    if abs(T - 1.0) > 1e-4:
        with torch.no_grad():
            export_model.classifier[-1].weight.data.div_(T)
            if export_model.classifier[-1].bias is not None:
                export_model.classifier[-1].bias.data.div_(T)
        print(f"Folded Temperature Scaling (1/{T:.4f}) into export linear classifier.")

    dummy = (torch.randn(1, 30, 5), torch.randn(1, 30, 5), torch.randn(1, bridge_dim))
    print(f"Exporting to ONNX: {cand_onnx}...")
    for stale in (cand_onnx, cand_onnx + ".data"):
        if os.path.exists(stale):
            os.remove(stale)
    # Legacy TorchScript exporter (dynamo=False): single-file graph with a truly dynamic batch axis.
    # The dynamo exporter bakes batch=1 into MultiheadAttention reshapes and writes external .onnx.data,
    # which the C# runtime (loads one file) would not find.
    torch.onnx.export(
        export_model, dummy, cand_onnx, export_params=True, opset_version=17, do_constant_folding=True,
        input_names=["macro_x", "micro_x", "bridge_x"], output_names=["logit"],
        dynamic_axes={"macro_x": {0: "batch_size"}, "micro_x": {0: "batch_size"},
                      "bridge_x": {0: "batch_size"}, "logit": {0: "batch_size"}},
        dynamo=False)
    if os.path.exists(cand_onnx + ".data"):
        raise RuntimeError("ONNX export produced external data (.onnx.data); the C# runtime needs a single-file graph.")
    print(f"ONNX export successful! Size: {os.path.getsize(cand_onnx) / 1024:.1f} KB")

    import onnx
    onnx.checker.check_model(onnx.load(cand_onnx))
    print("ONNX structural validation passed (onnx.checker.check_model: OK)")

    # Parity on REAL held-out samples: ONNX(folded) == PyTorch(folded) == calibrated probability
    parity_ok = False
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(cand_onnx, providers=["CPUExecutionProvider"])
        n_par = min(512, len(ts_idx))
        feeds = {"macro_x": ts_macro[:n_par], "micro_x": ts_micro[:n_par], "bridge_x": ts_bridge[:n_par]}
        ort_out = sess.run(None, feeds)[0].reshape(-1)
        with torch.no_grad():
            pt_out = export_model(torch.from_numpy(feeds["macro_x"]), torch.from_numpy(feeds["micro_x"]),
                                  torch.from_numpy(feeds["bridge_x"])).numpy().reshape(-1)
        np.testing.assert_allclose(pt_out, ort_out, rtol=1e-3, atol=1e-4)
        p_onnx = 1.0 / (1.0 + np.exp(-ort_out.astype(np.float64)))
        np.testing.assert_allclose(p_onnx, p_test[:n_par], rtol=1e-3, atol=2e-4)
        # The C# runtime always calls with batch=1: verify that path sample by sample too.
        n_one = min(16, n_par)
        one_out = np.concatenate([
            sess.run(None, {k: v[i:i + 1] for k, v in feeds.items()})[0].reshape(-1) for i in range(n_one)])
        np.testing.assert_allclose(one_out, ort_out[:n_one], rtol=1e-3, atol=1e-4)
        parity_ok = True
        print(f"ONNX Runtime parity verified on {n_par} real test samples (batch and batch=1; equals calibrated probabilities).")
    except Exception as e:  # noqa: BLE001
        print(f"ONNX parity check FAILED: {e}")

    with open(cand_onnx, "rb") as f:
        sha = hashlib.sha256(f.read()).hexdigest()
    gate_live = next((r for r in full_rows if abs(r["tau"] - GATE_TAU) < 1e-9), None)
    meta = {
        "model_version": "dual_stream_onnx_v5",
        "trained_at_utc": datetime.now(timezone.utc).isoformat(),
        "dataset": os.path.basename(args.dataset),
        "dataset_meta": ds_meta,
        "horizon_sec": horizon,
        "n_train": int(len(tr_fit)), "n_val": int(len(val_fit)), "n_test": int(len(ts_idx)),
        "temperature": T, "best_epoch": best_epoch,
        "payout": PAYOUT, "break_even_winrate": BREAK_EVEN,
        # Read by the C# runtime (DualStreamOnnxService) instead of hardcoded numbers.
        # accuracy = live-like win-rate at the entry gate (tie = loss), NOT the optimistic tradeable-only accuracy.
        "accuracy": gate_live["winrate"] if gate_live else None,
        "accuracy_tradeable_only": acc_tr,
        "test_live_like": full_rows,
        "gate": {"pass": gate_pass, "tau": GATE_TAU, "min_trades": GATE_MIN_TRADES, "detail": gate_msg},
        "onnx_parity_ok": parity_ok,
        "onnx_sha256": sha,
    }
    cand_json = os.path.join(models_dir, "dual_stream_brain_v5.candidate.json")
    with open(cand_json, "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
    print(f"Saved metadata: {cand_json}")

    prod_json = os.path.join(models_dir, "dual_stream_brain_v5.json")
    if (gate_pass and parity_ok) or args.promote:
        if os.path.exists(prod_onnx):
            stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            shutil.copy2(prod_onnx, os.path.join(models_dir, f"dual_stream_brain_v5.{stamp}.bak.onnx"))
            if os.path.exists(prod_json):
                shutil.copy2(prod_json, os.path.join(models_dir, f"dual_stream_brain_v5.{stamp}.bak.json"))
        shutil.copy2(cand_onnx, prod_onnx)
        shutil.copy2(cand_json, prod_json)
        print(f"PROMOTED candidate -> {prod_onnx}" + ("" if gate_pass else "  (forced by --promote, gate FAILED)"))
        return 0
    print("NOT promoted: production dual_stream_brain_v5.onnx left untouched (gate or parity failed). "
          "Candidate files saved next to it; re-run with --promote to force.")
    return 1


if __name__ == "__main__":
    sys.exit(train_model())
