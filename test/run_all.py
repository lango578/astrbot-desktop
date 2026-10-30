"""Run every acceptance suite against one deliverable and print a consolidated report.

Defaults to the clean build copy so nothing here can wipe a live data folder:
verify_desktop_app.py deliberately recreates the app's data directory.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
DEFAULT_APP = BUILD / "dist-new" / "AstrBot"
ARTIFACTS = BUILD / "test" / "artifacts"


def tally(text: str) -> tuple[int, int, list[str]]:
    passed = text.count("PASS ") + len(re.findall(r"^\s{2}OK\s", text, re.M))
    failed_lines = [line.strip() for line in text.splitlines() if line.strip().startswith("FAIL")]
    return passed, len(failed_lines), failed_lines


def run_step(name: str, cmd: list[str], env: dict, read_report: Path | None = None, soft: bool = False, timeout: int = 2400) -> dict:
    """跑一步；超时/崩了也必须记账，不能把整份报告带走。"""
    try:
        proc = subprocess.run(cmd, cwd=str(BUILD), env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
        code, text = proc.returncode, (proc.stdout or "") + (proc.stderr or "")
    except subprocess.TimeoutExpired as exc:
        code = 124
        partial = exc.stdout or b""
        if isinstance(partial, bytes):
            partial = partial.decode("utf-8", "replace")
        text = f"TIMEOUT after {timeout}s\n{partial}"
    except Exception as exc:  # noqa: BLE001 - 一步炸了不该带走整轮
        code, text = 125, f"STEP CRASHED: {type(exc).__name__}: {exc}\n"
    if read_report and read_report.is_file():
        text = read_report.read_text(encoding="utf-8-sig", errors="replace") + "\n" + text
    passed, failed, failed_lines = tally(text)
    ok = code == 0
    if not ok and soft:
        ok = True
    return {
        "name": name,
        "ok": ok,
        "soft": soft,
        "rc": code,
        "passed": passed,
        "failed": failed,
        "failed_lines": failed_lines[:6],
        "text": text,
    }


def write_steps(steps: list[dict], report: Path) -> None:
    """每跑完一步就落一次盘：后面超时也不丢前面的结论。"""
    report.write_text("\n\n".join(f"### {s['name']}\n{s['text']}" for s in steps), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description="run the whole acceptance suite")
    parser.add_argument("--app", default=str(DEFAULT_APP), help="deliverable folder to test")
    parser.add_argument("--with-market", action="store_true", help="also run the marketplace install test (needs the plugin registry)")
    parser.add_argument("--allow-live-data", action="store_true",
                        help="明知这个目录在用、允许删掉它的 data\\ 重来（默认拒绝）")
    args = parser.parse_args()

    app = Path(args.app).resolve()
    core = app / "core" / "AstrBotCore.exe"
    launcher = app / "AstrBot.exe"
    if not core.is_file() or not launcher.is_file():
        print(f"[all] deliverable not found: {app}")
        return 2

    # 这套里有几步会删 data\ 重来（双击级验收要从空目录起步）。拿一个正在服役的目录跑它，
    # 等于删使用者的聊天记录与客户端 —— 2026-10-30 真就这么把使用者的 NapCat 删掉过。
    # 所以只要 data\ 里有"活过的痕迹"就停下来，让操作的人指向干净副本。
    live = app / "data"
    marks = [live / "onebot", live / "plugins", live / "config"]
    if any(m.exists() for m in marks) and not args.allow_live_data:
        print(f"[拒绝] {live} 里有使用者/客户端的东西（{', '.join(str(m.name) for m in marks if m.exists())}）。")
        print("       这套验收会删 data\\ 重跑。请指向干净副本，例如：")
        print("         venv\\Scripts\\python.exe -c \"import zipfile;pathlib\"  # 或直接：")
        print("         powershell -Command \"Expand-Archive installer\\AstrBot-4.29.3-desktop-portable.zip dist-verify\"")
        print("         venv\\Scripts\\python.exe test\\run_all.py --app dist-verify\\AstrBot")
        print("       确认这个目录可以牺牲掉，再加 --allow-live-data。")
        return 2

    env = dict(os.environ)
    env["ASTRBOT_APP_DIR"] = str(app)
    env["PYTHONIOENCODING"] = "utf-8"
    python = str(BUILD / "venv" / "Scripts" / "python.exe")
    temp = Path(tempfile.gettempdir())

    report = ARTIFACTS / "run-all.txt"
    steps: list[dict] = []

    def step(name: str, cmd: list[str], read_report: Path | None = None, soft: bool = False, timeout: int = 2400) -> dict:
        """跑一步、立刻落盘并打印：后面哪怕卡死，前面的结论也已经写进 run-all.txt。"""
        result = run_step(name, cmd, dict(env), read_report=read_report, soft=soft, timeout=timeout)
        steps.append(result)
        write_steps(steps, report)
        mark = "OK  " if result["ok"] else "BAD "
        print(f"{mark}{name}  rc={result['rc']} pass={result['passed']} fail={result['failed']}", flush=True)
        return result

    print("\n================ 整体测试（实时进度） ================")
    print(f"被测目录: {app}")
    step("核心自检 --self-check", [str(core), "--self-check"])
    step("界面自检 --uitest", [str(launcher), "--uitest=runall-ui.png"], read_report=temp / "astrbot-uitest-report.txt")
    step("核心验收 verify_app", [python, "test/verify_app.py", "--mode", "exe", "--boot-timeout", "240"])
    step("双击级验收 verify_desktop_app", [python, "test/verify_desktop_app.py"])
    step("面板功能接口冒烟 probe_panel_api", [python, "test/probe_panel_api.py"])
    step("插件依赖离线验收 verify_plugin_deps_offline", [python, "test/verify_plugin_deps_offline.py"])
    # 国内源验收要真下载归档、真装一个插件，给 30 分钟
    step("插件下载国内源验收 verify_plugin_sources", [python, "test/verify_plugin_sources.py"], timeout=1800)
    step("签名验收 verify_signature", [python, "test/verify_signature.py"])
    # 纠错验收要注入三种故障并等核心反复重启，给 25 分钟
    step("启动器纠错验收 verify_recovery", [python, "test/verify_recovery.py"],
         read_report=temp / "astrbot-recovery-report.txt", timeout=1500)
    step("内置扫码登录验收 verify_qq_bind", [python, "test/verify_qq_bind.py"], timeout=1800)
    # 掉线修复必须证明"关掉补丁会红"，否则这条断言等于没测
    step("OneBot 重连接管验收 verify_onebot_recovery", [python, "test/verify_onebot_recovery.py"], timeout=900)
    step("面板上传插件验收 verify_plugin_upload", [python, "test/verify_plugin_upload.py"], timeout=900)
    # QQ 接入客户端（个人号）这条链：拿一个假客户端把发现→起进程→面板判活→取二维码→
    # 核心画成 PNG→登录状态变 online→按 PID 杀干净整段跑一遍。本地备着已核对过摘要的
    # NapCat 包时，顺带验字节闸门（改坏一个字节必须拒装）。
    napcat_zip = BUILD / "tools" / "napcat" / "NapCat.Shell.zip"
    env["ASTRBOT_TEST_PYTHON"] = python
    env["ASTRBOT_TEST_MOCK"] = str(BUILD / "test" / "mock_napcat_client.py")
    if napcat_zip.is_file():
        env["ASTRBOT_TEST_LOCAL_ZIP"] = str(napcat_zip)
    # 「免安装包就是完整副本」这一层要真验：把刚产出的包指给修复器
    portable = BUILD / "installer" / "AstrBot-4.29.3-desktop-portable.zip"
    if portable.is_file():
        env["ASTRBOT_TEST_RESTORE_ZIP"] = str(portable)
    step("QQ 接入客户端验收 --onebottest", [str(launcher), "--onebottest=runall-ob.png"],
         read_report=temp / "astrbot-onebot-test-report.txt", timeout=900)
    # 一键修复要"注故障→修→复验"才算数：坏配置、缺目录、缺文件、占端口、孤儿核心各来一遍
    step("一键修复验收 --repairtest", [str(launcher), "--repairtest=runall-repair.png"],
         read_report=temp / "astrbot-repair-report.txt", timeout=900)

    if args.with_market:
        # 插件市场要连 Cloudflare 后面的注册表，这台机器时通时不通，默认不跑。
        step("插件市场安装验收 verify_plugin_install", [python, "test/verify_plugin_install.py"], soft=True)

    print("\n================ 整体测试 ================")
    print(f"被测目录: {app}")
    hard_failures = 0
    total_pass = 0
    for item in steps:
        mark = "OK  " if item["ok"] else "BAD "
        note = "（依赖外网，软失败）" if item["soft"] else ""
        print(f"{mark}{item['name']:<40} rc={item['rc']} pass={item['passed']} fail={item['failed']} {note}")
        for line in item["failed_lines"]:
            print("      " + line[:170])
        total_pass += item["passed"]
        if not item["ok"]:
            hard_failures += 1

    print(f"\n合计通过断言 {total_pass} 项，失败步骤 {hard_failures} 个")
    print(f"完整输出: {report}")
    return 1 if hard_failures else 0


if __name__ == "__main__":
    sys.exit(main())
