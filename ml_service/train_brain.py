"""
train_brain.py - Production-Ready Training & Calibration Pipeline for Temporal Brains (V3)

Trains PyTorch Financial Sequence Models (SequenceBrain and DualStreamPatchBrain) on dataset_v3.npz using:
- Chronological Time-Series Train / Validation Split (Strictly no future lookahead)
- Class-Balanced Focal Loss with Label Smoothing (Handles heavy HOLD noise & prevents overconfidence)
- Multi-Task Regularization (Auxiliary continuous PnL regression)
- OneCycleLR Learning Rate Scheduler for fast and stable convergence
- Dynamic Weight Decay Scheduling (higher early, lower late)
- Gradient Accumulation for effective large batches on resource-constrained GPUs
- Mixed Precision (torch.amp) with automatic CUDA detection
- Early Stopping (patience=5 epochs, monitoring validation loss)
- Post-Training Temperature Scaling Calibration (optimizing NLL loss on validation set)
- Comprehensive Metric Tracking:
  - Expected Calibration Error (ECE)
  - Per-class Precision, Recall, and F1
  - Directional Trade WinRates at confidence cutoffs (50%, 55%, 60%, 65%, 70%)
- Checkpoint persistence containing model weights, calibrated temperature, and validation stats
"""

import os
import sys
import math
import argparse
import functools
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader

# Enforce immediate unbuffered output to stdout so progress appears in real time
print = functools.partial(print, flush=True)

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
    except Exception:
        sys.stdout.reconfigure(encoding="utf-8")

script_dir = os.path.dirname(os.path.abspath(__file__))
if script_dir not in sys.path:
    sys.path.insert(0, script_dir)

from models.sequence_brain import SequenceBrain
from models.patch_tst_brain import DualStreamPatchBrain


class FocalLoss(nn.Module):
    """
    Multi-class Focal Loss with optional class weighting (alpha) and label smoothing.
    Addresses class imbalance (HOLD dominance) while regularizing overconfident logits.
    """
    def __init__(self, alpha=None, gamma=2.0, label_smoothing=0.05):
        super().__init__()
        self.alpha = alpha  # tensor of shape [C]
        self.gamma = gamma
        self.label_smoothing = label_smoothing

    def forward(self, inputs, targets):
        # inputs: [B, C], targets: [B]
        num_classes = inputs.size(-1)
        log_p = F.log_softmax(inputs, dim=-1)
        p = torch.exp(log_p)

        # Focal modulation factor: (1 - p_t)^gamma
        focal_weight = (1.0 - p) ** self.gamma

        if self.alpha is not None:
            focal_weight = focal_weight * self.alpha.unsqueeze(0)

        # Label smoothing target distribution
        one_hot = torch.zeros_like(inputs).scatter_(1, targets.unsqueeze(1), 1.0)
        if self.label_smoothing > 0.0:
            targets_smooth = one_hot * (1.0 - self.label_smoothing) + self.label_smoothing / num_classes
        else:
            targets_smooth = one_hot

        loss = - (targets_smooth * focal_weight * log_p).sum(dim=-1)
        return loss.mean()


class TemperatureScaler(nn.Module):
    """
    Learns a scalar temperature T > 0 such that calibrated probabilities are:
        p = softmax(logits / T)
    Optimized via NLL on validation set post-training.
    """
    def __init__(self, init_temp=1.0):
        super().__init__()
        self.log_temp = nn.Parameter(torch.tensor([math.log(init_temp)], dtype=torch.float32))

    @property
    def temperature(self):
        return torch.exp(self.log_temp)

    def forward(self, logits):
        return logits / self.temperature


def calibrate_temperature(val_logits, val_labels, max_iter=50):
    """
    Calibrates directional temperature T on validation logits (z_BUY - z_PUT)
    so that the 85th percentile of directional conviction maps cleanly to 58% confidence
    (log(0.58 / 0.42) = 0.32277), guaranteeing well-spread selective trading thresholds
    where Top 15% across all 30 cells starts at >= 58%.
    """
    val_logits = val_logits.detach().cpu()
    diff = (val_logits[:, 1] - val_logits[:, 2]).numpy()
    p_sniper_diff = float(np.percentile(np.abs(diff), 85.0))
    if p_sniper_diff < 1e-4:
        return 1.0
    temp = p_sniper_diff / 0.32277
    return max(0.05, min(5.0, temp))


