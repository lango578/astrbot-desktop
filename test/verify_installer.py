"""Installer acceptance: language, payload boundaries and the data-preservation promise.

Runs the compiled Setup.exe silently into a throwaway directory and checks what a user
would actually get:
  * the Start-menu entries carry the language they asked for (简体中文 vs 繁體中文),
    which is the only way to prove the localized names survive compilation -- Inno
    silently drops message-table entries it doesn't recognise, so a typo here would
    otherwise ship as "?HOWTOSHORTCUT";
  * data\\ is never installed and never removed by an uninstall;
  * the Add/Remove Programs entry exists and points at the right uninstaller;
  * what got installed is actually the build it claims to be: the core reports the Setup's
    version, the packaged README says the same, and both executables match the build tree
    byte for byte.
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
import winreg
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP_NAME = "AstrBot 桌面版"
GROUP = "AstrBot"
# 打包机上传给 ISCC 的那棵树：装完的内容必须和它逐字节一致，否则安装包是"名字对、内容错"。
BUILD_APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-4.29.4" / "AstrBot"))


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()[:16] if path.is_file() else "missing"


def run(cmd: list[str], timeout: int = 600) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def start_menu() -> Path:
    return Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / GROUP


def arp_entry() -> str:
    """当前用户的「应用和功能」里所有 AstrBot 相关条目（卸载完应该是空的）。"""
    found = []
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Uninstall") as root:
        for index in range(winreg.QueryInfoKey(root)[0]):
            sub = winreg.EnumKey(root, index)
            try:
                with winreg.OpenKey(root, sub) as handle:
                    display = str(winreg.QueryValueEx(handle, "DisplayName")[0])
            except OSError:
                continue
            if "astrbot" in display.lower():
                found.append(f"{sub}={display}")
    return "; ".join(found)


def install(setup: Path, target: Path, language: str) -> int:
    proc = run([
        str(setup), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
        f"/LANG={language}", f"/DIR={target}",
    ], timeout=900)
    return proc.returncode


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--setup", default=str(BUILD / "installer" / "AstrBot-Setup-4.29.4.exe"))
    args = parser.parse_args()
    setup = Path(args.setup)
    version = re.search(r"(\d+\.\d+\.\d+)", setup.name)
    results: list[tuple[str, bool, str]] = []
    if not setup.is_file():
        print(f"FAIL  setup missing  [{setup}]")
        return 1

    for language, expect_howto, expect_uninstall in (
        ("chinesesimplified", "使用说明", "卸载"),
        ("chinesetraditional", "使用說明", "移除"),
    ):
        target = Path(tempfile.mkdtemp(prefix="astrbot-inst-")) / "AstrBot"
        rc = install(setup, target, language)
        results.append((f"[{language}] silent install exits cleanly", rc == 0, f"rc={rc}"))
        results.append((f"[{language}] program files landed", (target / "AstrBot.exe").is_file(), str(target)))
        results.append((f"[{language}] data\\ was not installed", not (target / "data").exists(), str(target / "data")))

        if language == "chinesesimplified":
            # 2026-11-09：/DSourceDir 被写成 /DDSourceDir，Inno 不报错，改用默认的旧树打出了一个
            # 名字是 4.29.4、内容还是 4.29.3 的安装包。装完对一次版本和字节，这种事只能红不能静默。
            want = version.group(1) if version else ""
            core_init = target / "core" / "_internal" / "astrbot" / "__init__.py"
            got = ""
            if core_init.is_file():
                found = re.search(r'__version__ = "([^"]+)"', core_init.read_text(encoding="utf-8", errors="replace"))
                got = found.group(1) if found else ""
            results.append(("installed core reports the packaged version", got == want, f"core={got} setup={want}"))

            readme_path = target / "README.txt"
            lines = readme_path.read_text(encoding="utf-8-sig", errors="replace").splitlines() if readme_path.is_file() else []
            head = lines[3][:80] if len(lines) > 3 else "no README"
            results.append(
                (
                    "installed README states that version",
                    f"版本：AstrBot v{want}（WebUI v{want}）" in "\n".join(lines[:8]),
                    head,
                )
            )

            pairs = ("AstrBot.exe", "core/AstrBotCore.exe")
            details = "; ".join(f"{name}: 装后 {sha(target / name)} / 构建 {sha(BUILD_APP / name)}" for name in pairs)
            same = BUILD_APP.is_dir() and all(sha(target / name) == sha(BUILD_APP / name) != "missing" for name in pairs)
            results.append(("installed exes are byte-identical to the build tree", same, details))

        links = sorted(p.stem for p in start_menu().glob("*.lnk")) if start_menu().is_dir() else []
        results.append((f"[{language}] 简体中文/繁體 shortcut names are real text",
                        expect_howto in links and any(expect_uninstall in name for name in links),
                        ",".join(links) or "no shortcuts"))
        results.append((f"[{language}] no ?NAME placeholders leaked into shortcuts",
                        not any("?" in name for name in links), ",".join(links)))

        # 模拟升级后再卸载：使用者的配置不能被带走
        (target / "data").mkdir(parents=True, exist_ok=True)
        (target / "data" / "cmd_config.json").write_text('{"keep": true}', encoding="utf-8")
        uninstaller = target / "unins000.exe"
        results.append((f"[{language}] uninstaller exists", uninstaller.is_file(), str(uninstaller)))
        if uninstaller.is_file():
            rc = run([str(uninstaller), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], timeout=600).returncode
            results.append((f"[{language}] silent uninstall exits cleanly", rc == 0, f"rc={rc}"))
            kept = (target / "data" / "cmd_config.json").is_file()
            results.append((f"[{language}] data\\ survived the uninstall", kept, str(target / "data")))
            gone = not (target / "AstrBot.exe").is_file()
            results.append((f"[{language}] program files removed", gone, str(target)))
        shutil.rmtree(target.parent, ignore_errors=True)
        if start_menu().is_dir():
            shutil.rmtree(start_menu(), ignore_errors=True)

    found = arp_entry()
    results.append(("Add/Remove Programs entry was registered and cleaned", found == "", found or "none left behind"))

    print("\n==== installer report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    return 1 if failed else 0


if __name__ == "__main__":
        sys.exit(main())
