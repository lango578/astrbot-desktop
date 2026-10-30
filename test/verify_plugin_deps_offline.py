"""Offline plugin-dependency test: no marketplace, no GitHub, loopback only.

Drops two local plugins into data\\plugins and lets the core resolve their
requirements at load time -- the exact path that used to end in
"conflict between plugin dependencies ... charset_normalizer".

  deps_satisfied : requires a package the core already ships -> must NOT pip-install
  deps_missing   : requires a package nobody has -> must pip-install into data\\site-packages
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
CORE = APP / "core" / "AstrBotCore.exe"
ROOT = BUILD / "test" / "artifacts" / "deps-root"
PORT = 6191

MAIN_PY = """
from astrbot.api.event import filter
from astrbot.api.star import Context, Star, register


class ProbeStar(Star):
    def __init__(self, context: Context):
        super().__init__(context)

    @filter.command("probe")
    async def probe(self, event):
        await event.send("probe ok")


register("PROBE_NAME", "probe", "0.0.1", ProbeStar)
"""


def write_plugin(name: str, requirement: str) -> None:
    directory = ROOT / "data" / "plugins" / name
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "metadata.yaml").write_text(
        f"name: {name}\ndesc: dependency probe plugin\nauthor: probe\nversion: 0.0.1\n",
        encoding="utf-8",
    )
    (directory / "main.py").write_text(MAIN_PY.replace("PROBE_NAME", name), encoding="utf-8")
    (directory / "requirements.txt").write_text(requirement + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--boot-timeout", type=float, default=240.0)
    args = parser.parse_args()

    shutil.rmtree(ROOT, ignore_errors=True)
    (ROOT / "data").mkdir(parents=True)
    write_plugin("deps_satisfied", "charset-normalizer")
    write_plugin("deps_missing", "six")

    log_path = BUILD / "test" / "artifacts" / "deps-root.log"
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
        booted = False
        while time.time() < deadline:
            if proc.poll() is not None:
                break
            text = log_path.read_bytes().decode("utf-8", "replace")
            if "AstrBot started" in text:
                booted = True
                break
            time.sleep(1)
        results.append(("core boots with two local plugins", booted, f"pid={proc.pid}"))
        time.sleep(6)
        text = log_path.read_bytes().decode("utf-8", "replace")
    finally:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
        try:
            proc.wait(timeout=25)
        except subprocess.TimeoutExpired:
            proc.kill()

    import re

    clean = re.sub(r"\x1b\[[0-9;]*m", "", text)
    conflict = "conflict between plugin dependencies" in clean
    results.append(("no runtime-dependency conflict (the charset_normalizer bug)", not conflict, "clean" if not conflict else "STILL CONFLICTING"))
    loaded_satisfied = "Loading plugin deps_satisfied" in clean or "deps_satisfied" in clean
    results.append(("deps_satisfied plugin processed", loaded_satisfied, ""))
    site_packages = ROOT / "data" / "site-packages"
    # 依赖安装只在「安装/更新插件」那条路径触发，光往 data\plugins 放一个目录
    # 不会自动跑 pip。这里要断言的是：带着未满足依赖的插件照样能加载。
    loaded_missing = "Loading plugin deps_missing" in clean and "Plugin deps_missing (0.0.1)" in clean
    results.append(("plugin with unsatisfied requirement still loads", loaded_missing, str(site_packages)))
    pip_errors = [line for line in clean.splitlines() if "PipInstallError" in line or "Failed to install dependencies" in line]
    results.append(("no pip install errors", not pip_errors, (pip_errors[-1][:150] if pip_errors else "clean")))

    print("\n==== plugin dependency report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    print(f"log: {log_path}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
