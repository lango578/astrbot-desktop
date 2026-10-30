"""Probe which reverse-WS path/role combination the aiocqhttp adapter accepts."""

from __future__ import annotations

import asyncio
import json
import sys
import time

import websockets

BASE = "ws://127.0.0.1:6199"
COMBOS = [
    ("/ws", {"X-Client-Role": "universal"}),
    ("/ws", {"X-Client-Role": "Event"}),
    ("/ws", {"X-Client-Role": "event"}),
    ("/ws", {}),
    ("/ws/event", {"X-Client-Role": "event"}),
    ("/ws/api", {"X-Client-Role": "api"}),
    ("/onebot/v11/ws", {"X-Client-Role": "universal"}),
]


def event_frame(text: str) -> str:
    return json.dumps(
        {
            "time": int(time.time()),
            "self_id": 90210,
            "post_type": "message",
            "message_type": "private",
            "sub_type": "friend",
            "message_id": 1001,
            "user_id": 12345,
            "sender": {"user_id": 12345, "nickname": "Tester", "card": ""},
            "message": [{"type": "text", "data": {"text": text}}],
            "raw_message": text,
        }
    )


async def try_combo(path: str, headers: dict) -> None:
    label = f"{path} role={headers.get('X-Client-Role', '<none>')}"
    try:
        async with websockets.connect(BASE + path, additional_headers=headers, open_timeout=5) as ws:
            got = []
            await ws.send(event_frame("/help"))
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                try:
                    raw = await asyncio.wait_for(ws.recv(), timeout=max(0.1, deadline - time.monotonic()))
                except asyncio.TimeoutError:
                    break
                frame = json.loads(raw)
                if "action" in frame:
                    got.append(frame["action"])
                    await ws.send(json.dumps({"status": "ok", "retcode": 0, "data": {"message_id": 2}, "echo": frame.get("echo")}))
                else:
                    got.append(f"event:{frame.get('post_type')}")
            print(f"OPEN  {label} -> {got or 'no traffic'}")
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL  {label} -> {type(exc).__name__}: {str(exc)[:120]}")


async def main() -> None:
    for path, headers in COMBOS:
        await try_combo(path, headers)


if __name__ == "__main__":
    asyncio.run(main())
    sys.exit(0)
