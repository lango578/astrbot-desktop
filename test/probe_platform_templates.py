"""Find which panel API exposes each QQ adapter template, for the README steps."""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = BUILD / "dist" / "AstrBot"
ROOT = BUILD / "test" / "artifacts" / "exe-root"
PORT = 6185
NAMES = ("aiocqhttp", "qq_official", "qq_official_webhook", "satori")
ENDPOINTS = (
    "/api/v1/bot-types",
    "/api/v1/system-config/schema",
    "/api/v1/system-config",
    "/api/v1/config-profiles/schema",
    "/api/v1/config-profiles/default",
    "/api/v1/bots",
    "/api/abconf",
    "/api/config/metadata",
)


def call(method: str, path: str, body: dict | None = None, token: str | None = None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}{path}",
        data=json.dumps(body).encode() if body else None,
        method=method,
        headers=headers,
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except Exception as exc:  # noqa: BLE001
        return 0, repr(exc)


def wait_port(timeout: float, proc: subprocess.Popen) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            return False
        status, _ = call("GET", "/")
        if status == 200:
            return True
        time.sleep(1)
    return False


def main() -> int:
    password = sys.argv[1] if len(sys.argv) > 1 else ""
    if not password:
        log = (BUILD / "test" / "artifacts" / "exe.log").read_text(encoding="utf-8", errors="replace")
        found = re.findall(r"Initial password:\s*([A-Za-z0-9._\-]+)", log)
        password = found[-1] if found else ""

    env = os.environ.copy()
    env["ASTRBOT_ROOT"] = str(ROOT)
    env["ASTRBOT_DASHBOARD_HOST"] = "127.0.0.1"
    handle = (BUILD / "test" / "artifacts" / "template-probe.log").open("wb")
    proc = subprocess.Popen(
        [str(APP / "AstrBot.exe"), "--no-browser"],
        cwd=str(APP),
        env=env,
        stdout=handle,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
    )
    try:
        if not wait_port(180, proc):
            print("[probe] app did not come up")
            return 1
        status, body = call("POST", "/api/auth/login", {"username": "astrbot", "password": password})
        token = ""
        if status == 200:
            token = (json.loads(body).get("data") or {}).get("token", "")
        if not token:
            print(f"[probe] login failed status={status} body={body[:200]}")
            return 1

        for endpoint in ENDPOINTS:
            status, body = call("GET", endpoint, token=token)
            hits = [name for name in NAMES if f'"{name}"' in body]
            print(f"{status} {len(body):>7}B  {endpoint:<36} {hits}")
    finally:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
        try:
            proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            proc.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())
