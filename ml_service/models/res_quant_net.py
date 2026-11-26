"""
Antisymmetric Gated Residual MLP (`AntiSymResMLPNet`) for Subminute FX Quant
===========================================================================
Training-side PyTorch module paired with pure-NumPy inference inside
`ml_service/models/subminute_quant_predictor.py` (`eval_resnn_prob_*`).

Architecture properties:
- Robust median/IQR-clipped standardization + explicit missingness indicator (`2*D` inputs).
- Input Linear -> LayerNorm -> SiLU -> 2x Pre-Norm SwiGLU Residual Blocks.
- Exact mathematical mirror antisymmetry: `f(x, mirror(x)) = 0.5 * (h(x) - h(mirror(x)))`
  so `P(BUY | mirror(x)) == 1 - P(BUY | x)` holds strictly regardless of optimization state.
- Dual-task objective: Binary directional BCEWithLogits + 0.25 * Huber(signed return / ATR20).
"""

from __future__ import annotations

import copy
from typing import Any, Dict, Optional, Sequence, Tuple

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F


class GatedResBlock(nn.Module):
    """Pre-Norm SwiGLU Residual Block."""

    def __init__(self, dim: int, dropout: float = 0.25):
        super().__init__()
        self.norm = nn.LayerNorm(dim)
        self.gate_proj = nn.Linear(dim, dim * 2)
        self.drop = nn.Dropout(dropout)
        self.out_proj = nn.Linear(dim, dim)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        h = self.norm(x)
        a, b = self.gate_proj(h).chunk(2, dim=-1)
        h = a * torch.sigmoid(b)
        h = self.drop(h)
        return x + self.out_proj(h)


class AntiSymResMLPNet(nn.Module):
    """Compact antisymmetric SwiGLU Residual MLP with missingness-mask awareness."""

    def __init__(self, n_features: int, hidden: int = 128, dropout: float = 0.25):
        super().__init__()
        in_dim = n_features * 2
        self.in_proj = nn.Linear(in_dim, hidden)
        self.ln0 = nn.LayerNorm(hidden)
        self.block1 = GatedResBlock(hidden, dropout=dropout)
        self.block2 = GatedResBlock(hidden, dropout=dropout)
        self.dir_head = nn.Linear(hidden, 1)
        self.ret_head = nn.Linear(hidden, 1)

    def _encode(self, x: torch.Tensor) -> torch.Tensor:
        h = F.silu(self.ln0(self.in_proj(x)))
        h = self.block1(h)
        h = self.block2(h)
        return h

    def forward_single(self, x: torch.Tensor) -> Tuple[torch.Tensor, torch.Tensor]:
        h = self._encode(x)
        return self.dir_head(h).squeeze(-1), self.ret_head(h).squeeze(-1)

    def forward(self, xp: torch.Tensor, xn: torch.Tensor) -> Tuple[torch.Tensor, torch.Tensor]:
        dp, rp = self.forward_single(xp)
        dn, rn = self.forward_single(xn)
        return 0.5 * (dp - dn), 0.5 * (rp - rn)


def _fit_norm_stats(X_tr: np.ndarray, X_tr_mir: np.ndarray) -> Tuple[np.ndarray, np.ndarray]:
    """Computes symmetric robust medians & scales across original + mirrored training samples."""
    d = X_tr.shape[1]
    meds = np.zeros(d, dtype=np.float64)
    scales = np.ones(d, dtype=np.float64)
    both = np.vstack([X_tr, X_tr_mir])
    for j in range(d):
        col = both[:, j]
        fin = col[np.isfinite(col)]
        if len(fin) >= 8:
            m = float(np.median(fin))
            q10, q90 = np.percentile(fin, [10.0, 90.0])
            clipped = np.clip(fin, q10, q90)
            sd = float(np.std(clipped))
            meds[j] = m
            scales[j] = max(sd, 0.10)
    return meds, scales


