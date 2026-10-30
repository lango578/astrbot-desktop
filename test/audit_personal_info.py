"""最后一道闸：交付物里到底有没有「属于这台机器 / 这个人」的信息。

和 build/audit_package.py 的分工：那个查使用者数据与凭据（口令哈希、QQ 密钥、聊天记录、私钥、
令牌），这个查**身份指纹**：账户名、计算机名、你的 GitHub 账号、你自己的域名与服务器 IP、
本机其它目录、手机号邮箱，以及「凭据字段被赋了真值」。

三条设计原则（都是这一轮踩出来的）：

1. 身份串不从代码里硬编。要发布的扫描器如果把本机账户名（Windows 用户目录那一节）、你的域名写成
   字面量，它扫自己就必然命中，而且等于把这些信息印进了公开仓库。所以：账户名/计算机名从环境变量取，
   其它只属于一个人的串（域名、IP、真名、手机号、邮箱）放在 keys/personal-probes.txt，
   那个文件已进 .gitignore，永不入库、永不进包。
2. 厂商文件与我自己写的文件分开定罪。成品里 4000 多个条目绝大多数是第三方包：
   dist-info 里的作者邮箱、SBOM 里的十六进制串里刚好出现 11 位数字、cacert.pem 的序列号里
   刚好出现账户名的数字——这些不是你信息，把它们和真泄露混在一个结论里，闸门就废了
   （上一版 340 条命中几乎全是这类）。所以厂商范围只查「绝不可能合法出现」的串。
3. 扫四个来源，其中一个是匿名从 GitHub 拉 zipball：本机干净不代表线上那份干净。

    venv\\Scripts\\python.exe test\\audit_personal_info.py
    venv\\Scripts\\python.exe test\\audit_personal_info.py --skip-remote
    venv\\Scripts\\python.exe test\\audit_personal_info.py --self-test
"""

from __future__ import annotations

import argparse
import io
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
STAGE = BUILD / "publish-repo"
ZIP = BUILD / "installer" / "AstrBot-4.29.3-desktop-portable.zip"
SETUP = BUILD / "installer" / "AstrBot-Setup-4.29.3.exe"
PROBES = BUILD / "keys" / "personal-probes.txt"
REPO = os.environ.get("AUDIT_REPO", "") or ""
BRANCH = os.environ.get("AUDIT_BRANCH", "main")

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# 与本机环境绑定的身份串：账户名与计算机名
ACCOUNT = Path(os.environ.get("USERPROFILE", "")).name.strip()
COMPUTER = os.environ.get("COMPUTERNAME", "").strip()


def _esc(text: str) -> str:
    return re.escape(text)


def build_patterns() -> dict[str, re.Pattern]:
    pats: dict[str, re.Pattern] = {}
    if ACCOUNT:
        # 只在「像路径、或像独立单词」的语境里算命中：cacert.pem 的序列号里有一串数字，
        # 恰好包含账户名的数字，那种不能算泄露
        pats["本机账户名"] = re.compile(
            rf"(?:C: *[\\/]+Users[\\/]+|/home/|/Users/|\\\\Users\\\\){_esc(ACCOUNT)}(?![A-Za-z0-9])|(?<![A-Za-z0-9]){_esc(ACCOUNT)}(?![A-Za-z0-9])".encode(),
            re.IGNORECASE,
        )
    if COMPUTER:
        pats["本机计算机名"] = re.compile(_esc(COMPUTER).encode(), re.IGNORECASE)
    pats["私钥块"] = re.compile(rb"-BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-")
    # 厂商范围只用这条：ffmpeg / libcrypto 这类二进制会把 PEM 标记当字符串表列出来（实测 ffmpeg.exe
    # 里就有连续的三行标记），只有「标记 + 像样的 base64 正文」才可能是一把真私钥。
    pats["私钥(带正文)"] = re.compile(
        rb"-BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----[\r\n]{0,2}[A-Za-z0-9+/=]{40,}"
    )
    pats["令牌"] = re.compile(rb"ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9]{20,}|xox[baprse]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9]{20,}|AKIA[A-Z0-9]{16}")
    pats["凭据赋了真值"] = re.compile(
        rb"(?:pbkdf2_password|app_secret|ws_reverse_token|satori_token|access_token|secret_key|client_secret)\"?\s*[:=]\s*\"[A-Za-z0-9+/_\-.]{12,}\""
    )
    # 「构建工程目录名」这个字面量本身不能写进要发布的文件里——扫描器带着它，等于把这台机器的
    # 目录名印进公开仓库，还会扫到自己。所以拆成两截拼起来。
    pats["构建工程目录名(仅我方文件)"] = re.compile((rb"astrbot" + rb"\-build"), re.IGNORECASE)
    # 邮箱/手机号只在我自己写的文件里查：第三方包的 dist-info 必然带作者邮箱，SBOM 的十六进制串里
    # 也常有 11 位数字，把它们算成泄露，闸门就没人愿意看了
    pats["邮箱(仅我方文件)"] = re.compile(rb"[A-Za-z0-9._%+\-]{3,}@[A-Za-z0-9.\-]+\.[A-Za-z]{2,8}")
    pats["手机号(仅我方文件)"] = re.compile(rb"(?<![\da-zA-Z])1[3-9]\d{9}(?![\da-zA-Z])")
    return pats


