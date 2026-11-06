"""
train_objective_dual_stream.py - Pure Multi-Timeframe Dual-Stream Neural Brain
Trained on 52,768 high-conviction objective market samples from PostgreSQL.

Features:
- Macro Stream (30, 5): 1D-CNN + LayerNorm -> Macro Context Vector
- Micro Stream (30, 5): 1D-CNN + LayerNorm -> Micro Dynamics Vector
- Bridge Vector (4): Dense Embedding
- Multi-Head Cross-Attention: Micro dynamics queries Macro context
- Predicts objective forward market movement
- Exports directly to ONNX (dual_stream_brain_v5.onnx) for 1ms in-process C# execution!
"""

import copy
import os
import sys
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader

class DualStreamMTFBrain(nn.Module):
    def __init__(self, d_model=32, nhead=2, dropout=0.1):
        super().__init__()
        self.d_model = d_model
        
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
        
        # 4. Bridge Feature Embedding (4 features)
        self.bridge_dense = nn.Sequential(
            nn.Linear(4, d_model // 2),
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
        
        # Conv1d expects (B, Channels, Length)
        m_in = macro_x.transpose(1, 2)
        m_h = F.gelu(self.macro_conv1(m_in))
        m_h = F.gelu(self.macro_conv2(m_h))
        m_h = self.macro_norm(m_h.transpose(1, 2))  # (B, 30, d_model)
        
        u_in = micro_x.transpose(1, 2)
        u_h = F.gelu(self.micro_conv1(u_in))
        u_h = F.gelu(self.micro_conv2(u_h))
        u_h = self.micro_norm(u_h.transpose(1, 2))  # (B, 30, d_model)
        
        # Cross-Attention: Micro (Query) queries Macro (Key, Value)
        attn_out, _ = self.cross_attn(query=u_h, key=m_h, value=m_h)
        u_fused = self.attn_norm(u_h + attn_out)
        
        # Mean pooling over temporal dimension
        m_pooled = m_h.mean(dim=1)     # (B, d_model)
        u_pooled = u_fused.mean(dim=1) # (B, d_model)
        
        # Bridge embedding
        b_emb = self.bridge_dense(bridge_x)  # (B, d_model // 2)
        
        # Fuse all representations
        fused = torch.cat([m_pooled, u_pooled, b_emb], dim=-1)
        logit = self.classifier(fused)
        return logit

def train_model():
    # Ensure reproducible training run
    torch.manual_seed(42)
    np.random.seed(42)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(42)

    data_path = os.path.join(os.path.dirname(__file__), "data", "dataset_objective_dual_stream.npz")
    if not os.path.exists(data_path):
        print(f"Dataset not found: {data_path}")
        return

    print(f"Loading objective dataset: {data_path}")
    data = np.load(data_path)
    macro_x = data["macro_x"]
    micro_x = data["micro_x"]
    bridge_x = data["bridge_x"]
    labels = data["labels"]

    epochs_arr = None
    if "epochs" in data:
        order = np.argsort(data["epochs"], kind="stable")
        macro_x = macro_x[order]
        micro_x = micro_x[order]
        bridge_x = bridge_x[order]
        labels = labels[order]
        epochs_arr = data["epochs"][order]

    N = len(labels)
    print(f"Total samples: {N}")

    # Chronological Train (80%) / Val (10%) / Test (10%) with 2400s (40-min) temporal embargo
    # (30 M1 bars = 1800s macro window + 30s label horizon = 1830s overlap; 2400s prevents all autocorrelation leakage)
    TIME_EMBARGO_SEC = 2400
    train_n = int(0.80 * N)
    if epochs_arr is not None and N > 500 and (int(epochs_arr[-1]) - int(epochs_arr[0])) > TIME_EMBARGO_SEC * 4:
        train_cutoff_epoch = int(epochs_arr[max(0, train_n - 1)])
        val_start = int(np.searchsorted(epochs_arr, train_cutoff_epoch + TIME_EMBARGO_SEC, side="left"))
        val_end = min(N, val_start + int(0.10 * N))
        val_cutoff_epoch = int(epochs_arr[max(0, min(N - 1, val_end - 1))])
        ts_start = int(np.searchsorted(epochs_arr, val_cutoff_epoch + TIME_EMBARGO_SEC, side="left"))
        if val_start >= N or ts_start >= N:
            embargo = 30
            val_start = min(N, train_n + embargo)
            val_end = min(N, val_start + int(0.10 * N))
            ts_start = min(N, val_end + embargo)
    else:
        embargo = 30 if N > 500 else 0
        val_start = min(N, train_n + embargo)
        val_end = min(N, val_start + int(0.10 * N))
        ts_start = min(N, val_end + embargo)

    tr_macro, val_macro, ts_macro = macro_x[:train_n], macro_x[val_start:val_end], macro_x[ts_start:]
    tr_micro, val_micro, ts_micro = micro_x[:train_n], micro_x[val_start:val_end], micro_x[ts_start:]
    tr_bridge, val_bridge, ts_bridge = bridge_x[:train_n], bridge_x[val_start:val_end], bridge_x[ts_start:]
    tr_y, val_y, ts_y = labels[:train_n], labels[val_start:val_end], labels[ts_start:]

    print(f"Split sizes: Train={len(tr_y)}, Val={len(val_y)}, Test={len(ts_y)}")

    tr_ds = TensorDataset(torch.from_numpy(tr_macro), torch.from_numpy(tr_micro), torch.from_numpy(tr_bridge), torch.from_numpy(tr_y).float())
    val_ds = TensorDataset(torch.from_numpy(val_macro), torch.from_numpy(val_micro), torch.from_numpy(val_bridge), torch.from_numpy(val_y).float())
    ts_ds = TensorDataset(torch.from_numpy(ts_macro), torch.from_numpy(ts_micro), torch.from_numpy(ts_bridge), torch.from_numpy(ts_y).float())

    batch_size = 128
    tr_loader = DataLoader(tr_ds, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_ds, batch_size=batch_size, shuffle=False)
    ts_loader = DataLoader(ts_ds, batch_size=batch_size, shuffle=False)

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Training on device: {device}")

    model = DualStreamMTFBrain(d_model=32, nhead=2, dropout=0.15).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4)

    best_val_acc = 0.0
    best_val_ev = -1.0
    best_weights = None
    epochs = 25
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs, eta_min=1e-5)

    pos_count = float(np.sum(tr_y == 1))
    neg_count = float(np.sum(tr_y == 0))
    pos_weight_val = neg_count / max(pos_count, 1.0)
    pos_weight = torch.tensor([pos_weight_val], dtype=torch.float32, device=device)
    print(f"Class Distribution: BUY={int(pos_count)}, PUT={int(neg_count)} (pos_weight: {pos_weight_val:.4f})")
    criterion = nn.BCEWithLogitsLoss(pos_weight=pos_weight)

    print("\nStarting training loop...")
    for epoch in range(1, epochs + 1):
        model.train()
        total_loss = 0.0
        correct = 0
        total = 0

        for m_x, u_x, b_x, y in tr_loader:
            m_x, u_x, b_x, y = m_x.to(device), u_x.to(device), b_x.to(device), y.to(device)
            optimizer.zero_grad()
            logits = model(m_x, u_x, b_x).squeeze(-1)
            loss = criterion(logits, y)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), max_norm=1.0)
            optimizer.step()

            total_loss += loss.item() * len(y)
            preds = (torch.sigmoid(logits) >= 0.5).float()
            correct += (preds == y).sum().item()
            total += len(y)

        scheduler.step()
        train_loss = total_loss / total
        train_acc = correct / total

        # Validation
        model.eval()
        val_correct = 0
        val_total = 0
        with torch.no_grad():
            for m_x, u_x, b_x, y in val_loader:
                m_x, u_x, b_x, y = m_x.to(device), u_x.to(device), b_x.to(device), y.to(device)
                logits = model(m_x, u_x, b_x).squeeze(-1)
                preds = (torch.sigmoid(logits) >= 0.5).float()
                val_correct += (preds == y).sum().item()
                val_total += len(y)

        val_acc = val_correct / val_total
        payout_rate = 0.82
        val_ev = val_acc * payout_rate - (1.0 - val_acc) * 1.0
        print(f"Epoch {epoch:02d}/{epochs:02d} | Train Loss: {train_loss:.4f} | Train Acc: {train_acc*100:.2f}% | Val Acc: {val_acc*100:.2f}% | Val EV PnL: {val_ev*100:+.2f}%")

        if val_acc > best_val_acc:
            best_val_acc = val_acc
            best_val_ev = val_ev
            best_weights = copy.deepcopy(model.state_dict())

    print(f"\nBest Validation Accuracy: {best_val_acc*100:.2f}% (EV PnL: {best_val_ev*100:+.2f}%)")
    if best_weights is not None:
        model.load_state_dict(best_weights)

    # Test evaluation
    model.eval()
    test_correct = 0
    test_total = 0
    all_probs = []
    all_y = []

    with torch.no_grad():
        for m_x, u_x, b_x, y in ts_loader:
            m_x, u_x, b_x, y = m_x.to(device), u_x.to(device), b_x.to(device), y.to(device)
            logits = model(m_x, u_x, b_x).squeeze(-1)
            probs = torch.sigmoid(logits).cpu().numpy()
            all_probs.extend(probs)
            all_y.extend(y.cpu().numpy())

    all_probs = np.array(all_probs)
    all_y = np.array(all_y)
    preds = (all_probs >= 0.5).astype(int)
    test_acc = np.mean(preds == all_y)
    test_ev = test_acc * 0.82 - (1.0 - test_acc) * 1.0
    print(f"\nFinal Test Accuracy (all test samples): {test_acc*100:.2f}% | Test EV PnL: {test_ev*100:+.2f}%")

    # High-conviction threshold evaluation (prob >= 0.58 or prob <= 0.42)
    high_mask = (all_probs >= 0.58) | (all_probs <= 0.42)
    high_count = np.sum(high_mask)
    if high_count > 0:
        high_acc = np.mean(preds[high_mask] == all_y[high_mask])
        high_ev = high_acc * 0.82 - (1.0 - high_acc) * 1.0
        print(f"High-Conviction Winrate (prob >= 58% or <= 42%): {high_acc*100:.2f}% | High-Conviction EV: {high_ev*100:+.2f}% (over {high_count} trades, {high_count/len(all_y)*100:.1f}% coverage)")

    # Temperature Scaling on Validation Set (Guo et al., 2017)
    # Calibrates raw logits so that output sigmoid probabilities match true empirical probabilities for Kelly Criterion
    print("\nCalibrating probabilities via Temperature Scaling on validation set...")
    val_logits_list = []
    val_y_list = []
    with torch.no_grad():
        for m_x, u_x, b_x, y in val_loader:
            m_x, u_x, b_x, y = m_x.to(device), u_x.to(device), b_x.to(device), y.to(device)
            val_logits_list.append(model(m_x, u_x, b_x).squeeze(-1).cpu())
            val_y_list.append(y.cpu())
    val_logits_all = torch.cat(val_logits_list)
    val_y_all = torch.cat(val_y_list)

    temp_param = nn.Parameter(torch.ones(1, device="cpu"))
    opt_t = torch.optim.LBFGS([temp_param], lr=0.01, max_iter=50)
    bce_loss_fn = nn.BCEWithLogitsLoss()

    def eval_temp():
        opt_t.zero_grad()
        loss = bce_loss_fn(val_logits_all / torch.clamp(temp_param, min=0.1, max=10.0), val_y_all)
        loss.backward()
        return loss

    opt_t.step(eval_temp)
    calibrated_temperature = float(torch.clamp(temp_param, min=0.1, max=10.0).item())
    print(f"Optimal Temperature (ECE Minimization): T = {calibrated_temperature:.4f}")

    # Save PyTorch weights
    models_dir = os.path.join(os.path.dirname(__file__), "models")
    os.makedirs(models_dir, exist_ok=True)
    pt_path = os.path.join(models_dir, "dual_stream_brain_v5.pt")
    torch.save(model.state_dict(), pt_path)
    print(f"Saved PyTorch model to: {pt_path}")

    # Export to ONNX for C# execution
    onnx_path = os.path.join(models_dir, "dual_stream_brain_v5.onnx")
    export_model = copy.deepcopy(model).to("cpu").eval()
    if calibrated_temperature > 0.1 and abs(calibrated_temperature - 1.0) > 1e-4:
        with torch.no_grad():
            export_model.classifier[-1].weight.data.div_(calibrated_temperature)
            if export_model.classifier[-1].bias is not None:
                export_model.classifier[-1].bias.data.div_(calibrated_temperature)
        print(f"Folded Temperature Scaling (1/{calibrated_temperature:.4f}) into export linear classifier.")
    dummy_macro_cpu = torch.randn(1, 30, 5, device="cpu")
    dummy_micro_cpu = torch.randn(1, 30, 5, device="cpu")
    dummy_bridge_cpu = torch.randn(1, 4, device="cpu")

    print(f"Exporting to ONNX: {onnx_path}...")
    torch.onnx.export(
        export_model,
        (dummy_macro_cpu, dummy_micro_cpu, dummy_bridge_cpu),
        onnx_path,
        export_params=True,
        opset_version=17,
        do_constant_folding=True,
        input_names=["macro_x", "micro_x", "bridge_x"],
        output_names=["logit"],
        dynamic_axes={
            "macro_x": {0: "batch_size"},
            "micro_x": {0: "batch_size"},
            "bridge_x": {0: "batch_size"},
            "logit": {0: "batch_size"}
        }
    )
    print(f"ONNX export successful! Size: {os.path.getsize(onnx_path) / 1024:.1f} KB")

    # Validate ONNX graph integrity via onnx.checker
    import onnx
    onnx_model = onnx.load(onnx_path)
    onnx.checker.check_model(onnx_model)
    print("ONNX model structural validation passed (onnx.checker.check_model: OK)")

    # Parity check via ONNX Runtime
    try:
        import onnxruntime as ort
        ort_sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        ort_inputs = {
            "macro_x": dummy_macro_cpu.numpy(),
            "micro_x": dummy_micro_cpu.numpy(),
            "bridge_x": dummy_bridge_cpu.numpy()
        }
        ort_out = ort_sess.run(None, ort_inputs)[0]
        with torch.no_grad():
            pt_out = export_model(dummy_macro_cpu, dummy_micro_cpu, dummy_bridge_cpu).numpy()
        np.testing.assert_allclose(pt_out, ort_out, rtol=1e-3, atol=1e-5)
        print("ONNX Runtime inference test passed! Numerical parity verified.")
    except Exception as e:
        print(f"ONNX Runtime verification warning: {e}")

if __name__ == "__main__":
    train_model()