def _prep_pair_inputs(
    X: np.ndarray,
    X_mir: np.ndarray,
    meds: np.ndarray,
    scales: np.ndarray,
) -> Tuple[np.ndarray, np.ndarray]:
    """Transforms (X, mirror(X)) into float32 (N, 2*D) standardized + finite-mask inputs."""
    inv_s = 1.0 / np.maximum(scales, 1e-6)
    fin_p = np.isfinite(X)
    xp_imp = np.where(fin_p, X, meds)
    xp_norm = np.clip((xp_imp - meds) * inv_s, -6.0, 6.0)
    xp_in = np.concatenate([xp_norm, fin_p.astype(np.float64)], axis=1).astype(np.float32)

    fin_n = np.isfinite(X_mir)
    xn_imp = np.where(fin_n, X_mir, meds)
    xn_norm = np.clip((xn_imp - meds) * inv_s, -6.0, 6.0)
    xn_in = np.concatenate([xn_norm, fin_n.astype(np.float64)], axis=1).astype(np.float32)
    return xp_in, xn_in


def train_res_quant_model(
    X_fit: np.ndarray,
    X_fit_mir: np.ndarray,
    y_fit: np.ndarray,
    ret_atr_fit: np.ndarray,
    X_es: Optional[np.ndarray] = None,
    X_es_mir: Optional[np.ndarray] = None,
    y_es: Optional[np.ndarray] = None,
    max_epochs: int = 24,
    fixed_epochs: Optional[int] = None,
    hidden: int = 128,
    dropout: float = 0.25,
    lr: float = 8e-4,
    weight_decay: float = 1e-2,
    patience: int = 5,
    seed: int = 42,
) -> Tuple[AntiSymResMLPNet, np.ndarray, np.ndarray, int]:
    """
    Trains `AntiSymResMLPNet` on CPU cleanly with optional internal chronological early-stopping.
    Returns `(model, meds, scales, best_epoch)`.
    """
    torch.manual_seed(seed)
    np.random.seed(seed)

    meds, scales = _fit_norm_stats(X_fit, X_fit_mir)
    xp_tr, xn_tr = _prep_pair_inputs(X_fit, X_fit_mir, meds, scales)
    n_tr, n_feat = X_fit.shape

    use_es = (fixed_epochs is None) and (X_es is not None) and (X_es_mir is not None) and (y_es is not None) and len(X_es) >= 200
    if use_es:
        xp_es, xn_es = _prep_pair_inputs(X_es, X_es_mir, meds, scales)
        es_xp_t = torch.from_numpy(xp_es)
        es_xn_t = torch.from_numpy(xn_es)
        y_es_np = y_es.astype(np.float64)

    n_epochs = int(fixed_epochs) if fixed_epochs is not None else int(max_epochs)
    batch_sz = 2048 if n_tr >= 200_000 else (1024 if n_tr >= 50_000 else 512)

    model = AntiSymResMLPNet(n_features=n_feat, hidden=hidden, dropout=dropout)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=weight_decay)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=max(n_epochs, 1), eta_min=1e-5)

    xp_t = torch.from_numpy(xp_tr)
    xn_t = torch.from_numpy(xn_tr)
    y_t = torch.from_numpy(y_fit.astype(np.float32))
    r_t = torch.from_numpy(np.clip(ret_atr_fit, -4.0, 4.0).astype(np.float32))

    rng = np.random.default_rng(seed)
    best_loss = 999.0
    best_ep = max(1, n_epochs)
    best_state: Optional[Dict[str, torch.Tensor]] = None
    no_imp = 0

    for ep in range(1, n_epochs + 1):
        model.train()
        perm = rng.permutation(n_tr)
        for s in range(0, n_tr, batch_sz):
            b = torch.from_numpy(perm[s : s + batch_sz])
            opt.zero_grad(set_to_none=True)
            d_log, r_pred = model(xp_t[b], xn_t[b])
            loss_bce = F.binary_cross_entropy_with_logits(d_log, y_t[b])
            loss_ret = F.smooth_l1_loss(r_pred, r_t[b], beta=1.0)
            loss = loss_bce + 0.25 * loss_ret
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 2.0)
            opt.step()
        sched.step()

        if use_es:
            model.eval()
            with torch.no_grad():
                p_list = []
                for s in range(0, len(es_xp_t), 4096):
                    dl, _ = model(es_xp_t[s : s + 4096], es_xn_t[s : s + 4096])
                    p_list.append(torch.sigmoid(dl).numpy())
                p_es = np.clip(np.concatenate(p_list).astype(np.float64), 1e-6, 1.0 - 1e-6)
                va_loss = float(-np.mean(y_es_np * np.log(p_es) + (1.0 - y_es_np) * np.log(1.0 - p_es)))
            if va_loss < best_loss - 1e-5:
                best_loss = va_loss
                best_ep = ep
                best_state = copy.deepcopy(model.state_dict())
                no_imp = 0
            else:
                no_imp += 1
                if no_imp >= patience:
                    break

    if best_state is not None:
        model.load_state_dict(best_state)
    model.eval()
    return model, meds, scales, best_ep


