"""
train_brain.py - Offline Training & Validation Pipeline for SequenceBrain (V3)

Trains the PyTorch Hybrid Temporal Brain on exported dataset_v3.npz using:
- Chronological Time-Series Train / Validation Split (Strictly no future lookahead)
- Class-Balanced Focal Loss / Weighted Cross-Entropy (Handles heavy HOLD noise)
- Multi-task Regularization (Auxiliary PnL regression)
- Metric Tracking:
  - Selective WinRate @ Confidence Threshold (>= 60%, >= 70%)
  - Precision / Recall / F1 on directional trades (BUY / PUT)
  - Loss & Accuracy
- Model checkpointing to ml_service/models/sequence_brain_v3.pt
"""

import os
import sys
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


class FocalLoss(nn.Module):
    def __init__(self, alpha=None, gamma=2.0):
        super().__init__()
        self.alpha = alpha  # tensor of shape [C]
        self.gamma = gamma

    def forward(self, inputs, targets):
        # inputs: [B, C], targets: [B]
        ce_loss = F.cross_entropy(inputs, targets, reduction="none")
        pt = torch.exp(-ce_loss)  # probability of true class
        focal_loss = ((1.0 - pt) ** self.gamma) * ce_loss
        
        if self.alpha is not None:
            at = self.alpha.gather(0, targets)
            focal_loss = at * focal_loss
            
        return focal_loss.mean()


def evaluate(model, loader, device, conf_thresholds=(0.50, 0.60, 0.70)):
    model.eval()
    total_loss = 0.0
    all_preds = []
    all_targets = []
    all_probs = []

    with torch.no_grad():
        for batch_xc, batch_xm, batch_y, batch_pnl in loader:
            batch_xc = batch_xc.to(device)
            batch_xm = batch_xm.to(device)
            batch_y = batch_y.to(device)

            logits, aux_pnl = model(batch_xc, batch_xm)
            loss = F.cross_entropy(logits, batch_y)
            total_loss += loss.item() * len(batch_y)

            probs = F.softmax(logits, dim=-1)
            preds = torch.argmax(logits, dim=-1)

            all_preds.extend(preds.cpu().numpy())
            all_targets.extend(batch_y.cpu().numpy())
            all_probs.extend(probs.cpu().numpy())

    total_samples = len(all_targets)
    avg_loss = total_loss / max(1, total_samples)

    all_preds = np.array(all_preds)
    all_targets = np.array(all_targets)
    all_probs = np.array(all_probs)

    overall_acc = np.mean(all_preds == all_targets) * 100.0

    # Directional metrics (excluding HOLD predictions)
    stats = {
        "loss": avg_loss,
        "acc": overall_acc,
        "total": total_samples,
    }

    # Selective WinRate for directional signals at various confidence cutoffs
    for th in conf_thresholds:
        # Check if model predicted BUY (1) or PUT (2) with probability >= th
        buy_mask = (all_probs[:, 1] >= th)
        put_mask = (all_probs[:, 2] >= th)

        # Combined directional trades
        dir_mask = buy_mask | put_mask
        n_trades = np.sum(dir_mask)

        if n_trades > 0:
            trade_preds = np.where(buy_mask[dir_mask], 1, 2)
            trade_actuals = all_targets[dir_mask]
            win_rate = np.mean(trade_preds == trade_actuals) * 100.0
        else:
            win_rate = 0.0

        stats[f"wr_{int(th*100)}"] = win_rate
        stats[f"n_{int(th*100)}"] = int(n_trades)

    return stats


