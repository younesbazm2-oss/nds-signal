"""نقطه ورود موتور تصمیم NDS.

اجرا:
    cd nds-signal
    python -m engine.main --config config/engine.yaml
"""
from __future__ import annotations

import argparse
import asyncio
from pathlib import Path
from typing import Any, Dict

import yaml

from .gate import FuzzyGate
from .journal import JournalWriter
from .model_adapter import load_model
from .server import EngineServer

ROOT = Path(__file__).resolve().parents[1]


def load_yaml(path: Path) -> Dict[str, Any]:
    with open(path, "r", encoding="utf-8") as fh:
        return yaml.safe_load(fh) or {}


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(prog="nds-engine", description="موتور تصمیم سیستم سیگنال NDS")
    parser.add_argument("--config", default=str(ROOT / "config" / "engine.yaml"))
    args = parser.parse_args(argv)

    config_path = Path(args.config)
    if not config_path.is_absolute():
        config_path = ROOT / config_path

    cfg = load_yaml(config_path)
    cfg["fuzzy"] = load_yaml(ROOT / "config" / "fuzzy.yaml")

    journal_path = str(cfg.get("journal", {}).get("path", "journal/journal.csv"))
    if not Path(journal_path).is_absolute():
        journal_path = str(ROOT / journal_path)

    journal = JournalWriter(journal_path)
    gate = FuzzyGate(cfg)
    model = load_model(cfg, gate)

    server_cfg = cfg.get("server", {})
    server = EngineServer(
        host=str(server_cfg.get("host", "127.0.0.1")),
        port=int(server_cfg.get("port", 46320)),
        model=model,
        journal=journal,
        debug=bool(cfg.get("debug", False)),
    )

    print(f"[nds-engine] journal: {journal_path}")
    try:
        asyncio.run(server.serve())
    except KeyboardInterrupt:
        print("[nds-engine] stopped.")
    finally:
        journal.close()


if __name__ == "__main__":
    main()
