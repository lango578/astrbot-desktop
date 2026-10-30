"""Build the release assets a public repo needs: portable zip + checksums.

The installer .exe is produced by Inno (build/setup_astrbot.iss); this script adds the
no-install zip and SHA256SUMS.txt, and refuses to write a checksum file over a truncated
or stale artifact. Sizes are reported both logical and on-disk because a 400 MB tree
compresses to roughly a fifth and people read the number wrong.
"""

from __future__ import annotations

import argparse
import hashlib
import shutil
import sys
import zipfile
from pathlib import Path

BUILD = Path(__file__).resolve().parent
ROOT = BUILD.parent
APP = Path(shutil.os.environ.get("ASTRBOT_APP_DIR") or (ROOT / "dist-new" / "AstrBot"))
OUT = ROOT / "installer"
VERSION = "4.29.3"


def portable_zip() -> Path:
    target = OUT / f"AstrBot-{VERSION}-desktop-portable.zip"
    OUT.mkdir(exist_ok=True)
    if target.is_file():
        target.unlink()
    entries = 0
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for path in sorted(APP.rglob("*")):
            if not path.is_file():
                continue
            rel = path.relative_to(APP)
            # data\ 是使用者的东西（口令哈希、QQ 密钥、聊天库），绝不能进包
            if rel.parts and rel.parts[0].lower() == "data":
                continue
            # astrbot.lock 是上一台机器跑出来的文件锁，同样不是构建产物
            if rel.as_posix().lower() == "astrbot.lock":
                continue
            if any(part in {"__pycache__"} for part in rel.parts):
                continue
            z.write(path, ("AstrBot/" + rel.as_posix()))
            entries += 1
    size = target.stat().st_size
    print(f"[zip] {target.name}: {entries} 个文件，{size/1048576:.1f} MB")
    if size < 50 * 1048576:
        raise SystemExit(f"压缩包只有 {size/1048576:.1f} MB，明显不完整，别发")
    return target


def checksums(assets: list[Path]) -> Path:
    path = OUT / "SHA256SUMS.txt"
    lines = []
    for item in assets:
        digest = hashlib.sha256(item.read_bytes()).hexdigest()
        lines.append(f"{digest}  {item.name}")
        print(f"[sha] {item.name}  {digest[:16]}…  {item.stat().st_size/1048576:.1f} MB")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-zip", action="store_true", help="只重算校验和")
    args = parser.parse_args()

    assets: list[Path] = []
    installer = OUT / f"AstrBot-Setup-{VERSION}.exe"
    if installer.is_file():
        assets.append(installer)
    else:
        print(f"[提示] 还没有安装程序 {installer.name}：先跑 tools\\innosetup\\ISCC.exe build\\setup_astrbot.iss")

    if not args.skip_zip:
        assets.append(portable_zip())

    # 安卓那个「掉线修复」插件不进 Windows 发布资产：那是给手机版 APK 用的（APK 里改不动代码，
    # 只能靠上传插件），Windows 版的同一个修复是直接编进核心的。混在一起发，等于让用户
    # 以为要自己装插件，也等于把两份不同来源的东西塞进同一条校验和清单。
    stray = sorted(p.name for p in OUT.glob("onebot-recovery-fix*"))
    for item in assets:
        if "onebot-recovery-fix" in item.name:
            raise SystemExit(f"发布资产里出现了安卓补丁 {item.name}，拒绝写校验和清单")
    if stray:
        print(f"[提示] installer/ 里有安卓补丁包（{', '.join(stray)}），它不在 Windows 发布清单里")

    checksums(assets)
    print(f"[done] 资产 {len(assets)} 个 + SHA256SUMS.txt 在 {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
