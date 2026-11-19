"""ژورنال — آرشیو کامل پیام‌های ورودی و تصمیم‌ها برای کالیبراسیون و ترین مدل."""
from __future__ import annotations

import csv
import json
import os
import threading
import time
from typing import Any, Dict

FIELDS = ["ts_utc", "recorded_at", "type", "instrument", "payload"]


class JournalWriter:
    def __init__(self, path: str):
        self.path = path
        self._lock = threading.Lock()
        parent = os.path.dirname(os.path.abspath(path))
        if parent:
            os.makedirs(parent, exist_ok=True)
        is_new = (not os.path.exists(path)) or os.path.getsize(path) == 0
        self._fh = open(path, "a", newline="", encoding="utf-8")
        self._writer = csv.writer(self._fh)
        if is_new:
            self._writer.writerow(FIELDS)
            self._fh.flush()

    def write(self, msg: Dict[str, Any]) -> None:
        row = [
            time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            f"{time.time():.3f}",
            str(msg.get("type", "")),
            str(msg.get("instrument", "")),
            json.dumps(msg, ensure_ascii=False),
        ]
        with self._lock:
            self._writer.writerow(row)
            self._fh.flush()

    def close(self) -> None:
        with self._lock:
            try:
                self._fh.close()
            except Exception:
                pass