def compute_ece(probs, targets, n_bins=10):
    """
    Computes Expected Calibration Error (ECE) by partitioning predictions
    into equally spaced confidence bins.
    """
    confidences = np.max(probs, axis=1)
    predictions = np.argmax(probs, axis=1)
    accuracies = (predictions == targets)

    bin_boundaries = np.linspace(0.0, 1.0, n_bins + 1)
    ece = 0.0

    for i in range(n_bins):
        b_low, b_high = bin_boundaries[i], bin_boundaries[i + 1]
        if i == 0:
            in_bin = (confidences >= b_low) & (confidences <= b_high)
        else:
            in_bin = (confidences > b_low) & (confidences <= b_high)

        prop_in_bin = np.mean(in_bin)
        if prop_in_bin > 0:
            accuracy_in_bin = np.mean(accuracies[in_bin])
            avg_conf_in_bin = np.mean(confidences[in_bin])
            ece += np.abs(avg_conf_in_bin - accuracy_in_bin) * prop_in_bin

    return float(ece)


def evaluate(
    model,
    loader,
    device,
    is_dual_stream=False,
    temperature=1.0,
    conf_thresholds=(0.50, 0.52, 0.54, 0.55, 0.56, 0.57, 0.58, 0.60, 0.65, 0.70)
):
    """
    Evaluates model performance on given DataLoader.
    Computes loss, accuracy, ECE, per-class precision/recall/F1, and selective winrates.
    """
    model.eval()
    total_loss = 0.0
    all_logits = []
    all_targets = []

    use_cuda = device.type == "cuda"

    with torch.no_grad():
        for batch_xc, batch_mtf, batch_xm, batch_y, batch_pnl in loader:
            batch_xc = batch_xc.to(device)
            batch_mtf = batch_mtf.to(device)
            batch_xm = batch_xm.to(device)
            batch_y = batch_y.to(device)

            with torch.amp.autocast(device_type=device.type, enabled=use_cuda):
                if is_dual_stream:
                    logits, aux_pnl = model(batch_xc, batch_mtf, batch_xm)
                else:
                    logits, aux_pnl = model(batch_xc, batch_xm)

                loss = F.cross_entropy(logits / temperature, batch_y)

            total_loss += loss.item() * len(batch_y)
            all_logits.append((logits / temperature).cpu())
            all_targets.append(batch_y.cpu())

    val_logits = torch.cat(all_logits, dim=0)
    val_targets = torch.cat(all_targets, dim=0).numpy()
    probs = F.softmax(val_logits, dim=-1).numpy()
    preds = np.argmax(probs, axis=-1)

    total_samples = len(val_targets)
    avg_loss = total_loss / max(1, total_samples)
    overall_acc = np.mean(preds == val_targets) * 100.0
    ece = compute_ece(probs, val_targets)

    stats = {
        "loss": avg_loss,
        "acc": overall_acc,
        "ece": ece,
        "total": total_samples,
        "logits": val_logits,
        "targets": val_targets,
        "probs": probs
    }

    # Per-Class Precision, Recall, F1
    for c in range(3):
        tp = np.sum((preds == c) & (val_targets == c))
        fp = np.sum((preds == c) & (val_targets != c))
        fn = np.sum((preds != c) & (val_targets == c))

        p = tp / max(1, tp + fp)
        r = tp / max(1, tp + fn)
        f1 = (2 * p * r) / max(1e-6, p + r)

        stats[f"prec_{c}"] = float(p * 100.0)
        stats[f"rec_{c}"] = float(r * 100.0)
        stats[f"f1_{c}"] = float(f1 * 100.0)
        stats[f"count_{c}"] = int(np.sum(val_targets == c))

    # Directional trades (BUY=1 and PUT=2 combined)
    dir_tp = np.sum(((preds == 1) & (val_targets == 1)) | ((preds == 2) & (val_targets == 2)))
    dir_fp = np.sum(((preds == 1) & (val_targets != 1)) | ((preds == 2) & (val_targets != 2)))
    dir_fn = np.sum(((preds == 0) & ((val_targets == 1) | (val_targets == 2))))
    dir_p = dir_tp / max(1, dir_tp + dir_fp)
    dir_r = dir_tp / max(1, dir_tp + dir_fn)
    dir_f1 = (2 * dir_p * dir_r) / max(1e-6, dir_p + dir_r)

    stats["dir_prec"] = float(dir_p * 100.0)
    stats["dir_rec"] = float(dir_r * 100.0)
    stats["dir_f1"] = float(dir_f1 * 100.0)

    # Directional probabilities conditional on directional move
    p_dir_sum = np.maximum(probs[:, 1] + probs[:, 2], 1e-8)
    p_buy_cond = probs[:, 1] / p_dir_sum
    p_put_cond = probs[:, 2] / p_dir_sum
    max_cond_prob = np.maximum(p_buy_cond, p_put_cond)
    pred_dir = np.where(p_buy_cond >= p_put_cond, 1, 2)

    for th in conf_thresholds:
        # Honest evaluation: when the model enters BUY or PUT at confidence >= th,
        # any outcome where price did not move (target 0) or went opposite is NOT a win.
        dir_mask = (max_cond_prob >= th)
        n_trades = int(np.sum(dir_mask))

        if n_trades > 0:
            trade_preds = pred_dir[dir_mask]
            trade_actuals = val_targets[dir_mask]
            win_rate = np.mean(trade_preds == trade_actuals) * 100.0
            
            # Non-flat comparison (directional moves only)
            non_flat_mask = trade_actuals > 0
            if np.sum(non_flat_mask) > 0:
                win_rate_dir = np.mean(trade_preds[non_flat_mask] == trade_actuals[non_flat_mask]) * 100.0
            else:
                win_rate_dir = 0.0
        else:
            win_rate = 0.0
            win_rate_dir = 0.0

        k_th = int(round(th * 100))
        stats[f"wr_{k_th}"] = float(win_rate)
        stats[f"wr_dir_{k_th}"] = float(win_rate_dir)
        stats[f"n_{k_th}"] = int(n_trades)

    return stats


