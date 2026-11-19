"""Run nodes + HookTracker on a daily CSV; print a report; write labels CSV and an SVG.

Usage (from the engine folder):
  python run_hooks.py --bars "E:/trade ninga/tests/reference/bars_ES_1440.csv" --strength 3
Outputs (folder --out, default ./out):
  labels_auto_S<strength>.csv   one row per node: date,hl,price,label,role,hook,secondary
  hooks_S<strength>.svg         picture for the owner to review (no extra libraries needed)
"""
from __future__ import annotations

import argparse
import csv
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from hooks_v2 import HookConfig, HookTracker
from nodes import SweepConfig, detect_nodes, load_daily_csv


def analyse(bars, strength: int, tick: float = 0.25):
    nodes = detect_nodes(bars, SweepConfig(strength=strength, min_bars_between=2, tick_size=tick))
    tr = HookTracker(HookConfig(tick_size=tick))
    for n in nodes:
        tr.on_node(n)
    return nodes, tr


def print_report(bars, nodes, tr) -> None:
    labs = tr.labels()
    print(f"bars={len(bars)} nodes={len(nodes)} hooks_total={len(tr.hooks)} recognized={len(tr.recognized())}")
    print("\nNODES (primary label, role, hook; secondary in brackets)")
    for n in nodes:
        e = labs.get(n.index)
        if e:
            p = e["primary"]
            sec = ",".join(f"{x[0]}@h{x[1]}" for x in e["secondary"])
            txt = f"{p[0]:3s} {p[2]:5s} h{p[1]}" + (f" [{sec}]" if sec else "")
        else:
            txt = "-"
        print(f"  {n.timeframe_time:%Y-%m-%d} {'H' if n.is_high else 'L'} {n.price:9.2f}  {txt}")
    print("\nHOOKS")
    for h in tr.hooks:
        imp = ",".join(f"{x.timeframe_time:%m-%d}" for x in h.imp)
        ctr = ",".join(f"{x.timeframe_time:%m-%d}" for x in h.ctr)
        f = f" F={h.f.timeframe_time:%m-%d}" if h.f else ""
        rec = "REC" if h in tr.recognized() else "   "
        print(f"  h{h.hid:<3d} {rec} {h.direction} d{h.depth} {h.reason:8s} T={h.t.timeframe_time:%Y-%m-%d} "
              f"{h.t.price:9.2f} state={h.state:6s} kind={h.kind} imp[{imp}] ctr[{ctr}]{f} "
              f"level={h.level:.2f} frac={h.fraction:.3f} ok={h.level_ok}")


def write_labels_csv(path, nodes, tr) -> None:
    labs = tr.labels()
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["date", "hl", "price", "label", "role", "hook", "secondary"])
        for n in nodes:
            e = labs.get(n.index)
            if e:
                p = e["primary"]
                sec = ";".join(f"{x[0]}:h{x[1]}:{x[2]}" for x in e["secondary"])
                w.writerow([f"{n.timeframe_time:%Y-%m-%d}", "H" if n.is_high else "L", n.price, p[0], p[2], p[1], sec])
            else:
                w.writerow([f"{n.timeframe_time:%Y-%m-%d}", "H" if n.is_high else "L", n.price, "", "", "", ""])


