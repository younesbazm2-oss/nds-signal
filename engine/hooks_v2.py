"""NDS hook tracker v2 (stdlib only). Replaces the old engine/hooks.py matcher.

Spec: CLAUDE.md section 14. Incremental: feed confirmed nodes in time order with on_node().
Nothing here looks at future nodes.

Vocabulary
  node   = nodes.Pivot (index, price, is_high, timeframe_time), alternating H/L.
  hook   = one T (start) plus two numbered sides.
           pos hook: T is a LOW.  impulse side = peaks (ascending), counter side = valleys (descending).
           neg hook: T is a HIGH. impulse side = valleys (descending), counter side = peaks (ascending).
  Numbering (book p14): per side, independent, anchored. The first node of a side after T is 1.
           A later node gets the next number only if it beats the last numbered node of that side
           by more than tol. Otherwise it stays unnumbered. Max 3 per side.
  Labels: peaks use prefix N, valleys use prefix S (always, for both hook directions).
  Failure: pos hook fails when a valley goes below T - tol; neg hook fails when a peak goes above T + tol.
  F:  depth-0 hook with 3 impulse nodes: the first later impulse-side node that beats node 3 is F.
      It spawns the opposite hook with T = F. A non-beating one is only logged as nd_candidate.
  Closing: the 3rd counter node closes the hook. fraction = retracement of the T->node(last impulse) leg.
           level_ok = |fraction - retrace| <= close_band.
  Spawn rules (T candidates):
      first   : the very first node
      S-d     : a node beyond the previous `spawn_lookback` same-type nodes (valley lower / peak higher)
      child   : counter node 2 of a depth-0 hook starts a depth-1 (micro) hook with T = that node
      F       : the F node starts the opposite hook
      closure : the closing node starts a new same-direction hook
      failure : the node that broke T starts a new same-direction hook (depth-0 parents only)
  Duplicates (same direction and same T) are never created twice.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

NAN = float("nan")


@dataclass(frozen=True)
class HookConfig:
    tick_size: float = 0.25
    tol_ticks: int = 2
    retrace: float = 0.864
    close_band: float = 0.05        # fraction of the T->end leg
    min_each_side: int = 2          # numbered nodes needed on BOTH sides to show a hook
    spawn_lookback: int = 3         # 0 disables rule S-d

    @property
    def tol(self) -> float:
        return self.tol_ticks * self.tick_size


@dataclass
class Hook:
    hid: int
    direction: str                  # 'pos' | 'neg'
    t: object                       # Pivot
    depth: int = 0                  # 0 macro, 1 micro (child)
    reason: str = "first"
    imp: list = field(default_factory=list)
    ctr: list = field(default_factory=list)
    f: Optional[object] = None
    nd: Optional[object] = None
    state: str = "forming"          # forming | closed | failed
    kind: str = "?"                 # A | B | ?
    level: float = NAN
    fraction: float = NAN
    level_ok: Optional[bool] = None
    closed_at: Optional[object] = None
    failed_by: Optional[object] = None

    @property
    def imp_is_high(self) -> bool:
        return self.direction == "pos"

    def last_index(self) -> int:
        idx = [self.t.index] + [n.index for n in self.imp] + [n.index for n in self.ctr]
        if self.f is not None:
            idx.append(self.f.index)
        return max(idx)

    def span(self) -> int:
        return self.last_index() - self.t.index


class HookTracker:
    def __init__(self, cfg: HookConfig = HookConfig()):
        self.cfg = cfg
        self.hooks: List[Hook] = []
        self.nodes: List[object] = []
        self.events: List[dict] = []
        self._next_id = 1

    # ------------------------------------------------------------------ public
    def on_node(self, node) -> List[dict]:
        ev: List[dict] = []
        spawn: List[Tuple[str, object, int, str]] = []

        for h in list(self.hooks):
            if h.state != "forming":
                continue
            if self._violates(h, node):
                h.state = "failed"
                h.failed_by = node
                ev.append(self._ev("hook_failed", h, node))
                if h.depth == 0:
                    spawn.append((h.direction, node, 0, "failure"))
                continue
            self._feed(h, node, ev, spawn)

        for direction, t, depth, reason in spawn:
            self._spawn(direction, t, depth, reason, ev)

        if self.cfg.spawn_lookback > 0 or not self.nodes:
            self._spawn_by_extreme(node, ev)

        self.nodes.append(node)
        self.events.extend(ev)
        return ev

    def recognized(self) -> List[Hook]:
        m = self.cfg.min_each_side
        out = []
        for h in self.hooks:
            if h.state == "failed":
                continue
            if len(h.imp) < m or len(h.ctr) < m:
                continue
            if h.state == "closed" and not h.level_ok:
                continue                      # closed away from the 86.4% level: not an NDS hook
            out.append(h)
        return out

    def labelable(self) -> List[Hook]:
        """Hooks whose numbered sides get labels. min_each_side still gates labeling:
        a hook with only one numbered side (e.g. a fresh failure/extreme restart) must
        not stamp its first node while the real hook is still forming — that stole the
        bump/unnumbered nodes in the reference data."""
        return self.recognized()

    def labels(self) -> Dict[int, dict]:
        """node index -> {'primary': (label, hook_id, role), 'secondary': [...]}.
        Primary owner: lower depth first (macro before micro), then larger span, then earlier T."""
        claims: Dict[int, List[Tuple[Tuple[int, int, int], str, int, str]]] = {}
        for h in self.labelable():
            role = "macro" if h.depth == 0 else "micro"
            key = (h.depth, -h.span(), h.t.index)
            for k, n in enumerate(h.imp, 1):
                claims.setdefault(n.index, []).append((key, self._prefix(n) + str(k), h.hid, role))
            for k, n in enumerate(h.ctr, 1):
                claims.setdefault(n.index, []).append((key, self._prefix(n) + str(k), h.hid, role))
            if h.f is not None:
                claims.setdefault(h.f.index, []).append((key, "F", h.hid, role))
        out: Dict[int, dict] = {}
        for idx, lst in claims.items():
            lst.sort(key=lambda c: c[0])
            out[idx] = {"primary": lst[0][1:], "secondary": [c[1:] for c in lst[1:]]}
        return out

    def snapshot(self) -> tuple:
        return tuple(
            (h.hid, h.direction, h.t.index, h.depth, h.state,
             tuple(n.index for n in h.imp), tuple(n.index for n in h.ctr),
             h.f.index if h.f is not None else None)
            for h in self.hooks)

    # ----------------------------------------------------------------- internal
    @staticmethod
    def _prefix(node) -> str:
        return "N" if node.is_high else "S"

    def _ev(self, name: str, h: Hook, node, **info) -> dict:
        d = {"event": name, "hook": h.hid, "index": node.index}
        d.update(info)
        return d

    def _violates(self, h: Hook, node) -> bool:
        tol = self.cfg.tol
        if h.direction == "pos":
            return (not node.is_high) and node.price < h.t.price - tol
        return node.is_high and node.price > h.t.price + tol

    def _feed(self, h: Hook, node, ev: list, spawn: list) -> None:
        tol = self.cfg.tol
        if node.is_high == h.imp_is_high:
            side, seq, ascending = "imp", h.imp, (h.direction == "pos")
        else:
            side, seq, ascending = "ctr", h.ctr, (h.direction == "neg")
            if not h.imp:
                return                         # counter node before impulse node 1: ignore

        def beats(last) -> bool:
            return node.price > last.price + tol if ascending else node.price < last.price - tol

        if not seq:
            seq.append(node)
            ev.append(self._ev("numbered", h, node, side=side, number=1))
            self._update_level(h)
            return

        if len(seq) < 3:
            if beats(seq[-1]):
                seq.append(node)
                ev.append(self._ev("numbered", h, node, side=side, number=len(seq)))
                self._update_level(h)
                if side == "ctr" and len(seq) == 2 and h.depth == 0:
                    spawn.append((h.direction, node, 1, "child"))
                if side == "ctr" and len(seq) == 3:
                    self._close(h, ev, spawn)
            return

        # side == 'imp' with 3 numbered nodes: F or ND candidate (depth-0 only, decided once)
        if side == "imp" and h.depth == 0 and h.f is None and h.nd is None:
            if beats(seq[-1]):
                h.f = node
                ev.append(self._ev("F", h, node))
                spawn.append(("neg" if h.direction == "pos" else "pos", node, 0, "F"))
            else:
                h.nd = node
                ev.append(self._ev("nd_candidate", h, node))

    def _update_level(self, h: Hook) -> None:
        if len(h.imp) < 2:
            return
        e = h.imp[-1]
        r = self.cfg.retrace
        if h.direction == "pos":
            h.level = e.price - r * (e.price - h.t.price)
        else:
            h.level = e.price + r * (h.t.price - e.price)

    def _close(self, h: Hook, ev: list, spawn: list) -> None:
        e, c = h.imp[-1], h.ctr[2]
        leg = (e.price - h.t.price) if h.direction == "pos" else (h.t.price - e.price)
        if leg <= 0:
            h.state = "failed"
            ev.append(self._ev("hook_failed", h, c, why="non-positive leg"))
            return
        frac = ((e.price - c.price) if h.direction == "pos" else (c.price - e.price)) / leg
        h.fraction = frac
        h.level_ok = abs(frac - self.cfg.retrace) <= self.cfg.close_band
        h.kind = "A" if len(h.imp) == 3 else ("B" if len(h.imp) == 2 else "?")
        h.state = "closed"
        h.closed_at = c
        ev.append(self._ev("hook_closed", h, c, hook_kind=h.kind, fraction=round(frac, 4),
                           level_ok=h.level_ok))
        if h.depth == 0:
            spawn.append((h.direction, c, 0, "closure"))

    def _spawn(self, direction: str, t, depth: int, reason: str, ev: list) -> None:
        for h in self.hooks:
            if h.direction == direction and h.t.index == t.index:
                return
        h = Hook(hid=self._next_id, direction=direction, t=t, depth=depth, reason=reason)
        self._next_id += 1
        self.hooks.append(h)
        ev.append(self._ev("hook_spawn", h, t, direction=direction, depth=depth, reason=reason))

    def _spawn_by_extreme(self, node, ev: list) -> None:
        k = self.cfg.spawn_lookback
        same = [n for n in self.nodes if n.is_high == node.is_high][-k:] if k > 0 else []
        if node.is_high:
            beyond = (not same) or node.price > max(n.price for n in same)
            direction = "neg"
        else:
            beyond = (not same) or node.price < min(n.price for n in same)
            direction = "pos"
        if not beyond:
            return
        # T = the hook-start node, not the extreme itself (T is the start of the hook,
        # the node before N1 — e.g. the 04-24 low 7208.25, not the deepest 04-01
        # extreme). The DEEPEST OPPOSITE node between this extreme and the previous
        # node in the chain is the hook start; fallback: the extreme node itself.
        t = self._hook_start(node, direction)
        self._spawn(direction, t, 0, "first" if not self.nodes else "extreme", ev)

    def _hook_start(self, node, direction: str):
        """For an extreme-spawned hook: T = the most extreme opposite node strictly
        between the PREVIOUS NODE in the chain and this node (a valley for a pos hook,
        a peak for neg). Fallback: the node itself (no opposite node between)."""
        opp_high = (direction == "pos")          # pos hook T is a low
        prev = self.nodes[-1] if self.nodes else None
        if prev is None or prev.index >= node.index:
            return node
        cands = [n for n in self.nodes
                 if prev.index < n.index < node.index and n.is_high == opp_high]
        if not cands:
            return node
        if opp_high:
            return max(cands, key=lambda n: n.price)
        return min(cands, key=lambda n: n.price)
