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

    if "epochs" in data:
        order = np.argsort(data["epochs"], kind="stable")
        macro_x = macro_x[order]
        micro_x = micro_x[order]
        bridge_x = bridge_x[order]
        labels = labels[order]

    N = len(labels)
    print(f"Total samples: {N}")

    # Chronological Train (80%) / Val (10%) / Test (10%) with 30-sample embargo gap to prevent window overlap leakage
    embargo = 30 if N > 500 else 0
    train_n = int(0.80 * N)
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
    criterion = nn.BCEWithLogitsLoss()

    best_val_acc = 0.0
    best_weights = None
    epochs = 25

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
            optimizer.step()

            total_loss += loss.item() * len(y)
            preds = (torch.sigmoid(logits) >= 0.5).float()
            correct += (preds == y).sum().item()
            total += len(y)

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
        print(f"Epoch {epoch:02d}/{epochs:02d} | Train Loss: {train_loss:.4f} | Train Acc: {train_acc*100:.2f}% | Val Acc: {val_acc*100:.2f}%")

        if val_acc > best_val_acc:
            best_val_acc = val_acc
            best_weights = copy.deepcopy(model.state_dict())

    print(f"\nBest Validation Accuracy: {best_val_acc*100:.2f}%")
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
    print(f"\nFinal Test Accuracy (all test samples): {test_acc*100:.2f}%")

    # High-conviction threshold evaluation (prob >= 0.58 or prob <= 0.42)
    high_mask = (all_probs >= 0.58) | (all_probs <= 0.42)
    high_count = np.sum(high_mask)
    if high_count > 0:
        high_acc = np.mean(preds[high_mask] == all_y[high_mask])
        print(f"High-Conviction Winrate (prob >= 58% or <= 42%): {high_acc*100:.2f}% (over {high_count} trades, {high_count/len(all_y)*100:.1f}% coverage)")

    # Save PyTorch weights
    models_dir = os.path.join(os.path.dirname(__file__), "models")
    os.makedirs(models_dir, exist_ok=True)
    pt_path = os.path.join(models_dir, "dual_stream_brain_v5.pt")
    torch.save(model.state_dict(), pt_path)
    print(f"Saved PyTorch model to: {pt_path}")

    # Export to ONNX for C# execution
    onnx_path = os.path.join(models_dir, "dual_stream_brain_v5.onnx")
    model.eval()
    dummy_macro = torch.randn(1, 30, 5, device=device)
    dummy_micro = torch.randn(1, 30, 5, device=device)
    dummy_bridge = torch.randn(1, 4, device=device)

    print(f"Exporting to ONNX: {onnx_path}...")
    torch.onnx.export(
        model,
        (dummy_macro, dummy_micro, dummy_bridge),
        onnx_path,
        export_params=True,
        opset_version=14,
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

if __name__ == "__main__":
    train_model()
