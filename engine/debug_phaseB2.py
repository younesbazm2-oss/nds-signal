"""Debug the S=2 HL hook in detail: why no N3@08-14?"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from nodes import SweepConfig, detect_nodes, load_daily_csv, node_list_debug
from hooks import MatchConfig, match_hooks

REF = Path(r"E:\trade ninga\tests\reference")
bars = load_daily_csv(str(REF / "bars_ES_1440.csv"))
nodes = detect_nodes(bars, SweepConfig(strength=2, min_bars_between=2))

print("node list (S=2 HL):")
for line in node_list_debug(nodes):
    print(" ", line)

print("\nfull S=2 node list with index:")
for i, p in enumerate(nodes):
    print(f"  {i:2d} {p.timeframe_time:%m-%d} {'H' if p.is_high else 'L'} {p.price:g}")

print("\nhooks (S=2 HL):")
for h in match_hooks(nodes, bars, MatchConfig()):
    d = h.as_dict()
    print(" ", d)
    print("   T =", f"{h.t.timeframe_time:%m-%d}", h.t.price,
          " n1 =", f"{h.n1.timeframe_time:%m-%d}", h.n1.price,
          " n2 =", f"{h.n2.timeframe_time:%m-%d}", h.n2.price,
          " n3 =", (f"{h.n3.timeframe_time:%m-%d}", h.n3.price) if h.n3 else None,
          " end =", f"{h.end.timeframe_time:%m-%d}", h.end.price,
          " kind =", h.kind, " state =", h.state)
