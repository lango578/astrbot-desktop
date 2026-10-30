"""Loopback end-to-end check for the AstrBot desktop app (source or packaged).

Checks, in order:
  1. the process boots and the core finishes startup,
  2. the bundled WebUI is served over HTTP,
  3. the panel API authenticates with the generated initial password,
  4. the QQ adapters (OneBot v11 + QQ official) are registered and exposed,
  5. a mock OneBot v11 client can push a "/help" message in and get an
     outgoing send_*_msg call back -- the actual QQ input/output path.

Nothing binds a non-loopback interface and no external traffic is generated.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
TEST = BUILD / "test"
ARTIFACTS = TEST / "artifacts"
VENV_PY = BUILD / "venv" / "Scripts" / "python.exe"
SRC = BUILD / "src" / "AstrBot-master"
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist" / "AstrBot"))

DASHBOARD_PORT = 6185
ONEBOT_PORT = 6199


def http(method: str, url: str, body: dict | None = None, token: str | None = None):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, resp.read().decode("utf-8", "replace"), resp.headers.get("Content-Type", "")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "replace"), exc.headers.get("Content-Type", "")
    except Exception as exc:  # noqa: BLE001 - report, do not crash the harness
        return 0, repr(exc), ""


def wait_port(port: int, timeout: float, proc: subprocess.Popen) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc.poll() is not None:
            return False
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.settimeout(0.5)
            if sock.connect_ex(("127.0.0.1", port)) == 0:
                return True
        time.sleep(0.5)
    return False


def seed_root(root: Path) -> None:
    """Fresh data root with a OneBot v11 listener on loopback."""
    if root.exists():
        shutil.rmtree(root)
    (root / "data").mkdir(parents=True)
    config = {
        "platform": [
            {
                "id": "qq_onebot",
                "name": "QQ OneBot v11 自检",
                "type": "aiocqhttp",
                "enable": True,
                "ws_reverse_host": "127.0.0.1",
                "ws_reverse_port": ONEBOT_PORT,
                "ws_reverse_token": "",
            }
        ],
        "providers": [],
        "t2i": False,
    }
    (root / "data" / "cmd_config.json").write_text(
        json.dumps(config, ensure_ascii=False, indent=4),
        encoding="utf-8",
    )


def start_app(mode: str, root: Path, log_path: Path) -> subprocess.Popen:
    env = os.environ.copy()
    env["ASTRBOT_ROOT"] = str(root)
    env["ASTRBOT_DASHBOARD_HOST"] = "127.0.0.1"
    env["ASTRBOT_DASHBOARD_PORT"] = str(DASHBOARD_PORT)
    env["PYTHONIOENCODING"] = "utf-8"
    if mode == "source":
        env["PYTHONPATH"] = str(SRC) + os.pathsep + env.get("PYTHONPATH", "")
        cmd = [str(VENV_PY), str(BUILD / "build" / "astrbot_desktop.py"), "--no-browser"]
        cwd = SRC
    else:
        app_dir = APP
        cmd = [str(app_dir / "core" / "AstrBotCore.exe"), "--no-browser"]
        cwd = app_dir
    handle = log_path.open("wb")
    return subprocess.Popen(cmd, cwd=str(cwd), env=env, stdout=handle, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)


def kill(proc: subprocess.Popen) -> None:
    if proc.poll() is not None:
        return
    subprocess.run(
        ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
        capture_output=True,
        text=True,
    )
    try:
        proc.wait(timeout=20)
    except subprocess.TimeoutExpired:
        proc.kill()


def extract_password(text: str) -> str | None:
    match = re.search(r"Initial password:\s*([A-Za-z0-9._\-]+)", text)
    return match.group(1) if match else None


def login(password: str) -> tuple[str | None, str]:
    for path in ("api/login", "api/auth/login"):
        status, body, _ctype = http("POST", f"http://127.0.0.1:{DASHBOARD_PORT}/{path}", {"username": "astrbot", "password": password})
        if status == 200:
            try:
                data = json.loads(body)
            except json.JSONDecodeError:
                continue
            token = (data.get("data") or {}).get("token")
            if token:
                return token, path
    return None, ""


def run_self_check(mode: str, root: Path) -> tuple[int, str]:
    """Run the app's own packaging self-check for the same target under test."""
    env = os.environ.copy()
    env["ASTRBOT_ROOT"] = str(root)
    env["PYTHONIOENCODING"] = "utf-8"
    if mode == "source":
        env["PYTHONPATH"] = str(SRC) + os.pathsep + env.get("PYTHONPATH", "")
        cmd = [str(VENV_PY), str(BUILD / "build" / "astrbot_desktop.py"), "--self-check"]
        cwd = SRC
    else:
        app_dir = APP
        cmd = [str(app_dir / "core" / "AstrBotCore.exe"), "--self-check"]
        cwd = app_dir
    proc = subprocess.run(cmd, cwd=str(cwd), env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def pe_subsystem(path: Path) -> int:
    """Return the PE subsystem (2 = GUI/windowed, 3 = console)."""
    data = path.read_bytes()[:0x400]
    e_lfanew = int.from_bytes(data[0x3C:0x40], "little")
    if data[e_lfanew : e_lfanew + 4] != b"PE\x00\x00":
        return -1
    # Optional header starts right after the 20-byte file header; Subsystem is at
    # offset 68 of the optional header for both PE32 and PE32+.
    opt = e_lfanew + 24
    return int.from_bytes(data[opt + 68 : opt + 70], "little")


def main() -> int:
    parser = argparse.ArgumentParser(description="verify astrbot desktop app")
    parser.add_argument("--mode", choices=("source", "exe"), default="exe")
    parser.add_argument("--boot-timeout", type=float, default=240.0)
    args = parser.parse_args()

    ARTIFACTS.mkdir(exist_ok=True)
    root = ARTIFACTS / f"{args.mode}-root"
    log_path = ARTIFACTS / f"{args.mode}.log"
    seed_root(root)

    results: list[tuple[str, bool, str]] = []

    if args.mode == "exe":
        app_dir = APP
        for label, path in (("launcher", app_dir / "AstrBot.exe"), ("core", app_dir / "core" / "AstrBotCore.exe")):
            subsystem = pe_subsystem(path) if path.is_file() else -1
            results.append((f"no console window for {label} (PE subsystem=GUI)", subsystem == 2, f"subsystem={subsystem}"))
    proc = start_app(args.mode, root, log_path)
    print(f"[verify] started {args.mode} pid={proc.pid}, root={root}")

    try:
        port_up = wait_port(DASHBOARD_PORT, args.boot_timeout, proc)
        results.append(("core started / dashboard port listening", port_up, f"pid={proc.pid} exit={proc.returncode}"))
        if not port_up:
            raise SystemExit(_report(results, log_path))

        status, body, _ctype = http("GET", f"http://127.0.0.1:{DASHBOARD_PORT}/")
        served_ui = status == 200 and "<html" in body.lower() and 'id="app"' in body
        results.append(("bundled WebUI served offline", served_ui, f"status={status} bytes={len(body)}"))

        # Static assets are the part that breaks when mimetypes or the dist
        # mapping is wrong, so fetch one bundle the page actually references.
        asset = re.search(r'src="(/assets/[^"]+\.js)"', body)
        asset_status, asset_ctype = 0, ""
        if asset:
            asset_status, _, asset_ctype = http("GET", f"http://127.0.0.1:{DASHBOARD_PORT}{asset.group(1)}")
        results.append(
            (
                "WebUI JS bundle served as javascript",
                asset_status == 200 and "javascript" in asset_ctype.lower(),
                f"{asset.group(1) if asset else 'no asset referenced'} -> {asset_status} {asset_ctype}",
            )
        )

        text = log_path.read_text(encoding="utf-8", errors="replace")
        password = extract_password(text)
        results.append(("initial dashboard password printed", password is not None, "found" if password else "not found"))

        token = None
        if password:
            token, path = login(password)
            results.append(("panel API login", token is not None, path))

        if token:
            # /api/v1/bot-types is what the panel's add-bot dialog reads, and it
            # lists adapters that have actually loaded -- here the seeded
            # OneBot v11 bot. The other three QQ adapters are covered by
            # --self-check, which imports them inside this same runtime.
            status, body, _ctype = http("GET", f"http://127.0.0.1:{DASHBOARD_PORT}/api/v1/bot-types", token=token)
            registered: set[str] = set()
            try:
                registered = {item.get("type", "") for item in json.loads(body)["data"]["bot_types"]}
            except (json.JSONDecodeError, KeyError, TypeError):
                pass
            results.append(
                (
                    "loaded QQ adapter shows up in the panel API",
                    "aiocqhttp" in registered,
                    f"status={status} types={sorted(registered)}",
                )
            )
        else:
            results.append(("loaded QQ adapter shows up in the panel API", False, "skipped: no login"))

        mock = subprocess.run(
            [
                str(VENV_PY),
                str(TEST / "mock_onebot_client.py"),
                "--url",
                f"ws://127.0.0.1:{ONEBOT_PORT}/ws",
                "--text",
                "/help",
                "--wait",
                "30",
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        (ARTIFACTS / f"{args.mode}-mock.out").write_text(mock.stdout + "\n" + mock.stderr, encoding="utf-8")
        qq_io = mock.returncode == 0
        tail = (mock.stdout or "").strip().splitlines()[-1] if mock.stdout else f"rc={mock.returncode}"
        results.append(("QQ message in -> AstrBot reply call out", qq_io, tail))

        final_text = log_path.read_text(encoding="utf-8", errors="replace")
        banner_ok = "Logging error in Loguru" not in final_text
        results.append(
            (
                "startup banner encoded cleanly (password visible in logs)",
                banner_ok,
                "clean" if banner_ok else "loguru UnicodeEncodeError dropped the banner",
            )
        )

        # Bundled plugins are imported from main.py on disk; a bundle that froze
        # them as bytecode only leaves star_manager skipping them silently.
        plugins_loaded = "Loading plugin builtin_commands" in final_text
        plugins_skipped = "neither main.py" in final_text
        results.append(
            (
                "bundled plugins loaded from disk",
                plugins_loaded and not plugins_skipped,
                f"loaded={plugins_loaded} skipped={plugins_skipped}",
            )
        )
    finally:
        kill(proc)

    selfcheck_path = ARTIFACTS / f"{args.mode}-selfcheck.txt"
    rc, out = run_self_check(args.mode, root)
    selfcheck_path.write_text(out, encoding="utf-8")
    results.append(
        (
            "QQ adapters import inside the target runtime (--self-check)",
            rc == 0,
            f"rc={rc} details: {selfcheck_path}",
        )
    )

    return _report(results, log_path)


def _report(results: list[tuple[str, bool, str]], log_path: Path) -> int:
    print("\n==== verify report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    print(f"log: {log_path}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
