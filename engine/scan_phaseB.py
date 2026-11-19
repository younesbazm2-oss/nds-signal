"""Scan node configs + hook matcher vs the owner's 4 numbered labels.

Owner ground truth (Text rows): N1@05-15 H 7669, N2@06-02 H 7761.25,
S2@06-12 L 7361.25, N3@08-14 H 7906.25. (S1 at 05-09 is a text-only quirk; V1 is our S1.)

Find the (Strength, SwingSource) whose hook-matching reproduces all four.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from nodes import SweepConfig, detect_nodes, detect_nodes_close, load_daily_csv, node_list_debug
from hooks import MatchConfig, match_hooks

REF = Path(r"E:\trade ninga\tests\reference")
bars = load_daily_csv(str(REF / "bars_ES_1440.csv"))

OWNER = {"05-15": ("H", "N1"), "06-02": ("H", "N2"), "06-12": ("L", "S2"), "08-14": ("H", "N3")}

for s in range(1, 7):
    for uc in (False, True):
        cfg = SweepConfig(strength=s, use_close=uc, min_bars_between=2)
        nodes = detect_nodes_close(bars, cfg) if uc else detect_nodes(bars, cfg)
        hooks = match_hooks(nodes, bars, MatchConfig())
        labels = {}
        for h in hooks:
            labels.update(h.label_bars)
        keys = {n.index: f"{n.timeframe_time:%m-%d}|{'H' if n.is_high else 'L'}|{n.price:g}"
                for n in nodes}
        lab = {}
        for bar, L in labels.items():
            if bar in keys:
                lab[L] = lab.get(L, []) + [keys[bar].split("|")[0]]
        tag = "Close" if uc else "HL  "
        print(f"S={s} {tag} nodes={len(nodes)} hooks={len(hooks)} labels="
              f"{[(k, v) for k, v in sorted(lab.items())]}")
