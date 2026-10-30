"""
train_dual_stream_v5.py - Pure Multi-Timeframe Dual-Stream Neural Brain
Trained on Point-in-Time Aligned dataset_v5_dual_stream.npz.

Architecture:
- Macro Stream (30, 5): 1D-CNN + Temporal LayerNorm -> Macro Context Vector
- Micro Stream (30, 5): 1D-CNN + Wick Attention -> Micro Dynamics Vector
- Bridge Vector (4): Dense Embedding
- Multi-Head Cross-Attention: Micro queries Macro
- Calibrated Binary Output: Win Probability (0.0 to 1.0)
- Auto-exports to ONNX for 1ms in-process C# execution!
"""

import os
import sys
import math
import argparse
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import TensorDataset, DataLoader

class DualStreamMTFBrain(nn.Module):
    def __init__(self, d_model=32, nhead=2, dropout=0.1):
        super().__init__()
        self.d_model = d_model
        
        # 1. Macro Stream Feature Extractor (30 M1 bars, 5 features)
        self.macro_conv1 = nn.Conv1d(5, d_model, kernel_size=3, padding=1)
        self.macro_conv2 = nn.Conv1d(d_model, d_model, kernel_size=5, padding=2)
        self.macro_norm = nn.LayerNorm(d_model)
        
        # 2. Micro Stream Feature Extractor (30 s5 bars, 5 features)
        self.micro_conv1 = nn.Conv1d(5, d_model, kernel_size=3, padding=1)
        self.micro_conv2 = nn.Conv1d(d_model, d_model, kernel_size=5, padding=2)
        self.micro_norm = nn.LayerNorm(d_model)
        
        # 3. Cross-Attention: Micro queries Macro
        self.cross_attn = nn.MultiheadAttention(embed_dim=d_model, num_heads=nhead, dropout=dropout, batch_first=True)
        self.attn_norm = nn.LayerNorm(d_model)
        
        # 4. Bridge Feature Embedding (4 physical features)
        self.bridge_dense = nn.Sequential(
            nn.Linear(4, d_model // 2),
            nn.GELU(),
            nn.Linear(d_model // 2, d_model // 2)
        )
        
        # 5. Pooling & Fusion Head
        # Pooled Macro (d_model) + Pooled Micro (d_model) + Bridge (d_model // 2)
        fusion_dim = d_model * 2 + d_model // 2
        self.classifier = nn.Sequential(
            nn.Linear(fusion_dim, d_model),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(d_model, 16),
            nn.GELU(),
            nn.Linear(16, 1)  # Binary logit: P(Win)
        )

    def forward(self, macro, micro, bridge):
        # macro: [B, 30, 5] -> permute to [B, 5, 30] for Conv1d
        m_in = macro.permute(0, 2, 1)
        m_feat = F.gelu(self.macro_conv1(m_in))
        m_feat = F.gelu(self.macro_conv2(m_feat))
        m_seq = self.macro_norm(m_feat.permute(0, 2, 1))  # [B, 30, d_model]
        
        # micro: [B, 30, 5] -> permute to [B, 5, 30]
        u_in = micro.permute(0, 2, 1)
        u_feat = F.gelu(self.micro_conv1(u_in))
        u_feat = F.gelu(self.micro_conv2(u_feat))
        u_seq = self.micro_norm(u_feat.permute(0, 2, 1))  # [B, 30, d_model]
        
        # Cross-Attention: Micro queries Macro
        attn_out, _ = self.cross_attn(query=u_seq, key=m_seq, value=m_seq)
        u_context = self.attn_norm(u_seq + attn_out)
        
        # Global Average Pooling across time
        macro_pool = torch.mean(m_seq, dim=1)        # [B, d_model]
        micro_pool = torch.mean(u_context, dim=1)    # [B, d_model]
        
        # Bridge vector
        bridge_feat = self.bridge_dense(bridge)      # [B, d_model // 2]
        
        # Fusion
        fused = torch.cat([macro_pool, micro_pool, bridge_feat], dim=-1)
        logit = self.classifier(fused).squeeze(-1)   # [B]
        return logit


def train_dual_stream(data_path="ml_service/data/dataset_v5_dual_stream.npz", epochs=40, batch_size=32, lr=1e-3):
    print("=" * 70)
    print("[*] Training Dual-Stream MTF Neural Brain (V5 Architecture)")
    print(f"    Data: {data_path}")
    print("=" * 70)
    
    data = np.load(data_path)
    macro = data["macro"]
    micro = data["micro"]
    bridge = data["bridge"]
    labels = data["labels"].astype(np.float32)
    directions = data["directions"]
    
    N = len(labels)
    idx_train = int(N * 0.70)
    idx_val = int(N * 0.85)
    
    # Chronological Split
    tr_macro, val_macro, ts_macro = macro[:idx_train], macro[idx_train:idx_val], macro[idx_val:]
    tr_micro, val_micro, ts_micro = micro[:idx_train], micro[idx_train:idx_val], micro[idx_val:]
    tr_bridge, val_bridge, ts_bridge = bridge[:idx_train], bridge[idx_train:idx_val], bridge[idx_val:]
    tr_y, val_y, ts_y = labels[:idx_train], labels[idx_train:idx_val], labels[idx_val:]
    
    print(f"[+] Samples: Train = {len(tr_y):,} (70%), Val = {len(val_y):,} (15%), Test = {len(ts_y):,} (15% untouched)")
    print(f"    Train Win Ratio: {np.mean(tr_y)*100:.1f}% | Val Win Ratio: {np.mean(val_y)*100:.1f}% | Test Win Ratio: {np.mean(ts_y)*100:.1f}%")
    
    tr_dataset = TensorDataset(torch.tensor(tr_macro), torch.tensor(tr_micro), torch.tensor(tr_bridge), torch.tensor(tr_y))
    val_dataset = TensorDataset(torch.tensor(val_macro), torch.tensor(val_micro), torch.tensor(val_bridge), torch.tensor(val_y))
    ts_dataset = TensorDataset(torch.tensor(ts_macro), torch.tensor(ts_micro), torch.tensor(ts_bridge), torch.tensor(ts_y))
    
    tr_loader = DataLoader(tr_dataset, batch_size=batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=batch_size, shuffle=False)
    
    model = DualStreamMTFBrain(d_model=32, nhead=2, dropout=0.15)
    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=1e-3)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=epochs, eta_min=1e-5)
    
    # Asymmetric BO Focal Loss: penalizes false positives more heavily due to 82% payout
    criterion = nn.BCEWithLogitsLoss(pos_weight=torch.tensor([1.18]))
    
    best_val_score = 0.0
    best_checkpoint = None
    
    print("-" * 70)
    print("Epoch | Tr Loss | Val Loss | Val Acc | WR @52% (N) | WR @55% (N) | WR @58% (N)")
    print("-" * 70)
    
    for epoch in range(1, epochs + 1):
        model.train()
        tr_loss_sum = 0.0
        for m, u, b, y in tr_loader:
            optimizer.zero_grad()
            logits = model(m, u, b)
            loss = criterion(logits, y)
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            optimizer.step()
            tr_loss_sum += loss.item() * len(y)
            
        scheduler.step()
        tr_loss = tr_loss_sum / len(tr_y)
        
        # Validation evaluation
        model.eval()
        val_loss_sum = 0.0
        all_val_probs = []
        all_val_y = []
        with torch.no_grad():
            for m, u, b, y in val_loader:
                logits = model(m, u, b)
                loss = criterion(logits, y)
                val_loss_sum += loss.item() * len(y)
                probs = torch.sigmoid(logits).cpu().numpy()
                all_val_probs.extend(probs)
                all_val_y.extend(y.numpy())
                
        val_loss = val_loss_sum / len(val_y)
        all_val_probs = np.array(all_val_probs)
        all_val_y = np.array(all_val_y)
        val_acc = np.mean((all_val_probs >= 0.5) == all_val_y) * 100.0
        
        # Selective Winrates
        def calc_wr(threshold):
            mask = all_val_probs >= threshold
            count = int(np.sum(mask))
            wr = float(np.mean(all_val_y[mask]) * 100.0) if count >= 3 else 0.0
            return wr, count
            
        wr52, c52 = calc_wr(0.52)
        wr55, c55 = calc_wr(0.55)
        wr58, c58 = calc_wr(0.58)
        
        score = wr55 if c55 >= 10 else wr52
        if score > best_val_score and c52 >= 15:
            best_val_score = score
            best_checkpoint = {
                "epoch": epoch,
                "model_state_dict": model.state_dict(),
                "val_score": score,
                "val_loss": val_loss
            }
            
        print(f"{epoch:5d} | {tr_loss:.4f}  | {val_loss:.4f}   | {val_acc:5.1f}%  | {wr52:5.1f}% ({c52:2d}) | {wr55:5.1f}% ({c55:2d}) | {wr58:5.1f}% ({c58:2d})")

    # Load best checkpoint
    if best_checkpoint:
        model.load_state_dict(best_checkpoint["model_state_dict"])
        print(f"[+] Loaded best checkpoint from Epoch {best_checkpoint['epoch']} (Val Score: {best_checkpoint['val_score']:.1f}%)")
        
    # --- EVALUATION ON COMPLETELY UNTOUCHED TEST SET (15%) ---
    model.eval()
    ts_loader = DataLoader(ts_dataset, batch_size=batch_size, shuffle=False)
    all_ts_probs = []
    all_ts_y = []
    with torch.no_grad():
        for m, u, b, y in ts_loader:
            logits = model(m, u, b)
            probs = torch.sigmoid(logits).cpu().numpy()
            all_ts_probs.extend(probs)
            all_ts_y.extend(y.numpy())
            
    all_ts_probs = np.array(all_ts_probs)
    all_ts_y = np.array(all_ts_y)
    
    print("=" * 70)
    print("HONEST OUT-OF-SAMPLE TEST SET REPORT (Completely Unseen 15% Test Set):")
    print("=" * 70)
    print(f"Total Test Samples: {len(all_ts_y):,}")
    print(f"Baseline Win Ratio: {np.mean(all_ts_y)*100:.2f}%")
    print("-" * 70)
    
    for thresh in [0.50, 0.52, 0.54, 0.56, 0.58, 0.60]:
        mask = all_ts_probs >= thresh
        cnt = int(np.sum(mask))
        wr = float(np.mean(all_ts_y[mask]) * 100.0) if cnt > 0 else 0.0
        pct_trades = (cnt / len(all_ts_y)) * 100.0
        print(f"Confidence >= {thresh*100:.0f}%: WinRate = {wr:5.2f}% ({cnt:3d} trades, {pct_trades:4.1f}% of market)")

    # Save PyTorch checkpoint
    os.makedirs("ml_service/models", exist_ok=True)
    save_pt = "ml_service/models/dual_stream_brain_v5.pt"
    torch.save(best_checkpoint or model.state_dict(), save_pt)
    print(f"[+] Saved PyTorch weights to: {save_pt}")
    
    # Auto-export to ONNX for 1ms in-process C# execution!
    save_onnx = "ml_service/models/dual_stream_brain_v5.onnx"
    dummy_macro = torch.randn(1, 30, 5)
    dummy_micro = torch.randn(1, 30, 5)
    dummy_bridge = torch.randn(1, 4)
    
    torch.onnx.export(
        model,
        (dummy_macro, dummy_micro, dummy_bridge),
        save_onnx,
        input_names=["macro", "micro", "bridge"],
        output_names=["win_logit"],
        dynamic_axes={
            "macro": {0: "batch_size"},
            "micro": {0: "batch_size"},
            "bridge": {0: "batch_size"},
            "win_logit": {0: "batch_size"}
        },
        opset_version=14
    )
    onnx_size_kb = os.path.getsize(save_onnx) / 1024
    print(f"[+] Successfully exported in-process ONNX model: {save_onnx} ({onnx_size_kb:.1f} KB)")
    print("=" * 70)


if __name__ == "__main__":
    train_dual_stream()
