"""سرور TCP لوکال موتور — پروتکل JSON Line (هر پیام یک خط)."""
from __future__ import annotations

import asyncio
import json
from typing import Any, Dict

from .journal import JournalWriter
from .model_adapter import DecisionModel
from .protocol import ProtocolError, parse_message


class EngineServer:
    def __init__(
        self,
        host: str,
        port: int,
        model: DecisionModel,
        journal: JournalWriter,
        debug: bool = False,
    ):
        self.host = host
        self.port = port
        self.model = model
        self.journal = journal
        self.debug = debug

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        try:
            while True:
                line = await reader.readline()
                if not line:
                    break
                raw = line.decode("utf-8", errors="replace").strip()
                if not raw:
                    continue
                try:
                    msg = parse_message(raw)
                except ProtocolError as exc:
                    await self._send(writer, {"type": "error", "message": str(exc)})
                    continue

                self.journal.write(msg)
                if self.debug:
                    print(f"[nds-engine] in : {raw[:200]}")

                if msg.get("type") == "state":
                    decision = self.model.decide(msg)
                    self.journal.write(decision)
                    if self.debug:
                        print(f"[nds-engine] out: {decision}")
                    await self._send(writer, decision)
        except (ConnectionResetError, asyncio.IncompleteReadError):
            pass
        finally:
            try:
                writer.close()
                await writer.wait_closed()
            except Exception:
                pass

    async def _send(self, writer: asyncio.StreamWriter, obj: Dict[str, Any]) -> None:
        writer.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
        await writer.drain()

    async def serve(self) -> None:
        server = await asyncio.start_server(self._handle, self.host, self.port)
        addrs = ", ".join(str(sock.getsockname()) for sock in server.sockets)
        print(f"[nds-engine] listening on {addrs} | model={self.model.name} | Ctrl+C برای توقف")
        async with server:
            await server.serve_forever()
