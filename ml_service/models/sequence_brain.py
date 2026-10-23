"""
sequence_brain.py - Neural Brain Model Architecture (V3 Layer 1)

Hybrid Temporal Architecture for Financial Micro-Horizon Forecasting:
- Input 1: Sequence of 160 stationary candles [batch_size, 160, 5]
  (Relative Returns, Shadows, Log-Volume)
- Input 2: Macro Context & Spatial Anchors [batch_size, D_macro]
  (Day/Asian ranges, DXY Momentum, Basket Sync, Spreads, SMC flags, Cyclical time)

Core Pipeline:
1. Multi-Scale 1D Temporal Convolution (Captures micro-patterns, candlesticks, wicks)
2. Bidirectional Temporal Encoder (BiGRU or Transformer Encoder with Positional Encoding)
3. Multi-Head Scaled Dot-Product Attention Pooling across sequence timesteps
4. Dense Context Projection with GELU & LayerNorm
5. Multimodal Fusion Head -> 3-Class Calibrated Logits:
   [0: HOLD/Chop, 1: BUY, 2: PUT]
"""

import math
import torch
import torch.nn as nn
import torch.nn.functional as F


class PositionalEncoding(nn.Module):
    def __init__(self, d_model: int, max_len: int = 500):
        super().__init__()
        pe = torch.zeros(max_len, d_model)
        position = torch.arange(0, max_len, dtype=torch.float).unsqueeze(1)
        div_term = torch.exp(torch.arange(0, d_model, 2).float() * (-math.log(10000.0) / d_model))
        pe[:, 0::2] = torch.sin(position * div_term)
        pe[:, 1::2] = torch.cos(position * div_term)
        self.register_buffer("pe", pe.unsqueeze(0))  # [1, max_len, d_model]

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x: [B, T, D]
        return x + self.pe[:, :x.size(1)]


class TemporalAttentionPooling(nn.Module):
    """
    Self-attention pooling: computes an attention weight for each timestep
    and outputs a weighted context vector representing the entire trajectory.
    """
    def __init__(self, d_model: int):
        super().__init__()
        self.query = nn.Linear(d_model, 1)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        # x: [B, T, D]
        weights = F.softmax(self.query(x), dim=1)  # [B, T, 1]
        pooled = torch.sum(x * weights, dim=1)    # [B, D]
        return pooled


