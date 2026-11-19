"""گیت تصمیم فازی v1 — اسکلت قابل توسعه (کالیبراسیون نهایی در فاز 5)."""
from __future__ import annotations

from typing import Any, Dict

DEFAULT_STATE_BIAS: Dict[str, float] = {
    "rally_up": 0.70,    # رالی صعودی
    "flag_pos": 0.60,    # فلگ مثبت
    "flag_neg": 0.40,    # فلگ منفی
    "rally_down": 0.30,  # رالی نزولی
    "unknown": 0.50,
}


class FuzzyGate:
    """ورودی: state (مالتی‌تایم‌فریم) — خروجی: تصمیم تایپ‌شده + درصد اطمینان.

    bias هر تایم‌فریم عددی در [0,1] به سمت LONG است؛ یا مستقیم در پیام می‌آید
    یا از وضعیت NDS (rally/flag) با نگاشت فازی config خوانده می‌شود.
    """

    def __init__(self, config: Dict[str, Any]):
        thresholds = config.get("thresholds", {})
        self.execute_threshold = float(thresholds.get("execute", 0.85))
        self.warn_threshold = float(thresholds.get("warn", 0.60))
        fuzzy = config.get("fuzzy", {})
        self.state_bias = {**DEFAULT_STATE_BIAS, **(fuzzy.get("state_bias") or {})}
        self.tf_weights = fuzzy.get("tf_weights") or {}

    def decide(self, state: Dict[str, Any]) -> Dict[str, Any]:
        tf = state.get("tf") or {}
        scores, reasons = [], []
        for tf_name, tf_state in tf.items():
            if not isinstance(tf_state, dict):
                continue
            bias = tf_state.get("bias")
            if bias is None:
                key = str(tf_state.get("nds_state", "unknown")).lower()
                bias = self.state_bias.get(key, DEFAULT_STATE_BIAS["unknown"])
            bias = max(0.0, min(1.0, float(bias)))
            weight = float(self.tf_weights.get(str(tf_name), 1.0))
            scores.append((bias, weight))
            reasons.append(f"tf{tf_name}={bias:.2f}")

        if scores:
            weight_sum = sum(w for _, w in scores) or 1.0
            net = sum(b * w for b, w in scores) / weight_sum
        else:
            net = 0.5
            reasons.append("no tf data")

        action = "LONG" if net > 0.5 else "SHORT"
        confidence = net if net > 0.5 else 1.0 - net

        if confidence >= self.execute_threshold:
            gate = "execute"
        elif confidence >= self.warn_threshold:
            gate = "warn"
        else:
            gate, action = "log", "NONE"

        return {
            "type": "decision",
            "action": action,
            "confidence": round(confidence, 4),
            "gate": gate,
            "reasons": reasons,
            "engine": "fuzzy-v1",
        }