def write_svg(path, bars, nodes, tr, title: str = "") -> None:
    W, H, ML, MR, MT, MB = 1700, 760, 70, 30, 50, 50
    n = len(bars)
    lo = min(b.low for b in bars)
    hi = max(b.high for b in bars)
    pad = (hi - lo) * 0.05 or 1.0
    lo, hi = lo - pad, hi + pad

    def X(i):
        return ML + (W - ML - MR) * (i / max(1, n - 1))

    def Y(p):
        return MT + (H - MT - MB) * (1 - (p - lo) / (hi - lo))

    o = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" '
         f'font-family="Consolas,monospace">',
         f'<rect width="{W}" height="{H}" fill="#0d1117"/>',
         f'<text x="{ML}" y="24" fill="#c9d1d9" font-size="14">{title}</text>']
    step = 100 if (hi - lo) > 500 else 50
    p = int(lo // step * step)
    while p < hi:
        if p > lo:
            o.append(f'<line x1="{ML}" y1="{Y(p):.1f}" x2="{W-MR}" y2="{Y(p):.1f}" stroke="#21262d"/>')
            o.append(f'<text x="6" y="{Y(p)+4:.1f}" fill="#8b949e" font-size="11">{p}</text>')
        p += step
    for i in range(0, n, max(1, n // 14)):
        o.append(f'<text x="{X(i)-18:.1f}" y="{H-18}" fill="#8b949e" font-size="11">{bars[i].time_utc:%m-%d}</text>')
    for i, b in enumerate(bars):
        col = "#3fb950" if b.close >= b.open else "#f85149"
        o.append(f'<line x1="{X(i):.1f}" y1="{Y(b.high):.1f}" x2="{X(i):.1f}" y2="{Y(b.low):.1f}" stroke="{col}" stroke-width="1"/>')
    for h in tr.recognized():
        if h.level == h.level:   # not NaN
            x1 = X(h.t.index)
            o.append(f'<line x1="{x1:.1f}" y1="{Y(h.level):.1f}" x2="{W-MR}" y2="{Y(h.level):.1f}" '
                     f'stroke="#00d4a0" stroke-dasharray="6 4" opacity="0.8"/>')
            o.append(f'<text x="{x1+4:.1f}" y="{Y(h.level)-4:.1f}" fill="#00d4a0" font-size="11">'
                     f'86.4% h{h.hid} {h.level:.1f}</text>')
        tx, ty = X(h.t.index), Y(h.t.price)
        o.append(f'<polygon points="{tx:.1f},{ty-7:.1f} {tx+6:.1f},{ty:.1f} {tx:.1f},{ty+7:.1f} {tx-6:.1f},{ty:.1f}" '
                 f'fill="#e3b341"/>')
        o.append(f'<text x="{tx+8:.1f}" y="{ty+(-8 if h.t.is_high else 16):.1f}" fill="#e3b341" font-size="11">T h{h.hid}</text>')
    labs = tr.labels()
    for nd in nodes:
        x, y = X(nd.index), Y(nd.price)
        col = "#58a6ff" if nd.is_high else "#ff9248"
        o.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="3.5" fill="{col}"/>')
        e = labs.get(nd.index)
        if not e:
            continue
        label, hid, role = e["primary"]
        dy = -9 if nd.is_high else 18
        if label == "F":
            color, size = "#f778ba", 14
        elif role == "macro":
            color, size = "#ffffff", 14
        else:
            color, size = "#9aa4b2", 11
        o.append(f'<text x="{x:.1f}" y="{y+dy:.1f}" fill="{color}" font-size="{size}" text-anchor="middle">{label}</text>')
        for k, (sl, shid, srole) in enumerate(e["secondary"]):
            o.append(f'<text x="{x:.1f}" y="{y+dy+(-13 if nd.is_high else 13)*(k+1):.1f}" fill="#6e7681" '
                     f'font-size="10" text-anchor="middle">{sl}</text>')
    o.append("</svg>")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(o))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--bars", default=r"E:\trade ninga\tests\reference\bars_ES_1440.csv")
    ap.add_argument("--strength", type=int, default=3)
    ap.add_argument("--tick", type=float, default=0.25)
    ap.add_argument("--out", default="out")
    a = ap.parse_args()
    bars = load_daily_csv(a.bars)
    nodes, tr = analyse(bars, a.strength, a.tick)
    print_report(bars, nodes, tr)
    os.makedirs(a.out, exist_ok=True)
    write_labels_csv(os.path.join(a.out, f"labels_auto_S{a.strength}.csv"), nodes, tr)
    svg = os.path.join(a.out, f"hooks_S{a.strength}.svg")
    write_svg(svg, bars, nodes, tr, f"NDS hooks  strength={a.strength}  bars={len(bars)}")
    print("\nwrote:", os.path.join(a.out, f"labels_auto_S{a.strength}.csv"), "and", svg)


if __name__ == "__main__":
    main()
