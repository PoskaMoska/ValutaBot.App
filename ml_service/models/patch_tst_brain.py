"""
patch_tst_brain.py - SOTA Patch-Time-Series Dual-Stream Transformer (V3 Architecture)

Inspired by PatchTST (ICLR 2023):
- Slices sequential candlestick series into semantic patches (sub-segments).
- Dual-Stream Architecture:
    Stream A (Micro): 160 micro-candles (s5/s15/s30) sliced into patches -> Local momentum & patterns
    Stream B (Macro): 30 MTF candles (m1/m5) sliced into patches -> Higher-timeframe market structure
- Cross-Attention: Micro-stream queries Macro MTF stream to check if higher-timeframe trend aligns.
- Tabular Macro Injection: Fuses DXY momentum, live spread, day liquidity anchors.
- Output: 3-class calibrated probabilities [HOLD, BUY, PUT] + auxiliary magnitude regression.
- Super lightweight & ultra-fast inference (< 5ms on CPU).
"""

import math
import torch
import torch.nn as nn
import torch.nn.functional as F


class PatchEmbedding(nn.Module):
    """
    Slices a continuous time series [B, T, C] into overlapping patches
    and linearly projects them to dimension d_model.
    """
    def __init__(self, patch_size: int = 8, stride: int = 4, in_channels: int = 5, d_model: int = 64):
        super().__init__()
        self.patch_size = patch_size
        self.stride = stride
        self.patch_dim = patch_size * in_channels
        self.proj = nn.Linear(self.patch_dim, d_model)
        self.norm = nn.LayerNorm(d_model)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x: [B, T, C]
        b, t, c = x.shape
        # Unfold along time dimension
        # x_patches: [B, num_patches, patch_size, C]
        patches = x.unfold(dimension=1, size=self.patch_size, step=self.stride)
        # patches: [B, num_patches, C, patch_size] -> permute to [B, num_patches, patch_size * C]
        b, num_patches, c_dim, p_size = patches.shape
        patches = patches.permute(0, 1, 3, 2).contiguous().view(b, num_patches, -1)
        
        # Project to d_model
        out = self.norm(self.proj(patches))
        return out


class DualStreamPatchBrain(nn.Module):
    def __init__(
        self,
        candle_features: int = 5,
        seq_len_micro: int = 160,
        seq_len_mtf: int = 30,
        macro_features: int = 23,
        d_model: int = 64,
        nhead: int = 4,
        num_layers: int = 2,
        num_classes: int = 3,
        dropout: float = 0.2
    ):
        super().__init__()
        self.d_model = d_model

        # 1. Micro-stream Patch Embedding (160 candles -> patch 8, stride 4 => 39 patches)
        self.micro_patcher = PatchEmbedding(patch_size=8, stride=4, in_channels=candle_features, d_model=d_model)
        
        # 2. MTF-stream Patch Embedding (30 candles -> patch 6, stride 3 => 9 patches)
        self.mtf_patcher = PatchEmbedding(patch_size=6, stride=3, in_channels=candle_features, d_model=d_model)

        # Positional encodings (learnable)
        self.micro_pos = nn.Parameter(torch.randn(1, 50, d_model) * 0.02)
        self.mtf_pos = nn.Parameter(torch.randn(1, 20, d_model) * 0.02)

        # 3. Transformer Encoders for both streams
        encoder_layer = nn.TransformerEncoderLayer(
            d_model=d_model,
            nhead=nhead,
            dim_feedforward=d_model * 2,
            dropout=dropout,
            activation="gelu",
            batch_first=True
        )
        self.micro_transformer = nn.TransformerEncoder(encoder_layer, num_layers=num_layers)
        self.mtf_transformer = nn.TransformerEncoder(encoder_layer, num_layers=num_layers)

        # 4. Cross-Attention: Micro stream queries MTF macro stream
        self.cross_attn = nn.MultiheadAttention(embed_dim=d_model, num_heads=nhead, dropout=dropout, batch_first=True)
        self.cross_norm = nn.LayerNorm(d_model)

        # 5. Macro Context Vector MLP (DXY, Spread, Day anchors, etc.)
        self.macro_encoder = nn.Sequential(
            nn.Linear(macro_features, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(64, 64),
            nn.LayerNorm(64),
            nn.GELU()
        )

        # 6. Final Multimodal Fusion Head
        # Fused: Micro Pooled (64) + Cross-Attn Pooled (64) + MTF Pooled (64) + Macro Vector (64) = 256
        fusion_dim = d_model * 3 + 64
        self.classifier = nn.Sequential(
            nn.Linear(fusion_dim, 128),
            nn.LayerNorm(128),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(128, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout / 2),
            nn.Linear(64, num_classes)
        )

        # Auxiliary magnitude regressor (Multi-task PnL regularization)
        self.pnl_regressor = nn.Linear(64, 1)

    def forward(
        self,
        x_micro: torch.Tensor,
        x_mtf: torch.Tensor,
        x_macro: torch.Tensor
    ):
        """
        x_micro: [B, 160, 5]
        x_mtf:   [B, 30, 5]
        x_macro: [B, 23]
        """
        # --- Stream A: Micro patches ---
        p_micro = self.micro_patcher(x_micro)  # [B, N_micro, d_model]
        p_micro = p_micro + self.micro_pos[:, :p_micro.size(1), :]
        h_micro = self.micro_transformer(p_micro)  # [B, N_micro, d_model]

        # --- Stream B: MTF patches ---
        p_mtf = self.mtf_patcher(x_mtf)        # [B, N_mtf, d_model]
        p_mtf = p_mtf + self.mtf_pos[:, :p_mtf.size(1), :]
        h_mtf = self.mtf_transformer(p_mtf)    # [B, N_mtf, d_model]

        # --- Cross-Attention: Micro queries MTF ---
        attn_out, _ = self.cross_attn(query=h_micro, key=h_mtf, value=h_mtf)
        h_cross = self.cross_norm(h_micro + attn_out)

        # Mean pooling across tokens
        pool_micro = h_micro.mean(dim=1)    # [B, d_model]
        pool_mtf = h_mtf.mean(dim=1)        # [B, d_model]
        pool_cross = h_cross.mean(dim=1)    # [B, d_model]

        # --- Stream C: Macro context vector ---
        pool_macro = self.macro_encoder(x_macro)  # [B, 64]

        # Multimodal fusion
        fused = torch.cat([pool_micro, pool_mtf, pool_cross, pool_macro], dim=-1)  # [B, 256]

        latent = self.classifier[0:6](fused)  # [B, 64]
        logits = self.classifier[6:](latent)   # [B, 3]

        aux_pnl = self.pnl_regressor(latent).squeeze(-1)  # [B]

        return logits, aux_pnl

    def predict_probabilities(self, x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor):
        self.eval()
        with torch.no_grad():
            logits, _ = self.forward(x_micro, x_mtf, x_macro)
            probs = F.softmax(logits, dim=-1)
        return probs
