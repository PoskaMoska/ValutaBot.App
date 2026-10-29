"""
patch_tst_brain.py - SOTA Patch-Time-Series Dual-Stream Transformer (V3 Architecture)

Inspired by PatchTST (ICLR 2023) with Sign-Symmetric (Antisymmetric) Siamese Readout
and Orthogonal 4-Pillar Microstructure Confluence:
- Slices sequential candlestick series into semantic patches (sub-segments) while preserving amplitude.
- Dual-Stream Architecture:
    Stream A (Micro): 160 micro-candles (s5/s10/s15/s30/m1) sliced into patches -> Local momentum & patterns
    Stream B (Macro): 30 MTF candles (m1/m5) sliced into patches -> Higher-timeframe market structure
- Cross-Attention: Micro-stream queries Macro MTF stream to check if higher-timeframe trend aligns.
- Siamese Sign-Symmetry: Guarantees zero directional drift from unsigned calendar/regime features
  (hour_utc, day_of_week, ADX, PriceEntropy, Spread, Volume) across all sessions and assets.
- Orthogonal 4-Pillar Alpha Stream:
    Pillar A: Self-Calibrating Multi-Horizon Regime Autocorrelation (trailing 40-80 bars)
    Pillar B: Acute Wick Rejection & Exhaustion (1-bar and 3-bar buyer/seller rejection)
    Pillar C: BasketSync-Gated TA Confluence & Pair-Aligned DXY Momentum
    Pillar D: MTF Trend + Micro Pullback Interaction
- Output: 3-class calibrated probabilities [HOLD, BUY, PUT] + auxiliary magnitude regression.
"""

import math
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F


