import unittest

from engine.gate import FuzzyGate

CFG = {
    "thresholds": {"execute": 0.85, "warn": 0.60},
    "fuzzy": {"state_bias": {"rally_down": 0.30}, "tf_weights": {"30": 1.0}},
}


class TestGate(unittest.TestCase):
    def test_short_warn(self):
        decision = FuzzyGate(CFG).decide({"type": "state", "tf": {"30": {"nds_state": "rally_down"}}})
        self.assertEqual(decision["action"], "SHORT")
        self.assertEqual(decision["gate"], "warn")
        self.assertAlmostEqual(decision["confidence"], 0.70, places=3)

    def test_long_execute(self):
        decision = FuzzyGate(CFG).decide({"type": "state", "tf": {"30": {"bias": 0.9}}})
        self.assertEqual(decision["action"], "LONG")
        self.assertEqual(decision["gate"], "execute")

    def test_neutral_log(self):
        decision = FuzzyGate(CFG).decide({"type": "state", "tf": {"30": {"bias": 0.5}}})
        self.assertEqual(decision["action"], "NONE")
        self.assertEqual(decision["gate"], "log")

    def test_no_tf_data(self):
        decision = FuzzyGate(CFG).decide({"type": "state"})
        self.assertEqual(decision["gate"], "log")
        self.assertIn("no tf data", decision["reasons"])

    def test_weighting(self):
        # میانگین وزنی: (0.8*3 + 0.2*1) / 4 = 0.65 → LONG با اطمینان 0.65 (هشدار)
        cfg = {
            "thresholds": {"execute": 0.85, "warn": 0.60},
            "fuzzy": {"tf_weights": {"240": 3.0, "30": 1.0}},
        }
        decision = FuzzyGate(cfg).decide({"type": "state", "tf": {"240": {"bias": 0.8}, "30": {"bias": 0.2}}})
        self.assertEqual(decision["action"], "LONG")
        self.assertAlmostEqual(decision["confidence"], 0.65, places=3)


if __name__ == "__main__":
    unittest.main()
