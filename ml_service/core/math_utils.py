import numpy as np

def _logit(p: float, eps: float = 1e-6) -> float:
    p = min(max(p, eps), 1.0 - eps)
    return float(np.log(p / (1.0 - p)))

def _sigmoid(x: float) -> float:
    return float(1.0 / (1.0 + np.exp(-x)))

def bayesian_fusion(prob_a: float, prob_b: float, weight_a: float, weight_b: float) -> float:
    """
    Fuse two probability estimates via logarithmic opinion pooling (Bayes-consistent).
    
    weight_a, weight_b should ideally be normalized (sum to 1) reliability weights
    (e.g. derived from historical accuracy or sample count). If they don't sum to 1,
    they are normalized internally to preserve calibration.
    
    Returns: fused probability P(y=1) in (0, 1).
    """
    total_w = weight_a + weight_b
    if total_w < 1e-9:
        return 0.5
    w_a = weight_a / total_w
    w_b = weight_b / total_w

    combined_logit = w_a * _logit(prob_a) + w_b * _logit(prob_b)
    return _sigmoid(combined_logit)
