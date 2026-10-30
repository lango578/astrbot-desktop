"""Verify the exact phone flow: upload the plugin zip in the panel, restart, patched.

This is the route a phone user has (the host app only exposes the web panel), so it is
worth proving end to end rather than assuming the folder-drop test covers it:
boot -> log in -> POST /api/v1/plugins/install/upload (multipart) -> the plugin shows up
in the plugin list -> restart -> the boot log carries the patch's own confirmation line.

The desktop build's built-in patch is disabled via ASTRBOT_DISABLE_SESSION_PATCH=1 so the
only thing that can make the assertion pass is the uploaded plugin. Loopback only.
"""

from __future__ import annotations

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
import uuid
import zipfile
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
CORE = APP / "core" / "AstrBotCore.exe"
ROOT = BUILD / "test" / "artifacts" / "plugin-upload-root"
LOG = BUILD / "test" / "artifacts" / "plugin-upload.log"
PLUGIN_DIR = BUILD / "test" / "fixtures" / "upload-probe-plugin"
PLUGIN_ZIP = BUILD / "test" / "artifacts" / "upload-probe-plugin.zip"
PANEL = 6196
MARK = "[上传验收] 探针插件已加载"
LOAD_FAIL = "Failed to load plugin"


def call(method: str, path: str, token: str = "", body: bytes | None = None, content_type: str = "application/json", timeout: int = 120):
    req = urllib.request.Request(
        f"http://127.0.0.1:{PANEL}{path}",
        data=body,
        method=method,
        headers={"Content-Type": content_type, **({"Authorization": f"Bearer {token}"} if token else {})},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "replace")
    except Exception as exc:  # noqa: BLE001
        return 0, repr(exc)


def multipart(field: str, filename: str, payload: bytes) -> tuple[bytes, str]:
    boundary = "----astrbot" + uuid.uuid4().hex
    head = (
        f"--{boundary}\r\n"
        f'Content-Disposition: form-data; name="{field}"; filename="{filename}"\r\n'
        "Content-Type: application/zip\r\n\r\n"
    ).encode()
    tail = f"\r\n--{boundary}--\r\n".encode()
    return head + payload + tail, f"multipart/form-data; boundary={boundary}"


def boot() -> subprocess.Popen:
    handle = LOG.open("ab")
    env = {
        **os.environ,
        "ASTRBOT_ROOT": str(ROOT),
        "ASTRBOT_DASHBOARD_HOST": "127.0.0.1",
        "ASTRBOT_DASHBOARD_PORT": str(PANEL),
        "ASTRBOT_DISABLE_SESSION_PATCH": "1",
        "PYTHONIOENCODING": "utf-8",
    }
    proc = subprocess.Popen(
        [str(CORE), "--no-browser"], cwd=str(APP), env=env, stdout=handle, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL
    )
    deadline = time.time() + 300
    while time.time() < deadline:
        if proc.poll() is not None:
            break
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.settimeout(0.5)
            if probe.connect_ex(("127.0.0.1", PANEL)) == 0:
                return proc
        time.sleep(1)
    return proc


def wait_password(seconds: float = 90.0) -> str:
    deadline = time.time() + seconds
    while time.time() < deadline:
        found = re.findall(r"Initial password:\s*([A-Za-z0-9._\-]{6,})", LOG.read_bytes().decode("utf-8", "replace"))
        if found:
            return found[-1]
        time.sleep(1)
    return ""


def kill(proc: subprocess.Popen) -> None:
    subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
    try:
        proc.wait(timeout=25)
    except subprocess.TimeoutExpired:
        proc.kill()


def find_landed_plugin() -> Path | None:
    """在 data/plugins 下找到探针插件本体，不假设核心建出来的目录名是什么。"""
    root = ROOT / "data" / "plugins"
    marker = "探针插件已加载"
    for candidate in sorted(root.rglob("main.py")) if root.is_dir() else []:
        try:
            if marker in candidate.read_text(encoding="utf-8", errors="replace"):
                return candidate.parent
        except OSError:
            continue
    return None