def predict_res_quant(
    model: AntiSymResMLPNet,
    meds: np.ndarray,
    scales: np.ndarray,
    X: np.ndarray,
    X_mir: np.ndarray,
) -> np.ndarray:
    """Evaluates PyTorch `AntiSymResMLPNet` probabilities in batches."""
    model.eval()
    xp, xn = _prep_pair_inputs(X, X_mir, meds, scales)
    xp_t = torch.from_numpy(xp)
    xn_t = torch.from_numpy(xn)
    out = []
    with torch.no_grad():
        for s in range(0, len(xp_t), 4096):
            dl, _ = model(xp_t[s : s + 4096], xn_t[s : s + 4096])
            out.append(torch.sigmoid(dl).numpy())
    return np.concatenate(out).astype(np.float64)


def pack_resnn_for_npz(
    model: AntiSymResMLPNet,
    meds: np.ndarray,
    scales: np.ndarray,
    prefix: str,
) -> Dict[str, np.ndarray]:
    """
    Exports `AntiSymResMLPNet` weights into flat float64 `.npz` arrays evaluated identically
    in pure NumPy by `eval_resnn_prob_single` / `eval_resnn_prob_batch` with zero PyTorch runtime dependency.
    """
    sd = {k: v.detach().cpu().numpy().astype(np.float64) for k, v in model.state_dict().items()}
    return {
        f"{prefix}_meds": np.asarray(meds, dtype=np.float64),
        f"{prefix}_scale": np.asarray(scales, dtype=np.float64),
        f"{prefix}_in_w": sd["in_proj.weight"],
        f"{prefix}_in_b": sd["in_proj.bias"],
        f"{prefix}_ln0_w": sd["ln0.weight"],
        f"{prefix}_ln0_b": sd["ln0.bias"],
        f"{prefix}_n1_w": sd["block1.norm.weight"],
        f"{prefix}_n1_b": sd["block1.norm.bias"],
        f"{prefix}_g1_w": sd["block1.gate_proj.weight"],
        f"{prefix}_g1_b": sd["block1.gate_proj.bias"],
        f"{prefix}_o1_w": sd["block1.out_proj.weight"],
        f"{prefix}_o1_b": sd["block1.out_proj.bias"],
        f"{prefix}_n2_w": sd["block2.norm.weight"],
        f"{prefix}_n2_b": sd["block2.norm.bias"],
        f"{prefix}_g2_w": sd["block2.gate_proj.weight"],
        f"{prefix}_g2_b": sd["block2.gate_proj.bias"],
        f"{prefix}_o2_w": sd["block2.out_proj.weight"],
        f"{prefix}_o2_b": sd["block2.out_proj.bias"],
        f"{prefix}_dir_w": sd["dir_head.weight"].reshape(-1),
        f"{prefix}_dir_b": sd["dir_head.bias"].reshape(-1),
    }
