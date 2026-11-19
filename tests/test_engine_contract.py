"""تست قرارداد: پیام واقعی اندیکاتور (shape کامل) ⇒ تصمیم درست موتور."""
import asyncio
import json
import os
import tempfile
import unittest

from engine.gate import FuzzyGate
from engine.journal import JournalWriter
from engine.model_adapter import FuzzyV1Model
from engine.server import EngineServer

CFG = {
    "thresholds": {"execute": 0.85, "warn": 0.60},
    "fuzzy": {
        "state_bias": {
            "rally_up": 0.70,
            "flag_pos": 0.60,
            "flag_neg": 0.40,
            "rally_down": 0.30,
            "unknown": 0.50,
        },
        "tf_weights": {"240": 1.0},
    },
}


def indicator_state(nds_state: str) -> dict:
    """دقیقا همان کلیدهایی که اندیکاتور C# می‌فرستد."""
    return {
        "type": "state",
        "instrument": "ES 12-26",
        "ts": "2026-10-23T15:42:11.250Z",
        "tf": {
            "240": {
                "nds_state": nds_state,
                "hook": "A",
                "points": {"1": 5800.0, "2": 5840.0, "3": 5812.0},
                "level86": 5805.6,
                "level86_broken": False,
                "close": 5832.25,
            }
        },
    }


def roundtrip(msg: dict):
    async def run():
        tmp = tempfile.NamedTemporaryFile(suffix=".csv", delete=False)
        tmp.close()
        journal = JournalWriter(tmp.name)
        server = EngineServer("127.0.0.1", 0, FuzzyV1Model(FuzzyGate(CFG)), journal)
        srv = await asyncio.start_server(server._handle, "127.0.0.1", 0)
        port = srv.sockets[0].getsockname()[1]

        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        writer.write((json.dumps(msg) + "\n").encode("utf-8"))
        await writer.drain()
        line = await asyncio.wait_for(reader.readline(), timeout=2)
        decision = json.loads(line)

        writer.close()
        srv.close()
        await srv.wait_closed()
        journal.close()

        with open(tmp.name, "r", encoding="utf-8") as fh:
            rows = fh.read().splitlines()
        os.unlink(tmp.name)
        return decision, rows

    return asyncio.run(run())


class TestEngineContract(unittest.TestCase):
    def test_rally_up_gives_long_warn(self):
        decision, rows = roundtrip(indicator_state("rally_up"))
        self.assertEqual(decision["type"], "decision")
        self.assertEqual(decision["action"], "LONG")
        self.assertEqual(decision["gate"], "warn")  # 0.70 < 0.85
        self.assertTrue(any("240" in row and "rally_up" in row for row in rows))

    def test_rally_down_gives_short_warn(self):
        decision, _ = roundtrip(indicator_state("rally_down"))
        self.assertEqual(decision["action"], "SHORT")
        self.assertEqual(decision["gate"], "warn")  # 0.70

    def test_flag_pos_gives_long_warn(self):
        decision, _ = roundtrip(indicator_state("flag_pos"))
        self.assertEqual(decision["action"], "LONG")
        self.assertEqual(decision["gate"], "warn")  # 0.60 مرز هشدار

    def test_unknown_gives_log(self):
        decision, _ = roundtrip(indicator_state("unknown"))
        self.assertEqual(decision["action"], "NONE")
        self.assertEqual(decision["gate"], "log")


if __name__ == "__main__":
    unittest.main()