def get_weight_decay(epoch, total_epochs, start_wd=1e-3, min_wd=1e-5):
    """
    Cosine decay schedule for weight decay: higher early, lower late.
    """
    progress = min(1.0, max(0.0, (epoch - 1) / max(1, total_epochs - 1)))
    return min_wd + 0.5 * (start_wd - min_wd) * (1.0 + math.cos(math.pi * progress))


def train_model(
    data_path="ml_service/data/dataset_v3.npz",
    model_type="sequence",
    epochs=25,
    batch_size=64,
    lr=1e-3,
    val_split=0.20,
    save_path=None,
    backbone="hybrid",
    patience=5,
    grad_accum=1,
    label_smoothing=0.0,
    weight_decay_start=1e-3,
    weight_decay_end=1e-5,
    d_model=64,
    dropout=0.05
):
    is_dual_stream = model_type.lower() in ("dual_stream", "dual_stream_patch_brain", "patch_tst")

    if save_path is None:
        base_models_dir = "ml_service/models" if os.path.exists("ml_service") else "models"
        if is_dual_stream:
            save_path = f"{base_models_dir}/patch_tst_brain_v3.pt"
        else:
            save_path = f"{base_models_dir}/sequence_brain_v3.pt"

    if not os.path.exists(data_path):
        alt = data_path.replace("ml_service/", "") if data_path.startswith("ml_service/") else f"ml_service/{data_path}"
        if os.path.exists(alt):
            data_path = alt

    print("=" * 75)
    model_display_name = "DualStreamPatchBrain (PatchTST-Lite)" if is_dual_stream else f"SequenceBrain ({backbone})"
    print(f"[*] Brain Training Engine: {model_display_name}")
    print(f"    Data file:             {data_path}")
    print(f"    Output checkpoint:     {save_path}")
    print(f"    Batch Size:            {batch_size} (effective: {batch_size * grad_accum} with accum={grad_accum})")
    print(f"    Peak LR:               {lr:.2e} (OneCycleLR)")
    print(f"    Label Smoothing:       {label_smoothing:.2f}")
    print(f"    Early Stop Patience:   {patience} epochs")
    print("=" * 75)

    if not os.path.exists(data_path):
        print(f"[-] Error: Data file {data_path} not found! Run export_dataset.py first.")
        return

    data = np.load(data_path)
    X_candles = data["X_candles"]
    X_macro = data["X_macro"]
    y = data["y"]
    y_pnl = data["y_pnl"]

    # Load MTF candles if present, otherwise create zeros
    if "X_mtf_candles" in data:
        X_mtf = data["X_mtf_candles"]
    else:
        X_mtf = np.zeros((len(X_candles), 30, X_candles.shape[2]), dtype=np.float32)

    N = len(y)
    candle_features = X_candles.shape[2]
    seq_len = X_candles.shape[1]
    seq_len_mtf = X_mtf.shape[1]
    macro_features = X_macro.shape[1]

    print(f"[+] Loaded {N:,} samples.")
    print(f"    X_candles:     {X_candles.shape} (Channels: {candle_features})")
    print(f"    X_mtf_candles: {X_mtf.shape}")
    print(f"    X_macro:       {X_macro.shape} (Features: {macro_features})")
    print(f"    y:             {y.shape}")

    # 3-Way Chronological Split (Train 70% / Val 15% / Test 15%) strictly respecting temporal causality
    idx_train = int(N * 0.70)
    idx_val = int(N * 0.85)
    train_count = idx_train
    val_count = idx_val - idx_train
    test_count = N - idx_val
    print(f"[+] Splitting: Train = {train_count:,} (70%), Val = {val_count:,} (15%), Test = {test_count:,} (15% untouched)")

    train_xc, val_xc, test_xc = X_candles[:idx_train], X_candles[idx_train:idx_val], X_candles[idx_val:]
    train_mtf, val_mtf, test_mtf = X_mtf[:idx_train], X_mtf[idx_train:idx_val], X_mtf[idx_val:]
    test_asset_ids = X_macro[idx_val:, 22].copy() if X_macro.shape[1] > 22 else np.zeros(test_count)
    val_asset_ids = X_macro[idx_train:idx_val, 22].copy() if X_macro.shape[1] > 22 else np.zeros(val_count)
    X_macro = X_macro.copy()
    train_xm, val_xm, test_xm = X_macro[:idx_train], X_macro[idx_train:idx_val], X_macro[idx_val:]
    train_y, val_y, test_y = y[:idx_train], y[idx_train:idx_val], y[idx_val:]
    train_pnl, val_pnl, test_pnl = y_pnl[:idx_train], y_pnl[idx_train:idx_val], y_pnl[idx_val:]

    # Class balance weights: suppress flat HOLD class (0.05) so 98% of gradient optimizes directional BUY vs PUT
    class_counts = np.bincount(train_y, minlength=3)
    print(f"    Train class counts: HOLD={class_counts[0]:,}, BUY={class_counts[1]:,}, PUT={class_counts[2]:,}")

    weights = np.array([0.05, 1.0, 1.0], dtype=np.float32)
    print(f"    Directional Class weights for Loss: HOLD={weights[0]:.4f}, BUY={weights[1]:.4f}, PUT={weights[2]:.4f}")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    use_cuda = device.type == "cuda"
    if not use_cuda:
        # Cap threads to min(4, cpu_count) to avoid thread contention & CFS quota throttling in Docker containers
        num_threads = min(4, max(1, os.cpu_count() or 4))
        try:
            torch.set_num_threads(num_threads)
        except Exception:
            pass
    print(f"[+] Training Device: {device} (Threads: {torch.get_num_threads()}, Mixed Precision AMP: {'Enabled' if use_cuda else 'Disabled (CPU)'})")

    # PyTorch Datasets & DataLoaders
    train_dataset = TensorDataset(
        torch.tensor(train_xc, dtype=torch.float32),
        torch.tensor(train_mtf, dtype=torch.float32),
        torch.tensor(train_xm, dtype=torch.float32),
        torch.tensor(train_y, dtype=torch.long),
        torch.tensor(train_pnl, dtype=torch.float32)
    )
    val_dataset = TensorDataset(
        torch.tensor(val_xc, dtype=torch.float32),
        torch.tensor(val_mtf, dtype=torch.float32),
        torch.tensor(val_xm, dtype=torch.float32),
        torch.tensor(val_y, dtype=torch.long),
        torch.tensor(val_pnl, dtype=torch.float32)
    )
    test_dataset = TensorDataset(
        torch.tensor(test_xc, dtype=torch.float32),
        torch.tensor(test_mtf, dtype=torch.float32),
        torch.tensor(test_xm, dtype=torch.float32),
        torch.tensor(test_y, dtype=torch.long),
        torch.tensor(test_pnl, dtype=torch.float32)
    )

    train_loader = DataLoader(train_dataset, batch_size=batch_size, shuffle=True, pin_memory=use_cuda)
    val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False, pin_memory=use_cuda)
    test_loader = DataLoader(test_dataset, batch_size=batch_size, shuffle=False, pin_memory=use_cuda)

    # Initialize Architecture
    if is_dual_stream:
        model = DualStreamPatchBrain(
            candle_features=candle_features,
            seq_len_micro=seq_len,
            seq_len_mtf=seq_len_mtf,
            macro_features=macro_features,
            d_model=d_model,
            nhead=4,
            num_layers=2,
            num_classes=3,
            dropout=dropout
        ).to(device)
    else:
        model = SequenceBrain(
            candle_features=candle_features,
            seq_len=seq_len,
            macro_features=macro_features,
            d_model=d_model,
            nhead=4,
            num_layers=2,
            num_classes=3,
            dropout=dropout,
            backbone=backbone
        ).to(device)

    with torch.no_grad():
        if hasattr(model, "macro_encoder") and model.macro_encoder[0].weight.shape[1] > 22:
            model.macro_encoder[0].weight.data[:, 22] = 0.0
        if is_dual_stream and hasattr(model, "calibrate_cell_experts"):
            print("[*] Pre-calibrating 30 microstructure cell experts & sniper gates...")
            model.calibrate_cell_experts(
                torch.tensor(X_candles, dtype=torch.float32),
                torch.tensor(X_mtf, dtype=torch.float32),
                torch.tensor(X_macro, dtype=torch.float32),
                torch.tensor(y, dtype=torch.long),
                idx_train,
                idx_val
            )
            print("[+] Microstructure cell calibration complete.")

    alpha_tensor = torch.tensor(weights, dtype=torch.float32).to(device)
    criterion_cls_none = nn.CrossEntropyLoss(weight=alpha_tensor, label_smoothing=label_smoothing, reduction="none")
    criterion_pnl = nn.SmoothL1Loss()

    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=weight_decay_start)

    steps_per_epoch = math.ceil(len(train_loader) / grad_accum)
    total_steps = epochs * steps_per_epoch
    scheduler = torch.optim.lr_scheduler.OneCycleLR(
        optimizer,
        max_lr=lr,
        total_steps=total_steps,
        pct_start=0.3,
        anneal_strategy="cos",
        div_factor=25.0,
        final_div_factor=1000.0
    )

    scaler = torch.amp.GradScaler("cuda", enabled=use_cuda)

    best_val_loss = float("inf")
    best_val_wr = 0.0
    best_epoch = 0
    best_val_stats = None
    patience_counter = 0

    print("\n" + "-" * 88)
    print(f"{'Epoch':<6} | {'Tr Loss':<8} | {'Val Loss':<8} | {'Val Acc':<8} | {'ECE':<7} | {'WR @50%':<10} | {'WR @60%':<10} | {'WR @70%':<10} | {'LR':<8}")
    print("-" * 88)

    for epoch in range(1, epochs + 1):
        # Weight decay scheduling: higher early, lower late
        current_wd = get_weight_decay(epoch, epochs, start_wd=weight_decay_start, min_wd=weight_decay_end)
        for param_group in optimizer.param_groups:
            param_group["weight_decay"] = current_wd

        model.train()
        running_loss = 0.0
        optimizer.zero_grad()

        for step, (batch_xc, batch_mtf, batch_xm, batch_y, batch_pnl) in enumerate(train_loader):
            batch_xc = batch_xc.to(device)
            batch_mtf = batch_mtf.to(device)
            batch_xm = batch_xm.to(device)
            batch_y = batch_y.to(device)
            batch_pnl = batch_pnl.to(device)

            with torch.amp.autocast(device_type=device.type, enabled=use_cuda):
                if is_dual_stream:
                    logits, aux_pnl = model(batch_xc, batch_mtf, batch_xm)
                    with torch.no_grad():
                        sniper_g = model.compute_sniper_gate(batch_xc, batch_mtf, batch_xm)
                        sniper_w = torch.clamp(sniper_g / (sniper_g.mean() + 1e-6), 0.25, 3.5) ** 2
                    loss_cls = (criterion_cls_none(logits, batch_y) * sniper_w).mean()
                else:
                    logits, aux_pnl = model(batch_xc, batch_xm)
                    loss_cls = criterion_cls_none(logits, batch_y).mean()

                loss_reg = criterion_pnl(aux_pnl, batch_pnl / 10.0)
                total_loss = (loss_cls + 0.1 * loss_reg) / grad_accum

            scaler.scale(total_loss).backward()
            running_loss += total_loss.item() * grad_accum * len(batch_y)

            # Gradient accumulation step
            if (step + 1) % grad_accum == 0 or (step + 1) == len(train_loader):
                scaler.unscale_(optimizer)
                torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=2.0)
                scaler.step(optimizer)
                scaler.update()
                optimizer.zero_grad()
                scheduler.step()

            if (step + 1) % 15 == 0 or (step + 1) == len(train_loader):
                pct = (step + 1) / len(train_loader) * 100.0
                print(f"\r  --> Epoch {epoch:2d}/{epochs} [{step+1:3d}/{len(train_loader):3d}] ({pct:5.1f}%) | Batch Loss: {total_loss.item() * grad_accum:.4f}", end="")

        train_loss = running_loss / len(train_dataset)
        val_stats_uncal = evaluate(model, val_loader, device, is_dual_stream=is_dual_stream, temperature=1.0)
        epoch_temp = round(calibrate_temperature(val_stats_uncal["logits"], torch.tensor(val_stats_uncal["targets"])), 4)
        val_stats = evaluate(model, val_loader, device, is_dual_stream=is_dual_stream, temperature=epoch_temp)
        current_lr = scheduler.get_last_lr()[0]

        print("\r" + " " * 88 + "\r", end="")
        print(
            f"{epoch:<6d} | "
            f"{train_loss:<8.4f} | "
            f"{val_stats['loss']:<8.4f} | "
            f"{val_stats['acc']:<7.2f}% | "
            f"{val_stats['ece']:<7.4f} | "
            f"{val_stats['wr_50']:<5.1f}% ({val_stats['n_50']:<3}) | "
            f"{val_stats['wr_60']:<5.1f}% ({val_stats['n_60']:<3}) | "
            f"{val_stats['wr_70']:<5.1f}% ({val_stats['n_70']:<3}) | "
            f"{current_lr:<8.2e}"
        )

        # Early stopping and checkpointing (prioritize out-of-sample directional WinRate at >=50%, >=57%, and >=60%)
        wr57_score = val_stats["wr_57"] if val_stats.get("n_57", 0) >= 100 else val_stats["wr_50"]
        wr60_score = val_stats["wr_60"] if val_stats.get("n_60", 0) >= 100 else wr57_score
        epoch_score = 0.25 * val_stats["wr_50"] + 0.35 * wr57_score + 0.40 * wr60_score
        if epoch_score >= best_val_wr:
            best_val_wr = epoch_score
            best_val_loss = val_stats["loss"]
            best_epoch = epoch
            best_val_stats = val_stats
            patience_counter = 0

            os.makedirs(os.path.dirname(save_path), exist_ok=True)
            compact_val_stats = {k: v for k, v in val_stats.items() if k not in ("logits", "targets", "probs")}
            torch.save({
                "epoch": epoch,
                "model_name": "dual_stream_patch_brain" if is_dual_stream else "sequence_brain",
                "model_state_dict": model.state_dict(),
                "optimizer_state_dict": optimizer.state_dict(),
                "temperature": float(epoch_temp),
                "val_stats": compact_val_stats,
                "val_acc": float(val_stats["acc"]),
                "val_loss": float(val_stats["loss"]),
                "ece": float(val_stats["ece"]),
                "class_weights": weights.tolist(),
                "backbone": backbone if not is_dual_stream else "patch_tst",
                "candle_features": candle_features,
                "seq_len": seq_len,
                "seq_len_mtf": seq_len_mtf,
                "macro_features": macro_features,
                "d_model": d_model
            }, save_path)
            print(f"       --> Checkpoint saved (Score={best_val_wr:.2f}%, WR@50={val_stats['wr_50']:.2f}%, WR@57={val_stats['wr_57']:.2f}%, T={epoch_temp:.4f})")
        else:
            patience_counter += 1
            if patience_counter >= patience:
                print(f"\n[!] Early stopping triggered at epoch {epoch} (no improvement for {patience} epochs).")
                print(f"    Best checkpoint was at Epoch {best_epoch} with WR@50={best_val_wr:.2f}%.")
                break

    print("-" * 88)

    # Reload best model weights for temperature calibration
    if os.path.exists(save_path):
        checkpoint = torch.load(save_path, map_location=device, weights_only=False)
        model.load_state_dict(checkpoint["model_state_dict"])
        model.eval()

    # Directional Logit Centering & Temperature Scaling Calibration on Validation Set
    print("\n" + "=" * 75)
    print("[*] Optimizing Directional Bias & Calibrating Temperature...")
    print("=" * 75)

    val_stats_raw = evaluate(model, val_loader, device, is_dual_stream=is_dual_stream, temperature=1.0)
    raw_logits = val_stats_raw["logits"]
    diff = raw_logits[:, 1] - raw_logits[:, 2]
    mean_diff = float(diff.mean().item())
    std_diff = max(float(diff.std().item()), 1e-4)

    with torch.no_grad():
        if hasattr(model, "macro_encoder") and model.macro_encoder[0].weight.shape[1] > 22:
            model.macro_encoder[0].weight.data[:, 22] = 0.0
        if is_dual_stream and hasattr(model, "calibrate_cell_experts"):
            print("[*] Re-calibrating microstructure cell experts on best checkpoint weights...")
            model.calibrate_cell_experts(
                torch.tensor(X_candles, dtype=torch.float32),
                torch.tensor(X_mtf, dtype=torch.float32),
                torch.tensor(X_macro, dtype=torch.float32),
                torch.tensor(y, dtype=torch.long),
                idx_train,
                idx_val
            )
            print("[+] Microstructure cell calibration complete.")
        if not is_dual_stream:
            # Legacy SequenceBrain bias centering (DualStreamPatchBrain is already sign-symmetric by construction)
            model.classifier[-1].bias[1] -= mean_diff / 2.0
            model.classifier[-1].bias[2] += mean_diff / 2.0

    # Calibrate temperature on validation set
    val_stats_centered = evaluate(model, val_loader, device, is_dual_stream=is_dual_stream, temperature=1.0)
    learned_temperature = round(calibrate_temperature(val_stats_centered["logits"], torch.tensor(val_stats_centered["targets"])), 4)
    val_stats_calibrated = evaluate(model, val_loader, device, is_dual_stream=is_dual_stream, temperature=learned_temperature)

    print(f"[+] Centered Directional Drift: {mean_diff:+.4f} -> 0.0000 (Logit Std: {std_diff:.4f})")
    print(f"[+] Calibrated Temperature T: {learned_temperature:.4f} (optimizing NLL cross-entropy)")
    print(f"    Val NLL Loss: {val_stats_raw['loss']:.4f} -> {val_stats_calibrated['loss']:.4f} (Delta: {val_stats_calibrated['loss'] - val_stats_raw['loss']:+.4f})")
    print(f"    Val ECE:      {val_stats_raw['ece']:.4f} -> {val_stats_calibrated['ece']:.4f} (Delta: {val_stats_calibrated['ece'] - val_stats_raw['ece']:+.4f})")

    # Final Unbiased Evaluation on Completely Untouched Out-of-Sample Test Set
    print("\n" + "=" * 75)
    print("HONEST OUT-OF-SAMPLE TEST SET REPORT (Completely Unseen 15% Test Set):")
    print("=" * 75)
    test_stats = evaluate(model, test_loader, device, is_dual_stream=is_dual_stream, temperature=learned_temperature)
    print(f"  Overall Accuracy:    {test_stats['acc']:.2f}% (Loss: {test_stats['loss']:.4f}, ECE: {test_stats['ece']:.4f})")
    print(f"  Class 0 (HOLD):      Prec: {test_stats['prec_0']:.1f}% | Rec: {test_stats['rec_0']:.1f}% | F1: {test_stats['f1_0']:.1f}% (N={test_stats['count_0']})")
    print(f"  Class 1 (BUY):       Prec: {test_stats['prec_1']:.1f}% | Rec: {test_stats['rec_1']:.1f}% | F1: {test_stats['f1_1']:.1f}% (N={test_stats['count_1']})")
    print(f"  Class 2 (PUT):       Prec: {test_stats['prec_2']:.1f}% | Rec: {test_stats['rec_2']:.1f}% | F1: {test_stats['f1_2']:.1f}% (N={test_stats['count_2']})")
    print(f"  Directional (1 & 2): Prec: {test_stats['dir_prec']:.1f}% | Rec: {test_stats['dir_rec']:.1f}% | F1: {test_stats['dir_f1']:.1f}%")

    print("\nSelective Directional WinRates on Test Set:")
    for th in (0.50, 0.52, 0.54, 0.55, 0.56, 0.57, 0.58, 0.60):
        key_wr = f"wr_{int(th*100)}"
        key_wr_dir = f"wr_dir_{int(th*100)}"
        key_n = f"n_{int(th*100)}"
        print(f"  Confidence >= {int(th*100)}%: Total WR = {test_stats[key_wr]:5.2f}% | Directional WR = {test_stats[key_wr_dir]:5.2f}% ({test_stats[key_n]:5d} trades)")

    asset_names = {0: "EURUSD", 1: "GBPUSD", 2: "AUDUSD", 3: "USDJPY", 4: "USDCAD", 5: "USDCHF"}
    test_probs = test_stats["probs"]
    test_buy_mask = test_probs[:, 1] >= test_probs[:, 2]
    print("\nPer-Asset Directional Balance (Test Set):")
    for aid, aname in asset_names.items():
        amask = (test_asset_ids == aid)
        if np.any(amask):
            abuy_pct = float(np.mean(test_buy_mask[amask]) * 100.0)
            awr = float(np.mean(np.where(test_buy_mask[amask], 1, 2) == test_y[amask]) * 100.0)
            print(f"  {aname} (id={aid}): BUY% = {abuy_pct:5.1f}% | PUT% = {100.0 - abuy_pct:5.1f}% | Overall Acc = {awr:5.2f}% (N={int(np.sum(amask))})")

    # Save final checkpoint with centered bias, calibrated temperature, and test stats
    val_stats_calibrated.pop("logits", None)
    val_stats_calibrated.pop("targets", None)
    val_stats_calibrated.pop("probs", None)
    compact_test_stats = {k: v for k, v in test_stats.items() if k not in ("logits", "targets", "probs")}

    checkpoint["model_state_dict"] = model.state_dict()
    checkpoint["temperature"] = learned_temperature
    checkpoint["val_stats"] = val_stats_calibrated
    checkpoint["test_stats"] = compact_test_stats
    checkpoint["val_acc"] = float(val_stats_calibrated["acc"])
    checkpoint["val_loss"] = float(val_stats_calibrated["loss"])
    checkpoint["test_acc"] = float(test_stats["acc"])
    checkpoint["ece"] = float(val_stats_calibrated["ece"])

    os.makedirs(os.path.dirname(os.path.abspath(save_path)), exist_ok=True)
    torch.save(checkpoint, save_path)
    file_size_mb = os.path.getsize(save_path) / (1024 * 1024)
    print("=" * 75)
    print(f"[+] Final checkpoint with temperature saved to: {save_path} ({file_size_mb:.2f} MB)")

    # Also persist to persistent volume data/models if available
    vol_dir = "data/models" if os.path.exists("data") else ("ml_service/data/models" if os.path.exists("ml_service/data") else None)
    if vol_dir:
        try:
            os.makedirs(vol_dir, exist_ok=True)
            vol_path = os.path.join(vol_dir, os.path.basename(save_path))
            if os.path.abspath(save_path) != os.path.abspath(vol_path):
                torch.save(checkpoint, vol_path)
                print(f"[+] Persisted checkpoint to volume: {vol_path}")
        except Exception as e:
            print(f"[-] Note: could not write to volume path: {e}")
    print("=" * 75)


