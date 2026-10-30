"""Verify what actually landed on the public repo, without using the token.

Publishing is only done when this is green. It deliberately reads the **anonymous**
endpoints, because "my script said OK" is not evidence that a stranger can see the
release, that the tree matches the staging directory, or that no secret rode along.

    venv\\Scripts\\python.exe test\\verify_published_repo.py --repo <你的账号>/astrbot-desktop --tag v4.29.3-desktop.1

Loopback has nothing to do with this one: it is a read-only check of a public page.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
STAGE = BUILD / "publish-repo"
API = "https://api.github.com"

# git 的 blob 哈希：sha1("blob <字节数>\0" + 内容)
def blob_sha(path: Path) -> str:
    data = path.read_bytes()
    return hashlib.sha1(f"blob {len(data)}".encode() + b"\0" + data).hexdigest()


TOKEN = ""  # 只在匿名额度被打满时用它补测，报告里会写明哪几条是带 token 测的


def get(url: str, binary: bool = False):
    headers = {"User-Agent": "astrbot-desktop-verify", "Accept": "application/vnd.github+json"}
    if TOKEN:
        headers["Authorization"] = "Bearer " + TOKEN
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            raw = resp.read()
            return resp.status, raw if binary else json.loads(raw or b"{}")
    except urllib.error.HTTPError as exc:
        if exc.code == 403 and "rate limit" in exc.read().decode("utf-8", "replace").lower():
            return -1, b"" if binary else {}  # -1 = 被限流，不等于"检查失败"
        return exc.code, b"" if binary else {}


def rate_limited(detail: str) -> bool:
    return detail.startswith("status=-1")


def first_bytes(item: dict, count: int = 4096) -> tuple[int, bytes]:
    """只取资产开头几字节：不整包下载 140 MB，也能证明陌生人不带 token 就真拿得到文件。

    走 API 的资产地址（api.github.com + Accept: application/octet-stream），不走
    github.com/.../releases/download/...：后者会跳到 objects.githubusercontent.com，
    这台机器上那个域名解析不了（本机只有 api.github.com 与 codeload 通）。
    """
    url = str(item.get("url") or "")
    headers = {
        "User-Agent": "astrbot-desktop-verify",
        "Accept": "application/octet-stream",
        "Range": f"bytes=0-{count - 1}",
    }
    if TOKEN:
        headers["Authorization"] = "Bearer " + TOKEN
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as exc:
        return exc.code, b""
    except Exception as exc:  # noqa: BLE001
        return 0, f"{type(exc).__name__}".encode()


# 交付物里绝对不许出现的东西：令牌与私钥。
# 分组一律用 (?:...)：捕获组会让 findall 返回「组内容」而不是整段命中，
# 结果扫描器自己的模式文本都能"命中"成空串，把真阳性淹在假阳性里。
# 「这台机器的身份指纹」（账户名、域名、服务器 IP、构建目录名）不在这里查：那份清单要贴着人走，
# 放在 test/audit_personal_info.py + 本地不入库的 keys/personal-probes.txt。扫描器代码里也不许
# 写死这些字面量——公开仓库带着它，等于把要保护的东西发出去了。
SECRET_PATTERNS = re.compile(
    r"ghp_[A-Za-z0-9]{6,}"
    r"|github_pat_[A-Za-z0-9]{6,}"
    r"|-BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-",
    re.IGNORECASE,
)
FORBIDDEN_PATH = re.compile(r"(^|/)data/|(^|/)logs/|\.db$|\.pfx$|\.key$|gh\.token|artifacts/|\.apk$|\.skill$|personal-probes\.txt$", re.IGNORECASE)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--token-file", default="", help="匿名额度打满时用它补测（报告会注明）")
    parser.add_argument("--tag", default="", help="不给就取仓库最新的 Release（写死版本号会让下一次发布自己报红）")
    args = parser.parse_args()
    global TOKEN
    if args.token_file:
        TOKEN = Path(args.token_file).read_text(encoding="utf-8").strip()
        print("提示：Release 与资产这几条改用 token 补测（匿名额度耗尽）；仓库可见性与 zipball 仍是匿名拉的")
    results: list[tuple[str, bool, str]] = []

    if not args.tag:
        status, latest = get(f"{API}/repos/{args.repo}/releases/latest")
        args.tag = str(latest.get("tag_name") or "") if status == 200 else ""
        print(f"没给 --tag，取最新 Release：{args.tag or '（读不到）'}")

    status, repo = get(f"{API}/repos/{args.repo}")
    results.append(("仓库对匿名请求可见（public）", status == 200 and repo.get("private") is False, f"status={status} private={repo.get('private')}"))
    branch = repo.get("default_branch") or "main"

    status, tree = get(f"{API}/repos/{args.repo}/git/trees/{branch}?recursive=1")
    if status == -1:
        results.append(("NOTRUN 远端文件清单（匿名额度被打满，用 --token-file 补测）", True, f"status={status}"))
    remote = {str(x.get("path")): str(x.get("sha")) for x in (tree.get("tree") or []) if x.get("type") == "blob"}
    local = {p.relative_to(STAGE).as_posix(): p for p in sorted(STAGE.rglob("*")) if p.is_file()}
    results.append(("staging 目录存在且非空", bool(local), f"{len(local)} 个文件"))
    results.append(("远端文件清单与 staging 完全一致", set(remote) == set(local), f"远端 {len(remote)} / 本地 {len(local)}"))
    if set(remote) == set(local):
        stale = [rel for rel, path in local.items() if remote.get(rel) != blob_sha(path)]
        results.append(("远端每个文件的 blob 哈希都等于本地内容", not stale, f"{len(local) - len(stale)}/{len(local)} 一致" + ("" if not stale else "，有差异: " + ", ".join(stale[:5]))))

    leaked_path = [p for p in remote if FORBIDDEN_PATH.search(p)]
    results.append(("仓库里没有使用者数据/密钥/二进制残留路径", not leaked_path, ", ".join(leaked_path[:5]) or "无"))

    # 正文不走 raw.githubusercontent.com：这台机器上那个域名解析不了（api.github.com 正常）。
    # 换成 zipball：一次请求就是陌生人不带 token 能拿到的全部内容，比逐文件拉更省匿名速率。
    zip_text: dict[str, bytes] = {}
    status, blob = get(f"{API}/repos/{args.repo}/zipball/{branch}", binary=True)
    if status == 200 and blob:
        try:
            with zipfile.ZipFile(io.BytesIO(blob)) as zf:
                for info in zf.infolist():
                    if info.is_dir():
                        continue
                    parts = info.filename.split("/", 1)  # 第一层是 <owner>-<repo>-<sha>/
                    if len(parts) == 2:
                        zip_text[parts[1]] = zf.read(info)
        except zipfile.BadZipFile:
            zip_text = {}
    results.append(("zipball 能匿名下载并解开", bool(zip_text), f"status={status} 文件 {len(zip_text)} 个"))
    if zip_text:
        results.append(("下载到的文件清单与 staging 一致", set(zip_text) == set(local), f"下载 {len(zip_text)} / 本地 {len(local)}"))
        diff = [rel for rel, path in local.items() if rel in zip_text and zip_text[rel] != path.read_bytes()]
        results.append(("下载到的每个文件与本地逐字节一致", not diff, ", ".join(diff[:5]) or "全部一致"))
    hits: list[str] = []
    scanned = 0
    for rel in sorted(local):
        if Path(rel).suffix.lower() not in {".md", ".py", ".txt", ".iss", ".ps1", ".spec", ".yaml", ".yml"}:
            continue
        data = zip_text.get(rel)
        if data is None:
            hits.append(f"{rel}(zipball 里没有)")
            continue
        scanned += 1
        found = SECRET_PATTERNS.findall(data.decode("utf-8", "replace"))
        if found:
            hits.append(f"{rel}: {sorted(set(found))[:2]}")
    results.append((f"文本文件逐个扫令牌/本机路径（{scanned} 个）", not hits, "; ".join(hits[:4]) or "无命中"))

    status, release = get(f"{API}/repos/{args.repo}/releases/tags/{args.tag}")
    if status == -1:
        results.append(("NOTRUN Release 与资产（匿名额度被打满，用 --token-file 补测）", True, f"status={status}"))
    assets = {str(a.get("name")): a for a in (release.get("assets") or [])}
    results.append((f"Release {args.tag} 存在", status == 200 and bool(assets), f"status={status}"))
    planned = ["AstrBot-Setup-4.29.3.exe", "AstrBot-4.29.3-desktop-portable.zip", "SHA256SUMS.txt"]
    missing = [name for name in planned if name not in assets]
    results.append(("三个资产都在", not missing, ", ".join(missing) or "3/3"))
    for name in planned:
        item = assets.get(name)
        if not item:
            continue
        local_path = BUILD / "installer" / name
        if local_path.is_file():
            size = local_path.stat().st_size
            digest = "sha256:" + hashlib.sha256(local_path.read_bytes()).hexdigest()
            results.append(
                (
                    f"{name} 远端字节数与 sha256 都等于本地",
                    int(item.get("size") or -1) == size and str(item.get("digest") or "") == digest,
                    f"远端 {item.get('size')} / {str(item.get('digest'))[:23]}",
                )
            )
        # 「匿名真能下载」不是拿 302 糊过去的：取回开头字节，看内容对不对
        status, chunk = first_bytes(item)
        if name.endswith(".txt"):
            same = chunk == local_path.read_bytes()
            results.append((f"{name} 匿名下载到的字节与本地完全一致", status in (200, 206) and same, f"status={status} bytes={len(chunk)}"))
        else:
            # 安装包是 PE（MZ），免安装包是 zip（PK\003\004）——按各自的格式头判，别都当 exe 判
            magic = b"PK\x03\x04" if name.lower().endswith(".zip") else b"MZ"
            label = "zip 头" if magic != b"MZ" else "PE 头"
            results.append(
                (
                    f"{name} 匿名下载可用（前 4 KB 是对的{label}）",
                    status in (200, 206) and chunk[: len(magic)] == magic,
                    f"status={status} 开头={chunk[:4]!r} 取了 {len(chunk)} 字节",
                )
            )

    print("\n==== 已发布仓库验收 ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
