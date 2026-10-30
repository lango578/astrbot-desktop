"""Assemble the AstrBot desktop deliverable.

PyInstaller writes its frozen core under --distpath (this project always passes
--distpath dist-new); this script moves it under core/, puts the native launcher at the
root so the only thing a user ever double-clicks is AstrBot.exe, and refreshes the
supporting files.

NEVER point it at a directory that holds a user's data. It deletes data\\ and logs\\
(they are run artifacts, not build products) and overwrites the program files, and the
deletion is a permanent rmtree -- no Recycle Bin. That is exactly how dist\\AstrBot\\data
on this machine (a working instance's config, chat database and plugins) was destroyed
on 2026-10-29: this script's default target used to be dist\\AstrBot, and one run forgot
the ASTRBOT_APP_DIR override. The default is now the staging tree, and any target that
still contains a data\\ directory is refused unless --allow-data is passed explicitly.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from pathlib import Path

BUILD = Path(__file__).resolve().parent
ROOT = BUILD.parent
# ASTRBOT_APP_DIR remains the way to stage somewhere else; the default is the
# throwaway staging tree, never a directory a person might be running from.
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (ROOT / "dist-new" / "AstrBot"))
CORE = APP / "core"
LAUNCHER = BUILD / "launcher" / "AstrBot.exe"
ICO = BUILD / "assets" / "astrbot.ico"
LOGO = BUILD / "assets" / "astrbot.png"
SRC = ROOT / "src" / "AstrBot-master"


def human(size: int) -> str:
    return f"{size / 1048576:.1f} MB"


def tree_size(path: Path) -> int:
    if path.is_file():
        return path.stat().st_size
    return sum(f.stat().st_size for f in path.rglob("*") if f.is_file())


def move_into_core() -> list[str]:
    moved = []
    CORE.mkdir(parents=True, exist_ok=True)
    for name in ("AstrBotCore.exe", "_internal"):
        src = APP / name
        dst = CORE / name
        if not src.exists():
            continue
        if dst.exists():
            shutil.rmtree(dst) if dst.is_dir() else dst.unlink()
        shutil.move(str(src), str(dst))
        moved.append(name)
    return moved


def copy_in(label: str, src: Path, dst: Path) -> None:
    if not src.exists():
        print(f"[skip] {label}: {src} missing")
        return
    if dst.exists():
        dst.unlink()
    shutil.copy2(src, dst)
    print(f"[copy] {label}: {human(tree_size(dst))}")


def main() -> int:
    parser = argparse.ArgumentParser(description="把冻结好的核心与启动器组装成交付目录")
    parser.add_argument(
        "--allow-data",
        action="store_true",
        help="目标目录里有 data\\ 时也继续（会永久删除它）。默认拒绝，防止对着有人正在用的实例打包",
    )
    args = parser.parse_args()

    print(f"[target] {APP}")
    if not args.allow_data:
        for name in ("data", "logs"):
            victim = APP / name
            if victim.exists():
                print(
                    f"[拒绝] {victim} 存在。这个脚本会把 data\\ 当运行残留 rmtree 掉——"
                    f"那是使用者装的东西。\n"
                    f"        确认那是构建产物、可以删，就加 --allow-data；"
                    f"想打的是别的目录，设 ASTRBOT_APP_DIR。\n"
                    f"        背景：2026-10-29 就是漏了这个检查，把 dist\\AstrBot\\data 连配置带聊天记录删掉了。"
                )
                return 2

    fresh = APP / "AstrBotCore.exe"
    moved = CORE / "AstrBotCore.exe"
    if not fresh.is_file() and not moved.is_file():
        print(f"[fail] PyInstaller output missing: {fresh} (run AstrBot.spec first)")
        return 1
    if not LAUNCHER.is_file():
        print(f"[fail] launcher not built: {LAUNCHER}")
        return 1

    if fresh.is_file():
        print("[move] into core/:", ", ".join(move_into_core()))
    else:
        print("[move] core already in place, re-staging the rest")
    copy_in("AstrBot.exe (启动器)", LAUNCHER, APP / "AstrBot.exe")
    copy_in("astrbot.ico", ICO, APP / "astrbot.ico")
    copy_in("astrbot.png", LOGO, APP / "astrbot.png")
    copy_in("README.txt", BUILD / "README.txt", APP / "README.txt")
    # 交付包里跑的是 AstrBot（AGPL-3.0）的构建产物，许可证原文和上游地址必须随包走。
    copy_in("LICENSE-AstrBot.txt", SRC / "LICENSE", APP / "LICENSE-AstrBot.txt")
    copy_in("ffmpeg.exe", BUILD / "assets" / "ffmpeg.exe", APP / "ffmpeg.exe")
    # 公钥证书跟着成品走：拿到包的人能核对这两个 exe 是不是同一次构建、有没有被改过。
    copy_in("astrbot-signing.cer", ROOT / "keys" / "astrbot-signing.cer", APP / "astrbot-signing.cer")

    # WebView2 内嵌面板要的三个文件：两个托管程序集 + 原生加载器。
    for name in (
        "Microsoft.Web.WebView2.Core.dll",
        "Microsoft.Web.WebView2.WinForms.dll",
        "WebView2Loader.dll",
    ):
        copy_in(name, BUILD / "vendor" / "webview2" / name, APP / name)

    runtime_dst = APP / "runtime"
    if runtime_dst.exists():
        shutil.rmtree(runtime_dst)
    shutil.copytree(ROOT / "py312", runtime_dst, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    print(f"[copy] runtime/: {human(tree_size(runtime_dst))}")

    # filelock leaves the lock file behind; a fresh copy must not ship one.
    (APP / "astrbot.lock").unlink(missing_ok=True)

    for stray in ("data", "logs"):
        target = APP / stray
        if target.exists():
            shutil.rmtree(target)
            print(f"[clean] removed run artifact: {stray}")

    unsigned = tree_size(APP / "AstrBot.exe")
    print(f"[done] app folder: {APP} ({human(tree_size(APP))} total)")
    print(r"[下一步] 这两个 exe 刚被覆盖成未签名副本：先跑 build\sign_dist.ps1，再编安装程序/打 zip，"
          f"否则发出去的就是没签名的（启动器 {unsigned} 字节）")
    print("[tree]", sorted(p.name for p in APP.iterdir()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
