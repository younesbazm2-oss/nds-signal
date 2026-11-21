[README.md](https://github.com/user-attachments/files/35210870/README.md)
# NDS Signal System

A personal research project that implements the **NDS structure method** for futures charts: automatic swing-node detection, 1-2-3 sequence numbering on two axes (X and Y), hook classification (A / B+ / B-), and a Python decision engine that combines per-timeframe bias into a fuzzy LONG / SHORT / NONE output.

> Status: active development. The chart structure layer (nodes, numbering, hooks) is implemented and being refined against hand-drawn reference charts. The decision engine is a skeleton: it currently averages per-timeframe bias values and does not yet consume the indicator's hook structure.

---

## How it works

### 1. Nodes first

Everything is derived from swing **nodes** (peaks and troughs):

- **Heikin-Ashi mode** (default): a node is the extreme wick of a Heikin-Ashi colour run, confirmed by the first opposite-colour candle. `SwingStrength` is the minimum run length in bars.
- **Fractal mode**: a node is a centre bar more extreme than `SwingStrength` bars on each side, then confirmed by opposite Heikin-Ashi candle(s).

Candidates are filtered for minimum bar spacing, equal double tops/bottoms within a tick tolerance, and strict peak/trough alternation.

### 2. Numbering (X / Y axes)

Nodes are numbered **before** any hook exists:

- Each side (highs, lows) is counted in **1-2-3 sequences**. A node that advances past the previous same-side node takes the next number; a node that does not advance starts a new sequence at 1.
- An unfinished sequence stays open: a later node advancing past its 2 becomes that sequence's 3. A cycle ends only when its 3 forms.
- **Colour:** a forming sequence is white (X axis). Nodes turn yellow (Y axis) only when node 3 forms — the whole 1-2-3 turns yellow together.

### 3. Hooks

Hooks are derived from the numbered nodes:

- **Direction:** positive (impulse = highs) or negative (impulse = lows).
- **Kind:** `A` when the impulse side completes 1-2-3; `B+` / `B-` when the impulse side stalls at 2 (the counter side completes 3 first).
- **Chains:** H1 → H2 → R1, R2, … (a closing hook spawns the next one at its closing node).
- **BIG:** an H1+H2 pair where H2 fails at its own start without breaking H1's start.
- **86.4% level:** drawn as a symmetry guide/target. It is **diagnostic only** — hook validity is decided by node-3 formation, not by the level.

### 4. Decision engine (Python)

- `run_engine.py` starts a local TCP server (JSON Lines) on `127.0.0.1:46320`.
- The indicator can stream state to it; the engine replies with a typed decision and journals everything to `journal/journal.csv`.
- Current model `fuzzy-v1`: weighted average of per-timeframe bias values with confidence thresholds (`config/fuzzy.yaml`). Planned: calibration on the journal, then a model adapter for external models.

---

## Project layout

```
nds-signal/
├── indicator/
│   └── NDSStructure.cs     ← NinjaTrader 8 indicator (C#)
├── run_engine.py           ← engine launcher
├── engine/                 ← decision engine (Python)
│   ├── main.py             ← engine entry point
│   ├── server.py           ← local TCP server (JSON Lines)
│   ├── protocol.py         ← message validation
│   ├── gate.py             ← fuzzy gate v1
│   ├── model_adapter.py    ← model selection
│   └── journal.py          ← CSV journal
├── config/
│   ├── engine.yaml         ← port, thresholds, model, journal path
│   └── fuzzy.yaml          ← state→bias mapping, timeframe weights
├── tests/                  ← automated tests (unittest)
└── journal/                ← journal output (created automatically)
```

---

## Install the indicator (NinjaTrader 8.1)

1. Copy `indicator/NDSStructure.cs` to
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. In NinjaTrader: `New > NinjaScript Editor` → open `NDSStructure` → **F5** (Compile).
3. On a chart: right-click → `Indicators` → add **NDSStructure**.

All options (node sensitivity, colours, labels, levels, engine connection) live in the indicator's Properties window and can be saved as templates.

### Run the decision engine (optional)

```powershell
cd nds-signal
python run_engine.py
```

Then enable the engine connection group in the indicator settings so state is streamed to it.

### Run the tests

```powershell
cd nds-signal
python -m unittest discover -s tests -t . -v
```

---

## Roadmap

- [x] Project skeleton + base indicator
- [x] Full NDS structure: nodes, 1-2-3 numbering, hooks A/B, 86.4% level, symmetry
- [ ] Delta / CVD / volume profile + signal arrow from engine decisions
- [ ] Calibration of thresholds on the journal
- [ ] External model connection via `model_adapter`

---

## Notes

- Built for **NinjaTrader 8.1 (.NET 4.8)**. Calculations run **OnBarClose**.
- This is a personal research and learning project. Nothing here is financial advice.
