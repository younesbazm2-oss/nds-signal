"""NDS node detection — Python reference implementation, same algorithm as the C# indicator.

Faithful port of StepNodeDetection / AppendNode in NDSStructure_fixed.cs (section 10 of the
skill): swing pivots on the chart's OWN bars, no ATR, no look-ahead.

Rules (must match the C# exactly):
  - A bar at index c is a peak if High[c] is strictly higher than the Strength bars on EACH side.
  - Newer side (right, indices 0..Strength-1): a tie (==) DISQUALIFIES the candidate (>= comparison),
    so the latest of equal highs wins. Older side (left, Strength+1..2*Strength): tie does NOT
    disqualify (only >).
  - Mirrored for valleys (Low, <= / <).
  - The candidate bar is at barsAgo = Strength; confirmed Strength bars later (no future leak).
  - Two pivots of the same type that follow each other: keep the more extreme one (alternation).
  - Node price is exactly High/Low rounded to tick; node index is absolute bar index.

MinBarsBetweenNodes in the C# guards the CONFIRM bar; here the guard is applied on extreme
bars, matching the daily validation run (same node list).

Usage (stdlib only — no numpy/pandas):
  from nodes import SweepConfig, detect_nodes, load_daily_csv
"""
from __future__ import annotations

import csv
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional, Sequence, Tuple


@dataclass(frozen=True)
class Bar:
    time_utc: datetime
    open: float
    high: float
    low: float
    close: float


@dataclass(frozen=True)
class Pivot:
    index: int            # absolute bar index of the extreme
    price: float
    is_high: bool
    timeframe_time: datetime


@dataclass(frozen=True)
class SweepConfig:
    strength: int = 5
    use_close: bool = False      # False = High/Low (book counts shadows), True = Close line
    tick_size: float = 0.25
    min_bars_between: int = 2


