"""Debug: why is the owner's 05-09 low not a swing, and which config finds it?"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from nodes import SweepConfig, detect_nodes, load_daily_csv
from hooks import MatchConfig, match_hooks

REF = Path(r"E:\trade ninga\tests\reference")
bars = load_daily_csv(str(REF / "bars_ES_1440.csv"))

# The early bars around 05-09 (printed previously):
# 05-08 L=7474.5, 05-09 L=7465.25, 05-12 L=7519.5, 05-07 L=7422.5, 05-06 L=7352.75
# With s=5 the older side contains 05-07's LOWER low -> 05-09 disqualified.
# With s=1 the older side is only 05-08 (7474.5 > 7465.25) and the newer side
# 05-12 (7519.5 > 7465.25) -> 05-09 IS a swing at S=1.

print("early bars:")
for b in bars[:35]:
    print(f"{b.time_utc:%Y-%m-%d} H={b.high} L={b.low}")

print("\nS=1 nodes (first 8):")
nodes = detect_nodes(bars, SweepConfig(strength=1, min_bars_between=2))
for p in nodes[:8]:
    print(f"  {p.timeframe_time:%m-%d}", "H" if p.is_high else "L", p.price)

print("\nS=1 hooks:")
hooks = match_hooks(nodes, bars, MatchConfig())
for h in hooks:
    print(" ", h.as_dict())
