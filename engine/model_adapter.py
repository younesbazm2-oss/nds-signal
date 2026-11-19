"""انتخاب مدل تصمیم: فازی v1 (پیش‌فرض فاز 1) یا Laya/Kev (فاز 6)."""
from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any, Dict

from .gate import FuzzyGate


class DecisionModel(ABC):
    name = "base"

    @abstractmethod
    def decide(self, state: Dict[str, Any]) -> Dict[str, Any]:
        ...


class FuzzyV1Model(DecisionModel):
    name = "fuzzy-v1"

    def __init__(self, gate: FuzzyGate):
        self.gate = gate

    def decide(self, state: Dict[str, Any]) -> Dict[str, Any]:
        return self.gate.decide(state)


class LayaAdapter(DecisionModel):
    """فاز 6 — endpoint سازگار با فرمت TypeSafe (سؤال تایپ‌شده Choice/Score/Boolean).

    Laya به‌صورت سرور HTTP لوکال (یا API ابری) بالا می‌آید و فقط این adapter
    با آن صحبت می‌کند؛ نینجا و اندیکاتور هیچ ارتباطی با مدل ندارند.
    """

    name = "laya"

    def __init__(self, endpoint: str, timeout: float = 2.0):
        self.endpoint = endpoint
        self.timeout = timeout

    def decide(self, state: Dict[str, Any]) -> Dict[str, Any]:
        raise NotImplementedError(
            "اتصال Laya در فاز 6 فعال می‌شود؛ در حال حاضر گیت فازی v1 را انتخاب کن."
        )


def load_model(engine_cfg: Dict[str, Any], gate: FuzzyGate) -> DecisionModel:
    model_name = str(engine_cfg.get("model", "fuzzy-v1")).lower()
    if model_name in ("fuzzy-v1", "fuzzy"):
        return FuzzyV1Model(gate)
    if model_name in ("laya", "kev"):
        endpoint = engine_cfg.get("model_endpoint", "http://127.0.0.1:8080/v1/decide")
        return LayaAdapter(endpoint)
    raise ValueError(f"unknown model: {model_name}")
