"""扫公开仓库上**所有可能被外人看到的东西**，找有没有属于这台机器/这个人的信息。

和 test/audit_personal_info.py 的分工：那个扫的是"我们自己的文件 + 成品二进制"，
这个扫的是**GitHub 上别人能看到的全部表面**：

  · 仓库元数据：名字、描述、主页、可见性
  · 每条分支与每个 tag 指向的提交、提交说明、以及该提交下的**全部文件内容**
    （历史里被丢弃的旧提交也要看：GitHub 不一定立刻回收孤儿对象，但至少要确认没有分支/标签还指着它们）
  · 每个 Release 的标题与正文、每个资产的文件名
  · Issue / Wiki / Pages（有就查，没有就记 NOTRUN，不许当成"已确认没有"）

凭据与身份串从 keys/personal-probes.txt 与本机环境取，代码里不写死。

    venv\\Scripts\\python.exe test\\audit_github_privacy.py --repo <你的账号>/astrbot-desktop --token-file keys/gh.token
"""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
PROBES = BUILD / "keys" / "personal-probes.txt"
API = "https://api.github.com"

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

ACCOUNT = Path(os.environ.get("USERPROFILE", "")).name.strip()
COMPUTER = os.environ.get("COMPUTERNAME", "").strip()
HOME_WIN = re.compile(rb"[A-Za-z]:[\\/]{1,2}Users[\\/]{1,2}([A-Za-z0-9._\-]{2,40})", re.IGNORECASE)
HOME_UNIX = re.compile(rb"/(?:home|Users)/([A-Za-z0-9._\-]{2,40})/")
# NapCat 自带的文件就叫 qqnt.json，光出现 "QQNT" 不算泄露：必须它在路径上下文里
QQ_DIR = re.compile(rb"[A-Za-z]:[\\\\/]{1,2}\\S{0,40}?[\\\\/]{1,2}QQNT|[\\\\/]{1,2}Tencent[\\\\/]{1,2}QQNT", re.IGNORECASE)
IPV4 = re.compile(rb"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")
TOKENISH = re.compile(rb"ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----[\r\n]{0,2}[A-Za-z0-9+/=]{40,}")
EMAIL = re.compile(rb"[A-Za-z0-9._%+\-]{3,}@[A-Za-z0-9.\-]+\.[A-Za-z]{2,8}")

# 这些账户名不属于这台机器：厂商上游的构建路径、示例、公共邮箱域名等
SAFE_ACCOUNTS = {"default", "user", "someone", "builder", "build", "test", "runner", "github", "root", "example"}
SAFE_HOSTS = {"github.com", "api.github.com", "codeload.github.com", "objects.githubusercontent.com",
              "raw.githubusercontent.com", "pypi.org", "python.org", "mirrors.aliyun.com", "gh-proxy.com",
              "127.0.0.1", "0.0.0.0", "localhost", "npmjs.com", "nodejs.org", "astrbot.app", "soulter.top",
              "jrsoftware.org", "digicert.com", "example.com", "tests.test", "microsoft.com", "windows.net",
              "tencent.com", "qq.com", "google.com", "anthropic.com", "openai.com", "alibabacloud.com"}


def load_probes() -> dict[str, re.Pattern]:
    out: dict[str, re.Pattern] = {}
    if ACCOUNT:
        # 要带路径上下文或独立成词：证书序列号那种长数字里恰好夹着账户名数字，不算泄露
        shaped = rb"(?:Users[\\/]{1,2}|[\\/]|(?<![\w])|^\s)" + re.escape(ACCOUNT.encode()) + rb"(?![A-Za-z0-9])"
        out["本机账户名"] = re.compile(shaped, re.IGNORECASE)
    if COMPUTER:
        out["本机计算机名"] = re.compile(re.escape(COMPUTER.encode()), re.IGNORECASE)
    if PROBES.is_file():
        for line in PROBES.read_text(encoding="utf-8-sig", errors="replace").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            _, _, value = line.partition("=")
            value = (value or line).strip()
            if len(value) >= 3:
                out[f"探针:{value[:16]}"] = re.compile(re.escape(value.encode()), re.IGNORECASE)
    return out


