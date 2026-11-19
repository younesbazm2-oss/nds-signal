"""Phase A sweep: which SwingStrength reproduces the owner's hand labels on ES DEC26 daily?

Sweeps Strength 1..8 x SwingSource (HighLow, CloseLine) and reports hits/misses/extra vs the
Dot rows of labels_ES_1440.csv. Stdlib only.

Run:  python "E:/trade ninga/nds-signal/engine/phaseA_sweep.py"
NOTE: in this batch file, a plain for-range loop over a *candidate-relative* window must use
the candidate index c as the anchor (newer = c+1..c+s, older = c-1..c-s) — NOT bars[0..s-1].
That indexing bug cost one wasted sweep run; the brute-force check above is the cross-check.
NOTE 2: labels CSV `type` column = drawing kind (Text/Dot), NOT H/L. H/L lives in `side`/tag.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from nodes import (SweepConfig, detect_nodes, detect_nodes_close, label_dots,
                   load_daily_csv, load_labels, match_labels, node_list_debug)

REF = Path(r"E:\trade ninga\tests\reference")
BARS = REF / "bars_ES_1440.csv"
LABELS = REF / "labels_ES_1440.csv"


def main():
    bars = load_daily_csv(str(BARS))
    labels = load_labels(str(LABELS))
    dots = label_dots(labels)
    print(f"bars={len(bars)}  label_dots={len(dots)}")

    best = None
    for strength in range(1, 9):
        for use_close in (False, True):
            cfg = SweepConfig(strength=strength, use_close=use_close, min_bars_between=2)
            nodes = detect_nodes_close(bars, cfg) if use_close else detect_nodes(bars, cfg)
            hits, total, missed, extra = match_labels(nodes, dots, max_bars_off=2)
            prec = hits / len(nodes) if nodes else 0.0
            rec = hits / total if total else 0.0
            tag = "CloseLine" if use_close else "HighLow  "
            print(f"S={strength} {tag}  nodes={len(nodes):3d}  hits={hits:2d}/{total}  "
                  f"prec={prec:.2f} rec={rec:.2f}")
            score = rec * 1.0 + prec * 0.5   # phase A wants recall first
            if best is None or score > best[0]:
                best = (score, strength, use_close, nodes, missed, extra)

    _, s, uc, nodes, missed, extra = best
    print("\n=== BEST (recall-weighted) ===")
    print(f"Strength={s}  SwingSource={'CloseLine' if uc else 'HighLow'}")
    print("\n-- node list --")
    for line in node_list_debug(nodes):
        print(line)
    print("\n-- missed owner labels (no node within +-2 bars) --")
    for m in missed:
        print(m)
    print("-- extra nodes (no owner label) --")
    for e in extra:
        print(e)


if __name__ == "__main__":
    main()
