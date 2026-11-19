"""Tests for hooks_v2.HookTracker (stdlib unittest).

Run:  python test_hooks_v2.py
Groups
  A) synthetic unit tests (hook A, hook B, failure, F, spawn rules, no look-ahead)
  B) SCENARIO test: the owner's ES daily structure rebuilt as a node list. The nodes marked
     ASSUMED (T0, early-August top, pullback valley, early-September valley and bump) are
     placeholders for nodes not yet located in real data; everything else uses dates/prices
     from the S=5 node list. This proves the LOGIC reproduces the owner's labels.
  C) real-data report (only if bars_ES_1440.csv and labels_owner_ES_1440.csv exist): prints a
     PASS/FAIL table per owner row for Strength 2, 3 and 5; it never fails the suite.
"""
import os
import sys
import unittest
from datetime import datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from hooks_v2 import HookConfig, HookTracker
from nodes import Pivot

D0 = datetime(2026, 1, 1)


def mk(spec, start_index=0):
    """spec: list of ('H'|'L', price) -> alternating Pivots with consecutive indexes."""
    out = []
    for i, (hl, price) in enumerate(spec):
        out.append(Pivot(start_index + i, float(price), hl == "H", D0 + timedelta(days=start_index + i)))
    return out


def run(nodes, cfg=HookConfig()):
    tr = HookTracker(cfg)
    for n in nodes:
        tr.on_node(n)
    return tr


def lab(tr):
    return {i: d["primary"][0] for i, d in tr.labels().items()}


class TestSynthetic(unittest.TestCase):
    def test_pos_hook_A_closes_on_level(self):
        # T=100; peaks 110,120,130 ascending; valleys 107,105,104 descending, all above T
        nodes = mk([("L", 100), ("H", 110), ("L", 107), ("H", 120), ("L", 105), ("H", 130), ("L", 104)])
        tr = run(nodes)
        h = [x for x in tr.hooks if x.t.index == 0 and x.direction == "pos"][0]
        self.assertEqual(h.state, "closed")
        self.assertEqual(h.kind, "A")
        self.assertTrue(h.level_ok, h.fraction)
        self.assertEqual(lab(tr), {1: "N1", 2: "S1", 3: "N2", 4: "S2", 5: "N3", 6: "S3"})

    def test_pos_hook_B_has_unnumbered_bump(self):
        # impulse stops at 2 (bump 115 < 120 is unnumbered); counter reaches 3
        nodes = mk([("L", 100), ("H", 110), ("L", 106), ("H", 120), ("L", 104), ("H", 115), ("L", 102.5)])
        tr = run(nodes)
        h = [x for x in tr.hooks if x.t.index == 0 and x.direction == "pos"][0]
        self.assertEqual((h.kind, h.state, len(h.imp), len(h.ctr)), ("B", "closed", 2, 3))
        self.assertTrue(h.level_ok, h.fraction)
        self.assertNotIn(5, lab(tr), "the bump must stay unnumbered")
        self.assertEqual(lab(tr)[6], "S3")

    def test_neg_hook_letters_are_not_swapped(self):
        # mirror of hook A: T=130 (high); valleys descend; peaks ascend
        nodes = mk([("H", 130), ("L", 120), ("H", 123), ("L", 112), ("H", 125), ("L", 104), ("H", 126)])
        tr = run(nodes)
        h = [x for x in tr.hooks if x.t.index == 0 and x.direction == "neg"][0]
        self.assertEqual(h.state, "closed")
        got = lab(tr)
        for i, n in enumerate(nodes):
            if i in got:
                self.assertEqual(got[i][0], "N" if n.is_high else "S", f"letter swapped at {i}")

    def test_pos_hook_fails_below_T(self):
        nodes = mk([("L", 100), ("H", 110), ("L", 105), ("H", 118), ("L", 99)])
        tr = run(nodes)
        h = [x for x in tr.hooks if x.t.index == 0 and x.direction == "pos"][0]
        self.assertEqual(h.state, "failed")
        self.assertEqual(h.failed_by.index, 4)
        # the breaker becomes the next T
        self.assertTrue(any(x.t.index == 4 and x.direction == "pos" for x in tr.hooks))

    def test_F_after_node3_and_opposite_hook_spawns(self):
        nodes = mk([("L", 100), ("H", 110), ("L", 106), ("H", 120), ("L", 103), ("H", 130),
                    ("L", 124), ("H", 140)])
        tr = run(nodes)
        h = [x for x in tr.hooks if x.t.index == 0 and x.direction == "pos"][0]
        self.assertIsNotNone(h.f)
        self.assertEqual(h.f.index, 7)
        self.assertTrue(any(x.t.index == 7 and x.direction == "neg" and x.reason == "F" for x in tr.hooks))

    def test_lower_high_after_node3_is_nd_candidate(self):
        nodes = mk([("L", 100), ("H", 110), ("L", 106), ("H", 120), ("L", 103), ("H", 130),
                    ("L", 118), ("H", 126)])
        tr = run(nodes)
        h = [x for x in tr.hooks if x.t.index == 0 and x.direction == "pos"][0]
        self.assertIsNone(h.f)
        self.assertEqual(h.nd.index, 7)

    def test_no_duplicate_hooks(self):
        nodes = mk([("L", 100), ("H", 110), ("L", 106), ("H", 120), ("L", 103), ("H", 130)])
        tr = run(nodes)
        keys = [(h.direction, h.t.index) for h in tr.hooks]
        self.assertEqual(len(keys), len(set(keys)))


