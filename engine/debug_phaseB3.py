"""Print macro + micro hooks with their 86.4% levels on ES DEC26 daily (S=2)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from nodes import SweepConfig, detect_nodes, load_daily_csv
from hooks import MatchConfig, match_hooks

REF = Path(r"E:\trade ninga\tests\reference")
bars = load_daily_csv(str(REF / "bars_ES_1440.csv"))
nodes = detect_nodes(bars, SweepConfig(strength=2, min_bars_between=2))
hooks = match_hooks(nodes, bars, MatchConfig())

for h in hooks:
    d = h.as_dict()
    lbl = f"{d['t_index']:>4} (T {d['t_price']:g})  {d['direction']} {d['kind']} [{d['role']}]"
    print(f"{lbl:38s} level={d['level864']:g}  state={d['state']}")
    for bn, lab in h.label_bars.items():
        print(f"      {nodes[0].timeframe_time:%Y} bar {bn}: {lab}")
