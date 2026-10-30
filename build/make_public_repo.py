"""Assemble the public-repo staging tree and refuse to publish anything sensitive.

Why a staging dir instead of publishing the working tree as-is: it holds the recipient's
live data (panel password hash, QQ appid/secret, chat database), the whole upstream source,
800 MB of venv, third-party binaries (ffmpeg, WebView2, and a downloaded APK we have no
right to redistribute). Only our own build/launcher/test sources plus the public certificate
belong in the repo; the binaries go as Release assets.

    venv\\Scripts\\python.exe build\\make_public_repo.py
"""

from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import sys
from pathlib import Path

BUILD = Path(__file__).resolve().parent
ROOT = BUILD.parent
STAGE = ROOT / "publish-repo"

ALLOW = (
    "build/astrbot_desktop.py",
    "build/AstrBot.spec",
    "build/package_dist.py",
    "build/make_stamp.py",
    "build/make_factory.py",
    # 上游许可全文要随仓库走：README 里说了副本的再分发条件，条件本身不能只在构建机上
    "build/vendor/napcat/LICENSE-NapCat.txt",
    "build/prep_assets.py",
    "build/make_icon_from_original.py",
    "build/make_public_repo.py",
    "build/make_release_assets.py",
    "build/audit_package.py",
    # README 第十节让后来人用这条：csc @build\launcher\csc-sources.rsp —— 文件必须随仓库走，
    # 不然那句指令是空的（里面的路径全是相对构建工程根目录，不含任何本机信息）
    "build/launcher/csc-sources.rsp",
    "build/publish_github.py",
    "build/sign_dist.ps1",
    "build/make_signing_cert.ps1",
    "build/signature_proof.ps1",
    "build/setup_astrbot.iss",
    "build/README.txt",
    "build/INSTALL_NOTE.txt",
    "build/INSTALL_NOTE_zh_TW.txt",
    "build/INSTALL_NOTE_en.txt",
    "launcher",
    "test",
    ".gitignore",
)
# 公钥和指纹是公开信息（随包走的那份就是这个），私钥绝不进仓库
ALLOW_KEYS = ("astrbot-signing.cer", "signing-thumbprint.txt")

# 安卓用的掉线修复插件（contrib/onebot-recovery-fix）不进 Windows 仓库：
# Windows 侧的问题直接改在程序里，不靠插件糊。仓库的门面文档写在 docs/ 下，
# 进仓库时改成正经名字
DOCS = {
    "docs/repo-README.md": "README.md",
    "docs/repo-MODIFICATIONS.md": "MODIFICATIONS.md",
}

# 命中任何一条就不许发布。宁可停下来，也不要把别人的机器人凭据推到公网。
SECRET_PATTERNS = {
    "面板口令哈希": re.compile(r"pbkdf2_password", re.I),
    "OneBot/平台令牌": re.compile(r"(ws_reverse_token|access_token|satori_token)\s*[=:]\s*['\"]?[A-Za-z0-9._\-]{6,}"),
    "QQ 密钥": re.compile(r"(app_secret|bot_encrypt_secret|secret)\s*[=:]\s*['\"]?[A-Za-z0-9._\-]{12,}"),
    "API 令牌": re.compile(r"(ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-)"),
    "私钥文件": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "本机用户目录": re.compile(r"[A-Za-z]:\\\\Users\\\\[^\\\\\"'\s]+"),
}
# 测试跑出来的残渣：里面会有真的面板口令哈希、SQLite WAL、截图。
# 只有 .py 是源码，artifacts 整个目录不许出现在仓库里。
SKIP_DIRS = {"__pycache__", ".venv", "artifacts", "node_modules"}
# 允许随仓库走的公开二进制：只有验签公钥（安装包内本来就带这一份）
PUBLIC_BINARIES = {".cer"}

