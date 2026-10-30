"""Reproduce and verify the OneBot reverse-WS session bug that makes QQ "掉线".

Symptom reported against the Android port (issues #8/#9, both closed without a
diagnosis): after the phone freezes NapCat and it reconnects, the bot looks online
but never answers again.

Mechanism, from the vendored aiocqhttp used by AstrBot's adapter:
  * `_add_wsr_api_client`    stores `_wsr_api_clients[self_id] = ws`
  * `_remove_wsr_api_client` deletes `_wsr_api_clients[self_id]` *by key*, without
    checking that the stored socket is still the one being removed.
So when a second connection with the same self_id arrives (NapCat restarted) and the
frozen first connection is cleaned up afterwards, the newcomer's entry is deleted. The
event socket is still there (that removal is identity-safe), so the panel happily shows
"已连接" while every outgoing API call has nowhere to go.

This test drives exactly that sequence over loopback:
  A connects (silent) -> B connects (same self_id) -> A closes -> send an event via B
and requires AstrBot to answer through B. Unpatched it fails; with the identity-safe
session handling it passes. Nothing leaves this machine.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import shutil
import socket
import subprocess
import sys
import time
from pathlib import Path

import websockets

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
CORE = APP / "core" / "AstrBotCore.exe"
ROOT = BUILD / "test" / "artifacts" / "onebot-recovery-root"
LOG = BUILD / "test" / "artifacts" / "onebot-recovery.log"
PANEL = 6197
WS_PORT = 6198
SELF_ID = "10001"
USER_ID = 12345
TOKEN = "recovery-token"


def headers() -> dict:
    return {
        "X-Client-Role": "universal",
        "X-Self-ID": SELF_ID,
        "Authorization": f"Bearer {TOKEN}",
    }


def message_event(text: str) -> str:
    return json.dumps(
        {
            "time": int(time.time()),
            "self_id": int(SELF_ID),
            "post_type": "message",
            "message_type": "private",
            "sub_type": "friend",
            "message_id": 4001,
            "user_id": USER_ID,
            "group_id": 0,
            "sender": {"user_id": USER_ID, "nickname": "Tester", "card": ""},
            "message": [{"type": "text", "data": {"text": text}}],
            "raw_message": text,
            "font": 0,
        }
    )


def write_platform() -> None:
    ini = ROOT / "qq.ini"
    ini.write_text(
        "[platform]\n"
        "type=aiocqhttp\n"
        "id=onebot_recovery\n"
        "name=OneBot 掉线验收\n"
        "enable=true\n"
        f"ws_reverse_host=127.0.0.1\n"
        f"ws_reverse_port={WS_PORT}\n"
        f"ws_reverse_token={TOKEN}\n"
        "[/platform]\n",
        encoding="utf-8",
    )
    proc = subprocess.run(
        [str(CORE), "--platform-file", str(ini)],
        cwd=str(APP),
        env={**os.environ, "ASTRBOT_ROOT": str(ROOT), "PYTHONIOENCODING": "utf-8"},
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=180,
    )
    if "PLATFORM_WRITTEN" not in (proc.stdout or ""):
        raise RuntimeError(f"写平台配置失败: rc={proc.returncode} {proc.stdout[-200:]} {proc.stderr[-200:]}")


def wait_port(port: int, timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.settimeout(0.5)
            if probe.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.5)
    return False


async def answer(ws, seconds: float) -> int:
    """扮演 NapCat：应答 AstrBot 发来的 API 调用，返回看到的出站消息调用数。"""
    sent = 0
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.1, deadline - time.monotonic()))
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            break
        try:
            frame = json.loads(raw)
        except (TypeError, json.JSONDecodeError):
            continue
        if "action" in frame:
            if frame["action"] in {"send_msg", "send_private_msg", "send_group_msg"}:
                sent += 1
            await ws.send(
                json.dumps({"status": "ok", "retcode": 0, "data": {"message_id": 2002}, "echo": frame.get("echo")})
            )
    return sent


async def drill() -> tuple[list[tuple[str, bool, str]], list[str]]:
    results: list[tuple[str, bool, str]] = []
    notes: list[str] = []
    url = f"ws://127.0.0.1:{WS_PORT}/ws"

    # A：连上后一声不吭，就是被安卓冻结的 NapCat。
    a = await websockets.connect(url, max_size=None, additional_headers=headers())
    results.append(("first (frozen) connection accepted", True, "A open"))
    await asyncio.sleep(1.0)

    # B：NapCat 重启后带着同一个 self_id 重连。
    b = await websockets.connect(url, max_size=None, additional_headers=headers())
    results.append(("replacement connection accepted while the stale one lingers", True, "B open"))
    await asyncio.sleep(1.5)

    # 旧连接此刻才死掉（TCP 被回收）：它的清理不能把 B 的登记抹掉。
    await a.close()
    await asyncio.sleep(1.5)

    await b.send(message_event("/help"))
    answered = await answer(b, 40.0)
    results.append(
        (
            "the new connection still receives API calls after the stale one dies",
            answered > 0,
            f"outgoing calls on B = {answered}",
        )
    )
    await b.close()
    return results, notes


def install_plugin(src: Path) -> None:
    """把插件装进本次使用的数据目录，证明「只靠插件」也能修好。"""
    target = ROOT / "data" / "plugins" / src.name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(src, target, dirs_exist_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--boot-timeout", type=float, default=300.0)
    parser.add_argument("--plugin", default="", help="把这个目录当插件装进 data\\plugins 再测")
    args = parser.parse_args()

    if not CORE.is_file():
        print(f"FAIL  frozen core missing  [{CORE}]")
        return 1

    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)
    if args.plugin:
        src = Path(args.plugin).resolve()
        if not (src / "main.py").is_file():
            print(f"FAIL  插件目录里没有 main.py  [{src}]")
            return 1
        install_plugin(src)
    write_platform()

    env = {
        **os.environ,
        "ASTRBOT_ROOT": str(ROOT),
        "ASTRBOT_DASHBOARD_HOST": "127.0.0.1",
        "ASTRBOT_DASHBOARD_PORT": str(PANEL),
        "PYTHONIOENCODING": "utf-8",
    }
    handle = LOG.open("wb")
    proc = subprocess.Popen(
        [str(CORE), "--no-browser"],
        cwd=str(APP),
        env=env,
        stdout=handle,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
    )
    results: list[tuple[str, bool, str]] = []
    notes: list[str] = []
    try:
        listening = wait_port(WS_PORT, args.boot_timeout)
        results.append(("core boots with the aiocqhttp adapter listening", listening, f"port={WS_PORT}"))
        if listening and args.plugin:
            text = LOG.read_bytes().decode("utf-8", "replace")
            results.append((
                # 不写死插件名：上一版写的是 "Failed to load plugin onebot_recovery"，
                # 而核心打的可能是模块名、也可能是中文名，名字一变这条断言就悄悄永远为真。
                "the supplied plugin loads cleanly (not just its import side effect)",
                "Failed to load plugin" not in text,
                "有 Failed to load plugin" if "Failed to load plugin" in text else "clean",
            ))
        if listening:
            results, notes = asyncio.run(asyncio.wait_for(drill(), timeout=180))
    finally:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
        try:
            proc.wait(timeout=25)
        except subprocess.TimeoutExpired:
            proc.kill()
        handle.close()
        shutil.rmtree(ROOT, ignore_errors=True)

    print("\n==== onebot session-recovery report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    for note in notes:
        print("NOTE  " + note)
    print(f"log: {LOG}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