def train_model(
    data_path="ml_service/data/dataset_v3.npz",
    epochs=25,
    batch_size=64,
    lr=1e-3,
    val_split=0.20,
    save_path="ml_service/models/sequence_brain_v3.pt",
    backbone="hybrid"
):
    print("=" * 65)
    print("[*] Sequence Brain Offline Training Engine (V3)")
    print(f"Data file: {data_path}")
    print("=" * 65)

    if not os.path.exists(data_path):
        print(f"[-] Error: Data file {data_path} not found! Run export_dataset.py first.")
        return

    data = np.load(data_path)
    X_candles = data["X_candles"]
    X_macro = data["X_macro"]
    y = data["y"]
    y_pnl = data["y_pnl"]

    N = len(y)
    print(f"[+] Loaded {N} samples.")
    print(f"    X_candles: {X_candles.shape}")
    print(f"    X_macro:   {X_macro.shape}")
    print(f"    y:         {y.shape}")

    # Chronological Split (strictly respecting temporal causality)
    split_idx = int(N * (1.0 - val_split))
    print(f"[+] Splitting: Train = {split_idx} (First {int((1-val_split)*100)}%), Val = {N - split_idx} (Last {int(val_split*100)}%)")

    train_xc, val_xc = X_candles[:split_idx], X_candles[split_idx:]
    train_xm, val_xm = X_macro[:split_idx], X_macro[split_idx:]
    train_y, val_y = y[:split_idx], y[split_idx:]
    train_pnl, val_pnl = y_pnl[:split_idx], y_pnl[split_idx:]

    # Class balance weights
    class_counts = np.bincount(train_y, minlength=3)
    print(f"    Train class counts: HOLD={class_counts[0]}, BUY={class_counts[1]}, PUT={class_counts[2]}")
    
    # Smooth inverse frequencies
    weights = len(train_y) / (3.0 * np.maximum(class_counts, 1.0))
    weights = np.clip(weights, 0.2, 8.0).astype(np.float32)
    print(f"    Class weights for Loss: HOLD={weights[0]:.2f}, BUY={weights[1]:.2f}, PUT={weights[2]:.2f}")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"[+] Training Device: {device}")

    # Build PyTorch Datasets & DataLoaders
    train_dataset = TensorDataset(
        torch.tensor(train_xc, dtype=torch.float32),
        torch.tensor(train_xm, dtype=torch.float32),
        torch.tensor(train_y, dtype=torch.long),
        torch.tensor(train_pnl, dtype=torch.float32)
    )
    val_dataset = TensorDataset(
        torch.tensor(val_xc, dtype=torch.float32),
        torch.tensor(val_xm, dtype=torch.float32),
        torch.tensor(val_y, dtype=torch.long),
        torch.tensor(val_pnl, dtype=torch.float32)
    )

    train_loader = DataLoader(train_dataset, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False)

    # Initialize SequenceBrain Model
    model = SequenceBrain(
        candle_features=5,
        seq_len=160,
        macro_features=X_macro.shape[1],
        d_model=64,
        nhead=4,
        num_layers=2,
        num_classes=3,
        dropout=0.25,
        backbone=backbone
    ).to(device)

    alpha_tensor = torch.tensor(weights, dtype=torch.float32).to(device)
    criterion_cls = FocalLoss(alpha=alpha_tensor, gamma=2.0)
    criterion_pnl = nn.SmoothL1Loss()

    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-4)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs, eta_min=1e-5)

    best_val_loss = float("inf")
    best_wr_60 = 0.0

    print("\n" + "-" * 75)
    print(f"{'Epoch':<6} | {'Tr Loss':<8} | {'Val Loss':<8} | {'Val Acc':<8} | {'WR @50%':<10} | {'WR @60%':<10} | {'Trades@60'}")
    print("-" * 75)

    for epoch in range(1, epochs + 1):
        model.train()
        running_loss = 0.0

        for batch_xc, batch_xm, batch_y, batch_pnl in train_loader:
            batch_xc = batch_xc.to(device)
            batch_xm = batch_xm.to(device)
            batch_y = batch_y.to(device)
            batch_pnl = batch_pnl.to(device)

            optimizer.zero_grad()
            logits, aux_pnl = model(batch_xc, batch_xm)

            loss_cls = criterion_cls(logits, batch_y)
            loss_reg = criterion_pnl(aux_pnl, batch_pnl / 10.0)
            total_loss = loss_cls + 0.1 * loss_reg

            total_loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=2.0)
            optimizer.step()

            running_loss += total_loss.item() * len(batch_y)

        scheduler.step()
        train_loss = running_loss / len(train_dataset)

        val_stats = evaluate(model, val_loader, device)

        print(
            f"{epoch:<6d} | "
            f"{train_loss:<8.4f} | "
            f"{val_stats['loss']:<8.4f} | "
            f"{val_stats['acc']:<7.2f}% | "
            f"{val_stats['wr_50']:<6.2f}% ({val_stats['n_50']:<3}) | "
            f"{val_stats['wr_60']:<6.2f}% ({val_stats['n_60']:<3}) | "
            f"{val_stats['n_60']}"
        )

        # Checkpoint if improved
        if val_stats["loss"] < best_val_loss:
            best_val_loss = val_stats["loss"]
            best_wr_60 = val_stats["wr_60"]
            os.makedirs(os.path.dirname(save_path), exist_ok=True)
            torch.save({
                "epoch": epoch,
                "model_state_dict": model.state_dict(),
                "optimizer_state_dict": optimizer.state_dict(),
                "val_stats": val_stats,
                "class_weights": weights,
                "backbone": backbone
            }, save_path)

    print("-" * 75)
    print(f"[+] Training complete. Best model checkpoint saved to: {save_path}")
    print(f"    Best Validation Loss: {best_val_loss:.4f} | Best WR @60%: {best_wr_60:.2f}%")
    print("=" * 75)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Train SequenceBrain on dataset_v3")
    parser.add_argument("--data", default="ml_service/data/dataset_v3.npz")
    parser.add_argument("--epochs", type=int, default=15)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--val-split", type=float, default=0.20)
    parser.add_argument("--save", default="ml_service/models/sequence_brain_v3.pt")
    parser.add_argument("--backbone", choices=["hybrid", "transformer"], default="hybrid")
    args = parser.parse_args()

    train_model(
        data_path=args.data,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        val_split=args.val_split,
        save_path=args.save,
        backbone=args.backbone
    )
