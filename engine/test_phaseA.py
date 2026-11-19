"""Phase A acceptance test — node detection vs the owner's hand labels (ES DEC26 daily).

Asserts the reference node list at the validated parameters:
  Strength = 5, SwingSource = HighLow, MinBarsBetweenNodes = 2, EqualTolerance = 2 ticks.

The reference input is the OLD 144-bar file (bars_ES_1440_old.csv) — the 18-Dot label export
belongs to that bar set. The current 216-bar file is validated separately via the real-data
report in test_hooks_v2 (S=5 = 14/14 owner reference rows).

Run:  python "E:/trade ninga/nds-signal/engine/test_phaseA.py"
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from nodes import (SweepConfig, detect_nodes, label_dots, load_daily_csv,
                   load_labels, match_labels)

REF = Path(r"E:\trade ninga\tests\reference")


class TestPhaseA(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bars = load_daily_csv(str(REF / "bars_ES_1440_old.csv"))
        cls.dots = label_dots(load_labels(str(REF / "labels_ES_1440.csv")))

    def test_s5_highlow_matches_reference(self):
        """The fixed reference configuration produces exactly this node list.
        Same-type pivots separated by the deepest opposite swing (08-06/08-07, 09-03/09-04)
        are kept so the chain alternates and the owner's N3/F + micro anchors survive."""
        cfg = SweepConfig(strength=5, use_close=False, min_bars_between=2)
        nodes = detect_nodes(self.bars, cfg)
        got = [f"{p.timeframe_time:%Y-%m-%d}|{'H' if p.is_high else 'L'}|{p.price:g}"
               for p in nodes]
        expected = [
            "2026-05-15|H|7669", "2026-05-20|L|7483.25", "2026-06-02|H|7761.25",
            "2026-06-12|L|7361.25", "2026-06-16|H|7716.5", "2026-06-27|L|7425",
            "2026-07-17|H|7699.75", "2026-07-30|L|7391.75", "2026-08-06|H|7888",
            "2026-08-07|L|7792", "2026-08-14|H|7906.25", "2026-08-25|L|7722.75",
            "2026-08-29|H|7850.25", "2026-09-03|L|7686.25", "2026-09-04|H|7834.25",
            "2026-09-17|L|7575", "2026-09-23|H|7848.5", "2026-10-02|L|7672.75",
            "2026-10-07|H|7897.5",
        ]
        self.assertEqual(got, expected)

    def test_s5_alternation(self):
        cfg = SweepConfig(strength=5, use_close=False, min_bars_between=2)
        nodes = detect_nodes(self.bars, cfg)
        for a, b in zip(nodes, nodes[1:]):
            self.assertNotEqual(a.is_high, b.is_high,
                                f"alternation broken at {a.index}/{b.index}")

    def test_s5_recall_vs_labels(self):
        cfg = SweepConfig(strength=5, use_close=False, min_bars_between=2)
        nodes = detect_nodes(self.bars, cfg)
        hits, total, missed, extra = match_labels(nodes, self.dots, max_bars_off=2)
        # after the pass-1 fix: 15/18 recall at S=5 (was 13/18 pre-fix)
        self.assertGreaterEqual(hits, 15)
        self.assertEqual(total, 18)

    def test_sweep_finds_best_candidate(self):
        """Documented sweep result: some strength reaches 16+ recall with few extra nodes;
        S=1 HighLow reaches 18/18 (46 extra) on the old 144-bar set."""
        best_hits = -1
        for s in range(1, 9):
            for uc in (False, True):
                cfg = SweepConfig(strength=s, use_close=uc, min_bars_between=2)
                nodes = (detect_nodes_close(self.bars, cfg) if uc
                         else detect_nodes(self.bars, cfg))
                hits, _, _, _ = match_labels(nodes, self.dots, max_bars_off=2)
                best_hits = max(best_hits, hits)
        self.assertGreaterEqual(best_hits, 16)


if __name__ == "__main__":
    from nodes import detect_nodes_close  # noqa: F401  (import for test 4)
    unittest.main()
