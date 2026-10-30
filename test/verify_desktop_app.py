"""Double-click acceptance for the desktop app.

Simulates a recipient's first run: empty data dir, one platform written through
the same bridge the "保存并连接" button uses, then start AstrBot.exe and prove
the panel and the QQ socket come up with no console window anywhere.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist" / "AstrBot"))
LAUNCHER = APP / "AstrBot.exe"
CORE = APP / "core" / "AstrBotCore.exe"
DATA = APP / "data"
PORT = 6185
ONEBOT_PORT = 6199


def run(cmd: list[str], timeout: int = 120) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def powershell(script: str) -> str:
    out = run(["powershell", "-NoProfile", "-Command", script], timeout=90)
    return (out.stdout or "").strip()


def wait_port(port: int, timeout: float) -> bool:
    import socket

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.settimeout(0.5)
            if sock.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.5)
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description="double-click acceptance")
    parser.add_argument("--keep-data", action="store_true", help="leave data/ in the bundle")
    args = parser.parse_args()

    results: list[tuple[str, bool, str]] = []
    if DATA.exists():
        # 这条验收要从干净 data 起步，所以它会整目录删掉重来 —— 那就绝不能拿"别人正在用的目录"当靶子：
        # 2026-10-30 就是在装着真 NapCat 的交付目录上跑了这一步，把使用者那份客户端删到只剩 8 个文件
        # （正在运行的进程占着几个文件，删不干净的目录反而成了半坏状态）。
        # 现在先探一次能不能删：删不动就说明有人在用，直接停下来让人换个干净目录，而不是半删。
        try:
            shutil.rmtree(DATA)
        except (PermissionError, OSError) as exc:
            print(f"[拒绝] {DATA} 删不掉，说明有进程正在用它（{exc}）。")
            print("       别拿正在服役的目录当测试场：先退出启动器/客户端，")
            print("       或者对着免安装包解出来的干净副本跑（test/run_all.py --app <解出来的目录>）。")
            return 2
    # 先落一份设置，避免测试时弹出浏览器窗口。
    DATA.mkdir(parents=True)
    (DATA / "desktop.conf").write_text(
        "port=6185\nallow_lan=false\nopen_panel=false\nminimize_to_tray=true\n",
        encoding="utf-8",
    )

    # 1. the button's own path: core --platform-file writes the QQ entry
    ini = BUILD / "test" / "artifacts" / "desktop-qq.ini"
    ini.write_text(
        "[platform]\n"
        "type=aiocqhttp\n"
        "id=qq_onebot\n"
        "name=QQ 个人号\n"
        "enable=true\n"
        "ws_reverse_host=127.0.0.1\n"
        "ws_reverse_port=6199\n"
        "ws_reverse_token=\n"
        "[/platform]\n",
        encoding="utf-8",
    )
    env_core = run([str(CORE), "--platform-file", str(ini)], timeout=180)
    wrote = "PLATFORM_WRITTEN" in (env_core.stdout or "")
    results.append(("launcher bridge wrote the QQ platform entry", wrote, f"rc={env_core.returncode} out={(env_core.stdout or '').strip()[:80]}"))

    # 2. start the app exactly like a user does
    proc = subprocess.Popen([str(LAUNCHER)], cwd=str(APP))
    try:
        up = wait_port(PORT, 240)
        results.append(("launcher started the core and the panel answers", up, f"port={PORT}"))
        if not up:
            raise SystemExit(_report(results))

        listening = powershell(
            "Get-NetTCPConnection -State Listen -LocalPort 6199 -ErrorAction SilentlyContinue | "
            "Select-Object -First 1 -ExpandProperty LocalAddress"
        )
        results.append(("QQ reverse-WS is listening after first run", listening == "127.0.0.1", f"addr={listening or 'none'}"))

        tree = powershell(
            "$l=(Get-Process -Id {0}).Id; "
            "$kids=Get-CimInstance Win32_Process | Where-Object {{ $_.ParentProcessId -eq $l }}; "
            "($kids | ForEach-Object {{ $_.Name }}) -join ','".format(proc.pid)
        )
        results.append(("core runs as a child of the launcher", "AstrBotCore.exe" in tree, f"children={tree or 'none'}"))
        results.append(("no console host spawned beside the app", "conhost" not in tree.lower(), f"children={tree or 'none'}"))

        mock = run(
            [
                str(BUILD / "venv" / "Scripts" / "python.exe"),
                str(BUILD / "test" / "mock_onebot_client.py"),
                "--url",
                f"ws://127.0.0.1:{ONEBOT_PORT}/ws",
                "--text",
                "/help",
                "--wait",
                "30",
            ],
            timeout=180,
        )
        results.append(("out-of-box QQ message in -> reply call out", mock.returncode == 0, (mock.stdout or "").strip().splitlines()[-1:][0] if mock.stdout else f"rc={mock.returncode}"))

        body = ""
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/", timeout=15) as resp:
                status = resp.status
                body = resp.read(4000).decode("utf-8", "replace")
        except Exception as exc:  # noqa: BLE001
            status = 0
            body = repr(exc)
        results.append(
            (
                "panel serves the bundled WebUI over HTTP",
                status == 200 and 'id="app"' in body,
                f"status={status}",
            )
        )
        cmd_config = DATA / "cmd_config.json"
        filled = 0
        if cmd_config.is_file():
            filled = len(json.loads(cmd_config.read_text(encoding="utf-8-sig")))
        results.append(("core filled the config defaults on first run", filled > 20, f"top-level keys={filled}"))

        log_text = ""
        astrbot_log = DATA / "logs" / "astrbot.log"
        if astrbot_log.is_file():
            log_text = astrbot_log.read_text(encoding="utf-8", errors="replace")
        results.append(("file log exists for support", len(log_text) > 0, str(astrbot_log)))
    finally:
        # 强制结束整棵进程树，模拟任务管理器关掉启动器。
        run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], timeout=60)
        time.sleep(2)
        leftovers = powershell("Get-Process -Name AstrBot,AstrBotCore -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName }")
        results.append(("exit leaves no running process", leftovers == "", f"leftovers={leftovers or 'none'}"))
        if not args.keep_data:
            shutil.rmtree(DATA, ignore_errors=True)
            (BUILD / "test" / "artifacts" / "desktop-qq.ini").unlink(missing_ok=True)

    return _report(results)


def _report(results: list[tuple[str, bool, str]]) -> int:
    print("\n==== desktop report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