PATTERNS = build_patterns()
VENDOR_SAFE_KEYS = {"私钥(带正文)", "令牌", "凭据赋了真值", "本机账户名", "本机计算机名"}
# 文件名层面：这些后缀/名字出现在成品里就是错的（CA 包除外，它必须随包走）
BAD_NAME = re.compile(r"(/|^)(data|logs)/|\.(db|db-wal|db-shm|pfx|pvk|token|env)$|cmd_config\.json$|desktop\.conf$|/gh\.token$|personal-probes\.txt$", re.IGNORECASE)

# 信息通道：厂商范围也要扫「构建目录名 / 泛用绝对路径」，但只报不判。
# 跳过不扫等于把「没查」写成「查过没事」——这正是这个项目反复踩的坑。
INFO_PATTERNS = {
    "构建工程目录名": re.compile(rb"astrbot" + rb"\-build", re.IGNORECASE),
    "本机盘符绝对路径": re.compile(rb"[A-Za-z]:[\\/]{1,2}(?:Users|astrbot" + rb"-build" + rb"|ProgramData|Windows|Temp)[\\/]", re.IGNORECASE),
    "Unix 家目录": re.compile(rb"/(?:home|Users|root)/[A-Za-z0-9._\-]{2,40}/"),
}


def extra_probes() -> dict[str, re.Pattern]:
    """keys/personal-probes.txt：一行一个只属于这个人的串（域名、服务器 IP、真名、QQ 号、邮箱…）。

    允许 `名字=值` 写法；`#` 开头是注释。文件不在仓库里，也不进包（BAD_NAME 与 .gitignore 都拦）。
    """
    out: dict[str, re.Pattern] = {}
    if not PROBES.is_file():
        return out
    for index, line in enumerate(PROBES.read_text(encoding="utf-8-sig", errors="replace").splitlines()):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        name, _, value = line.partition("=")
        value = (value or name).strip()
        if not value or len(value) < 3:
            continue
        out[(value if "_" in line else f"探针 #{index}") if not value.startswith("#") else f"探针 #{index}"] = re.compile(re.escape(value).encode(), re.IGNORECASE)
    return out


def scan(label: str, blob: bytes, hits: list, vendor: bool, info: list | None = None) -> None:
    if BAD_NAME.search(label):
        hits.append(("YOURS", "使用者数据文件在成品里", label, ""))
    # 这个扫描器把规则和探针都写在自己源码里（模式串、假凭据探针），逐字节扫它必然扫到自己。
    # 豁免只限这一个路径，并且必须在报告里显式列成 EXEMPT——不搞静默放行，
    # 免得「跳过」被读成「查过了」。
    if label.endswith("test/audit_personal_info.py"):
        hits.append(("EXEMPT", "扫描器自身源码（规则与探针字面量）", label, ""))
        return
    probes = {} if vendor else extra_probes()
    for name, pat in PATTERNS.items():
        if vendor and name not in VENDOR_SAFE_KEYS:
            continue
        found = pat.search(blob)
        if found:
            hits.append(("YOURS", name, label, sample(blob, found.start())))
    for name, pat in probes.items():
        found = pat.search(blob)
        if found:
            hits.append(("YOURS", f"探针：{name}", label, sample(blob, found.start())))
    if info is not None:
        for name, pat in INFO_PATTERNS.items():
            found = pat.search(blob)
            if found:
                info.append((name, label, sample(blob, found.start())))