class SequenceBrain(nn.Module):
    def __init__(
        self,
        candle_features: int = 5,
        seq_len: int = 160,
        macro_features: int = 23,
        d_model: int = 64,
        nhead: int = 4,
        num_layers: int = 2,
        num_classes: int = 3,
        dropout: float = 0.2,
        backbone: str = "hybrid"  # 'hybrid' (Conv1D + BiGRU + Attn) or 'transformer'
    ):
        super().__init__()
        self.backbone_type = backbone
        self.d_model = d_model

        # 1. Multi-scale Convolutional Feature Extractor
        # Conv1d expects [B, Channels, Length]
        self.conv1 = nn.Conv1d(candle_features, d_model // 2, kernel_size=3, padding=1)
        self.spatial_drop = nn.Dropout2d(dropout)  # Drops entire feature channels
        self.conv2 = nn.Conv1d(candle_features, d_model // 2, kernel_size=7, padding=3)
        self.conv_norm = nn.LayerNorm(d_model)

        # 2. Sequence Encoder
        if backbone == "transformer":
            self.pos_encoder = PositionalEncoding(d_model, max_len=seq_len + 50)
            encoder_layer = nn.TransformerEncoderLayer(
                d_model=d_model,
                nhead=nhead,
                dim_feedforward=d_model * 2,
                dropout=dropout,
                activation="gelu",
                batch_first=True
            )
            self.transformer_encoder = nn.TransformerEncoder(encoder_layer, num_layers=num_layers)
            self.seq_proj = nn.Linear(d_model, d_model)
        else:
            # Hybrid: BiGRU
            self.bigru = nn.GRU(
                input_size=d_model,
                hidden_size=d_model // 2,
                num_layers=num_layers,
                batch_first=True,
                bidirectional=True,
                dropout=dropout if num_layers > 1 else 0.0
            )
            self.seq_proj = nn.Linear(d_model, d_model)

        # Attention Pooling
        self.attn_pool = TemporalAttentionPooling(d_model)

        # 3. Macro Context Projection MLP
        self.macro_encoder = nn.Sequential(
            nn.Linear(macro_features, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(64, 64),
            nn.LayerNorm(64),
            nn.GELU()
        )

        # 4. Multimodal Fusion Head
        fusion_dim = d_model + 64
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

        # Auxiliary heads (Multi-task learning for regularization)
        # Predicts continuous PnL and MFE to force latent representation to learn magnitude
        self.pnl_regressor = nn.Linear(64, 1)

        # Learned temperature for output calibration (optimized during training)
        self.temperature = nn.Parameter(torch.tensor(1.5))

    def _forward_single(self, x_candles: torch.Tensor, x_macro: torch.Tensor):
        xc_trans = x_candles.transpose(1, 2)
        c1 = F.gelu(self.conv1(xc_trans))
        c2 = F.gelu(self.conv2(xc_trans))
        conv_out = torch.cat([c1, c2], dim=1)

        seq_features = conv_out.transpose(1, 2)
        seq_features = self.spatial_drop(seq_features.unsqueeze(2)).squeeze(2)
        seq_features = self.conv_norm(seq_features)

        if self.backbone_type == "transformer":
            seq_features = self.pos_encoder(seq_features)
            enc_out = self.transformer_encoder(seq_features)
        else:
            enc_out, _ = self.bigru(seq_features)

        enc_out = F.gelu(self.seq_proj(enc_out))
        pooled_seq = self.attn_pool(enc_out)
        macro_out = self.macro_encoder(x_macro)
        fused = torch.cat([pooled_seq, macro_out], dim=-1)

        latent = self.classifier[0:6](fused)
        logits = self.classifier[6:](latent)
        aux_pnl = self.pnl_regressor(latent).squeeze(-1)
        return logits, aux_pnl

    def forward(self, x_candles: torch.Tensor, x_macro: torch.Tensor):
        """
        x_candles: [B, seq_len, 8]
        x_macro:   [B, macro_features]
        """
        from models.patch_tst_brain import DualStreamPatchBrain
        xm_pos, xm_neg = DualStreamPatchBrain._sanitize_and_flip_macro(x_macro)
        logits_pos, pnl_pos = self._forward_single(x_candles, xm_pos)
        logits_neg, pnl_neg = self._forward_single(DualStreamPatchBrain._flip_candles(x_candles), xm_neg)

        deep_dir_raw = 0.5 * ((logits_pos[:, 1] - logits_pos[:, 2]) - (logits_neg[:, 1] - logits_neg[:, 2]))
        deep_dir = 0.002 * torch.tanh(deep_dir_raw)

        # Fallback dummy MTF tensor from last 30 micro candles for single-stream SequenceBrain
        x_mtf_proxy = x_candles[:, -30:, :]
        alpha_dir, _ = DualStreamPatchBrain._compute_orthogonal_alpha(x_candles, x_mtf_proxy, x_macro)

        total_dir = alpha_dir + deep_dir
        hold_logit = 0.5 * (logits_pos[:, 0] + logits_neg[:, 0]) - 2.5
        buy_logit = 0.5 * total_dir
        put_logit = -0.5 * total_dir

        logits = torch.stack([hold_logit, buy_logit, put_logit], dim=-1)
        aux_pnl = 0.5 * (pnl_pos - pnl_neg)
        return logits, aux_pnl

    def predict_probabilities(self, x_candles: torch.Tensor, x_macro: torch.Tensor):
        self.eval()
        with torch.no_grad():
            logits, _ = self.forward(x_candles, x_macro)
            probs = F.softmax(logits, dim=-1)
        return probs
