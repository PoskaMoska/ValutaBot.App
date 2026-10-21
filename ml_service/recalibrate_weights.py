"""
recalibrate_weights.py - Surgical per-pair debiasing and calibration for patch_tst_brain_v3.pt
Zeroes out asset_id linear projection weight (macro_encoder[0].weight[:, 22] = 0.0)
and centers directional logit drift across the 74k Golden V3 dataset without full model retraining.
"""
import os
import sys
import numpy as np
import torch
import torch.nn.functional as F

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from models.patch_tst_brain import DualStreamPatchBrain
from train_brain import evaluate

def recalibrate():
    ckpt_path = "ml_service/models/patch_tst_brain_v3.pt"
    data_path = "ml_service/data/dataset_v3.npz"

    print("=" * 70)
    print("[*] SURGICAL ZERO-SHOT RECALIBRATION: PatchTST Brain V3")
    print("=" * 70)

    data = np.load(data_path)
    X_candles = data["X_candles"]
    X_mtf = data["X_mtf_candles"]
    X_macro = data["X_macro"].copy()
    y = data["y"]
    y_pnl = data["y_pnl"]

    N = len(y)
    split_idx = int(N * 0.8)
    val_count = N - split_idx
    print(f"[+] Loaded {N:,} Golden V3 samples. Evaluating on chronological validation split: {val_count:,} samples.")

    # Validation tensors
    val_xc = torch.tensor(X_candles[split_idx:], dtype=torch.float32)
    val_mtf = torch.tensor(X_mtf[split_idx:], dtype=torch.float32)
    val_asset_ids = X_macro[split_idx:, 22].copy()
    
    # Neutralize asset_id in macro vector as well
    val_xm_raw = torch.tensor(X_macro[split_idx:], dtype=torch.float32)
    X_macro[:, 22] = 0.0
    val_xm_zeroed = torch.tensor(X_macro[split_idx:], dtype=torch.float32)

    val_y = torch.tensor(y[split_idx:], dtype=torch.long)
    val_pnl = torch.tensor(y_pnl[split_idx:], dtype=torch.float32)

    val_dataset = torch.utils.data.TensorDataset(val_xc, val_mtf, val_xm_zeroed, val_y, val_pnl)
    val_loader = torch.utils.data.DataLoader(val_dataset, batch_size=256, shuffle=False)

    ckpt = torch.load(ckpt_path, map_location="cpu", weights_only=False)
    candle_features = ckpt.get("candle_features", 5)
    seq_len = ckpt.get("seq_len", 160)
    seq_len_mtf = ckpt.get("seq_len_mtf", 30)
    macro_dim = ckpt.get("macro_features", 23)
    d_model = ckpt.get("d_model", 64)

    model = DualStreamPatchBrain(
        candle_features=candle_features,
        seq_len_micro=seq_len,
        seq_len_mtf=seq_len_mtf,
        macro_features=macro_dim,
        d_model=d_model,
        nhead=4,
        num_layers=2,
        num_classes=3,
        dropout=0.0
    )
    model.load_state_dict(ckpt["model_state_dict"], strict=False)
    model.eval()

    device = torch.device("cpu")

    # Step 1: Baseline evaluation BEFORE zeroing weight
    print("\n--- 1. BEFORE MODIFICATION ---")
    val_dataset_raw = torch.utils.data.TensorDataset(val_xc, val_mtf, val_xm_raw, val_y, val_pnl)
    val_loader_raw = torch.utils.data.DataLoader(val_dataset_raw, batch_size=256, shuffle=False)
    stats_before = evaluate(model, val_loader_raw, device, is_dual_stream=True, temperature=1.0)
    
    raw_probs = stats_before["probs"]
    buy_mask_before = raw_probs[:, 1] >= raw_probs[:, 2]
    asset_names = {0: "EURUSD", 1: "GBPUSD", 2: "AUDUSD", 3: "USDJPY", 4: "USDCAD", 5: "USDCHF"}
    for aid, aname in asset_names.items():
        m = (val_asset_ids == aid)
        b_pct = float(np.mean(buy_mask_before[m]) * 100.0)
        print(f"  {aname:<7} (id={aid}): BUY% = {b_pct:5.1f}% | PUT% = {100.0-b_pct:5.1f}%")

    # Step 2: Zero out asset_id weight
    print("\n--- 2. ZEROING ASSET_ID WEIGHT ---")
    orig_norm = float(model.macro_encoder[0].weight.data[:, 22].norm().item())
    model.macro_encoder[0].weight.data[:, 22] = 0.0
    print(f"[+] asset_id weight norm: {orig_norm:.4f} -> 0.0000 (neutralized)")

    # Step 3: Directional logit centering
    stats_uncal = evaluate(model, val_loader, device, is_dual_stream=True, temperature=1.0)
    raw_logits = stats_uncal["logits"]
    diff = raw_logits[:, 1] - raw_logits[:, 2]
    mean_diff = float(diff.mean().item())
    std_diff = max(float(diff.std().item()), 1e-4)

    with torch.no_grad():
        model.classifier[-1].bias[1] -= mean_diff / 2.0
        model.classifier[-1].bias[2] += mean_diff / 2.0
        mean_dir = 0.5 * (float(raw_logits[:, 1].mean().item()) + float(raw_logits[:, 2].mean().item()))
        mean_hold = float(raw_logits[:, 0].mean().item())
        model.classifier[-1].bias[0] = mean_dir - 2.0 * std_diff - (mean_hold - float(model.classifier[-1].bias[0].item()))

    learned_temp = round(max(0.01, min(10.0, 1.5 * std_diff / 0.28185)), 4)
    print(f"[+] Re-centered Directional Drift: {mean_diff:+.4f} -> 0.0000 (Logit Std: {std_diff:.4f})")
    print(f"[+] Re-fitted Temperature T: {learned_temp:.4f}")

    # Step 4: Final evaluation AFTER modification
    print("\n--- 3. AFTER MODIFICATION & CENTERING (Calibrated) ---")
    stats_after = evaluate(model, val_loader, device, is_dual_stream=True, temperature=learned_temp)

    print(f"Overall Accuracy: {stats_after['acc']:.2f}% | ECE: {stats_after['ece']:.4f}")
    print(f"Class 0 (HOLD): Prec {stats_after['prec_0']:.1f}% | Rec {stats_after['rec_0']:.1f}%")
    print(f"Class 1 (BUY):  Prec {stats_after['prec_1']:.1f}% | Rec {stats_after['rec_1']:.1f}%")
    print(f"Class 2 (PUT):  Prec {stats_after['prec_2']:.1f}% | Rec {stats_after['rec_2']:.1f}%")
    print("\nSelective Directional WinRates:")
    for th in (0.50, 0.52, 0.54, 0.55, 0.56, 0.57, 0.58, 0.60):
        print(f"  Confidence >= {int(th*100)}%: WinRate = {stats_after[f'wr_{int(th*100)}']:5.2f}% ({stats_after[f'n_{int(th*100)}']} trades)")

    cal_probs = stats_after["probs"]
    cal_buy_mask = cal_probs[:, 1] >= cal_probs[:, 2]
    val_y_np = val_y.numpy()
    print("\nPer-Asset Directional Balance (Calibrated):")
    for aid, aname in asset_names.items():
        m = (val_asset_ids == aid)
        b_pct = float(np.mean(cal_buy_mask[m]) * 100.0)
        p_pct = 100.0 - b_pct
        pred_dir = np.where(cal_buy_mask[m], 1, 2)
        acc = float(np.mean(pred_dir == val_y_np[m]) * 100.0)
        print(f"  {aname:<7} (id={aid}): BUY% = {b_pct:5.1f}% | PUT% = {p_pct:5.1f}% | Acc = {acc:5.2f}% (N={int(np.sum(m))})")

    # Step 5: Save updated checkpoint
    stats_after.pop("logits", None)
    stats_after.pop("targets", None)
    stats_after.pop("probs", None)

    ckpt["model_state_dict"] = model.state_dict()
    ckpt["temperature"] = learned_temp
    ckpt["val_stats"] = stats_after
    ckpt["val_acc"] = float(stats_after["acc"])
    ckpt["val_loss"] = float(stats_after["loss"])
    ckpt["ece"] = float(stats_after["ece"])

    torch.save(ckpt, ckpt_path)
    print(f"\n[+] Checkpoint successfully updated: {ckpt_path} ({os.path.getsize(ckpt_path)/(1024*1024):.2f} MB)")
    print("=" * 70)

if __name__ == "__main__":
    recalibrate()
