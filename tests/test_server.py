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
    "fuzzy": {"state_bias": {"rally_down": 0.30}, "tf_weights": {"30": 1.0}},
}


class TestServerRoundtrip(unittest.TestCase):
    def test_roundtrip(self):
        async def run():
            tmp = tempfile.NamedTemporaryFile(suffix=".csv", delete=False)
            tmp.close()
            journal = JournalWriter(tmp.name)
            server = EngineServer("127.0.0.1", 0, FuzzyV1Model(FuzzyGate(CFG)), journal)
            srv = await asyncio.start_server(server._handle, "127.0.0.1", 0)
            port = srv.sockets[0].getsockname()[1]

            reader, writer = await asyncio.open_connection("127.0.0.1", port)
            payload = json.dumps({"type": "state", "tf": {"30": {"nds_state": "rally_down"}}})
            writer.write(payload.encode("utf-8") + b"\n")
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

        decision, rows = asyncio.run(run())
        self.assertEqual(decision["type"], "decision")
        self.assertEqual(decision["action"], "SHORT")
        self.assertTrue(len(rows) >= 3)  # header + state + decision


if __name__ == "__main__":
    unittest.main()