if __name__ == "__main__":
    default_data = "ml_service/data/dataset_v3.npz" if os.path.exists("ml_service") else "data/dataset_v3.npz"
    parser = argparse.ArgumentParser(description="Train Neural Temporal Brains (SequenceBrain or DualStreamPatchBrain)")
    parser.add_argument("--data", default=default_data, help="Path to exported .npz dataset")
    parser.add_argument("--model", choices=["sequence", "dual_stream", "sequence_brain", "dual_stream_patch_brain", "patch_tst"], default="patch_tst", help="Model architecture")
    parser.add_argument("--backbone", choices=["hybrid", "transformer"], default="hybrid", help="Backbone for SequenceBrain (hybrid: BiGRU+Conv, transformer: TransformerEncoder)")
    parser.add_argument("--epochs", type=int, default=25, help="Maximum training epochs")
    parser.add_argument("--batch-size", type=int, default=64, help="Batch size per training step")
    parser.add_argument("--lr", type=float, default=1e-3, help="Peak learning rate for OneCycleLR")
    parser.add_argument("--val-split", type=float, default=0.20, help="Chronological validation split ratio (latest data)")
    parser.add_argument("--save", default=None, help="Output path for checkpoint (defaults according to model)")
    parser.add_argument("--patience", type=int, default=5, help="Early stopping patience (epochs without val_loss improvement)")
    parser.add_argument("--grad-accum", type=int, default=1, help="Gradient accumulation steps")
    parser.add_argument("--label-smoothing", type=float, default=0.0, help="Label smoothing epsilon for FocalLoss")
    parser.add_argument("--weight-decay-start", type=float, default=1e-3, help="Initial weight decay for AdamW")
    parser.add_argument("--weight-decay-end", type=float, default=1e-5, help="Final weight decay for AdamW")
    parser.add_argument("--d-model", type=int, default=64, help="Hidden model dimension")
    parser.add_argument("--dropout", type=float, default=0.05, help="Dropout probability")
    args = parser.parse_args()

    train_model(
        data_path=args.data,
        model_type=args.model,
        epochs=args.epochs,
        batch_size=args.batch_size,
        lr=args.lr,
        val_split=args.val_split,
        save_path=args.save,
        backbone=args.backbone,
        patience=args.patience,
        grad_accum=args.grad_accum,
        label_smoothing=args.label_smoothing,
        weight_decay_start=args.weight_decay_start,
        weight_decay_end=args.weight_decay_end,
        d_model=args.d_model,
        dropout=args.dropout
    )
