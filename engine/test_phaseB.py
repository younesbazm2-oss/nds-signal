"""Phase B test — hook matcher must reproduce the owner's numbers on ES DEC26 daily.

Owner's Text labels (ground truth for numbering):
  05-09 S1 @7465.25 area (text-only; the V1 swing low is 05-20), 05-15 N1 @7669,
  06-02 N2 @7761.25, 06-12 S2 @7361.25, 08-14 N3 @7906.25.

The hook chain is found at SwingStrength=2 (HighLow): with S=5 the T node (04-24 low) is
not a swing, so the macro hook cannot start. The wood p14 number rule reproduces all four.

Run:  python "E:/trade ninga/nds-signal/engine/test_phaseB.py"
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from hooks import MatchConfig, match_hooks
from nodes import (SweepConfig, detect_nodes, label_dots, load_daily_csv, load_labels)

REF = Path(r"E:\trade ninga\tests\reference")


class TestPhaseB(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bars = load_daily_csv(str(REF / "bars_ES_1440.csv"))
        cls.nodes = detect_nodes(cls.bars, SweepConfig(strength=2, min_bars_between=2))
        cls.labels = load_labels(str(REF / "labels_ES_1440.csv"))
        cls.hooks = match_hooks(cls.nodes, cls.bars)
        cls.labels_out = {}
        cls.micro = {}
        for h in cls.hooks:
            if h.role == "macro":
                cls.labels_out.update(h.label_bars)
            else:
                cls.micro.update(h.label_bars)

    def test_at_least_one_macro_hook(self):
        self.assertGreaterEqual(len(self.hooks), 1)
        self.assertTrue(any(h.role == "macro" for h in self.hooks))
        for h in self.hooks:
            h.as_dict()   # serialisable

    def test_owner_numbers_reproduced(self):
        """N1@05-15, N2@06-02, S2@06-12, N3@08-14 (and our S1 on the 05-20 low)."""
        idx = {p.index: f"{p.timeframe_time:%m-%d}|{'H' if p.is_high else 'L'}|{p.price:g}"
               for p in self.nodes}
        labelled = {idx[bar]: lab for bar, lab in self.labels_out.items() if bar in idx}
        print("\nprimary labels produced by the matcher:")
        for k, v in sorted(labelled.items()):
            print(f"  {k} -> {v}")
        self.assertTrue(any("05-15" in k and v == "N1" for k, v in labelled.items()),
                        f"N1 missing: {labelled}")
        self.assertTrue(any("06-02" in k and v == "N2" for k, v in labelled.items()),
                        f"N2 missing: {labelled}")
        self.assertTrue(any("06-12" in k and v == "S2" for k, v in labelled.items()),
                        f"S2 missing: {labelled}")
        self.assertTrue(any("08-14" in k and v == "N3" for k, v in labelled.items()),
                        f"N3 missing: {labelled}")
        self.assertTrue(any("05-20" in k and v == "S1" for k, v in labelled.items()),
                        f"S1 missing: {labelled}")

    def test_no_post_n3_numbering(self):
        """After N3 (08-14) the owner leaves swings unnumbered — phase D region."""
        idx = {p.index: f"{p.timeframe_time:%m-%d}" for p in self.nodes}
        late = {idx[bar]: lab for bar, lab in self.labels_out.items()
                if bar in idx and idx[bar] > "08-14"}
        self.assertEqual(late, {}, f"unexpected labels after N3: {late}")

    def test_hook_directions_sane(self):
        for h in self.hooks:
            if h.kind != "A":
                continue
            if h.direction == "pos":
                self.assertFalse(h.t.is_high)
                self.assertLess(h.n1.price, h.n2.price)
                if h.v2 is not None:
                    self.assertGreater(h.v1.price, h.v2.price)
            else:
                self.assertTrue(h.t.is_high)
                self.assertGreater(h.n1.price, h.n2.price)
                if h.v2 is not None:
                    self.assertLess(h.v1.price, h.v2.price)

    def test_level864_between_t_and_node3(self):
        """level = P3 - 0.864*(P3-T) must sit between T and the end peak (pos A)."""
        for h in self.hooks:
            if h.kind != "A" or h.direction != "pos":
                continue
            self.assertLess(h.t.price, h.level864)
            self.assertLess(h.level864, h.n3.price)


if __name__ == "__main__":
    unittest.main(verbosity=2)