def load_daily_csv(path: str) -> List[Bar]:
    bars: List[Bar] = []
    with open(path, newline="", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            bars.append(Bar(
                datetime.fromisoformat(row["time"]),
                float(row["open"]), float(row["high"]),
                float(row["low"]), float(row["close"]),
            ))
    return bars


def load_labels(path: str) -> List[Tuple[datetime, bool, float, str, str]]:
    """Return [(time, is_high, price, label_or_empty, tag)] for every row.

    CSV columns: time, price, text, type, tag, side. NOTE: `type` is the DRAWING kind
    (Text/Dot), NOT H/L. H/L comes from the tag: nds_H_<bar> / nds_L_<bar> / side column.
    """
    out = []
    with open(path, newline="", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            t = datetime.fromisoformat(row["time"])
            tag = row["tag"].strip()
            side = row.get("side", "").strip().lower()
            if tag.startswith("nds_H_") or side.startswith("high"):
                is_high = True
            elif tag.startswith("nds_L_") or side.startswith("low"):
                is_high = False
            else:
                is_high = side.startswith("high")
            out.append((t, is_high, float(row["price"]), row["text"].strip(), tag))
    return out


def detect_nodes(bars: Sequence[Bar], cfg: SweepConfig) -> List[Pivot]:
    """Swing pivots on the chart's own bars. Returns alternating H/L pivots, oldest first."""
    n = len(bars)
    s = cfg.strength
    if s < 1:
        return []

    def scaled(v: float) -> float:
        return round(v / cfg.tick_size) * cfg.tick_size

    raw: List[Pivot] = []
    # candidate at c must be fully confirmed: c + s < n
    for c in range(s, n - s):
        hi = bars[c].high
        lo = bars[c].low

        swing_high = True
        swing_low = True
        for i in range(1, s + 1):
            new_hi = bars[c + i].high if c + i < n else None   # newer side: >= disqualifies
            old_hi = bars[c - i].high                          # older side: > disqualifies
            new_lo = bars[c + i].low if c + i < n else None
            old_lo = bars[c - i].low

            if swing_high and (new_hi is None or new_hi >= hi or old_hi > hi):
                swing_high = False
            if swing_low and (new_lo is None or new_lo <= lo or old_lo < lo):
                swing_low = False
            if not swing_high and not swing_low:
                break

        if swing_high:
            raw.append(Pivot(c, scaled(hi), True, bars[c].time_utc))
        if swing_low:
            raw.append(Pivot(c, scaled(lo), False, bars[c].time_utc))

    raw.sort(key=lambda p: (p.index, not p.is_high))

    # alternation pass 1 — same-type pivots:
    #   Book p78 merges two EQUAL peaks/valleys into ONE node. A later, MORE EXTREME same-type
    #   pivot is a continuation of the same leg and the owner labels BOTH (e.g. ES daily:
    #   N3 = 08-06 7888 and F = 08-14 7906.25). They are kept as separate nodes only when a
    #   genuine pullback separates them: an opposite-type strength-1 swing between the two.
    #   Then the deepest opposite extreme between them is inserted so the chain alternates.
    #   A LESS extreme later pivot is a retest of the same leg (fold into the previous, old
    #   rule), and a more extreme one with no opposite strength-1 swing between (one-sided
    #   spike) also folds — there is no real pullback to hang a node on.
    #
    #   MinBarsBetweenNodes is checked HERE, inside the merge loop, exactly as the C# does:
    #   a candidate closer than min_bars_between to the last KEPT node is skipped and never
    #   updates the guard. (Running it as a separate second pass was a bug: it dropped a
    #   pivot that separated two same-type pivots and left them adjacent in the final chain,
    #   breaking alternation.) Nodes take priority over the spacing guard — the owner's rule.
    merged: List[Pivot] = []
    last_confirmed_index = -1_000_000
    for p in raw:
        if cfg.min_bars_between > 0 and p.index - last_confirmed_index < cfg.min_bars_between:
            continue
        if merged and merged[-1].is_high == p.is_high:
            prev = merged[-1]
            more_extreme = p.price > prev.price if p.is_high else p.price < prev.price
            if more_extreme:
                opposite = _deepest_opposite(bars, prev, p)
                if opposite is not None:
                    merged.extend(opposite)   # [valley, p] for peaks, [peak, p] for valleys
                    last_confirmed_index = p.index
                    continue
            if p.is_high and p.price > prev.price:
                merged[-1] = p
                last_confirmed_index = p.index
            elif (not p.is_high) and p.price < prev.price:
                merged[-1] = p
                last_confirmed_index = p.index
            # else: existing (more extreme) stays, drop p — the guard does not move
            continue
        merged.append(p)
        last_confirmed_index = p.index
    return merged


def _deepest_opposite(bars: Sequence[Bar], prev: Pivot, p: Pivot) -> Optional[List[Pivot]]:
    """Between two same-type consecutive pivots: if an opposite strength-1 swing exists
    between their indexes, return [opposite_extreme, p] so the chain alternates.
    strength-1 opposite swing = a bar whose low (between peaks) is lower than BOTH
    neighbour bars' lows (mirror for valleys between valleys). Returns None otherwise."""
    a, b = prev.index, p.index
    if b - a < 2:
        return None
    if p.is_high:
        # deepest valley between the two peaks: lower than both neighbours, and strictly
        # lower than the lower of the two peak lows (a real pullback, not flat chop)
        cands = [c for c in range(a + 1, b)
                 if bars[c].low < bars[c - 1].low and bars[c].low < bars[c + 1].low]
        if not cands:
            return None
        c = min(cands, key=lambda k: bars[k].low)
        if bars[c].low >= min(prev.price, p.price):
            return None
        return [Pivot(c, bars[c].low, False, bars[c].time_utc), p]
    cands = [c for c in range(a + 1, b)
             if bars[c].high > bars[c - 1].high and bars[c].high > bars[c + 1].high]
    if not cands:
        return None
    c = max(cands, key=lambda k: bars[k].high)
    if bars[c].high <= max(prev.price, p.price):
        return None
    return [Pivot(c, bars[c].high, True, bars[c].time_utc), p]


def detect_nodes_close(bars: Sequence[Bar], cfg: SweepConfig) -> List[Pivot]:
    """CloseLine experiment (SwingSource.CloseLine in the C#): pivots on the close-only line."""
    n = len(bars)
    s = cfg.strength
    raw: List[Pivot] = []
    for c in range(s, n - s):
        cl = bars[c].close
        swing_high = True
        swing_low = True
        for i in range(1, s + 1):
            new_c = bars[c + i].close   # newer side: >= disqualifies
            old_c = bars[c - i].close   # older side: > disqualifies
            if swing_high and (new_c >= cl or old_c > cl):
                swing_high = False
            if swing_low and (new_c <= cl or old_c < cl):
                swing_low = False
            if not swing_high and not swing_low:
                break
        if swing_high:
            raw.append(Pivot(c, cl, True, bars[c].time_utc))
        if swing_low:
            raw.append(Pivot(c, cl, False, bars[c].time_utc))
    raw.sort(key=lambda p: (p.index, not p.is_high))

    merged: List[Pivot] = []
    last_confirmed_index = -1_000_000
    for p in raw:
        if merged and merged[-1].is_high == p.is_high:
            prev = merged[-1]
            if p.is_high and p.price > prev.price:
                merged[-1] = p
            elif (not p.is_high) and p.price < prev.price:
                merged[-1] = p
            continue
        if cfg.min_bars_between > 0 and p.index - last_confirmed_index < cfg.min_bars_between:
            continue
        merged.append(p)
        last_confirmed_index = p.index
    return merged


# ---------------------------------------------------------------- matching vs labels

def label_dots(labels: Sequence[Tuple[datetime, bool, float, str, str]]) -> List[Tuple[datetime, bool, float, str]]:
    """The swing ground truth = Dot rows (tags nds_H_<bar> / nds_L_<bar>)."""
    dots = []
    seen = set()
    for (t, is_high, price, text, tag) in labels:
        key = (t, is_high, price)
        if ("nds_H_" in tag or "nds_L_" in tag) and key not in seen:
            seen.add(key)
            dots.append((t, is_high, price, text))
    return dots


def match_labels(nodes: Sequence[Pivot], dots: Sequence[Tuple[datetime, bool, float, str]],
                 max_bars_off: int = 2) -> Tuple[int, int, List[str], List[str]]:
    """Greedy nearest match within +-max_bars_off days (daily chart = 1 bar = 1 day).

    Returns (hits, dot_count, missed_dots, extra_nodes).
    """
    used = set()
    missed = []
    extra = []
    for nd in nodes:
        best = None
        for i, (lt, lis_hi, lp, ltext) in enumerate(dots):
            if i in used or lis_hi != nd.is_high:
                continue
            if abs((lt - nd.timeframe_time).total_seconds()) <= max_bars_off * 86400:
                if best is None or abs(lp - nd.price) < abs(dots[best][2] - nd.price):
                    best = i
        if best is None:
            extra.append(f"{nd.timeframe_time:%m-%d}|{'H' if nd.is_high else 'L'}|{nd.price:g}")
        else:
            used.add(best)
    for i, (lt, lis_hi, lp, ltext) in enumerate(dots):
        if i not in used:
            missed.append(f"{lt:%m-%d}|{'H' if lis_hi else 'L'}|{lp:g}|{ltext or '-'}")
    return len(dots) - len(missed), len(dots), missed, extra


def match_labels_all_types(nodes, dots, max_bars_off: int = 2):
    """Variant: match H nodes against H labels OR L labels (diagnostic only — a label row's
    type column is the DOT type, and Text rows inherit it, so this should not be needed)."""
    return match_labels(nodes, dots, max_bars_off)


def node_list_debug(nodes: Sequence[Pivot]) -> List[str]:
    return [f"{p.timeframe_time:%Y-%m-%d}|{'H' if p.is_high else 'L'}|{p.price:g}" for p in nodes]
