"""
benchmark_brains.py - SOTA Architecture Head-to-Head Benchmark for Binary Options

Compares two cutting-edge neural architectures on the exact same data split:
- Model A: SequenceBrain (Conv1D + BiGRU + Attention Pooling + Macro Fusion)
- Model B: PatchTST-Lite (Dual-Stream Patch Transformer + Cross-Attention + Macro Fusion)

Key Evaluation Metrics:
1. Selective WinRate @ 60%, 65%, 70% Confidence Thresholds
2. Number of high-conviction sniper trades taken
3. Inference Latency (ms) - crucial for binary options
4. Out-of-sample Cross-Entropy Loss & Accuracy
"""

import os
import sys
import time
import argparse
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

script_dir = os.path.dirname(os.path.abspath(__file__))
if script_dir not in sys.path:
    sys.path.insert(0, script_dir)

from models.sequence_brain import SequenceBrain
from models.patch_tst_brain import DualStreamPatchBrain


class FocalLoss(nn.Module):
    def __init__(self, alpha=None, gamma=2.0):
        super().__init__()
        self.alpha = alpha
        self.gamma = gamma

    def forward(self, inputs, targets):
        ce_loss = F.cross_entropy(inputs, targets, reduction="none")
        pt = torch.exp(-ce_loss)
        focal_loss = ((1.0 - pt) ** self.gamma) * ce_loss
        if self.alpha is not None:
            at = self.alpha.gather(0, targets)
            focal_loss = at * focal_loss
        return focal_loss.mean()


def evaluate_model(model, loader, device, model_type="sequence"):
    model.eval()
    total_loss = 0.0
    all_targets = []
    all_probs = []

    # Measure latency on single inference
    latencies = []

    with torch.no_grad():
        for batch in loader:
            batch_xc, batch_xmtf, batch_xm, batch_y, batch_pnl = [b.to(device) for b in batch]

            t0 = time.perf_counter()
            if model_type == "sequence":
                logits, aux_pnl = model(batch_xc, batch_xm)
            else:
                logits, aux_pnl = model(batch_xc, batch_xmtf, batch_xm)
            t1 = time.perf_counter()
            latencies.append((t1 - t0) / len(batch_y) * 1000.0)  # ms per sample

            loss = F.cross_entropy(logits, batch_y)
            total_loss += loss.item() * len(batch_y)

            probs = F.softmax(logits, dim=-1)
            all_targets.extend(batch_y.cpu().numpy())
            all_probs.extend(probs.cpu().numpy())

    total_samples = len(all_targets)
    avg_loss = total_loss / max(1, total_samples)
    all_targets = np.array(all_targets)
    all_probs = np.array(all_probs)
    preds = np.argmax(all_probs, dim=-1) if hasattr(all_probs, 'dim') else np.argmax(all_probs, axis=-1)

    overall_acc = np.mean(preds == all_targets) * 100.0
    avg_latency = float(np.mean(latencies))

    stats = {
        "loss": avg_loss,
        "acc": overall_acc,
        "latency_ms": avg_latency,
        "total": total_samples
    }

    # Selective WinRate for directional signals (excluding HOLD)
    for th in (0.50, 0.60, 0.65, 0.70):
        buy_mask = (all_probs[:, 1] >= th)
        put_mask = (all_probs[:, 2] >= th)
        dir_mask = buy_mask | put_mask
        n_trades = int(np.sum(dir_mask))

        if n_trades > 0:
            trade_preds = np.where(buy_mask[dir_mask], 1, 2)
            trade_actuals = all_targets[dir_mask]
            win_rate = float(np.mean(trade_preds == trade_actuals) * 100.0)
        else:
            win_rate = 0.0

        key = int(th * 100)
        stats[f"wr_{key}"] = win_rate
        stats[f"n_{key}"] = n_trades

    return stats