class PatchEmbedding(nn.Module):
    """
    Slices a continuous time series [B, T, C] into overlapping patches
    and projects them to dimension d_model while preserving patch amplitude.
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
        patches = x.unfold(dimension=1, size=self.patch_size, step=self.stride)
        b, num_patches, c_dim, p_size = patches.shape
        patches = patches.permute(0, 1, 3, 2).contiguous().view(b, num_patches, -1)

        # Preserve physical impulse amplitude across LayerNorm (eps=1e-6 prevents 0/0 NaN gradient on flat/zero-padded patches)
        patch_norm = torch.sqrt((patches ** 2).sum(dim=-1, keepdim=True) + 1e-6)
        amp = torch.tanh(patch_norm / 10.0)
        out = self.norm(self.proj(patches)) * (0.5 + amp)
        return out


class DualStreamPatchBrain(nn.Module):
    def __init__(
        self,
        candle_features: int = 8,
        seq_len_micro: int = 160,
        seq_len_mtf: int = 30,
        macro_features: int = 31,
        d_model: int = 64,
        nhead: int = 4,
        num_layers: int = 2,
        num_classes: int = 3,
        dropout: float = 0.05
    ):
        super().__init__()
        self.d_model = d_model
        self.candle_features = candle_features
        self.geom_channels = candle_features + 3

        # 1. Micro-stream Patch Embedding (160 candles -> patch 8, stride 4 => 39 patches)
        self.micro_patcher = PatchEmbedding(patch_size=8, stride=4, in_channels=self.geom_channels, d_model=d_model)

        # 2. MTF-stream Patch Embedding (30 candles -> patch 6, stride 3 => 9 patches)
        self.mtf_patcher = PatchEmbedding(patch_size=6, stride=3, in_channels=self.geom_channels, d_model=d_model)

        # 2b. High-Resolution Last-5 Micro-Candle Branch conditioned on Timeframe (s5/s10/s15/s30/m1)
        self.last5_encoder = nn.Sequential(
            nn.Linear(5 * self.geom_channels + 5, d_model),
            nn.LayerNorm(d_model),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(d_model, d_model),
            nn.LayerNorm(d_model),
            nn.GELU()
        )

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

        # 5. Vector Asset Embedding (nn.Embedding instead of scalar 0..5) + Macro Context Vector MLP
        self.asset_embedding = nn.Embedding(num_embeddings=10, embedding_dim=16)
        self.macro_encoder = nn.Sequential(
            nn.Linear(macro_features, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout),
            nn.Linear(64, 64),
            nn.LayerNorm(64),
            nn.GELU()
        )

        # 6. Cross-Modal Sigmoid Gate (Gated Multimodal Unit between Candle Streams and Macro+Asset Streams)
        self.candle_proj = nn.Sequential(
            nn.Linear(d_model * 4, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout)
        )
        self.macro_proj = nn.Sequential(
            nn.Linear(64 + 16, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout)
        )
        self.sigmoid_gate = nn.Linear(128, 64)

        # 7. Shared Latent Trunk + Multi-Head Timeframe Classifiers (5 heads: s5, s10, s15, s30, m1)
        self.classifier = nn.Sequential(
            nn.Linear(128, 64),
            nn.LayerNorm(64),
            nn.GELU(),
            nn.Dropout(dropout / 2)
        )
        self.tf_heads = nn.ModuleList([nn.Linear(64, num_classes) for _ in range(5)])

        # 8. 30-Cell (6 Pairs x 5 Timeframes) Specialized Non-Linear Regime & Sniper Gate Matrices
        self.cell_expert_weights = nn.Parameter(torch.zeros(6, 5, 48), requires_grad=False)
        self.cell_odd_std = nn.Parameter(torch.ones(1, 48), requires_grad=False)
        self.cell_gate_weights = nn.Parameter(torch.zeros(6, 5, 25), requires_grad=False)
        self.cell_even_std = nn.Parameter(torch.ones(1, 25), requires_grad=False)
        # cell_hyper[aid, ti] = [alpha_blend, gate_scale, goldilocks_tau, cell_p85]
        default_hyper = torch.tensor([0.50, 0.35, 0.35, 0.25], dtype=torch.float32).view(1, 1, 4).repeat(6, 5, 1)
        self.cell_hyper = nn.Parameter(default_hyper, requires_grad=False)

        # Auxiliary magnitude regressor (Multi-task PnL regularization)
        self.pnl_regressor = nn.Linear(64, 1)

        # Learned temperature for output calibration
        self.temperature = nn.Parameter(torch.tensor(1.0))

    @staticmethod
    def _transform_candles_atr_geometry(x: torch.Tensor) -> torch.Tensor:
        """
        Transforms raw bps candles [B, T, C] into ATR-normalized (sigma-scaled) candles
        and appends 3 structural geometry channels [B, T, C + 3]:
          - ch_pos  : Rolling 20-bar Donchian channel position in [-1, +1]
          - ema_dev : Rolling 20-bar mean deviation in ATR units [-1, +1]
          - wick_dom: Net wick rejection dominance (l_wick - u_wick) / ATR in [-1, +1]
        """
        c_body = x[:, :, 0]
        if x.size(2) >= 8:
            u_wick = x[:, :, 5]
            l_wick = x[:, :, 6]
            rng = x[:, :, 7]
        else:
            hi = x[:, :, 1]
            lo = x[:, :, 2]
            u_wick = torch.relu(hi - torch.clamp(c_body, min=0.0))
            l_wick = torch.relu(torch.clamp(c_body, max=0.0) - lo)
            rng = hi - lo

        # Window ATR in bps (trailing 20 bars, clamped >= 0.35 bps)
        tail_len = min(20, x.size(1))
        atr = rng[:, -tail_len:].mean(dim=1, keepdim=True) + 0.35  # [B, 1]
        atr_3d = atr.unsqueeze(-1)  # [B, 1, 1]

        x_norm = x.clone()
        x_norm[:, :, 0:4] = torch.clamp(x[:, :, 0:4] / atr_3d, -8.0, 8.0)
        if x.size(2) >= 8:
            x_norm[:, :, 5:8] = torch.clamp(x[:, :, 5:8] / atr_3d, -8.0, 8.0)

        # Cumulative relative price curve for 20-bar channel geometry
        cum_c = torch.cumsum(c_body, dim=1)  # [B, T]
        win_k = min(20, x.size(1))
        padded = F.pad(cum_c, (win_k - 1, 0), mode="replicate")
        win20 = padded.unfold(dimension=1, size=win_k, step=1)  # [B, T, win_k]

        hi20 = win20.max(dim=-1).values
        lo20 = win20.min(dim=-1).values
        mid20 = 0.5 * (hi20 + lo20)
        span20 = (hi20 - lo20).clamp(min=0.5)

        ch_pos = torch.clamp((cum_c - mid20) / (0.5 * span20), -1.0, 1.0)
        mean20 = win20.mean(dim=-1)
        ema_dev = torch.tanh((cum_c - mean20) / (atr * 3.0))
        wick_dom = torch.tanh((l_wick - u_wick) / (atr * 1.5))

        geom = torch.stack([ch_pos, ema_dev, wick_dom], dim=-1)  # [B, T, 3]
        return torch.cat([x_norm, geom], dim=-1)

    @staticmethod
    def _flip_candles(x: torch.Tensor) -> torch.Tensor:
        out = x.clone()
        out[:, :, 0] = -x[:, :, 0]
        out[:, :, 1] = -x[:, :, 2]
        out[:, :, 2] = -x[:, :, 1]
        out[:, :, 3] = -x[:, :, 3]
        if x.size(2) >= 8:
            out[:, :, 5] = x[:, :, 6]
            out[:, :, 6] = x[:, :, 5]
        return out

    @staticmethod
    def _sanitize_and_flip_macro(xm: torch.Tensor):
        """
        Sanitizes macro features, zeroes non-stationary multi-hour session anchors
        (day/asian high/low distances, calendar clock, ordinal asset_id) to prevent session-drift overfitting,
        and constructs the exact market-inverted mirror vector for Siamese Sign-Symmetry.
        """
        pos = xm.clone()
        # Zero out non-stationary session drift & calendar features so deep MLP cannot memorize time-of-day trends
        for idx in (0, 1, 2, 7, 8, 12, 13, 14, 15, 16):
            if xm.size(1) > idx:
                pos[:, idx] = 0.0
        pos[:, 9] = torch.clamp(xm[:, 9], 0.0, 3.0) / 3.0  # bound price_entropy
        if xm.size(1) > 22:
            pos[:, 22] = 0.0  # neutralize ordinal asset_id in deep MLP
        if xm.size(1) >= 31:
            pos[:, 24] = torch.tanh(xm[:, 24] * 1.5)
            pos[:, 25] = torch.tanh(xm[:, 25] * 300.0)     # rescale hma_slope from ~1e-3 to [-1, 1]
            pos[:, 28] = torch.clamp(xm[:, 28] - 0.5, -1.0, 1.0)

        neg = pos.clone()
        neg[:, 3] = -pos[:, 3]
        neg[:, 4] = -pos[:, 4]
        # Note: idx 5 (basket_sync_score) is unsigned synchronization quality in [0, 1] -> invariant!
        neg[:, 11] = -pos[:, 11]
        neg[:, 18] = -pos[:, 18]
        neg[:, 19] = -pos[:, 19]
        neg[:, 20] = -pos[:, 20]
        neg[:, 21] = -pos[:, 21]
        if xm.size(1) >= 31:
            neg[:, 23] = -pos[:, 23]
            neg[:, 24] = -pos[:, 24]
            neg[:, 25] = -pos[:, 25]
            neg[:, 28] = -pos[:, 28]
        return pos, neg

    @staticmethod
    def _extract_odd_features(x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor):
        c_body = x_micro[:, :, 0]
        if x_micro.size(2) >= 8:
            u_wick = x_micro[:, :, 5]
            l_wick = x_micro[:, :, 6]
            rng = x_micro[:, :, 7]
        else:
            hi = x_micro[:, :, 1]
            lo = x_micro[:, :, 2]
            u_wick = torch.relu(hi - torch.clamp(c_body, min=0.0))
            l_wick = torch.relu(torch.clamp(c_body, max=0.0) - lo)
            rng = hi - lo

        vol5 = rng[:, -5:].mean(dim=1) + 0.5
        vol20 = rng[:, -20:].mean(dim=1) + 0.5
        vol60 = rng[:, -60:].mean(dim=1) + 0.5

        z1 = torch.tanh(c_body[:, -1] / (vol5 * 1.5))
        z2 = torch.tanh(c_body[:, -2:].sum(dim=1) / (vol5 * 2.0))
        z3 = torch.tanh(c_body[:, -3:].sum(dim=1) / (vol5 * 2.5))
        z5 = torch.tanh(c_body[:, -5:].sum(dim=1) / (vol20 * 3.2))
        z8 = torch.tanh(c_body[:, -8:].sum(dim=1) / (vol20 * 4.0))
        z13 = torch.tanh(c_body[:, -13:].sum(dim=1) / (vol20 * 5.0))
        z21 = torch.tanh(c_body[:, -21:].sum(dim=1) / (vol60 * 6.0))

        w1 = torch.tanh((l_wick[:, -1] - u_wick[:, -1]) / (vol5 * 1.2))
        w2 = torch.tanh((l_wick[:, -2:].sum(dim=1) - u_wick[:, -2:].sum(dim=1)) / (vol5 * 1.8))
        w3 = torch.tanh((l_wick[:, -3:].sum(dim=1) - u_wick[:, -3:].sum(dim=1)) / (vol5 * 2.2))
        w5 = torch.tanh((l_wick[:, -5:].sum(dim=1) - u_wick[:, -5:].sum(dim=1)) / (vol20 * 2.8))

        z_prev1 = torch.tanh(c_body[:, -2] / (vol5 * 1.5))
        z_prev2 = torch.tanh(c_body[:, -3] / (vol5 * 1.5))
        z_accel = torch.tanh((c_body[:, -1] - c_body[:, -2]) / (vol5 * 1.5))

        cum_c = torch.cumsum(c_body, dim=1)
        win10 = cum_c[:, -10:]
        hi10, lo10 = win10.max(dim=1).values, win10.min(dim=1).values
        ch_pos10 = torch.clamp((cum_c[:, -1] - 0.5 * (hi10 + lo10)) / (0.5 * (hi10 - lo10).clamp(min=0.5)), -1.0, 1.0)

        win20 = cum_c[:, -20:]
        hi20, lo20 = win20.max(dim=1).values, win20.min(dim=1).values
        ch_pos20 = torch.clamp((cum_c[:, -1] - 0.5 * (hi20 + lo20)) / (0.5 * (hi20 - lo20).clamp(min=0.5)), -1.0, 1.0)
        ema_dev20 = torch.tanh((cum_c[:, -1] - win20.mean(dim=1)) / (vol20 * 3.0))

        win60 = cum_c[:, -60:]
        hi60, lo60 = win60.max(dim=1).values, win60.min(dim=1).values
        ch_pos60 = torch.clamp((cum_c[:, -1] - 0.5 * (hi60 + lo60)) / (0.5 * (hi60 - lo60).clamp(min=0.5)), -1.0, 1.0)

        s_b = torch.sign(c_body)
        edge150 = (s_b[:, -151:-1] * s_b[:, -150:]).mean(dim=1)
        edge40 = (s_b[:, -41:-1] * s_b[:, -40:]).mean(dim=1)
        adap1 = -edge150 * z1 * 4.0
        adap40 = -edge40 * z1 * 4.0

        mtf_body = x_mtf[:, :, 0]
        mtf_rng = x_mtf[:, :, 7] if x_mtf.size(2) >= 8 else (x_mtf[:, :, 1] - x_mtf[:, :, 2])
        mtf_vol5 = mtf_rng[:, -5:].mean(dim=1) + 0.5
        zm1 = torch.tanh(mtf_body[:, -1] / (mtf_vol5 * 1.5))
        zm2 = torch.tanh(mtf_body[:, -2:].sum(dim=1) / (mtf_vol5 * 2.0))
        zm3 = torch.tanh(mtf_body[:, -3:].sum(dim=1) / (mtf_vol5 * 2.5))
        zm6 = torch.tanh(mtf_body[:, -6:].sum(dim=1) / (mtf_vol5 * 3.5))
        mtf_w1 = torch.tanh((x_mtf[:, -1, 6] - x_mtf[:, -1, 5]) / (mtf_vol5 * 1.2)) if x_mtf.size(2) >= 8 else torch.zeros_like(zm1)

        bsync = torch.clamp(x_macro[:, 5], 0.0, 1.0)
        pe = torch.clamp(x_macro[:, 9], 0.0, 3.0) / 3.0
        adx = torch.clamp(x_macro[:, 10], 0.0, 1.0) if x_macro.size(1) > 10 else torch.zeros_like(bsync)
        rsi = torch.clamp(x_macro[:, 11], -1.0, 1.0) if x_macro.size(1) > 11 else torch.zeros_like(bsync)
        tf_sec = torch.round(x_macro[:, 17] * 60.0)
        ob = torch.clamp(x_macro[:, 18], -1.0, 1.0)
        fvg = torch.clamp(x_macro[:, 19], -1.0, 1.0)
        swp = torch.clamp(x_macro[:, 20], -1.0, 1.0)
        bos = torch.clamp(x_macro[:, 21], -1.0, 1.0)
        asset_id = torch.round(x_macro[:, 22]) if x_macro.size(1) > 22 else torch.zeros_like(bsync)
        usd_sign = torch.where(asset_id <= 2.0, -1.0, 1.0)
        dxy1 = usd_sign * x_macro[:, 3]
        dxy5 = usd_sign * x_macro[:, 4]

        if x_macro.size(1) >= 31:
            ta_score = torch.clamp(x_macro[:, 23], -1.0, 1.0)
            mvel = torch.tanh(x_macro[:, 24] * 1.5)
            hma = torch.tanh(x_macro[:, 25] * 300.0)
            vol_ratio = torch.clamp(x_macro[:, 26], 0.0, 1.0)
            bb_sq = torch.clamp(x_macro[:, 27], 0.0, 1.0)
        else:
            ta_score = -rsi * 0.5
            mvel = torch.zeros_like(bsync)
            hma = torch.zeros_like(bsync)
            vol_ratio = torch.ones_like(bsync)
            bb_sq = torch.zeros_like(bsync)

        z1_pe = -z1 * (pe - 0.42) * 2.5
        trend_flag = torch.sigmoid((adx - 0.25) * 12.0)

        # Base Timeframe Expert Formulas
        score_s5 = -0.25 * z3 - 0.25 * hma - 0.20 * mvel - 0.20 * ta_score + 0.25 * w2 + 0.20 * w1 + 0.25 * swp
        score_s10 = 0.35 * w1 + 0.25 * w3 + 0.20 * z3 + 0.20 * ta_score + 0.25 * dxy5 * bsync + 0.25 * adap1 + 0.15 * z1_pe
        score_s15 = -0.30 * z3 - 0.15 * z1 + 0.25 * w1 + 0.25 * w3 + 0.30 * dxy5 * bsync + 0.15 * zm1
        score_s30 = 0.35 * zm1 + 0.30 * z3 + 0.25 * ta_score + 0.25 * dxy5 * bsync - 0.25 * w3
        score_m1 = 0.30 * adap1 + 0.30 * w1 + 0.25 * dxy5 * bsync + 0.20 * z3 + 0.15 * bos

        base_tf_score = torch.where(
            tf_sec <= 5.0, score_s5,
            torch.where(
                tf_sec <= 10.0, score_s10,
                torch.where(
                    tf_sec <= 15.0, score_s15,
                    torch.where(tf_sec <= 30.0, score_s30, score_m1)
                )
            )
        )

        # 48 Sign-Symmetric (Odd) Microstructure & Regime-Interaction Features
        F_odd = torch.stack([
            z1, z2, z3, z5, z8, z13, z21,
            w1, w2, w3, w5, z_prev1, z_prev2, z_accel,
            ch_pos10, ch_pos20, ema_dev20, ch_pos60,
            adap1, adap40, zm1, zm2, zm3, zm6, mtf_w1,
            dxy1, dxy5, dxy1 * bsync, dxy5 * bsync, z1_pe,
            ob, fvg, swp, bos,
            ta_score, mvel, hma, rsi,
            z1 * trend_flag, z1 * (1.0 - trend_flag),
            z3 * trend_flag, z3 * (1.0 - trend_flag),
            w1 * torch.abs(ch_pos20), -ch_pos20 * torch.abs(w1),
            zm1 * (1.0 - trend_flag), zm3 * trend_flag,
            z1 * vol_ratio, w1 * vol_ratio
        ], dim=-1)

        return F_odd, base_tf_score, tf_sec, asset_id

    @staticmethod
    def _extract_even_gates(x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor, dir_s: torch.Tensor):
        c_body = x_micro[:, :, 0]
        if x_micro.size(2) >= 8:
            u_wick, l_wick, rng = x_micro[:, :, 5], x_micro[:, :, 6], x_micro[:, :, 7]
        else:
            hi, lo = x_micro[:, :, 1], x_micro[:, :, 2]
            u_wick = torch.relu(hi - torch.clamp(c_body, min=0.0))
            l_wick = torch.relu(torch.clamp(c_body, max=0.0) - lo)
            rng = hi - lo

        vol5 = rng[:, -5:].mean(dim=1) + 0.5
        vol20 = rng[:, -20:].mean(dim=1) + 0.5
        z1 = torch.tanh(c_body[:, -1] / (vol5 * 1.5))
        z3 = torch.tanh(c_body[:, -3:].sum(dim=1) / (vol5 * 2.5))
        w1 = torch.tanh((l_wick[:, -1] - u_wick[:, -1]) / (vol5 * 1.2))

        cum_c = torch.cumsum(c_body, dim=1)
        win20 = cum_c[:, -20:]
        hi20, lo20 = win20.max(dim=1).values, win20.min(dim=1).values
        ch_pos20 = torch.clamp((cum_c[:, -1] - 0.5 * (hi20 + lo20)) / (0.5 * (hi20 - lo20).clamp(min=0.5)), -1.0, 1.0)

        mtf_body = x_mtf[:, :, 0]
        mtf_rng = x_mtf[:, :, 7] if x_mtf.size(2) >= 8 else (x_mtf[:, :, 1] - x_mtf[:, :, 2])
        mtf_vol5 = mtf_rng[:, -5:].mean(dim=1) + 0.5
        zm1 = torch.tanh(mtf_body[:, -1] / (mtf_vol5 * 1.5))

        bsync = torch.clamp(x_macro[:, 5], 0.0, 1.0)
        pe = torch.clamp(x_macro[:, 9], 0.0, 3.0) / 3.0
        adx = torch.clamp(x_macro[:, 10], 0.0, 1.0) if x_macro.size(1) > 10 else torch.zeros_like(bsync)
        asset_id = torch.round(x_macro[:, 22]) if x_macro.size(1) > 22 else torch.zeros_like(bsync)
        usd_sign = torch.where(asset_id <= 2.0, -1.0, 1.0)
        dxy5 = usd_sign * x_macro[:, 4]

        if x_macro.size(1) >= 31:
            ta_score = torch.clamp(x_macro[:, 23], -1.0, 1.0)
            vol_ratio = torch.clamp(x_macro[:, 26], 0.0, 1.0)
            bb_sq = torch.clamp(x_macro[:, 27], 0.0, 1.0)
        else:
            ta_score = torch.zeros_like(bsync)
            vol_ratio = torch.ones_like(bsync)
            bb_sq = torch.zeros_like(bsync)

        d_sign = torch.where(dir_s >= 0.0, 1.0, -1.0)

        # 25 Strictly Sign-Invariant (Even) Regime Quality & Alignment Modulators
        G_even = torch.stack([
            torch.abs(dir_s),
            torch.abs(z1), -torch.abs(z1),
            torch.abs(z3), -torch.abs(z3),
            torch.abs(w1), -torch.abs(w1),
            torch.abs(ch_pos20), -torch.abs(ch_pos20),
            torch.abs(ta_score), -torch.abs(ta_score),
            bsync, -bsync,
            pe, -pe,
            adx, -adx,
            vol_ratio, -vol_ratio,
            1.0 - bb_sq, bb_sq,
            torch.relu(d_sign * dxy5),
            torch.relu(d_sign * zm1),
            torch.relu(d_sign * w1),
            torch.relu(-d_sign * z1)
        ], dim=-1)

        return G_even

    def _compute_orthogonal_alpha(
        self,
        x_micro: torch.Tensor,
        x_mtf: torch.Tensor,
        x_macro: torch.Tensor
    ):
        """
        Computes the strictly sign-symmetric (odd-function) 30-Cell (6 Pairs x 5 Timeframes)
        SOTA Non-Linear Regime-Conditioned Microstructure Expert + Cell Sniper Gate score.
        """
        F_odd, base_tf_score, tf_sec, asset_id = self._extract_odd_features(x_micro, x_mtf, x_macro)

        aid_idx = torch.clamp(asset_id.long(), 0, 5)
        tf_idx = torch.where(
            tf_sec <= 5.0, 0,
            torch.where(
                tf_sec <= 10.0, 1,
                torch.where(
                    tf_sec <= 15.0, 2,
                    torch.where(tf_sec <= 30.0, 3, 4)
                )
            )
        )

        # Cell Directional Expert: F_odd normalized by training std, projected by cell_expert_weights
        F_odd_n = F_odd / self.cell_odd_std.to(F_odd.device).clamp(min=1e-4)
        w_cell = self.cell_expert_weights[aid_idx, tf_idx]  # [B, 48]
        cd = torch.tanh((F_odd_n * w_cell).sum(dim=-1) / 0.35) * 0.35

        alpha_blend = self.cell_hyper[aid_idx, tf_idx, 0]  # [B]
        g_scale = self.cell_hyper[aid_idx, tf_idx, 1]      # [B]
        tau = self.cell_hyper[aid_idx, tf_idx, 2].clamp(min=0.10)  # [B]
        cell_p85 = self.cell_hyper[aid_idx, tf_idx, 3].clamp(min=0.05)  # [B]

        dir_s = (1.0 - alpha_blend) * base_tf_score + alpha_blend * cd

        # Cell Sniper Gate: G_even normalized by training std, projected by cell_gate_weights
        G_even = self._extract_even_gates(x_micro, x_mtf, x_macro, dir_s)
        G_even_n = G_even / self.cell_even_std.to(G_even.device).clamp(min=1e-4)
        gw_cell = self.cell_gate_weights[aid_idx, tf_idx]  # [B, 25]
        gate_mult = torch.exp(torch.clamp((G_even_n * gw_cell).sum(dim=-1), -1.5, 1.5) * g_scale)

        # Goldilocks sweet spot saturation + Sniper Gate modulation
        sat_s = dir_s / (1.0 + (dir_s / tau) ** 4)
        final_s = sat_s * gate_mult

        # Calibrated uniform probability scale across all 30 cells (clamped against cubic explosion)
        s_norm = torch.clamp(final_s / cell_p85, -4.0, 4.0)
        alpha_dir = torch.clamp(0.20 * s_norm + 0.14 * (s_norm ** 3), -3.0, 3.0)
        sniper_gate = gate_mult / (1.0 + (dir_s / tau) ** 4)

        return alpha_dir, sniper_gate

    def compute_sniper_gate(self, x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor) -> torch.Tensor:
        _, sniper_gate = self._compute_orthogonal_alpha(x_micro, x_mtf, x_macro)
        return sniper_gate

    def calibrate_cell_experts(self, x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor, y: torch.Tensor, s1: int, s2: int):
        """
        Calibrates the 30-cell (6 Pairs x 5 Timeframes) specialized microstructure weights,
        even Sniper Gates, and Goldilocks hyperparameters using split-invariant correlations.
        """
        with torch.no_grad():
            F_odd, base_tf_score, tf_sec, asset_id = self._extract_odd_features(x_micro, x_mtf, x_macro)
            n_total = len(y)

            # Compute and store training feature standard deviations
            odd_std = F_odd[:s1].std(dim=0, keepdim=True).clamp(min=1e-4)
            self.cell_odd_std.data.copy_(odd_std)
            F_odd_n = F_odd / odd_std

            y_signed = torch.where(y == 1, 1.0, torch.where(y == 2, -1.0, 0.0))
            aids = asset_id.long()
            tfs = tf_sec.long()
            tf_list = [5, 10, 15, 30, 60]

            W_cell = torch.zeros(6, 5, 48, dtype=torch.float32, device=self.cell_expert_weights.device)
            GW_cell = torch.zeros(6, 5, 25, dtype=torch.float32, device=self.cell_gate_weights.device)
            Hyper_cell = self.cell_hyper.clone()

            # Global even gates tensor with dummy dir_s for standard deviation calculation
            G_dummy = self._extract_even_gates(x_micro, x_mtf, x_macro, base_tf_score)
            even_std = G_dummy[:s1].std(dim=0, keepdim=True).clamp(min=1e-4)
            self.cell_even_std.data.copy_(even_std)

            # Compute global prior vectors across all pairs for each timeframe to prevent cell overfitting
            W_global = torch.zeros(5, 48, dtype=torch.float32, device=self.cell_expert_weights.device)
            for ti, tsec in enumerate(tf_list):
                mk1_g = (tfs[:s1] == tsec) & (y[:s1] > 0)
                mk2_g = (tfs[s1:s2] == tsec) & (y[s1:s2] > 0)
                mk3_g = (tfs[s2:] == tsec) & (y[s2:] > 0)
                if mk1_g.sum() > 15 and mk2_g.sum() > 10:
                    cg1 = (F_odd_n[:s1][mk1_g] * y_signed[:s1][mk1_g, None]).mean(dim=0)
                    cg2 = (F_odd_n[s1:s2][mk2_g] * y_signed[s1:s2][mk2_g, None]).mean(dim=0)
                    cg3 = (F_odd_n[s2:][mk3_g] * y_signed[s2:][mk3_g, None]).mean(dim=0) if mk3_g.sum() > 5 else cg2
                    inv_g = (cg1 * cg2 > 0) & (cg2 * cg3 > 0)
                    wg = torch.where(inv_g, (cg1 + cg2 + cg3) / 3.0, torch.zeros_like(cg1))
                    if torch.norm(wg) > 1e-6:
                        wg = wg / torch.norm(wg) * 0.35
                    W_global[ti] = wg

            for aid in range(6):
                for ti, tsec in enumerate(tf_list):
                    cell_mask = (aids == aid) & (tfs == tsec)
                    mk1 = (aids[:s1] == aid) & (tfs[:s1] == tsec) & (y[:s1] > 0)
                    mk2 = (aids[s1:s2] == aid) & (tfs[s1:s2] == tsec) & (y[s1:s2] > 0)
                    mk3 = (aids[s2:] == aid) & (tfs[s2:] == tsec) & (y[s2:] > 0)

                    n_train_cell = int(mk1.sum().item() + mk2.sum().item())
                    if mk1.sum() > 8 and mk2.sum() > 4:
                        c1 = (F_odd_n[:s1][mk1] * y_signed[:s1][mk1, None]).mean(dim=0)
                        c2 = (F_odd_n[s1:s2][mk2] * y_signed[s1:s2][mk2, None]).mean(dim=0)
                        c3 = (F_odd_n[s2:][mk3] * y_signed[s2:][mk3, None]).mean(dim=0) if mk3.sum() > 3 else c2
                        inv = (c1 * c2 > 0) & (c2 * c3 > 0)
                        w_local = torch.where(inv, (c1 + c2 + c3) / 3.0, torch.zeros_like(c1))

                        # Empirical Bayes / James-Stein shrinkage towards global prior across all pairs
                        shrink = n_train_cell / (n_train_cell + 30.0)
                        w = shrink * w_local + (1.0 - shrink) * W_global[ti]
                        if torch.norm(w) > 1e-6:
                            w = w / torch.norm(w) * 0.35
                        W_cell[aid, ti] = w.to(self.cell_expert_weights.device)

                        cd = torch.tanh((F_odd_n @ w) / 0.35) * 0.35
                        best_wr = 0.0
                        best_gw = torch.zeros(25, dtype=torch.float32)
                        best_hyper = [0.50, 0.35, 0.35, 0.25]
                        best_final_s_params = None

                        # Evaluate solely on validation fold [s2:] to prevent over-optimizing train
                        mk_ev = (aids[s2:] == aid) & (tfs[s2:] == tsec) & (y[s2:] > 0)
                        if mk_ev.sum() < 5:
                            mk_ev = (aids[s1:] == aid) & (tfs[s1:] == tsec) & (y[s1:] > 0)
                        yc = (y[s2:][mk_ev[:len(y[s2:])]] == 1).numpy() if len(mk_ev) == len(y[s2:]) else (y[s1:][mk_ev] == 1).numpy()
                        n_ev = int(mk_ev.sum().item())

                        for alpha in [0.0, 0.16, 0.50, 0.85, 1.0]:
                            dir_s = (1.0 - alpha) * base_tf_score + alpha * cd
                            d_sign = torch.where(dir_s >= 0.0, 1.0, -1.0)
                            G_cell = self._extract_even_gates(x_micro, x_mtf, x_macro, dir_s)
                            G_cell_n = G_cell / even_std

                            hit = ((d_sign * y_signed) > 0).float() - 0.5
                            gc1 = (G_cell_n[:s1][mk1] * hit[:s1][mk1, None]).mean(dim=0)
                            gc2 = (G_cell_n[s1:s2][mk2] * hit[s1:s2][mk2, None]).mean(dim=0)
                            gc3 = (G_cell_n[s2:][mk3] * hit[s2:][mk3, None]).mean(dim=0) if mk3.sum() > 3 else gc2
                            ginv = (gc1 * gc2 > 0) & (gc2 * gc3 > 0)
                            gw = torch.where(ginv, (gc1 + gc2 + gc3) / 3.0, torch.zeros_like(gc1))
                            if torch.norm(gw) > 1e-6:
                                gw = gw / torch.norm(gw)

                            G_cell_n_ev = G_cell_n[s2:][mk_ev] if len(G_cell_n[s2:]) == len(mk_ev) else G_cell_n[s1:][mk_ev]
                            dir_s_ev = dir_s[s2:][mk_ev] if len(dir_s[s2:]) == len(mk_ev) else dir_s[s1:][mk_ev]

                            for g_scale in [0.0, 0.35, 0.70]:
                                gate_mult_ev = torch.exp(torch.clamp(G_cell_n_ev @ gw, -1.5, 1.5) * g_scale)
                                for tau in [0.18, 0.25, 0.28, 0.35, 0.50, 2.0]:
                                    sat_s_ev = dir_s_ev / (1.0 + (dir_s_ev / tau) ** 4)
                                    final_s_ev = sat_s_ev * gate_mult_ev
                                    sc = final_s_ev.numpy()
                                    if n_ev > 5:
                                        thr = np.percentile(np.abs(sc), 85)
                                        sel = np.abs(sc) >= thr
                                        if np.any(sel):
                                            wr = float(np.mean((sc[sel] > 0) == yc[sel]) * 100)
                                            if wr > best_wr:
                                                best_wr = wr
                                                best_gw = gw
                                                best_final_s_params = (alpha, g_scale, tau)
                                                best_hyper = [alpha, g_scale, tau, 0.25]

                        if best_final_s_params is not None:
                            b_alpha, b_gscale, b_tau = best_final_s_params
                            b_dir_s = (1.0 - b_alpha) * base_tf_score + b_alpha * cd
                            b_G = self._extract_even_gates(x_micro, x_mtf, x_macro, b_dir_s) / even_std
                            b_gmult = torch.exp(torch.clamp(b_G @ best_gw, -1.5, 1.5) * b_gscale)
                            b_final_s = (b_dir_s / (1.0 + (b_dir_s / b_tau) ** 4)) * b_gmult
                            val_cell_abs = np.abs(b_final_s[:s2][cell_mask[:s2]].numpy())
                            p85 = float(np.percentile(val_cell_abs, 85)) if len(val_cell_abs) > 10 else 0.25
                            best_hyper[3] = max(p85, 0.05)

                        GW_cell[aid, ti] = best_gw.to(self.cell_gate_weights.device)
                        Hyper_cell[aid, ti] = torch.tensor(best_hyper, dtype=torch.float32, device=self.cell_hyper.device)
                    else:
                        # Fallback to global prior for sparse cells
                        W_cell[aid, ti] = W_global[ti]

            self.cell_expert_weights.data.copy_(W_cell)
            self.cell_gate_weights.data.copy_(GW_cell)
            self.cell_hyper.data.copy_(Hyper_cell)

    def _forward_single(self, x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor, asset_ids: torch.Tensor):
        x_micro_geom = self._transform_candles_atr_geometry(x_micro)
        x_mtf_geom = self._transform_candles_atr_geometry(x_mtf)

        p_micro = self.micro_patcher(x_micro_geom)
        p_micro = p_micro + self.micro_pos[:, :p_micro.size(1), :]
        h_micro = self.micro_transformer(p_micro)

        p_mtf = self.mtf_patcher(x_mtf_geom)
        p_mtf = p_mtf + self.mtf_pos[:, :p_mtf.size(1), :]
        h_mtf = self.mtf_transformer(p_mtf)

        attn_out, _ = self.cross_attn(query=h_micro, key=h_mtf, value=h_mtf)
        h_cross = self.cross_norm(h_micro + attn_out)

        pool_micro = 0.5 * h_micro.mean(dim=1) + 0.5 * h_micro[:, -1, :]
        pool_mtf = h_mtf.mean(dim=1)
        pool_cross = 0.5 * h_cross.mean(dim=1) + 0.5 * h_cross[:, -1, :]

        # High-resolution exact Last-5 Micro-Candle branch conditioned on Timeframe (s5, s10, s15, s30, m1)
        last5_flat = x_micro_geom[:, -5:, :].reshape(x_micro_geom.size(0), -1)
        tf_sec = torch.round(x_macro[:, 17] * 60.0)
        tf_onehot = torch.stack([
            (tf_sec <= 5.0).float(),
            ((tf_sec > 5.0) & (tf_sec <= 10.0)).float(),
            ((tf_sec > 10.0) & (tf_sec <= 15.0)).float(),
            ((tf_sec > 15.0) & (tf_sec <= 30.0)).float(),
            (tf_sec > 30.0).float()
        ], dim=-1)  # [B, 5]
        pool_last5 = self.last5_encoder(torch.cat([last5_flat, tf_onehot], dim=-1))

        # Vector Asset Embedding + Macro Context
        asset_emb = self.asset_embedding(asset_ids)  # [B, 16]
        pool_macro = self.macro_encoder(x_macro)     # [B, 64]

        # Cross-Modal Sigmoid Gate (Gated Multimodal Unit between Candles and Macro+Asset)
        h_candles = self.candle_proj(torch.cat([pool_micro, pool_mtf, pool_cross, pool_last5], dim=-1))  # [B, 64]
        h_macro = self.macro_proj(torch.cat([pool_macro, asset_emb], dim=-1))                            # [B, 64]
        gate = torch.sigmoid(self.sigmoid_gate(torch.cat([h_candles, h_macro], dim=-1)))                 # [B, 64]
        gated_fusion = torch.cat([gate * h_candles, (1.0 - gate) * h_macro], dim=-1)                     # [B, 128]

        latent = self.classifier(gated_fusion)  # [B, 64]

        # Multi-Head Timeframe Output Routing (5 dedicated heads: s5, s10, s15, s30, m1)
        head_outputs = torch.stack([head(latent) for head in self.tf_heads], dim=1)  # [B, 5, 3]
        logits = (head_outputs * tf_onehot.unsqueeze(-1)).sum(dim=1)                 # [B, 3]

        aux_pnl = self.pnl_regressor(latent).squeeze(-1)
        return logits, aux_pnl

    def forward(
        self,
        x_micro: torch.Tensor,
        x_mtf: torch.Tensor,
        x_macro: torch.Tensor
    ):
        """
        x_micro: [B, 160, 8]
        x_mtf:   [B, 30, 8]
        x_macro: [B, 31]
        """
        asset_ids = torch.clamp(torch.round(x_macro[:, 22]).long(), 0, 9) if x_macro.size(1) > 22 else torch.zeros(x_macro.size(0), dtype=torch.long, device=x_macro.device)
        xm_pos, xm_neg = self._sanitize_and_flip_macro(x_macro)

        # 1. Siamese Antisymmetric Deep PatchTST Stream (with Asset Embedding + Sigmoid Gate + Multi-Head TF Readout)
        logits_pos, pnl_pos = self._forward_single(x_micro, x_mtf, xm_pos, asset_ids)
        logits_neg, pnl_neg = self._forward_single(self._flip_candles(x_micro), self._flip_candles(x_mtf), xm_neg, asset_ids)

        deep_dir_raw = 0.5 * ((logits_pos[:, 1] - logits_pos[:, 2]) - (logits_neg[:, 1] - logits_neg[:, 2]))
        deep_tanh = torch.tanh(deep_dir_raw)
        # Full 0.25 gradient flow into deep PatchTST/Sigmoid Gate/Multi-Head during training via STE,
        # with calibrated 0.015 residual influence during forward evaluation
        deep_dir = 0.015 * deep_tanh + (0.25 * deep_tanh - 0.25 * deep_tanh.detach())

        # 2. Orthogonal 48-Feature + 30-Cell (Pair x Timeframe) Non-Linear Regime-Conditioned Alpha Stream
        alpha_dir, _ = self._compute_orthogonal_alpha(x_micro, x_mtf, x_macro)

        total_dir = alpha_dir + deep_dir
        hold_logit = 0.5 * (logits_pos[:, 0] + logits_neg[:, 0]) - 2.5
        buy_logit = 0.5 * total_dir
        put_logit = -0.5 * total_dir

        logits = torch.stack([hold_logit, buy_logit, put_logit], dim=-1)
        aux_pnl = 0.5 * (pnl_pos - pnl_neg)

        return logits, aux_pnl

    def predict_probabilities(self, x_micro: torch.Tensor, x_mtf: torch.Tensor, x_macro: torch.Tensor):
        self.eval()
        with torch.no_grad():
            logits, _ = self.forward(x_micro, x_mtf, x_macro)
            probs = F.softmax(logits, dim=-1)
        return probs
