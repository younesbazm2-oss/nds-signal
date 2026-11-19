"""پروتکل ارتباطی اندیکاتور <-> موتور (JSON Line روی TCP لوکال)."""
from __future__ import annotations

import json
from typing import Any, Dict

ALLOWED_TYPES = {"state", "pivot", "decision", "error"}


class ProtocolError(ValueError):
    """پیام نامعتبر از نظر پروتکل."""


def parse_message(raw: str) -> Dict[str, Any]:
    """یک خط JSON را اعتبارسنجی و برمی‌گرداند؛ در صورت نامعتبری ProtocolError می‌دهد."""
    try:
        msg = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ProtocolError(f"invalid json: {exc}") from exc
    if not isinstance(msg, dict):
        raise ProtocolError("message must be a json object")
    mtype = msg.get("type")
    if mtype not in ALLOWED_TYPES:
        raise ProtocolError(f"unknown message type: {mtype!r}")
    return msg