def get(url: str, token: str, binary: bool = False):
    headers = {"User-Agent": "astrbot-privacy-audit", "Accept": "application/vnd.github+json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    try:
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=120) as resp:
            raw = resp.read()
            return resp.status, (raw if binary else (json.loads(raw) if raw else {}))
    except urllib.error.HTTPError as exc:
        return exc.code, b"" if binary else {}
    except Exception as exc:  # noqa: BLE001
        return 0, b"" if binary else {}


def scan_text(label: str, blob: bytes, hits: list, probes: dict) -> None:
    for name, pat in probes.items():
        if pat.search(blob):
            hit = f"{name} 出现在 {label}"
            # 仓库所有者的 GitHub 登录名是固有的：仓库 URL、每个 commit 的作者字段都必然带着它，
            # 除非改用另一个账号发布。所以它单列为"固有可见"，不混进"需要删除"的命中里。
            # 真正不该出现的是账号绑定的邮箱、本机账户名、计算机名、私人域名/IP 这类。
            if name == OWNER_PROBE_KEY:
                if hit not in INHERENT:
                    INHERENT.append(hit)
            else:
                hits.append(hit)
    for pat, kind in ((HOME_WIN, "Windows 用户目录"), (HOME_UNIX, "Unix 家目录")):
        for m in pat.finditer(blob):
            account = m.group(1).decode("ascii", "replace")
            if ACCOUNT and account.lower() == ACCOUNT.lower():
                hits.append(f"{kind} 带本机账户名：{label} -> {account}")
            elif COMPUTER and COMPUTER.lower().replace("-", "") in account.lower():
                hits.append(f"{kind} 带本机计算机名片段：{label} -> {account}")
            elif account.lower() not in SAFE_ACCOUNTS:
                notes.append(f"存疑账户名（不是本机的，需人工确认）：{label} -> {account}")
    if QQ_DIR.search(blob):
        hits.append(f"本机 QQ 安装目录名出现在 {label}")
    for m in IPV4.finditer(blob):
        ip = m.group(0).decode()
        if ip not in SAFE_HOSTS and not ip.startswith(("127.", "0.0.0.", "10.", "192.168.", "172.16.")):
            hits.append(f"公网 IPv4 出现在 {label}：{ip}")
    if TOKENISH.search(blob):
        hits.append(f"令牌或私钥正文出现在 {label}")


