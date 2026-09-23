from pydantic import BaseModel, Field
from typing import Optional, List, Dict

class PredictRequest(BaseModel):
    candles: List[Dict]
    mtf_candles: Optional[List[Dict]] = None
    use_tactician: bool = True
    use_shadow: bool = False

class PredictResult(BaseModel):
    direction: str
    probability: float
    version: str
    variance: Optional[float] = None
    shadow_probability: Optional[float] = None

class FeedbackOutcome(BaseModel):
    was_win: bool
    direction: str = Field(..., description="Trade direction, e.g., 'BUY' or 'PUT'")
    prob_lgbm: Optional[float] = None
    entry_price: float
    exit_price: float