# ----------------------------------------------------------------- scenario (owner's ES daily)
def d(m, day):
    return datetime(2026, m, day)


SCENARIO = [  # (date, H/L, price, ASSUMED?)
    (d(4, 24), "L", 7215.00, True),     # T0: April low (price assumed; needs more history)
    (d(5, 15), "H", 7669.00, False),
    (d(5, 20), "L", 7483.25, False),
    (d(6, 2), "H", 7761.25, False),
    (d(6, 12), "L", 7361.25, False),
    (d(6, 16), "H", 7716.50, False),
    (d(6, 27), "L", 7425.00, False),
    (d(7, 17), "H", 7699.75, False),
    (d(7, 30), "L", 7391.75, False),
    (d(8, 5), "H", 7870.00, True),      # early-August top (price assumed)
    (d(8, 10), "L", 7790.00, True),     # pullback valley (assumed)
    (d(8, 14), "H", 7906.25, False),
    (d(8, 25), "L", 7722.75, False),
    (d(8, 29), "H", 7850.25, False),
    (d(9, 3), "L", 7686.00, True),      # early-September valley (assumed)
    (d(9, 8), "H", 7790.00, True),      # bump (assumed)
    (d(9, 17), "L", 7575.00, False),
    (d(9, 23), "H", 7848.50, False),
    (d(10, 2), "L", 7672.75, False),
    (d(10, 7), "H", 7897.50, False),
]


def scenario_nodes():
    return [Pivot(i, p, hl == "H", dt) for i, (dt, hl, p, _) in enumerate(SCENARIO)]