# 测试里刻意写的哨兵串和脱敏用的关键字，不是凭据。命中它们仍然打印出来，
# 只是不拦发布——白名单必须看得见，不能悄悄放行。
BENIGN = (
    "SUP3RSECRETPW",
    "UITESTPWD123",
    "abcdef123456",  # 脱敏断言里的假密钥
    "zzz789012",
    "abc123",
    "recovery-token",
    "astrbot-signing",
    "mirror.invalid",
    "example.invalid",
    "pbkdf2_password",  # 出现在脱敏正则的字段名列表里，不是值
    "jwt_secret",
)


def copy_tree(src: Path, dst: Path) -> int:
    n = 0
    for path in sorted(src.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(src)
        if any(part in SKIP_DIRS for part in rel.parts):
            continue
        if path.suffix.lower() in {".exe", ".dll", ".so", ".pyc", ".zip", ".apk", ".db", ".db-wal", ".db-shm", ".log", ".png", ".ico", ".ini"}:
            if path.suffix.lower() not in PUBLIC_BINARIES:
                print(f"  [skip 二进制] {rel}")
                continue
        target = dst / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        n += 1
    return n


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep", action="store_true", help="不清空已有 staging 目录")
    args = parser.parse_args()

    if STAGE.exists() and not args.keep:
        shutil.rmtree(STAGE)
    STAGE.mkdir(parents=True, exist_ok=True)

    total = 0
    for entry in ALLOW:
        src = ROOT / entry
        if not src.exists():
            print(f"[缺失] {entry}")
            continue
        if src.is_file():
            dst = STAGE / entry
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            total += 1
        else:
            total += copy_tree(src, STAGE / entry)

    keys = ROOT / "keys"
    if keys.is_dir():
        for name in ALLOW_KEYS:
            src = keys / name
            if src.is_file():
                (STAGE / "keys").mkdir(exist_ok=True)
                shutil.copy2(src, STAGE / "keys" / name)
                total += 1

    lic = ROOT / "src" / "AstrBot-master" / "LICENSE"
    if lic.is_file():
        shutil.copy2(lic, STAGE / "LICENSE-AstrBot.txt")  # AGPL 原文必须随包/随仓库走
        total += 1

    for src_rel, dst_rel in DOCS.items():
        src = ROOT / src_rel
        if not src.is_file():
            print(f"[缺失] {src_rel}")
            continue
        (STAGE / dst_rel).write_bytes(src.read_bytes())
        total += 1
        print(f"[文档] {src_rel} -> {dst_rel}")

    print(f"[staging] {total} 个文件 -> {STAGE}")

    findings: list[str] = []
    reviewed: list[str] = []
    for path in sorted(STAGE.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() in PUBLIC_BINARIES:
            reviewed.append(f"{path.relative_to(STAGE)}: 公开验签公钥（随安装包分发，非凭据）")
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            findings.append(f"{path.relative_to(STAGE)}: 非文本文件，人工确认后再发布")
            continue
        for label, pattern in SECRET_PATTERNS.items():
            for match in pattern.finditer(text):
                line = text[: match.start()].count("\n") + 1
                hit = match.group(0)
                if any(b in hit for b in BENIGN):
                    reviewed.append(f"{path.relative_to(STAGE)}:{line} {label} → 已复核为测试哨兵/字段名: {hit[:50]}")
                    continue
                findings.append(f"{path.relative_to(STAGE)}:{line} 疑似{label}: {hit[:60]}")

    print("\n==== 泄露扫描 ====")
    for line in reviewed:
        print("  ok    ", line[:180])
    if findings:
        for f in findings[:25]:
            print("  HIT ", f[:200])
        print(f"  共 {len(findings)} 处，修掉之前不许发布")
        return 1
    print("  CLEAN 没有未复核的凭据/私钥/他人目录命中")

    digests = []
    for path in sorted(STAGE.rglob("*")):
        if path.is_file():
            digests.append(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(STAGE).as_posix()}")
    (STAGE / "SOURCE-SHA256.txt").write_text("\n".join(digests) + "\n", encoding="utf-8")
    print(f"[staging] 写了 SOURCE-SHA256.txt（{len(digests)} 项）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
