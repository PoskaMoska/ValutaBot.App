from __future__ import annotations

from pydantic import BaseModel, Field

class PredictRequest(BaseModel):
    candles: list[dict]
    mtf_candles: list[dict] | None = None
    use_tactician: bool = True
    use_shadow: bool = False

class PredictResult(BaseModel):
    direction: str
    probability: float
    version: str
    variance: float | None = None
    shadow_probability: float | None = None

class FeedbackOutcome(BaseModel):
    was_win: bool
    direction: str = Field(..., description="Trade direction, e.g., 'BUY' or 'PUT'")
    prob_lgbm: float | None = None
    entry_price: float
    exit_price: float
