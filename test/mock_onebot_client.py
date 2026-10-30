"""Mock OneBot v11 client: proves the packaged AstrBot's QQ input/output path.

AstrBot's aiocqhttp adapter listens for a *reverse* WebSocket, so this script
plays the role of NapCat/go-cqhttp: it connects, pushes one private text
message ("/help"), answers whatever API calls AstrBot sends back, and reports
whether an outgoing message call was observed.

Loopback only. Nothing leaves this machine.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import time

import websockets

DEFAULT_URL = "ws://127.0.0.1:6199/ws"
SELF_ID = 90210
USER_ID = 12345


def message_event(text: str) -> dict:
    return {
        "time": int(time.time()),
        "self_id": SELF_ID,
        "post_type": "message",
        "message_type": "private",
        "sub_type": "friend",
        "message_id": 1001,
        "user_id": USER_ID,
        "group_id": 0,
        "sender": {"user_id": USER_ID, "nickname": "Tester", "card": ""},
        "message": [{"type": "text", "data": {"text": text}}],
        "raw_message": text,
        "font": 0,
    }


async def run(url: str, token: str, text: str, wait_seconds: float) -> int:
    target = url
    if token:
        target = f"{url}?access_token={token}"
    calls: list[dict] = []
    # aiocqhttp keys the API channel by X-Self-ID and picks the role from
    # X-Client-Role; "universal" carries events and API calls over one socket,
    # which is what NapCat does.
    headers = {
        "X-Client-Role": "universal",
        "X-Self-ID": str(SELF_ID),
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    try:
        connect = websockets.connect(target, max_size=None, additional_headers=headers)
    except TypeError:  # websockets < 13 spelling
        connect = websockets.connect(target, max_size=None, extra_headers=headers)

    async with connect as ws:
        print(f"[mock] connected {target} as universal")
        await ws.send(json.dumps(message_event(text)))

        deadline = time.monotonic() + wait_seconds
        while time.monotonic() < deadline:
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=max(0.1, deadline - time.monotonic()))
            except asyncio.TimeoutError:
                break
            try:
                frame = json.loads(raw)
            except (TypeError, json.JSONDecodeError):
                print(f"[mock] non-json frame: {raw!r}")
                continue

            if "action" in frame:
                action = frame["action"]
                calls.append(frame)
                print(f"[mock] <-- API call: {action} {json.dumps(frame.get('params'), ensure_ascii=False)}")
                await ws.send(
                    json.dumps(
                        {
                            "status": "ok",
                            "retcode": 0,
                            "data": {"message_id": 2002},
                            "echo": frame.get("echo"),
                        }
                    )
                )
            else:
                print(f"[mock] <-- event frame: {json.dumps(frame, ensure_ascii=False)[:200]}")

    outgoing = [c for c in calls if c["action"] in {"send_msg", "send_private_msg", "send_group_msg"}]
    print(f"[mock] total api calls: {len(calls)}, outgoing messages: {len(outgoing)}")
    return 0 if outgoing else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="mock onebot v11 client")
    parser.add_argument("--url", default=DEFAULT_URL)
    parser.add_argument("--token", default="")
    parser.add_argument("--text", default="/help")
    parser.add_argument("--wait", type=float, default=25.0)
    args = parser.parse_args()
    raise SystemExit(asyncio.run(run(args.url, args.token, args.text, args.wait)))
