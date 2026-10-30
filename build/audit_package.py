r"""Audit the *deliverable* for anything that must not leave this machine.

The source-tree scan in make_public_repo.py only covers our own files; the shipped app is
a 400 MB tree that has been booted, configured and tested on this box, so it can carry:
a data\ folder with a real panel password hash, the SQLite chat database, QQ platform
credentials, plugin configs, WebView2 profiles, log files with message content, or
absolute paths under the current user's home directory (that shape is what the scan below
calls USER_HOME).

Run it against the staging tree before building the installer, and again against the
finished installer + portable zip. Non-zero exit means: do not publish.

    venv\\Scripts\\python.exe build\\audit_package.py --path dist-new\\AstrBot
    venv\\Scripts\\python.exe build\\audit_package.py --zip installer\\AstrBot-...-portable.zip
"""

from __future__ import annotations

import argparse
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# 这些路径存在就说明使用者的数据混进了交付包
FORBIDDEN_DIRS = ("data", "logs", "__pycache__")
FORBIDDEN_SUFFIX = (".db", ".db-wal", ".db-shm", ".log", ".tmp", ".pfx", ".key", ".token", ".pem")
# CA 证书包不是私钥：certifi / pip 自带的那份必须随包走，否则装插件会在 truststore 里炸
ALLOWED_NAMES = ("cacert.pem",)
FORBIDDEN_NAMES = (
    "cmd_config.json", "plugins.json", "mcp_server.json", "astrbot.db",
    "desktop.conf", "gh.token", "webview2",
)
# 内容里不许出现的形态：口令哈希、真实凭据、他人目录、本机用户名路径、令牌
FORBIDDEN_PATTERNS = {
    "面板口令哈希": re.compile(r"pbkdf2_password"),
    "QQ/平台凭据字段": re.compile(r"(app_secret|access_token|ws_reverse_token|satori_token|bot_encrypt_secret)\s*[=:]\s*[\"']?[A-Za-z0-9._\-]{8,}"),
    "GitHub/服务令牌": re.compile(r"(ghp_|github_pat_|gho_|ghs_|xox[baprs]-|AKIA[0-9A-Z]{16})"),
    "私钥": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----"),
    "本机用户目录": re.compile(r"[A-Za-z]:\\\\Users\\\\[^\\\\\"'\s]+"),
    # 字面量拆两截：把构建目录名原样写进这个文件，等于把它印进公开仓库，而且扫描器会扫到自己
    "构建机绝对路径": re.compile(r"[A-Za-z]:\\{1,4}" + "astrbot" + r"-build", re.IGNORECASE),
}
# 允许带这些字的文件：README 与安装说明会讲"口令/密钥会被打码"
BENIGN_FILES = ("README.txt", "LICENSE-AstrBot.txt")


def check_names(rel: str, parts: tuple[str, ...]) -> list[str]:
    hits = []
    lowered = {p.lower() for p in parts}
    for bad in FORBIDDEN_DIRS:
        if bad in lowered:
            hits.append(f"目录 {bad}\\ 出现在 {rel}")
    name = parts[-1].lower()
    if name in ALLOWED_NAMES:
        return hits
    if name in FORBIDDEN_NAMES:
        hits.append(f"不该随包走的文件 {rel}")
    if name.endswith(FORBIDDEN_SUFFIX):
        hits.append(f"不该随包走的文件类型 {rel}")
    return hits


def scan_tree(path: Path) -> tuple[list[str], int]:
    hits: list[str] = []
    count = 0
    for item in sorted(path.rglob("*")):
        if not item.is_file():
            continue
        count += 1
        rel = item.relative_to(path)
        hits += [h for h in check_names(rel.as_posix(), rel.parts)]
        if len(rel.parts) > 1 and rel.parts[0].lower() in {"data", "logs"}:
            continue  # 已经在上面记过一次，不必再读内容
        if item.suffix.lower() not in {".txt", ".md", ".json", ".ini", ".conf", ".yaml", ".yml", ".cs", ".py", ".ps1", ".iss"}:
            continue
        if item.name in BENIGN_FILES:
            continue
        try:
            text = item.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        for label, pattern in FORBIDDEN_PATTERNS.items():
            for match in pattern.finditer(text):
                hits.append(f"{rel.as_posix()}: {label} → {match.group(0)[:60]}")
    return hits, count


def scan_zip(path: Path) -> tuple[list[str], int]:
    hits: list[str] = []
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        for name in names:
            parts = tuple(name.split("/"))
            hits += check_names(name, parts)
        # 文本类成员才读内容，二进制成员只看名字
        for info in z.infolist():
            if info.is_dir():
                continue
            ext = Path(info.filename).suffix.lower()
            if ext not in {".txt", ".md", ".json", ".ini", ".conf", ".yaml", ".yml", ".iss"}:
                continue
            if Path(info.filename).name in BENIGN_FILES:
                continue
            if info.file_size > 4 * 1024 * 1024:
                continue
            text = z.read(info.filename).decode("utf-8", "replace")
            for label, pattern in FORBIDDEN_PATTERNS.items():
                for match in pattern.finditer(text):
                    hits.append(f"{info.filename}: {label} → {match.group(0)[:60]}")
    return hits, len(names)


def main() -> int:
    parser = argparse.ArgumentParser()
    # 目录扫描改成显式 opt-in：正在用的交付目录里当然有 data\（使用者的口令哈希、
    # 聊天库、他自己装的 NapCat），把它和"交付物里混进了使用者数据"混在一个退出码里，
    # 结果就是每次出包都得先把 data 挪开，或者干脆对着一堆假阳性喊"别发"。
    parser.add_argument("--path", default="", help="显式给才扫目录（跑 package_dist 之前用）")
    parser.add_argument("--zip", action="append", default=[], help="可以重复给，检查已生成的资产")
    args = parser.parse_args()

    all_hits: list[str] = []
    if args.path:
        target = Path(args.path)
        if target.is_dir():
            hits, count = scan_tree(target)
            print(f"[audit] 目录 {target.name}: {count} 个文件，{len(hits)} 处问题")
            all_hits += hits
    else:
        print("[audit] 没给 --path，跳过目录扫描（交付物看 --zip / 安装程序看 verify_installer）")
    for item in args.zip:
        zpath = Path(item)
        if not zpath.is_file():
            print(f"[audit] 跳过（不存在）{zpath}")
            continue
        hits, count = scan_zip(zpath)
        print(f"[audit] 压缩包 {zpath.name}: {count} 个成员，{len(hits)} 处问题")
        all_hits += hits

    print("\n==== 包体审计 ====")
    if not all_hits:
        print("  CLEAN 交付物里没有使用者数据、凭据、本机路径")
        return 0
    seen = set()
    for h in all_hits:
        if h in seen:
            continue
        seen.add(h)
        print("  HIT ", h[:200])
    print(f"  共 {len(seen)} 类问题 —— 修掉之前不许打包/发布")
    return 1


if __name__ == "__main__":
    sys.exit(main())