def main() -> int:
    results: list[tuple[str, bool, str]] = []
    if not CORE.is_file():
        print(f"FAIL  缺核心  [{CORE}]")
        return 1
    if not PLUGIN_DIR.is_dir():
        print(f"FAIL  缺探针插件  [{PLUGIN_DIR}]")
        return 1
    # 每次现打包，避免用到旧 zip 验旧内容
    with zipfile.ZipFile(PLUGIN_ZIP, "w", zipfile.ZIP_DEFLATED) as z:
        for item in sorted(PLUGIN_DIR.rglob("*")):
            if item.is_file():
                z.write(item, f"upload-probe-plugin/{item.name}")

    LOG.unlink(missing_ok=True)
    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)

    proc = boot()
    try:
        up = proc.poll() is None
        results.append(("core boots with the built-in patch disabled", up, f"pid={proc.pid}"))
        if not up:
            return _report(results)

        password = wait_password()
        token = ""
        if password:
            status, body = call("POST", "/api/auth/login", body=json.dumps({"username": "astrbot", "password": password}).encode())
            try:
                token = (json.loads(body).get("data") or {}).get("token", "")
            except json.JSONDecodeError:
                token = ""
        results.append(("panel API login", bool(token), f"status={status if password else 'no-password'}"))
        if not token:
            return _report(results)

        body, ctype = multipart("file", PLUGIN_ZIP.name, PLUGIN_ZIP.read_bytes())
        status, text = call("POST", "/api/v1/plugins/install/upload", token, body, ctype, timeout=300)
        ok = status == 200 and '"ok"' in text.replace(" ", "")
        results.append(("panel accepts the uploaded plugin zip", ok, f"status={status} body={text[:160]}"))

        # 目录名是核心自己定的（它按 metadata 的 name 建目录，不按 zip 里的文件夹名），
        # 上一版我写死了 zip 里的名字，结果断言红的是我自己的路径、不是安装失败。
        # 所以这里不猜路径：在 data/plugins 下找内容对得上的 main.py。
        landed = find_landed_plugin()
        results.append(
            (
                "uploaded plugin landed in data/plugins",
                landed is not None,
                str(landed) if landed else f"{ROOT / 'data' / 'plugins'} 下没有带探针标记的 main.py",
            )
        )

        status, text = call("GET", "/api/v1/plugins", token)
        listed = status == 200 and "upload_probe" in text
        results.append(("plugin shows up in the plugin list", listed, f"status={status}"))
    finally:
        kill(proc)

    # 重启一次：插件要真正被加载，才算「装完就是修好的状态」
    LOG.unlink(missing_ok=True)
    proc = boot()
    try:
        time.sleep(3)
        deadline = time.time() + 240
        seen = False
        while time.time() < deadline:
            if proc.poll() is not None:
                break
            if MARK in LOG.read_bytes().decode("utf-8", "replace"):
                seen = True
                break
            time.sleep(2)
        text = LOG.read_bytes().decode("utf-8", "replace")
        results.append(("after a restart the uploaded plugin really loads", seen, MARK if seen else "日志里没有那行确认"))
        # 上一版就是栽在这里：补丁靠 import 副作用生效、测试全绿，但插件在面板里是加载失败。
        results.append(("no plugin load failure in the log", LOAD_FAIL not in text, "有 Failed to load plugin" if LOAD_FAIL in text else "clean"))
        if not seen:
            tail = LOG.read_bytes().decode("utf-8", "replace").strip().splitlines()[-6:]
            for line in tail:
                print("   | " + line[:170])
    finally:
        kill(proc)
        shutil.rmtree(ROOT, ignore_errors=True)

    return _report(results)


def _report(results) -> int:
    print("\n==== plugin upload (phone route) report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    print(f"log: {LOG}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