def run_benchmark(data_path="ml_service/data/dataset_v3.npz", epochs=6, batch_size=64, lr=1e-3, val_split=0.20):
    print("=" * 80)
    print("🥊 SOTA NEURAL ARCHITECTURE BENCHMARK FOR BINARY OPTIONS")
    print(f"Dataset: {data_path} | Train/Val Split: {int((1-val_split)*100)}% / {int(val_split*100)}%")
    print("=" * 80)

    data = np.load(data_path)
    X_candles = data["X_candles"]
    X_mtf = data.get("X_mtf_candles")
    if X_mtf is None:
        X_mtf = np.zeros((len(X_candles), 30, 5), dtype=np.float32)
    X_macro = data["X_macro"]
    y = data["y"]
    y_pnl = data["y_pnl"]

    N = len(y)
    split_idx = int(N * (1.0 - val_split))

    print(f"[+] Loaded {N} samples:")
    print(f"    Micro candles: {X_candles.shape} (160 timesteps)")
    print(f"    MTF candles:   {X_mtf.shape} (30 timesteps)")
    print(f"    Macro vector:  {X_macro.shape} (23 features)")
    print(f"    Split: Train={split_idx}, Val={N - split_idx}")

    # Compute balanced weights
    class_counts = np.bincount(y[:split_idx], minlength=3)
    weights = len(y[:split_idx]) / (3.0 * np.maximum(class_counts, 1.0))
    weights = np.clip(weights, 0.2, 8.0).astype(np.float32)
    print(f"    Focal Loss Weights: HOLD={weights[0]:.2f}, BUY={weights[1]:.2f}, PUT={weights[2]:.2f}\n")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"[+] Benchmark compute device: {device}\n")

    # Build Loaders
    train_ds = TensorDataset(
        torch.tensor(X_candles[:split_idx], dtype=torch.float32),
        torch.tensor(X_mtf[:split_idx], dtype=torch.float32),
        torch.tensor(X_macro[:split_idx], dtype=torch.float32),
        torch.tensor(y[:split_idx], dtype=torch.long),
        torch.tensor(y_pnl[:split_idx], dtype=torch.float32)
    )
    val_ds = TensorDataset(
        torch.tensor(X_candles[split_idx:], dtype=torch.float32),
        torch.tensor(X_mtf[split_idx:], dtype=torch.float32),
        torch.tensor(X_macro[split_idx:], dtype=torch.float32),
        torch.tensor(y[split_idx:], dtype=torch.long),
        torch.tensor(y_pnl[split_idx:], dtype=torch.float32)
    )

    train_loader = DataLoader(train_ds, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_ds, batch_size=batch_size, shuffle=False)

    alpha_t = torch.tensor(weights, dtype=torch.float32).to(device)

    # -------------------------------------------------------------
    # 1. Train Model A: SequenceBrain (Conv1D + BiGRU + Attention)
    # -------------------------------------------------------------
    print("-" * 80)
    print("🔥 TRAINING MODEL A: SequenceBrain (Conv1D + BiGRU + Temporal Attention)")
    print("-" * 80)
    model_a = SequenceBrain(
        candle_features=5,
        seq_len=160,
        macro_features=X_macro.shape[1],
        d_model=64,
        num_layers=2,
        num_classes=3,
        dropout=0.25,
        backbone="hybrid"
    ).to(device)

    opt_a = torch.optim.AdamW(model_a.parameters(), lr=lr, weight_decay=1e-4)
    crit_a = FocalLoss(alpha=alpha_t, gamma=2.0)
    crit_pnl = nn.SmoothL1Loss()

    for ep in range(1, epochs + 1):
        model_a.train()
        for batch_xc, batch_xmtf, batch_xm, batch_y, batch_pnl in train_loader:
            batch_xc, batch_xm, batch_y, batch_pnl = batch_xc.to(device), batch_xm.to(device), batch_y.to(device), batch_pnl.to(device)
            opt_a.zero_grad()
            logits, aux = model_a(batch_xc, batch_xm)
            loss = crit_a(logits, batch_y) + 0.1 * crit_pnl(aux, batch_pnl / 10.0)
            loss.backward()
            opt_a.step()

    stats_a = evaluate_model(model_a, val_loader, device, model_type="sequence")
    print(f"[Model A Done] Val Loss: {stats_a['loss']:.4f} | Acc: {stats_a['acc']:.2f}% | Latency: {stats_a['latency_ms']:.3f} ms")
    print(f"               WR @50%: {stats_a['wr_50']:.1f}% ({stats_a['n_50']} trades)")
    print(f"               WR @60%: {stats_a['wr_60']:.1f}% ({stats_a['n_60']} trades)")
    print(f"               WR @65%: {stats_a['wr_65']:.1f}% ({stats_a['n_65']} trades)")
    print(f"               WR @70%: {stats_a['wr_70']:.1f}% ({stats_a['n_70']} trades)")

    # -------------------------------------------------------------
    # 2. Train Model B: PatchTST-Lite (Dual-Stream Cross-Attention)
    # -------------------------------------------------------------
    print("\n" + "-" * 80)
    print("🔥 TRAINING MODEL B: PatchTST-Lite (Dual-Stream Patch Transformer + Cross-Attention)")
    print("-" * 80)
    model_b = DualStreamPatchBrain(
        candle_features=5,
        seq_len_micro=160,
        seq_len_mtf=30,
        macro_features=X_macro.shape[1],
        d_model=64,
        nhead=4,
        num_layers=2,
        num_classes=3,
        dropout=0.25
    ).to(device)

    opt_b = torch.optim.AdamW(model_b.parameters(), lr=lr, weight_decay=1e-4)
    crit_b = FocalLoss(alpha=alpha_t, gamma=2.0)

    for ep in range(1, epochs + 1):
        model_b.train()
        for batch_xc, batch_xmtf, batch_xm, batch_y, batch_pnl in train_loader:
            batch_xc, batch_xmtf, batch_xm, batch_y, batch_pnl = [b.to(device) for b in (batch_xc, batch_xmtf, batch_xm, batch_y, batch_pnl)]
            opt_b.zero_grad()
            logits, aux = model_b(batch_xc, batch_xmtf, batch_xm)
            loss = crit_b(logits, batch_y) + 0.1 * crit_pnl(aux, batch_pnl / 10.0)
            loss.backward()
            opt_b.step()

    stats_b = evaluate_model(model_b, val_loader, device, model_type="patch_tst")
    print(f"[Model B Done] Val Loss: {stats_b['loss']:.4f} | Acc: {stats_b['acc']:.2f}% | Latency: {stats_b['latency_ms']:.3f} ms")
    print(f"               WR @50%: {stats_b['wr_50']:.1f}% ({stats_b['n_50']} trades)")
    print(f"               WR @60%: {stats_b['wr_60']:.1f}% ({stats_b['n_60']} trades)")
    print(f"               WR @65%: {stats_b['wr_65']:.1f}% ({stats_b['n_65']} trades)")
    print(f"               WR @70%: {stats_b['wr_70']:.1f}% ({stats_b['n_70']} trades)")

    # -------------------------------------------------------------
    # 3. Final Comparison Table
    # -------------------------------------------------------------
    print("\n" + "=" * 80)
    print("🏆 FINAL SOTA HEAD-TO-HEAD BENCHMARK RESULTS")
    print("=" * 80)
    print(f"{'Metric':<25} | {'Model A (SequenceBrain)':<25} | {'Model B (PatchTST-Lite)':<25}")
    print("-" * 80)
    print(f"{'Validation Loss':<25} | {stats_a['loss']:<25.4f} | {stats_b['loss']:<25.4f}")
    print(f"{'Overall Accuracy':<25} | {stats_a['acc']:<24.2f}% | {stats_b['acc']:<24.2f}%")
    print(f"{'Inference Latency':<25} | {stats_a['latency_ms']:<22.3f} ms | {stats_b['latency_ms']:<22.3f} ms")
    print(f"{'WinRate @ 50%':<25} | {stats_a['wr_50']:<5.1f}% ({stats_a['n_50']:<4} trades)       | {stats_b['wr_50']:<5.1f}% ({stats_b['n_50']:<4} trades)")
    print(f"{'WinRate @ 60% (Sniper)':<25} | {stats_a['wr_60']:<5.1f}% ({stats_a['n_60']:<4} trades)       | {stats_b['wr_60']:<5.1f}% ({stats_b['n_60']:<4} trades)")
    print(f"{'WinRate @ 65% (Ultra)':<25} | {stats_a['wr_65']:<5.1f}% ({stats_a['n_65']:<4} trades)       | {stats_b['wr_65']:<5.1f}% ({stats_b['n_65']:<4} trades)")
    print(f"{'WinRate @ 70% (Max)':<25} | {stats_a['wr_70']:<5.1f}% ({stats_a['n_70']:<4} trades)       | {stats_b['wr_70']:<5.1f}% ({stats_b['n_70']:<4} trades)")
    print("=" * 80)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int, default=5)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--lr", type=float, default=1e-3)
    args = parser.parse_args()

    run_benchmark(epochs=args.epochs, batch_size=args.batch_size, lr=args.lr)
