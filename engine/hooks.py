"""NDS Layer 2 — hook matcher + numbering (Architecture v2, section 13 of the skill).

A PURE function over the node list: numbering = f(nodes, params). Numbers 1-3 come from the
matched hook chain, not a rolling counter.

Numbering rule inside a hook (book p14, mirrored for valleys):
  the first peak after T is 1; a peak above it is 2; a peak above that is 3.
  Lower peaks in between get no number. Valleys mirror (lower than 1 is 2, lower than 2 is 3).

Hook start T (owner's answers, skill section 11/13):
  - a node outside every existing hook, or
  - the V2 (numbered "2" valley) of an existing hook — the owner: "T is the level where the
    previous cycle closed and where the next hook starts" (the second layer after S2).
After a kind A hook reaches node 3, following nodes belong to the rally/flag phase (layer 3,
phase D) and start no new hook yet — the owner leaves post-N3 swings unnumbered.

Overlap: one node can belong to two hooks (macro and micro). The larger-span hook owns the
label; a hook that starts inside another hook is marked role="micro" and its labels are kept
separately (option ShowMicroLayer, default off).

Levels (book p22/28/29, owner chart check in skill section 10):
  positive A: level = P3 - 0.864 * (P3 - T); the hook closes at V3 (first low after P3)
              when V3 sits within the tolerance band of the level.
  negative A: mirror — level = V3 + 0.864 * (T - V3); closes at P3 (first high after V3).
  kind B has no node 3 yet: the level is provisional (computed from node 2) and stays forming.

Usage (stdlib only):
    from hooks import match_hooks, MatchConfig
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import List, Optional, Sequence

from nodes import Pivot


@dataclass(frozen=True)
class MatchConfig:
    retrace_pct: float = 0.864          # the book's cycle level
    close_band_atr14: float = 0.25      # closing tolerance band = 0.25 x ATR(14) (owner decision)
    max_t_lookback: int = 12            # unused for now (kept for the skill's K-node idea)


@dataclass
class HookMatch:
    direction: str            # 'pos' | 'neg'
    kind: str                 # 'A' | 'B'
    role: str = "macro"       # 'macro' (primary labels) | 'micro' (started inside another hook)
    t: Optional[Pivot] = None
    n1: Optional[Pivot] = None   # impulse node 1 (peak for pos, valley for neg)
    v1: Optional[Pivot] = None   # counter node 1
    n2: Optional[Pivot] = None   # impulse node 2
    v2: Optional[Pivot] = None   # counter node 2
    n3: Optional[Pivot] = None   # impulse node 3 (None for kind B)
    end: Optional[Pivot] = None  # closing node: V3 for pos A, P3 for neg A
    level864: float = float("nan")
    state: str = "forming"       # forming | closed | failed
    closed_index: Optional[int] = None
    label_bars: dict = field(default_factory=dict)        # primary labels: bar -> "N1"/"S1"/...
    label_bars_micro: dict = field(default_factory=dict)  # micro-layer labels (ShowMicroLayer)

    @property
    def span(self) -> int:
        pts = [p.index for p in (self.t, self.n1, self.v1, self.n2, self.v2, self.n3, self.end)
               if p is not None]
        return (max(pts) - min(pts)) if pts else 0

    def as_dict(self) -> dict:
        d = {
            "direction": self.direction, "kind": self.kind, "role": self.role,
            "state": self.state,
            "t_index": self.t.index if self.t else None,
            "t_price": self.t.price if self.t else None,
            "level864": round(self.level864, 4),
            "labels": [{"bar": bar, "label": lab} for bar, lab in self.label_bars.items()],
        }
        if self.n3 is not None:
            d["n3_index"] = self.n3.index
        return d


def atr14(bars: Sequence, i: int, n: int = 14) -> float:
    cnt = min(n, i + 1)
    if cnt <= 0:
        return 0.0
    return sum(bars[i - k].high - bars[i - k].low for k in range(cnt)) / cnt


def match_hooks(nodes: Sequence[Pivot], bars: Sequence,
                cfg: MatchConfig = MatchConfig()) -> List[HookMatch]:
    """Scan T candidates oldest-first. A T is accepted when it is outside every existing
    hook, or is the V2 of an existing hook (the owner's second-layer rule). After a kind A
    hook's node 3, no new hook starts (rally/flag phase — phase D will own that region)."""
    hooks: List[HookMatch] = []
    n3_barrier: Optional[int] = None   # bar index of the last completed hook's node 3

    for ti in range(len(nodes)):
        t = nodes[ti]
        if n3_barrier is not None and t.index > n3_barrier:
            continue                    # post-node-3 phase: numbering pauses (phase D)
        role = "macro"
        for h in hooks:
            span_hi = max(p.index for p in (h.n1, h.v1, h.n2, h.v2, h.n3) if p is not None)
            numbered = {p.index for p in (h.t, h.n1, h.v1, h.n2, h.v2, h.n3) if p is not None}
            if t.index in numbered:
                # a numbered member of an existing hook: only its counter node 2 may
                # start a child (second layer); any other member is already owned
                role = "micro" if (h.v2 is not None and t.index == h.v2.index) else None
                break
            if h.t.index < t.index < span_hi:
                # interior swing of a larger hook -> micro layer, not a macro hook
                role = "micro"
                break
        if role is None:
            continue

        m = _try_hook(t, nodes, ti, cfg, bars)
        if m is None:
            continue
        m.role = role
        if role == "micro":
            m.label_bars_micro = m.label_bars
            m.label_bars = {}
        hooks.append(m)
        if m.kind == "A" and m.n3 is not None:
            n3_barrier = m.n3.index

    return hooks


def _numbered_chain(nodes_after: Sequence[Pivot], impulse_high: bool) -> dict:
    """Walk the alternating node list after T applying the p14 rule.

    impulse_high=True (positive hook, T is a low): impulse nodes are peaks —
      first peak = 1, first peak above it = 2, first peak above that = 3.
      Counter nodes are valleys: first after impulse-1 = 1, first lower = 2.
    impulse_high=False (negative hook, T is a high): mirror — impulse nodes are
      valleys (first = 1, first lower = 2, first lower = 3), counter peaks ascending.
    Returns {"n1":.., "v1":.., "n2":.., "v2":.., "n3":..}.
    """
    n1 = v1 = n2 = v2 = n3 = None
    for p in nodes_after:
        if p.is_high == impulse_high:
            if n1 is None:
                n1 = p
            elif n2 is None:
                better_n2 = p.price > n1.price if impulse_high else p.price < n1.price
                if better_n2:
                    n2 = p
            elif n3 is None:
                better_n3 = p.price > n2.price if impulse_high else p.price < n2.price
                if better_n3:
                    n3 = p
        elif n1 is not None:
            if v1 is None:
                v1 = p
            elif v2 is None:
                better_v2 = p.price < v1.price if impulse_high else p.price > v1.price
                if better_v2:
                    v2 = p
    return {"n1": n1, "v1": v1, "n2": n2, "v2": v2, "n3": n3}


def _try_hook(t: Pivot, nodes: Sequence[Pivot], ti: int,
              cfg: MatchConfig, bars: Sequence) -> Optional[HookMatch]:
    """Dispatch: pos (T is a low) or neg (T is a high)."""
    if t.is_high:
        return _try_neg(t, nodes, ti, cfg, bars)
    return _try_pos(t, nodes, ti, cfg, bars)


def _try_pos(t: Pivot, nodes: Sequence[Pivot], ti: int,
             cfg: MatchConfig, bars: Sequence) -> Optional[HookMatch]:
    c = _numbered_chain(nodes[ti + 1:], impulse_high=True)
    n1, v1, n2, v2, n3 = c["n1"], c["v1"], c["n2"], c["v2"], c["n3"]
    if n1 is None or n2 is None:
        return None
    kind = "A" if n3 is not None else "B"

    span = n3.price - t.price if kind == "A" else n2.price - t.price
    if span <= 0:
        return None
    end_peak = n3 if kind == "A" else n2
    level = end_peak.price - cfg.retrace_pct * span

    m = HookMatch(direction="pos", kind=kind, t=t, n1=n1, v1=v1, n2=n2, v2=v2,
                  n3=n3, level864=level)
    if kind == "A":
        # closing node = first low after P3 (the fall from node 3)
        v3 = next((p for p in nodes[ti + 1:] if not p.is_high and p.index > n3.index), None)
        m.end = v3 if v3 is not None else n3

    _apply_labels(m, t_is_low=True)
    _apply_closing(m, cfg, bars)
    return m


def _try_neg(t: Pivot, nodes: Sequence[Pivot], ti: int,
             cfg: MatchConfig, bars: Sequence) -> Optional[HookMatch]:
    c = _numbered_chain(nodes[ti + 1:], impulse_high=False)
    w1, u1, w2, u2, w3 = c["n1"], c["v1"], c["n2"], c["v2"], c["n3"]
    if w1 is None or w2 is None:
        return None
    kind = "A" if w3 is not None else "B"

    span = t.price - w3.price if kind == "A" else t.price - w2.price
    if span <= 0:
        return None
    end_valley = w3 if kind == "A" else w2
    level = end_valley.price + cfg.retrace_pct * span

    m = HookMatch(direction="neg", kind=kind, t=t, n1=w1, v1=u1, n2=w2, v2=u2,
                  n3=w3, level864=level)
    if kind == "A":
        # closing node = first high after V3 (the rise from node 3) — book p23/29
        p3 = next((p for p in nodes[ti + 1:] if p.is_high and p.index > w3.index), None)
        m.end = p3 if p3 is not None else w3

    _apply_labels(m, t_is_low=False)
    _apply_closing(m, cfg, bars)
    return m


def _apply_labels(m: HookMatch, t_is_low: bool) -> None:
    """Impulse side gets the N prefix for positive hooks (S for negative); counter side
    gets the other. Numbers 1/1/2/2/3 per the matched chain."""
    pfx_high, pfx_low = ("N", "S") if t_is_low else ("S", "N")
    lab = m.label_bars
    for node, num in ((m.n1, "1"), (m.v1, "1"), (m.n2, "2"), (m.v2, "2"), (m.n3, "3")):
        if node is not None:
            lab[node.index] = (pfx_high if node.is_high else pfx_low) + num


def _apply_closing(m: HookMatch, cfg: MatchConfig, bars: Sequence) -> None:
    """Kind A only: the closing node near the level within the tolerance band."""
    if m.kind != "A" or m.end is None:
        return
    band = cfg.close_band_atr14 * atr14(bars, m.end.index)
    if abs(m.end.price - m.level864) <= band:
        m.state = "closed"
        m.closed_index = m.end.index
