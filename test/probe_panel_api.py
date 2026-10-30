"""Probe the panel API of the packaged app to find which features actually fail.

Starts the frozen core on loopback, logs in with the generated password, then
GETs a battery of panel endpoints and reports status + the captured tracebacks.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist" / "AstrBot"))
CORE = APP / "core" / "AstrBotCore.exe"
ROOT = BUILD / "test" / "artifacts" / "probe-root"
PORT = 6185

ENDPOINTS = (
    "/api/v1/plugins",
    "/api/v1/plugins/failed",
    "/api/v1/plugin-sources",
    "/api/v1/bots",
    "/api/v1/bot-types",
    "/api/v1/providers",
    "/api/v1/provider-sources",
    "/api/v1/system-config/schema",
    "/api/v1/config-profiles",
    "/api/v1/logs/history",
    "/api/v1/stats",
    "/api/v1/knowledge-bases",
    "/api/v1/cron/jobs",
    "/api/v1/personas",
    "/api/v1/skills",
    "/api/v1/tools",
    "/api/v1/mcp/servers",
    "/api/v1/backups",
    "/api/v1/t2i/templates",
    "/api/v1/commands",
)


def call(path: str, token: str = "") -> tuple[int, str]:
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}{path}",
        headers={"Authorization": f"Bearer {token}"} if token else {},
    )
    try:
        with urllib.request.urlopen(req, timeout=25) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "replace")
    except Exception as exc:  # noqa: BLE001
        return 0, repr(exc)


def main() -> int:
    import os
    import shutil

    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)
    env = dict(os.environ)
    env.update(
        {
            "ASTRBOT_ROOT": str(ROOT),
            "ASTRBOT_DASHBOARD_HOST": "127.0.0.1",
            "ASTRBOT_DASHBOARD_PORT": str(PORT),
            "PYTHONIOENCODING": "utf-8",
        }
    )
    log_path = BUILD / "test" / "artifacts" / "probe-root.log"
    handle = log_path.open("wb")
    proc = subprocess.Popen([str(CORE), "--no-browser"], cwd=str(APP), env=env, stdout=handle, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)

    deadline = time.time() + 200
    up = False
    while time.time() < deadline:
        if proc.poll() is not None:
            break
        status, _ = call("/")
        if status == 200:
            up = True
            break
        time.sleep(1)
    print(f"[probe] panel up: {up} (pid={proc.pid})")
    if not up:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
        print(log_path.read_bytes().decode("utf-8", "replace")[-2000:])
        return 1

    password = ""
    deadline = time.time() + 90
    while time.time() < deadline and not password:
        text = log_path.read_bytes().decode("utf-8", "replace")
        found = re.findall(r"Initial password:\s*([A-Za-z0-9._\-]{6,})", text)
        if found:
            password = found[-1]
        else:
            time.sleep(1)
    print(f"[probe] password captured: {bool(password)}")
    token = ""
    if password:
        req = urllib.request.Request(
            f"http://127.0.0.1:{PORT}/api/auth/login",
            data=json.dumps({"username": "astrbot", "password": password}).encode(),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=20) as resp:
                token = (json.loads(resp.read().decode("utf-8", "replace")).get("data") or {}).get("token", "")
        except Exception as exc:  # noqa: BLE001
            print(f"[probe] login failed: {exc}")
    print(f"[probe] token: {'yes' if token else 'NO'}")

    bad = 0
    for path in ENDPOINTS:
        status, body = call(path, token)
        ok = status == 200
        bad += 0 if ok else 1
        snippet = re.sub(r"\s+", " ", body)[:110]
        print(f"{'PASS' if ok else 'FAIL'} {status} {path:<38} {snippet}")

    subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
    try:
        proc.wait(timeout=25)
    except subprocess.TimeoutExpired:
        proc.kill()

    final = log_path.read_bytes().decode("utf-8", "replace")
    errors = [line for line in final.splitlines() if "[ERROR]" in line or "CRITICAL" in line or "ModuleNotFound" in line or "ImportError" in line]
    print(f"\n[probe] failing endpoints: {bad}/{len(ENDPOINTS)}")
    print("[probe] error lines from the run log:")
    for line in errors[-25:]:
        print("  " + line[:200])
    return 0


if __name__ == "__main__":
    sys.exit(main())