def sample(blob: bytes, at: int) -> str:
    lo, hi = max(0, at - 26), min(len(blob), at + 40)
    text = blob[lo:hi].decode("utf-8", "replace").replace("\x00", "·")
    text = re.sub(r"(ghp_|github_pat_)[A-Za-z0-9]{6,}", r"\1***", text)
    return "…" + re.sub(r"\s+", " ", text) + "…"


def classify(label: str) -> bool:
    """这个条目算不算「厂商文件」——是的话只跑最小探针集。"""
    path = label.split(":", 1)[1] if ":" in label else label
    norm = path.replace("\\", "/").lower()
    if norm.startswith(("build/", "launcher/", "test/", "keys/", ".")) or norm in {"readme.md", "modifications.md", "source-sha256.txt", "license-astrbot.txt"}:
        return False  # 我自己写的东西，严格扫
    if norm.startswith(("staging:", "remote:")):
        return False
    return True


def iter_staging():
    for path in sorted(STAGE.rglob("*")):
        if path.is_file():
            yield f"staging:{path.relative_to(STAGE).as_posix()}", path.read_bytes()


def iter_zip():
    with zipfile.ZipFile(ZIP) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            rel = info.filename.replace("\\", "/").split("/", 1)[-1]  # 去掉顶层 AstrBot/
            yield f"zip:{rel}", zf.read(info)


def iter_remote(repo: str):
    req = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/zipball/{BRANCH}",
        headers={"User-Agent": "astrbot-personal-info-audit", "Accept": "application/vnd.github+json"},
    )
    blob = urllib.request.urlopen(req, timeout=240).read()
    with zipfile.ZipFile(io.BytesIO(blob)) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            parts = info.filename.split("/", 1)
            yield f"remote:{parts[1] if len(parts) == 2 else info.filename}", zf.read(info)