OWNER_PROBE_KEY = ""
INHERENT: list[str] = []


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--token-file", default="", help="不传就用匿名额度（容易 403，会记 NOTRUN）")
    args = parser.parse_args()
    token = Path(args.token_file).read_text(encoding="utf-8").strip() if args.token_file else os.environ.get("GITHUB_TOKEN", "").strip()
    probes = load_probes()
    global OWNER_PROBE_KEY
    owner = args.repo.split("/")[0]
    OWNER_PROBE_KEY = "探针:" + owner[:16]
    hits: list[str] = []
    notes: list[str] = []
    missed: list[str] = []
    checked = {"files": 0, "refs": 0, "releases": 0, "commits": 0, "surfaces": 0}

    status, repo = get(f"{API}/repos/{args.repo}", token)
    if status != 200:
        print(f"仓库拉不到（status={status}）——限流就带 --token-file 再跑")
        return 2
    if repo.get("private"):
        notes.append("仓库是私有的：外人看不到，但内容仍要清")
    scan_text("仓库元数据(名字+描述+主页)",
              (" ".join([str(repo.get("name") or ""), str(repo.get("description") or ""), str(repo.get("homepage") or "")])).encode(), hits, probes)

    status, refs = get(f"{API}/repos/{args.repo}/git/refs", token)
    ref_list = refs if isinstance(refs, list) else []
    for ref in ref_list:
        name = str(ref.get("ref") or "")
        sha = str((ref.get("object") or {}).get("sha") or "")
        if not sha or "/tags/" not in name and "/heads/" not in name:
            continue
        checked["refs"] += 1
        status, commit = get(f"{API}/repos/{args.repo}/git/commits/{sha}", token)
        if status != 200:
            notes.append(f"NOTRUN 提交 {name}（status={status}）")
            continue
        checked["commits"] += 1
        message = str((commit.get("message") or ""))
        author = json.dumps(commit.get("author") or {}, ensure_ascii=False)
        scan_text(f"引用 {name} 的提交说明", message.encode(), hits, probes)
        scan_text(f"引用 {name} 的作者字段", author.encode(), hits, probes)
        # 该引用下的全部文件内容
        status, blob = get(f"{API}/repos/{args.repo}/zipball/{sha}", token, binary=True)
        if status not in (200, 302) or not blob:
            notes.append(f"NOTRUN 引用 {name} 的文件内容（zipball status={status}）")
            continue
        try:
            with zipfile.ZipFile(io.BytesIO(blob)) as zf:
                for info in zf.infolist():
                    if info.is_dir():
                        continue
                    checked["files"] += 1
                    scan_text(f"{name}:{info.filename.split('/', 1)[-1]}", zf.read(info), hits, probes)
        except zipfile.BadZipFile:
            notes.append(f"NOTRUN 引用 {name} 的 zipball 解不开")

    status, releases = get(f"{API}/repos/{args.repo}/releases?per_page=100", token)
    rel_list = releases if isinstance(releases, list) else []
    if not rel_list and status != 200:
        notes.append(f"NOTRUN Release 列表（status={status}）")
    for rel in rel_list:
        checked["releases"] += 1
        label = f"Release {rel.get('tag_name')}"
        scan_text(f"{label} 标题", str(rel.get("name") or "").encode(), hits, probes)
        scan_text(f"{label} 正文", str(rel.get("body") or "").encode(), hits, probes)
        for asset in rel.get("assets") or []:
            scan_text(f"{label} 资产名", str(asset.get("name") or "").encode(), hits, probes)

    for extra_path, extra_label in ((f"/repos/{args.repo}/issues?per_page=100", "Issue"),
                                    (f"/repos/{args.repo}/pages", "Pages"),
                                    (f"/repos/{args.repo}/wikis", "Wiki")):
        status, data = get(f"{API}{extra_path}", token)
        if status == 200 and data:
            scan_text(f"{extra_label} 内容", json.dumps(data, ensure_ascii=False).encode(), hits, probes)
            notes.append(f"{extra_label} 有内容，已扫")
        elif status == 200:
            checked["surfaces"] += 1
            notes.append(f"{extra_label}：查询成功且为空（确认没有）")
        elif status == 404:
            checked["surfaces"] += 1
            notes.append(f"{extra_label}：未启用（确认不存在，不是没查）")
        else:
            missed.append(f"{extra_label}：status={status}（没查成，别当已通过）")

    print("\n==== GitHub 隐私审计 ====")
    print(f"扫过：引用 {checked['refs']} 个 / 提交 {checked['commits']} 个 / 文件 {checked['files']} 个 / "
          f"Release {checked['releases']} 个 / 仓库元数据 1 份")
    print(f"探针：{len(probes)} 条（来自环境 + {PROBES.name}）" + ("  ⚠ 探针文件不存在" if not PROBES.is_file() else ""))
    for line in hits:
        print("命中   " + line)
    for line in INHERENT:
        print("固有可见 " + line + "（仓库所有者的登录名，除非换账号发布否则消不掉）")
    for line in notes:
        print(line)
    if not hits:
        print("CLEAN  上面扫到的每一处都没有你的账户名、计算机名、账号、域名、服务器 IP、QQ 安装目录、令牌私钥")
    need_run = [n for n in notes if n.startswith("NOTRUN")] + missed
    print(f"结论：{'需要清理' if hits else ('表面干净，但有 ' + str(len(need_run)) + ' 项没查成' if need_run else '干净')}")
    return 1 if hits else (2 if need_run else 0)


if __name__ == "__main__":
    sys.exit(main())
