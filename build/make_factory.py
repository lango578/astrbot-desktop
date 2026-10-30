"""组装 dist-new\\AstrBot\\factory\\：包内的离线自救副本。

为什么要往包里塞这几十 MB：使用者遇到最难受的三种情况——杀毒软件把核心当可疑文件吃掉、
手动删东西删错、断网时下载器拿不到东西——在没有副本时都只能"重装/重下"，而重装本身也要网。
所以随包带一份出厂副本 + 一份逐文件摘要清单，「一键修复」才有东西可以还原，
而不是只能告诉人"你重装一次吧"。

NapCat 的两个压缩包也放这儿：上游 LICENSE 是「Limited Redistribution License」，
明文允许再分发（条件是随包带许可全文、写清来源，且不得商用）——全文落在 factory\\LICENSE-NapCat.txt。
本包是非商用分发，符合它的条件。装的时候仍然按写死的 SHA-256 逐字节核对，
副本被改过一个字节都不会被解压。

    venv\\Scripts\\python.exe build\\make_factory.py            # 组装（在 package_dist 与签名之后、打安装包之前跑）
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import sys
from pathlib import Path

BUILD = Path(__file__).resolve().parent
ROOT = BUILD.parent
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (ROOT / "dist-new" / "AstrBot"))
FACTORY = APP / "factory"
NAPCAT_SRC = ROOT / "tools" / "napcat"
LICENSE_SRC = BUILD / "vendor" / "napcat" / "LICENSE-NapCat.txt"

# 摘要只有一个真源：launcher/AstrBotOneBotInstall.cs 里那两串（装的时候核对的就是它）。
# 这里从那份源码里读，不复制粘贴——我第一版就是手抄错了一位，被自己的闸门拦下来了。
INSTALLER_CS = ROOT / "launcher" / "AstrBotOneBotInstall.cs"
NAPCAT_VERSION = "4.18.41"
VARIANT_SHA = re.compile(
    r"NewVariant\(\s*\"[a-z]+\"[^)]*?\"(NapCat\.[A-Za-z]+\.zip)\"\s*,\s*\"([0-9a-f]{64})\"")


def pinned_sha() -> dict[str, str]:
    text = INSTALLER_CS.read_text(encoding="utf-8-sig")
    found = {m.group(1): m.group(2) for m in VARIANT_SHA.finditer(text)}
    if len(found) != 2:
        raise SystemExit(f"[fail] 从 {INSTALLER_CS.name} 里只认出 {len(found)} 个摘要，期望 2 个")
    return found
# 这些小文件是"被误删/被改坏后最麻烦"的：图标丢了界面没图，签名公钥丢了验不了签
SMALL_FILES = [
    "astrbot.ico",
    "astrbot.png",
    "README.txt",
    "LICENSE-AstrBot.txt",
    "astrbot-signing.cer",
    "WebView2Loader.dll",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.WinForms.dll",
]
SKIP_TOP = {"data", "logs", "factory"}
# 测试跑完留下的残渣：这些名字出现在交付目录里就是"打包前没打扫"，一律拦下
RESIDUE = re.compile(r"repairtest|\.bad-\d|\.hidden$|\.tmp$|\.orig$|\.aside$", re.I)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app", default=str(APP), help="要组装的交付物目录")
    args = parser.parse_args()
    app = Path(args.app).resolve()
    factory = app / "factory"
    if not app.is_dir():
        print(f"[fail] 没有这个目录：{app}")
        return 1

    print(f"[factory] 目标 {factory}")
    factory.mkdir(parents=True, exist_ok=True)

    # 一、两个 exe 的出厂副本
    (factory / "bin").mkdir(exist_ok=True)
    for rel in ("AstrBot.exe", Path("core") / "AstrBotCore.exe"):
        src = app / rel
        if not src.is_file():
            print(f"[fail] 缺 {src}，先跑 package_dist")
            return 1
        dst = factory / "bin" / src.name
        shutil.copy2(src, dst)
        print(f"[copy] {rel} -> factory\\bin\\{src.name}  {sha256(dst)[:12]}…")

    # 二、小文件副本（图标/公钥/WebView2 那几个 dll）
    (factory / "small").mkdir(exist_ok=True)
    for rel in SMALL_FILES:
        src = app / rel
        if not src.is_file():
            print(f"[warn] 包里没有 {rel}，跳过这一项副本")
            continue
        shutil.copy2(src, factory / "small" / src.name)

    # 三、NapCat 离线副本：先按写死的摘要核对，对不上就不进包（宁可不带，也不带来源不明的）
    (factory / "napcat").mkdir(exist_ok=True)
    NAPCAT_SHA = pinned_sha()
    bundled = []
    for name, want in NAPCAT_SHA.items():
        src = NAPCAT_SRC / name
        if not src.is_file():
            print(f"[warn] 本机没有 tools\\napcat\\{name}，离线副本少一个（下载器仍会走线上）")
            continue
        got = sha256(src)
        if got != want:
            print(f"[fail] {name} 摘要与固定值不一致：{got[:16]}… != {want[:16]}…")
            print("       上游换过包了。要么核对新版摘要后改这里，要么别打进包。")
            return 1
        dst = factory / "napcat" / name
        shutil.copy2(src, dst)
        bundled.append(name)
        print(f"[copy] factory\\napcat\\{name}  {src.stat().st_size / 1048576:.1f} MB 摘要一致")

    if bundled:
        if not LICENSE_SRC.is_file():
            print(f"[fail] 缺 {LICENSE_SRC}：再分发条件要求许可全文随包走")
            return 1
        shutil.copy2(LICENSE_SRC, factory / "LICENSE-NapCat.txt")
        (factory / "来源与许可说明.txt").write_text(
            "这个目录里的 NapCat 压缩包不是 AstrBot 桌面版自己写的，是从上游发布页原样取来的：\n"
            f"  项目：https://github.com/NapNeko/NapCatQQ\n"
            f"  版本：v{NAPCAT_VERSION}\n"
            + "".join(
                f"  {name}  SHA-256 {NAPCAT_SHA[name]}\n" for name in sorted(bundled)
            )
            + "  许可：见同目录 LICENSE-NapCat.txt（上游是「Limited Redistribution License」，"
            "允许再分发，条件是把许可全文与来源随包带上，且不得用于商用；本包为非商用分发）。\n"
            "\n"
            "为什么打进包里：杀毒软件吃掉核心、或者断网拿不到下载的时候，"
            "「一键修复」和「下载 NapCat」得有个不需要网、不需要重装就能用的后备。\n"
            "安装时仍然按上面那串摘要逐字节核对，对不上就拒绝解压——副本被改一个字节也算对不上。\n",
            encoding="utf-8",
        )

    # 四、逐文件摘要清单：修复器靠它回答"到底哪个文件被改了/没了"
    # 残渣扫描只看会被交付的部分：data\ 本来就不进包（里面可能装着使用者自己跑的 NapCat，
    # 那不该拦我的打包，也不该被我去动）。
    residue = [
        p.relative_to(app).as_posix()
        for p in app.rglob("*")
        if p.is_file()
        and p.relative_to(app).parts[0] not in SKIP_TOP
        and RESIDUE.search(p.name)
    ]
    if residue:
        print("[fail] 包里混进了测试残渣，清掉再打包：" + "、".join(residue[:8]))
        return 1
    lines = ["# 包内文件清单（factory\\ 自己不在内）：相对路径 + 字节数 + SHA-256"]
    total = 0
    for path in sorted(app.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(app).as_posix()
        top = rel.split("/", 1)[0]
        if top in SKIP_TOP:
            continue
        lines.append(f"{rel}\t{path.stat().st_size}\t{sha256(path)}")
        total += 1
    (factory / "MANIFEST-SHA256.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"[manifest] {total} 个文件写进 factory\\MANIFEST-SHA256.txt")

    size = sum(p.stat().st_size for p in factory.rglob("*") if p.is_file())
    print(f"[factory] 本目录合计 {size / 1048576:.1f} MB")
    print("[顺序要求] 这一步必须在 sign_dist.ps1 **之后**、ISCC 之前跑：")
    print("           factory 里带的是签名后的字节，还原回去才不会把签名弄没。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