class TestScenario(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.nodes = scenario_nodes()
        cls.tr = run(cls.nodes)
        cls.by_date = {f"{n.timeframe_time:%m-%d}": n.index for n in cls.nodes}

    def claims(self, mmdd):
        d_ = self.tr.labels().get(self.by_date[mmdd])
        if not d_:
            return {}
        out = {d_["primary"][2]: d_["primary"][0]}
        for lab_, hid, role in d_["secondary"]:
            out[hid] = lab_
        return out

    def primary(self, mmdd):
        d_ = self.tr.labels().get(self.by_date[mmdd])
        return d_["primary"] if d_ else None

    def test_macro_labels(self):
        for mmdd, want in (("05-15", "N1"), ("05-20", "S1"), ("06-02", "N2"), ("06-12", "S2"),
                           ("08-05", "N3"), ("08-14", "F")):
            p = self.primary(mmdd)
            self.assertIsNotNone(p, mmdd)
            self.assertEqual((p[0], p[2]), (want, "macro"), mmdd)

    def test_second_layer(self):
        for mmdd, want in (("06-16", "N1"), ("06-27", "S1"), ("07-30", "S2")):
            p = self.primary(mmdd)
            self.assertIsNotNone(p, mmdd)
            self.assertEqual((p[0], p[2]), (want, "micro"), mmdd)
        self.assertIsNone(self.primary("07-17"), "07-17 must stay unnumbered")

    def test_post_F_negative_hook(self):
        for mmdd, want in (("08-25", "S1"), ("08-29", "N1"), ("09-03", "S2"), ("09-17", "S3"),
                           ("10-07", "N2")):
            p = self.primary(mmdd)
            self.assertIsNotNone(p, mmdd)
            self.assertEqual((p[0], p[2]), (want, "macro"), mmdd)
        self.assertIsNone(self.primary("09-23"), "09-23 must stay unnumbered")
        self.assertIsNone(self.primary("09-08"), "the bump must stay unnumbered")

    def test_shared_nodes(self):
        c = self.claims("08-05")
        self.assertEqual(sorted(c.values()), ["N2", "N3"])      # macro N3 + micro N2
        c = self.claims("08-14")
        self.assertEqual(sorted(c.values()), ["F", "N3"])       # macro F + micro N3

    def test_no_spurious_hook_from_07_30(self):
        t730 = self.by_date["07-30"]
        for h in self.tr.recognized():
            self.assertNotEqual(h.t.index, t730)

    def test_no_look_ahead(self):
        """snapshot after m nodes of the full run == final state of a run on nodes[:m]."""
        full = HookTracker()
        snaps = []
        for n in self.nodes:
            full.on_node(n)
            snaps.append(full.snapshot())
        for m in range(1, len(self.nodes) + 1):
            part = run(self.nodes[:m])
            self.assertEqual(snaps[m - 1], part.snapshot(), f"differs at m={m}")


# ---------------------------------------------------------------------- real data report
def real_data_report():
    try:
        from nodes import SweepConfig, detect_nodes, load_daily_csv
    except Exception as exc:
        print("real-data report skipped:", exc)
        return
    ref = Path(os.environ.get("NDS_REF", r"E:\trade ninga\tests\reference"))
    bars_p, lab_p = ref / "bars_ES_1440.csv", ref / "labels_owner_ES_1440.csv"
    if not bars_p.exists() or not lab_p.exists():
        print("real-data report skipped: missing", bars_p, "or", lab_p)
        return
    import csv
    rows = list(csv.DictReader(open(lab_p, newline="", encoding="utf-8-sig")))
    bars = load_daily_csv(str(bars_p))
    print(f"\nREAL DATA: bars={len(bars)} first={bars[0].time_utc:%Y-%m-%d} last={bars[-1].time_utc:%Y-%m-%d}")
    print("strength | passed/known | failed rows")
    for s in (2, 3, 5):
        nodes = detect_nodes(bars, SweepConfig(strength=s, min_bars_between=2))
        tr = run(nodes)
        labs = tr.labels()
        by_date = {}
        for n in nodes:
            by_date.setdefault(f"{n.timeframe_time:%Y-%m-%d}", []).append(n)
        known = ok = 0
        fails = []
        for r in rows:
            date = r["date"].strip()
            if date in ("", "?") or r.get("confidence", "").strip() == "low":
                continue
            known += 1
            want = r["label"].strip()
            cand = [n for n in by_date.get(date, []) if n.is_high == (r["hl"].strip() == "H")]
            if not cand:
                fails.append(f"{date} {r['hl']} {want or '-'} -> NO_NODE")
                continue
            ent = labs.get(cand[0].index)
            got = []
            if ent:
                got = [ent["primary"][0]] + [x[0] for x in ent["secondary"]]
            if (not want and not got) or (want and want in got):
                ok += 1
            else:
                fails.append(f"{date} {r['hl']} want={want or '-'} got={got or '-'}")
        print(f"S={s} | {ok}/{known} | " + "; ".join(fails))


if __name__ == "__main__":
    res = unittest.main(verbosity=2, exit=False)
    real_data_report()
    sys.exit(0 if res.result.wasSuccessful() else 1)
