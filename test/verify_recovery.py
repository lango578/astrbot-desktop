"""Launcher self-healing acceptance.

Two layers of proof:
  A) in-process drill (AstrBot.exe --recoverytest) that injects a busy port, an
     external taskkill of the core and a corrupted cmd_config.json;
  B) the same two faults from outside the shipped binary, with a real listener
     squatting the panel port and a real taskkill, watching the panel come back.
Only loopback ports are touched; nothing here reaches the network.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
LAUNCHER = APP / "AstrBot.exe"
DATA = APP / "data"
LOG_FILE = DATA / "logs" / "astrbot.log"
CONF = DATA / "desktop.conf"
TEMP = Path(tempfile.gettempdir())
PREFERRED = 6185
FALLBACK = 6186


def run(cmd: list[str], timeout: int = 120) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def powershell(script: str) -> str:
    out = run(["powershell", "-NoProfile", "-Command", script], timeout=90)
    return (out.stdout or "").strip()


def wait_port(port: int, timeout: float, expect_open: bool = True) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.settimeout(0.5)
            if (probe.connect_ex(("127.0.0.1", port)) == 0) == expect_open:
                return True
        time.sleep(0.5)
    return False


def process_rows(name: str) -> list[tuple[int, str]]:
    out = powershell(
        "Get-CimInstance Win32_Process -Filter \"Name='%s'\" | ForEach-Object { \"$($_.ProcessId)|$($_.ExecutablePath)\" }" % name
    )
    rows: list[tuple[int, str]] = []
    for line in out.splitlines():
        pid, _, path = line.partition("|")
        pid = pid.strip()
        if pid.isdigit():
            rows.append((int(pid), path.strip()))
    return rows


def owned_pids(name: str) -> list[int]:
    root = str(APP).lower()
    return [pid for pid, path in process_rows(name) if path.lower().startswith(root)]


def count_marker(path: Path, marker: str) -> int:
    if not path.is_file():
        return -1
    return len(re.findall(re.escape(marker), path.read_text(encoding="utf-8", errors="replace")))


def main() -> int:
    parser = argparse.ArgumentParser(description="launcher recovery acceptance")
    parser.add_argument("--keep-data", action="store_true", help="leave data/ in the bundle")
    parser.add_argument("--skip-drill", action="store_true", help="only run the out-of-process phase")
    args = parser.parse_args()

    results: list[tuple[str, bool, str]] = []
    started = time.time()
    if not LAUNCHER.is_file():
        print(f"FAIL  deliverable missing  [{LAUNCHER}]")
        return 1

    # 启动器的单实例互斥是全机的：本机跑着别人的实例就没法测，先看清楚再决定，不动它的进程。
    strangers = [pid for pid, path in process_rows("AstrBot.exe") if not path.lower().startswith(str(APP).lower())]
    if strangers:
        print(
            "FAIL  another launcher holds the single-instance mutex (pid "
            + ",".join(str(p) for p in strangers)
            + ") - exit that AstrBot before running this suite"
        )
        return 2
    for pid in owned_pids("AstrBot.exe") + owned_pids("AstrBotCore.exe"):
        run(["taskkill", "/PID", str(pid), "/T", "/F"], timeout=60)

    DATA.mkdir(parents=True, exist_ok=True)
    CONF.write_text(
        "port=%d\nallow_lan=false\nopen_panel=false\nminimize_to_tray=true\nview=overview\n" % PREFERRED,
        encoding="utf-8",
    )

    if not args.skip_drill:
        proc = run([str(LAUNCHER), "--recoverytest=runall-recovery.png"], timeout=1200)
        report = TEMP / "astrbot-recovery-report.txt"
        text = report.read_text(encoding="utf-8-sig", errors="replace") if report.is_file() else ""
        lines = [line for line in text.splitlines() if line.startswith(("PASS", "FAIL", "NOTE"))]
        results.append(("drill produced a report", report.is_file(), f"rc={proc.returncode} lines={len(lines)}"))
        fails = [line for line in lines if line.startswith("FAIL")]
        results.append((f"drill: {len(lines) - len(fails)} checks green", not fails, "; ".join(f[:160] for f in fails[:3])))
        for line in fails[:6]:
            print("   " + line[:200])
        note = [line for line in lines if line.startswith("NOTE")]
        for line in note:
            print("   " + line[:200])

    launcher_proc = None
    blocker: socket.socket | None = None
    try:
        # 故障 1（进程外）：把首选项端口占住，看真二进制会不会自己让开。
        # 故意不设 SO_REUSEADDR：真占着端口的人不会给您留后路，这样才能测出体检是否可靠。
        blocker = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        blocker.bind(("0.0.0.0", PREFERRED))
        blocker.listen(1)
        launcher_proc = subprocess.Popen([str(LAUNCHER), "--minimized"])
        up = wait_port(FALLBACK, 300)
        results.append(("panel avoids the squatted port and answers on the next one", up, f"port={FALLBACK}"))
        conf_port = 0
        for line in CONF.read_text(encoding="utf-8-sig").splitlines():
            if line.startswith("port="):
                conf_port = int(line.split("=", 1)[1])
        results.append(("the new port is remembered in desktop.conf", conf_port == FALLBACK, f"port={conf_port}"))

        # 故障 2（进程外）：真把核心杀掉，面板应自己回来。
        before = max(count_marker(LOG_FILE, "AstrBot started"), 0)
        victims = owned_pids("AstrBotCore.exe")
        killed = False
        if victims:
            run(["taskkill", "/PID", str(victims[0]), "/T", "/F"], timeout=60)
            killed = wait_port(FALLBACK, 30, expect_open=False)
        results.append(("core was killed from outside", killed, f"pid={victims[:1]}"))
        back = wait_port(FALLBACK, 240)
        results.append(("launcher restarted the core by itself", back, f"port={FALLBACK} answered={back}"))
        after = max(count_marker(LOG_FILE, "AstrBot started"), 0)
        results.append(("core log shows a second boot", after > before, f"boots {before} -> {after}"))
    finally:
        if blocker is not None:
            blocker.close()
        if launcher_proc is not None:
            run(["taskkill", "/PID", str(launcher_proc.pid), "/T", "/F"], timeout=60)
            time.sleep(3)
        leftovers = powershell("Get-Process -Name AstrBot,AstrBotCore -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName }")
        results.append(("test leaves no running process", leftovers == "", f"leftovers={leftovers or 'none'}"))

        fresh_bad = sorted(p.name for p in DATA.glob("cmd_config.bad-*.json") if p.stat().st_mtime >= started)
        results.append(("no quarantined config left behind", not fresh_bad, ",".join(fresh_bad) or "none"))
        diag_dir = DATA / "diagnostics"
        fresh_diag = sorted(p.name for p in diag_dir.glob("astrbot-diag-*.txt") if p.stat().st_mtime >= started) if diag_dir.is_dir() else []
        results.append(("no diagnostics file left behind", not fresh_diag, ",".join(fresh_diag) or "none"))
        stray = [p.name for p in DATA.glob("*.tmp")] if DATA.is_dir() else []
        results.append(("no half-written config left", not stray, ",".join(stray) or "none"))

        if not args.keep_data:
            shutil.rmtree(DATA, ignore_errors=True)

    print("\n==== launcher recovery report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    print(f"elapsed={time.time() - started:.0f}s")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
