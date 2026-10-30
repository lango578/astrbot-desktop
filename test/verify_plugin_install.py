"""End-to-end plugin install against the packaged app.

Reproduces what the panel's install button does -- market list, then
/api/v1/plugins/install/url -- and checks the dependency install actually lands
in data\\site-packages instead of dying inside pip's truststore.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
CORE = APP / "core" / "AstrBotCore.exe"
ROOT = BUILD / "test" / "artifacts" / "plugin-root"
PORT = 6190
WANT = ("pic_toolbox", "picstaging", "aosb_music")


def call(method: str, path: str, token: str = "", body: dict | None = None):
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}{path}",
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})},
    )
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "replace")
    except Exception as exc:  # noqa: BLE001
        return 0, repr(exc)


def main() -> int:
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--boot-timeout", type=float, default=240.0)
    args = parser.parse_args()

    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)
    log_path = BUILD / "test" / "artifacts" / "plugin-install.log"

    env = dict(os.environ)
    env.update(
        {
            "ASTRBOT_ROOT": str(ROOT),
            "ASTRBOT_DASHBOARD_HOST": "127.0.0.1",
            "ASTRBOT_DASHBOARD_PORT": str(PORT),
            "PYTHONIOENCODING": "utf-8",
        }
    )
    proc = subprocess.Popen(
        [str(CORE), "--no-browser"],
        cwd=str(APP),
        env=env,
        stdout=log_path.open("wb"),
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
    )
    results: list[tuple[str, bool, str]] = []
    try:
        deadline = time.time() + args.boot_timeout
        up = False
        while time.time() < deadline:
            if proc.poll() is not None:
                break
            status, _ = call("GET", "/")
            if status == 200:
                up = True
                break
            time.sleep(1)
        results.append(("panel boots", up, f"pid={proc.pid}"))
        if not up:
            return _report(results, log_path)

        password = ""
        deadline = time.time() + 90
        while time.time() < deadline and not password:
            text = log_path.read_bytes().decode("utf-8", "replace")
            found = re.findall(r"Initial password:\s*([A-Za-z0-9._\-]{6,})", text)
            password = found[-1] if found else ""
            if not password:
                time.sleep(1)
        token = ""
        if password:
            status, body = call("POST", "/api/auth/login", body={"username": "astrbot", "password": password})
            try:
                token = (json.loads(body).get("data") or {}).get("token", "")
            except json.JSONDecodeError:
                token = ""
        results.append(("panel API login", bool(token), f"status={status if password else 'no-password'}"))
        if not token:
            return _report(results, log_path)

        status, body = call("GET", "/api/v1/plugins/market", token)
        picked: dict = {}
        if status == 200:
            try:
                market = json.loads(body)
            except json.JSONDecodeError:
                market = {}
            items: list[dict] = []

            def walk(node, key=""):
                if isinstance(node, dict):
                    for name, value in node.items():
                        if isinstance(value, dict):
                            walk(value, name)
                    if any(field in node for field in ("repo", "url", "download_url")):
                        entry = dict(node)
                        entry.setdefault("_market_key", key)
                        items.append(entry)
                elif isinstance(node, list):
                    for value in node:
                        walk(value, key)

            walk(market)
            for name in WANT:
                for item in items:
                    blob = json.dumps(item, ensure_ascii=False)
                    if name in blob:
                        picked = item
                        break
                if picked:
                    break
            if not picked and items:
                picked = items[0]
        url = str(picked.get("download_url") or picked.get("url") or picked.get("repo") or "")
        results.append(("marketplace reachable", bool(url), f"status={status} url={url[:80]}"))
        if not url:
            return _report(results, log_path)

        # 面板的市场安装走 install_method=market，标识是 "作者/名称"
        # （market_plugin_id 缺省时服务端会用 author+name 拼），把 zip 地址当 git 仓库传会被挡。
        author = str(picked.get("author") or "")
        name = str(picked.get("name") or picked.get("plugin_name") or "")
        market_id = str(picked.get("market_plugin_id") or picked.get("_market_key") or "")
        payload: dict = {"ignore_version_check": True}
        if market_id or (author and name):
            payload["install_method"] = "market"
            if market_id:
                payload["market_plugin_id"] = market_id
            payload["author"] = author
            payload["name"] = name
        else:
            payload["url"] = url
            payload["download_url"] = url
        # 安装要下载 + 跑 pip，远超一次请求的合理等待，所以后台发请求、前台轮询列表。
        marker = name or (url.rstrip("/").split("/")[-1] if url else "")
        outcome: dict = {}

        def fire() -> None:
            outcome["status"], outcome["body"] = call("POST", "/api/v1/plugins/install/url", token, payload)

        thread = threading.Thread(target=fire, daemon=True)
        thread.start()

        listed = False
        deadline = time.time() + 600
        while time.time() < deadline:
            status, body = call("GET", "/api/v1/plugins", token)
            if status == 200 and marker and marker in body:
                listed = True
                break
            if not thread.is_alive():
                break
            time.sleep(10)
        thread.join(timeout=30)
        results.append(
            (
                "plugin install request finished",
                outcome.get("status") == 200 and '"ok"' in str(outcome.get("body", "")).replace(" ", ""),
                f"payload={json.dumps(payload, ensure_ascii=False)[:110]} body={str(outcome.get('body'))[:200]}",
            )
        )
        results.append(("plugin shows up in the plugin list", listed, f"marker={marker}"))

        site_packages = ROOT / "data" / "site-packages"
        landed = site_packages.is_dir() and any(site_packages.iterdir())
        results.append(("dependencies landed in data\\site-packages", landed, str(site_packages)))

        text = log_path.read_bytes().decode("utf-8", "replace")
        truststore_crash = "_create_truststore_ssl_context" in text
        results.append(("no truststore/CA crash during install", not truststore_crash, "clean" if not truststore_crash else "still crashing"))
        pip_errors = [line for line in text.splitlines() if "PipInstallError" in line or "Failed to install dependencies" in line]
        results.append(("no pip install errors in the log", not pip_errors, (pip_errors[-1][:120] if pip_errors else "clean")))
    finally:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
        try:
            proc.wait(timeout=25)
        except subprocess.TimeoutExpired:
            proc.kill()

    return _report(results, log_path)


def _report(results, log_path: Path) -> int:
    print("\n==== plugin install report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    print(f"log: {log_path}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
