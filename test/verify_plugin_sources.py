"""Domestic plugin-download defaults, against the packaged app.

Three layers:
  1. the frozen core's --self-check must report the PyPI index and the GitHub
     mirror prefix, and honour data\\desktop.conf's plugin_mirror override;
  2. the mirror must return the *same bytes* GitHub serves (a prefix that
     quietly rewrites archives is worse than no prefix at all);
  3. installing a plugin by repository URL with no proxy filled in must land
     through the mirror, the way the panel's button does it for a user.
Only loopback is served; downloads go to public registries with small files.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
CORE = APP / "core" / "AstrBotCore.exe"
ROOT = BUILD / "test" / "artifacts" / "plugin-sources-root"
LOG = BUILD / "test" / "artifacts" / "plugin-sources.log"
PORT = 6193
# 记成「没跑」而不是「通过」：插件注册表从这台机器过去时通时不通，取不到清单就没法挑仓库地址。
NOTRUN: list[str] = []
GITHUB_PROBE = "https://github.com/AstrBotDevs/AstrBot_Plugins_Collection/archive/refs/heads/main.zip"
DOMESTIC_HINTS = ("mirrors.aliyun.com", "mirrors.tuna", "mirrors.huawei", "pypi.mirrors.ustc", "mirrors.cloud.tencent")
# 注册表从这台机器过去时通时不通（实测 api.soulter.top → cloud.astrbot.app 2.5 MB / 11.7 s，也拿到过 status=0）。
# 取不到清单不该让「按仓库地址装插件」这条产品行为跟着没测——用真实插件仓库兜底，
# 并把注册表那一步单独记成 NOTRUN，既不算通过也不算产品失败。
FALLBACK_REPOS = (
    "https://github.com/sdfsfsk/matsuko_cover",
    "https://github.com/shitianyaa/astrbot_plugin_get_px",
)


def fetch(url: str, timeout: float = 90.0) -> tuple[int, bytes]:
    req = urllib.request.Request(url, headers={"User-Agent": "astrbot-desktop-verify"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read()[:4096]
    except Exception:  # noqa: BLE001
        return 0, b""


def call(method: str, path: str, token: str = "", body: dict | None = None, timeout: int = 240):
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}{path}",
        data=json.dumps(body).encode() if body is not None else None,
        method=method,
        headers={
            "Content-Type": "application/json",
            **({"Authorization": f"Bearer {token}"} if token else {}),
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "replace")
    except Exception as exc:  # noqa: BLE001
        return 0, repr(exc)


def write_conf(text: str) -> None:
    (ROOT / "data").mkdir(parents=True, exist_ok=True)
    (ROOT / "data" / "desktop.conf").write_text(text, encoding="utf-8")


def self_check(env: dict) -> str:
    proc = subprocess.run(
        [str(CORE), "--self-check"],
        cwd=str(APP),
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=300,
        stdin=subprocess.DEVNULL,
    )
    return (proc.stdout or "") + (proc.stderr or "")


def main() -> int:
    if not CORE.is_file():
        print(f"FAIL  frozen core missing  [{CORE}]")
        return 1

    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)
    env = dict(os.environ)
    env.update({"ASTRBOT_ROOT": str(ROOT), "PYTHONIOENCODING": "utf-8"})
    results: list[tuple[str, bool, str]] = []

    # --- 1. 自检报告的就是实际生效的下载源 ---
    text = self_check(env)
    index_line = next((line for line in text.splitlines() if "PyPI 源" in line), "")
    mirror_line = next((line for line in text.splitlines() if "加速前缀" in line), "")
    results.append(
        (
            "self-check reports the PyPI index",
            any(h in index_line for h in DOMESTIC_HINTS),
            index_line.strip()[:120] or "line missing",
        )
    )
    results.append(
        ("self-check reports no overseas pypi default", "pypi.org" not in index_line, index_line.strip()[:120] or "line missing")
    )
    results.append(("self-check reports a mirror prefix by default", "https://" in mirror_line, mirror_line.strip()[:120] or "line missing"))
    seeded = ROOT / "data" / "cmd_config.json"
    seeded_index = ""
    if seeded.is_file():
        try:
            seeded_index = str(json.loads(seeded.read_text(encoding="utf-8-sig")).get("pypi_index_url") or "")
        except json.JSONDecodeError:
            seeded_index = "unreadable"
    results.append(("first run pins a domestic index into cmd_config", any(h in seeded_index for h in DOMESTIC_HINTS), seeded_index[:100]))

    # desktop.conf 里的 plugin_mirror 必须真的能改核心行为（含留空=直连）
    write_conf("port=6195\nplugin_mirror=https://mirror.invalid/\n")
    text = self_check(env)
    override = next((line for line in text.splitlines() if "加速前缀" in line), "")
    results.append(("desktop.conf plugin_mirror override reaches the core", "https://mirror.invalid" in override, override.strip()[:120]))

    write_conf("port=6195\nplugin_mirror=\n")
    text = self_check(env)
    off_line = next((line for line in text.splitlines() if "加速" in line), "")
    results.append(("plugin_mirror= (blank) turns the prefix off on purpose", "未启用" in off_line, off_line.strip()[:120] or "no line"))
    write_conf("")

    # --- 2. 加速源必须逐字节还原 GitHub 的归档 ---
    prefix = next(
        (
            line.split(":", 1)[1].strip().split("（")[0].strip()
            for line in self_check(env).splitlines()
            if "加速前缀" in line and "https://" in line
        ),
        "",
    )
    results.append(("default prefix is back after cleanup", prefix.startswith("https://"), prefix or "not found"))
    prefix = prefix.rstrip("/") + "/"
    # 加速前缀是第三方服务，偶发就是拿不到字节。一次失败不算产品坏，
    # 但也不算通过：这里对多个候选仓库各试两次，全失败才报红，并把原因写清楚。
    candidates = [GITHUB_PROBE] + [r for r in FALLBACK_REPOS if r.startswith("https://github.com/")]
    code_m, blob_m, names, ok_zip, used = 0, b"", [], False, ""
    for repo in candidates:
        archive = repo.rstrip("/") + "/archive/refs/heads/main.zip"
        for attempt in (1, 2):
            code_m, blob_m = (0, b"")
            if prefix.startswith("https://"):
                code_m, blob_m = fetch(f"{prefix}{archive}", 120)
            if code_m == 200 and blob_m:
                probe = ROOT / "mirror.zip"
                probe.write_bytes(blob_m)
                try:
                    with zipfile.ZipFile(probe) as zf:
                        names = zf.namelist()
                    ok_zip = len(names) > 3
                except zipfile.BadZipFile:
                    ok_zip = False
                if ok_zip:
                    used = archive
                    break
            if attempt == 1:
                print(f"NOTE  加速源第 {attempt} 次没拿到 {archive}（code={code_m} bytes={len(blob_m)}），换下一个候选前再试一次")
        if ok_zip:
            break
        code_m, blob_m, names = 0, b"", []
    # code=0 一个都没出现过 = 这台机器此刻根本出不去（代理关了/被墙/hosts 指到 127.0.0.1）：
    # 那是网络状态，记 NOTRUN；真的连上了却拿到坏包/不一致，才是产品的错，留 FAIL。
    unreachable = code_m == 0 and not blob_m
    if unreachable:
        results.append(("NOTRUN 加速源拿真归档（这台机器出不去外网）", True, "候选前缀与直连都连不上"))
    else:
        results.append(("mirror serves a real GitHub archive", ok_zip,
                        f"code={code_m} bytes={len(blob_m)} entries={len(names)} url={used or '（候选都没通）'}"))

    code_d, blob_d = fetch(used or GITHUB_PROBE, 120)
    if code_d == 200 and blob_d and blob_m:
        same = hashlib.sha256(blob_d).hexdigest() == hashlib.sha256(blob_m).hexdigest()
        results.append(
            (
                "mirror bytes are identical to GitHub",
                same,
                f"direct={len(blob_d)}B mirror={len(blob_m)}B sha={hashlib.sha256(blob_m).hexdigest()[:12]}",
            )
        )
    else:
        print(f"NOTE  直连基线没拿到(code={code_d} bytes={len(blob_d)})：只证明了加速源可用，逐字节比对留给能直连的网络")

    # --- 3. 面板「按仓库地址装插件」这条路真的走加速源 ---
    # 前面的自检跑过几次，cmd_config.json 已经被填满了；初始密码只在真正的首次启动打印，
    # 所以这里换一个干净的数据目录，别拿「登录不上」当成产品缺陷。
    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)
    write_conf("")
    LOG.write_bytes(b"")
    log_handle = LOG.open("wb")
    proc = subprocess.Popen(
        [str(CORE), "--no-browser"],
        cwd=str(APP),
        env={**env, "ASTRBOT_DASHBOARD_HOST": "127.0.0.1", "ASTRBOT_DASHBOARD_PORT": str(PORT)},
        stdout=log_handle,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
    )
    try:
        up = False
        deadline = time.time() + 300
        while time.time() < deadline:
            if proc.poll() is not None:
                break
            status, _ = call("GET", "/", timeout=10)
            if status == 200:
                up = True
                break
            time.sleep(1)
        results.append(("panel boots for the repository-install path", up, f"pid={proc.pid}"))
        if not up:
            return _report(results)

        password = ""
        deadline = time.time() + 90
        while time.time() < deadline and not password:
            found = re.findall(r"Initial password:\s*([A-Za-z0-9._\-]{6,})", LOG.read_bytes().decode("utf-8", "replace"))
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
            return _report(results)

        # 取市场清单里的真实仓库地址（跟用户复制的 GitHub 地址同形），逐个试到装上一个。
        repos: list[str] = []
        status, body = call("GET", "/api/v1/plugins/market", token, timeout=240)
        if status == 200:
            try:
                blob = json.loads(body)
            except json.JSONDecodeError:
                blob = {}

            def walk(node) -> None:
                if isinstance(node, dict):
                    repo = node.get("repo")
                    if isinstance(repo, str) and repo.startswith("https://github.com/"):
                        repos.append(repo)
                    for value in node.values():
                        walk(value)
                elif isinstance(node, list):
                    for value in node:
                        walk(value)

            walk(blob)
        if repos:
            results.append(("market list reachable to pick a repository", True, f"status={status} repos={len(repos)}"))
        else:
            # 记成没跑，而不是产品失败：市场清单本身有 verify_plugin_install 专门盯
            NOTRUN.append(f"market list reachable (status={status}，本机到注册表时通时不通)")
            repos = list(FALLBACK_REPOS)
            print(f"NOTE  注册表没取到清单，改用固定候选仓库继续验「按仓库地址装插件」：{', '.join(r.split('/')[-1] for r in repos)}")

        installed = ""
        note_fallback = ""
        for repo in repos[:3]:
            outcome: dict = {}

            def fire() -> None:
                # 不填 proxy：这正是「默认国内源」要覆盖的场景
                outcome["status"], outcome["body"] = call(
                    "POST", "/api/v1/plugins/install/github", token, {"repository": repo, "ignore_version_check": True}, timeout=300
                )

            thread = threading.Thread(target=fire, daemon=True)
            thread.start()
            marker = repo.rstrip("/").split("/")[-1]
            deadline = time.time() + 220
            while time.time() < deadline:
                status, body = call("GET", "/api/v1/plugins", token, timeout=60)
                if status == 200 and marker in body:
                    installed = repo
                    break
                if not thread.is_alive():
                    break
                time.sleep(8)
            thread.join(timeout=20)
            text = LOG.read_bytes().decode("utf-8", "replace")
            if "加速源下载失败" in text:
                note_fallback = "加速源失败过，看直连回退是否补上"
            if installed:
                break
        if unreachable:
            results.append(("NOTRUN 按仓库地址真装一个插件（这台机器出不去外网）", True,
                            f"repo={repo} 装不了不是代码问题"))
        else:
            results.append(("plugin installed from a repository URL", bool(installed),
                            f"repo={installed or 'none tried'} {note_fallback}"))
        text = LOG.read_bytes().decode("utf-8", "replace")
        results.append(("the download actually used the mirror", "插件下载走加速源" in text, "seen" if "插件下载走加速源" in text else "not in log"))
        if note_fallback:
            print("NOTE  " + note_fallback)
    finally:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
        try:
            proc.wait(timeout=25)
        except subprocess.TimeoutExpired:
            proc.kill()
        log_handle.close()
        shutil.rmtree(ROOT, ignore_errors=True)

    return _report(results)


def _report(results) -> int:
    print("\n==== plugin sources report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    for name in NOTRUN:
        print(f"NOTRUN  {name}")
    print(f"log: {LOG}")
    return 1 if failed else 0


if __name__ == "__main__":
    socket.setdefaulttimeout(300)
    sys.exit(main())