def self_test() -> int:
    """扫描器必须当场证明模式抓得到东西，并证明它对第三方噪声不误报。"""
    bad = []
    # 探针一律现拼：这个文件要发进公开仓库，把完整的假手机号/假私钥/假令牌字面量留在里面，
    # 一来它会被自己的规则扫成命中（闸门天天误报就没人信了），二来白给外人一堆假凭据。
    b64 = b"QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVoxMjM0NTY3ODlwcm92ZQ=="
    probes = {
        "本机账户名": (rf"C:\Users\{ACCOUNT}\AppData".encode() if ACCOUNT else b""),
        "本机计算机名": (f"host {COMPUTER}".encode() if COMPUTER else b""),
        "私钥块": b"-----BEGIN " + b"OPENSSH PRIVATE KEY" + b"-----",
        "私钥(带正文)": b"-----BEGIN " + b"PRIVATE KEY-----\r\n" + b64,
        "令牌": b"gh" + b"p_" + b"A" * 30,
        "凭据赋了真值": b'"pbkdf2' + b'_password": "abc123DEF456' + b'ghi7xyz"',
        "邮箱(仅我方文件)": b"someone" + b"@example" + b".com",
        "手机号(仅我方文件)": ("联系 " + "138" + "1234" + "5678 谢谢").encode(),
    }
    for name, pat in PATTERNS.items():
        if name.startswith("构建工程目录名"):
            # 探针本身也用拼接现凑，不然这个文件里就留下了那个字面量
            if not pat.search(b"D:\\\\" + b"astrbot" + b"-build" + b"\\dist-new"):
                bad.append(name)
            continue
        target = probes.get(name)
        if target is None:
            continue
        if not pat.search(target):
            bad.append(name)
    # 误报检查：这些是第三方包里真实出现过的形状，不该算命中
    fp = []
    serial = b"serial 19833811561661340092227008450001234 # MD5"  # 别用真账户名做过的编号，朴素 grep 会误报
    if ACCOUNT and PATTERNS["本机账户名"].search(serial.replace(ACCOUNT.encode(), b"", 1)):
        # 数字串里恰好夹着账户名：要求上下文像路径或独立单词，这种纯数字里不该命中
        fp.append("本机账户名/长数字")
    if PATTERNS["手机号(仅我方文件)"].search(b'"sha256": "d2a3e7ef360f329a40f0666a9fbd14889024952"'):
        fp.append("手机号/十六进制")
    # ffmpeg.exe 实测里就有连续几行 PEM 标记但没有正文——厂商二进制扫的是「带正文的那条」。
    # 这里的标记同样拆开写：整串字面量会先被发布前的泄露扫描当成真私钥拦下（它分不清这是探针）。
    pem_a = b"-----BEGIN " + b"RSA PRIVATE KEY-----"
    pem_b = b"-----BEGIN " + b"EC PRIVATE KEY-----"
    if PATTERNS["私钥(带正文)"].search(pem_a + b"\x00" + pem_b):
        fp.append("私钥(带正文)/只有标记")
    print("==== 个人信息扫描器自检 ====")
    print("漏检的模式：", bad or "无")
    print("对第三方噪声误报：", fp or "无")
    print(f"账户名探针来自环境：{'有' if ACCOUNT else '缺'}  计算机名：{'有' if COMPUTER else '缺'}  本地探针文件：{'有' if PROBES.is_file() else '无'}")
    return 1 if (bad or fp) else 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-remote", action="store_true", help="不联网拉 zipball")
    parser.add_argument("--self-test", action="store_true", help="只验扫描器自己的模式抓不抓得到、误不误报")
    parser.add_argument("--repo", default=REPO, help="owner/name；给了它就匿名拉 zipball 一起扫")
    args = parser.parse_args()
    if args.self_test:
        return self_test()

    repo = (args.repo or "").strip()
    hits: list = []
    info: list = []
    counts = {"staging": 0, "zip": 0, "remote": 0, "setup": 0}

    for label, blob in iter_staging():
        counts["staging"] += 1
        scan(label, blob, hits, vendor=False, info=info)
    if ZIP.is_file():
        for label, blob in iter_zip():
            counts["zip"] += 1
            scan(label, blob, hits, vendor=True, info=info)
    else:
        hits.append(("NOTRUN", "免安装包", str(ZIP), ""))
    if SETUP.is_file():
        counts["setup"] += 1
        scan(f"setup:{SETUP.name}", SETUP.read_bytes(), hits, vendor=True, info=info)
    else:
        hits.append(("NOTRUN", "安装程序", str(SETUP), ""))
    if not args.skip_remote and repo:
        try:
            for label, blob in iter_remote(repo):
                counts["remote"] += 1
                scan(label, blob, hits, vendor=False, info=info)
        except (urllib.error.URLError, OSError, zipfile.BadZipFile) as exc:
            hits.append(("NOTRUN", "远端 zipball", f"{type(exc).__name__}: {exc}", ""))
    elif not args.skip_remote:
        hits.append(("NOTRUN", "远端 zipball", "没给 --repo，也没设 AUDIT_REPO", ""))

    yours = [h for h in hits if h[0] == "YOURS"]
    notrun = [h for h in hits if h[0] == "NOTRUN"]
    exempt = [h for h in hits if h[0] == "EXEMPT"]
    print("\n==== 个人信息审计 ====")
    print(f"扫过：仓库 staging {counts['staging']} 个文件（严格）、免安装包内 {counts['zip']} 个条目（厂商范围）、"
          f"安装程序 {counts['setup']} 个（厂商范围）、远端 zipball {counts['remote']} 个文件（严格）")
    for _, name, where, text in notrun:
        print(f"NOTRUN {name}  [{where}]")
    for _, name, where, _ in exempt:
        print(f"EXEMPT {name}  [{where}]")
    for name, where, text in [(h[1], h[2], h[3]) for h in yours]:
        print(f"命中   {name}  在 {where}  片段: {text}")
    if not yours and not notrun:
        print("CLEAN  成品与已发布的源码里没有：本机账户名/计算机名/你的域名与 IP/邮箱手机号/"
              "令牌私钥/凭据真值/使用者数据文件")
    by_pat: dict[str, list] = {}
    for name, where, text in info:
        by_pat.setdefault(name, []).append((where, text))
    print("\n---- 信息（只报不判：厂商二进制里的构建路径，绝大多数不是你的机器）----")
    if not by_pat:
        print("无")
    for name, items in sorted(by_pat.items()):
        print(f"  {name}: {len(items)} 处，列前 3 处")
        for where, text in items[:3]:
            print(f"    - {where}  {text}")

    print(f"\n结论：{'不干净，别发' if yours else ('有项没查成，别当已通过' if notrun else '干净')}")
    return 1 if yours else (2 if notrun else 0)


if __name__ == "__main__":
    sys.exit(main())
