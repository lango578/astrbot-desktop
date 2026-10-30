"""Publish the Windows build to a public GitHub repo over the REST API.

Why not git: `git ls-remote https://github.com/...` fails to connect on this box while
curl to the same host returns 200 — so pushing a packfile is not an option here. The API
path works: create the repo, write the source tree in one git/trees call, then attach the
binaries as Release assets (a ~190 MB installer cannot live in a git tree anyway: GitHub
rejects files over 100 MB, while release assets allow 2 GB).

Token handling: read from GITHUB_TOKEN or --token-file, never printed, and stripped out of
any error text before it reaches a log.

    $env:GITHUB_TOKEN="..."    # 或 --token-file 指向一个不进仓库的文件
    venv\\Scripts\\python.exe build\\publish_github.py --repo <owner>/astrbot-desktop --dry-run
    venv\\Scripts\\python.exe build\\publish_github.py --repo <owner>/astrbot-desktop
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import mimetypes
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

BUILD = Path(__file__).resolve().parent
ROOT = BUILD.parent
STAGE = ROOT / "publish-repo"
DIST = ROOT / "dist-new" / "AstrBot"
RELEASE_DIR = ROOT / "installer"
API = "https://api.github.com"
UPLOAD = "https://uploads.github.com"


def token(args: argparse.Namespace) -> str:
    if args.token_file:
        return Path(args.token_file).read_text(encoding="utf-8").strip()
    value = os.environ.get("GITHUB_TOKEN", "").strip()
    if not value:
        raise SystemExit("没有 token：设 GITHUB_TOKEN 或给 --token-file（别把 token 写进命令行历史）")
    return value


def scrub(text: str) -> str:
    return re.sub(r"(ghp_|github_pat_)[A-Za-z0-9_]+", r"\1***", text)


def api(method: str, path: str, tok: str, body: dict | None = None, base: str = API, attempts: int = 4, tolerate: tuple[int, ...] = ()):
    url = path if path.startswith("http") else base + path
    data = json.dumps(body).encode() if body is not None else None
    last = ""
    for attempt in range(1, attempts + 1):
        req = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": f"Bearer {tok}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "astrbot-desktop-release",
            **({"Content-Type": "application/json"} if data else {}),
        })
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                raw = resp.read()
                return resp.status, (json.loads(raw) if raw else {})
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", "replace")[:400]
            last = f"{exc.code} {scrub(detail)}"
            # 「查一下仓库在不在」的 404 是正常答案，不是失败：以前这里直接 SystemExit，
            # 结果第一次发布连「不存在就创建」那一步都走不到，整个发布在起跑线就停了。
            if exc.code in tolerate:
                try:
                    return exc.code, json.loads(detail or b"{}")
                except json.JSONDecodeError:
                    return exc.code, {}
            # 4xx 里只有速率限制值得重试；其余立刻报，别把半个仓库推上去
            if exc.code in (403, 429) and "rate limit" in detail.lower():
                time.sleep(min(60, 5 * attempt))
                continue
            if exc.code >= 500:
                time.sleep(2 * attempt)
                continue
            raise SystemExit(f"GitHub 拒绝了 {method} {url}：{last}")
        except Exception as exc:  # noqa: BLE001 - 网络抖动重试
            last = scrub(f"{type(exc).__name__}: {exc}")
            time.sleep(2 * attempt)
    raise SystemExit(f"{method} {url} 连续失败 {attempts} 次：{last}")


def release_assets(tok: str, assets_list_url: str) -> dict:
    """列出一个 Release 已有的资产。

    GET /repos/{o}/{n}/releases/{id}/assets 返回的是 **JSON 数组**，不是 {"assets": [...]}。
    上一版按 dict 去取 .get("assets")，AttributeError 把整个发布打断——那时两个大包已经传完了，
    等于差一个校验和文件却前功尽弃。

    传进来的既可能是 uploads.github.com 的上传地址，也可能是 api.github.com 的列表地址：
    这里统一换算成 API 域名，免得调用方各写一遍 replace 又漏掉（漏过一次，直接 400 Invalid name）。
    """
    list_url = str(assets_list_url).replace(UPLOAD, API)
    _, data = api("GET", list_url, tok, tolerate=(404,))
    items = data if isinstance(data, list) else (data.get("assets") or [])
    return {str(item.get("name")): item for item in items if item.get("name")}


def upload_asset(tok: str, upload_url: str, path: Path, attempts: int = 5) -> dict:
    name = path.name
    url = f"{upload_url}?name={name}"
    ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
    size = path.stat().st_size
    list_url = upload_url.replace(UPLOAD, API)
    last = ""
    for attempt in range(1, attempts + 1):
        # 资产上传不是幂等的：同名项（哪怕是上次断在半路的残货）会让 GitHub 直接 422 already exists，
        # 所以每轮开头先清掉同名的，再传
        for existing, item in release_assets(tok, list_url).items():
            if existing == name:
                api("DELETE", item["url"], tok, tolerate=(404,))
        with path.open("rb") as handle:
            req = urllib.request.Request(url, data=handle, method="POST", headers={
                "Authorization": f"Bearer {tok}",
                "Accept": "application/vnd.github+json",
                "Content-Type": ctype,
                "Content-Length": str(size),
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "astrbot-desktop-release",
            })
            try:
                with urllib.request.urlopen(req, timeout=3600) as resp:
                    return json.loads(resp.read() or b"{}")
            except urllib.error.HTTPError as exc:
                last = f"{exc.code} {scrub(exc.read().decode('utf-8', 'replace')[:300])}"
                if exc.code in (404, 422, 500, 502, 503, 504):
                    time.sleep(3 * attempt)
                    continue
                raise SystemExit(f"上传 {name} 失败：{last}")
            except Exception as exc:  # noqa: BLE001
                last = scrub(f"{type(exc).__name__}: {exc}")
                time.sleep(3 * attempt)
    raise SystemExit(f"上传 {name}（{size/1048576:.0f} MB）连续失败：{last}")


def git_blob_sha(path: Path) -> str:
    """git 的 blob 哈希（sha1("blob <字节数>\\0" + 内容)）——GitHub 树里每个文件挂的就是这个。"""
    data = path.read_bytes()
    head = f"blob {len(data)}".encode() + b"\0"
    return hashlib.sha1(head + data).hexdigest()


def tree_out_of_date(tok: str, owner: str, repo_name: str, head_sha: str, files: list[Path]) -> bool:
    """远端那棵源码树和本地 staging 是不是已经不一致。

    「跳过重复写入」以前只看 commit message，于是出现过：我改完脚本又重跑了组包，
    message 没变就跳过上传，远端留下的是上一版文件——看着发布成功，其实内容旧。
    """
    _, remote = api("GET", f"/repos/{owner}/{repo_name}/git/trees/{head_sha}?recursive=1", tok, tolerate=(404,))
    entries = {str(item.get("path")): str(item.get("sha")) for item in (remote.get("tree") or []) if item.get("type") == "blob"}
    local = {path.relative_to(STAGE).as_posix(): path for path in files}
    if set(entries) != set(local):
        print(f"  远端 {len(entries)} 个文件 / 本地 {len(local)} 个，清单不同 → 需要重传")
        return True
    stale = [rel for rel, path in local.items() if entries.get(rel) != git_blob_sha(path)]
    if stale:
        print(f"  内容有差异，需要重传：{', '.join(sorted(stale)[:6])}" + ("…" if len(stale) > 6 else ""))
        return True
    return False


def commit_author(owner: str) -> dict:
    """提交署名固定用 GitHub 的 noreply 邮箱。

    Data API 不传 author 时用的是 token 所属账号的**公开邮箱**，这台机器上早先那次发布
    把账号绑定的真实邮箱写进了每一个 commit 的作者与提交者字段，公开仓库上谁都能看到
    （那串地址不在这里复述——复述它就等于把它又发布一次）。
    所以这里显式传 noreply，不再依赖账号默认值。
    """
    return {"name": owner, "email": owner + "@users.noreply.github.com"}


def tree_entries(tok: str, owner: str, repo_name: str, files: list[Path]) -> list[dict]:
    """把 staging 里的文件变成 git tree 条目。

    POST /git/trees 的 `content` 要的是**文件原文**，不是 base64；base64 只在 POST /git/blobs
    那一步用。第一版搞反了，结果发上去的 48 个文件全都成了 base64 文本——页面能开、文件都在、
    看 commit 也正常，只有把内容拉回来逐字节比才发现（test/verify_published_repo.py 就是为这个而写的）。
    所以这里一律先建 blob 拿 sha，再按 sha 组树：文本和二进制（公钥 .cer）走同一条路。
    """
    entries: list[dict] = []
    for path in files:
        rel = path.relative_to(STAGE).as_posix()
        _, blob = api("POST", f"/repos/{owner}/{repo_name}/git/blobs", tok, {
            "content": base64.b64encode(path.read_bytes()).decode(),
            "encoding": "base64",
        })
        entries.append({"path": rel, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    return entries


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True, help="owner/name，例如 someone/astrbot-desktop")
    parser.add_argument("--tag", default="v4.29.3-desktop.1")
    parser.add_argument("--token-file", default="")
    parser.add_argument("--dry-run", action="store_true", help="只列出将要上传的东西，不联网写")
    parser.add_argument(
        "--squash",
        action="store_true",
        help="把本次 staging 写成唯一的根提交，并把 tag 移过去（重写公开历史，只用于刚建的、没有别人在用的仓库）",
    )
    parser.add_argument("--description", default="AstrBot Windows 桌面版（非官方构建）：原生启动器 + 无控制台核心 + 内嵌面板 + QQ 扫码登录")
    args = parser.parse_args()

    owner, _, name = args.repo.partition("/")
    if not owner or not name:
        raise SystemExit("--repo 要写成 owner/name")

    if not STAGE.is_dir():
        raise SystemExit("先跑 build/make_public_repo.py 生成 staging 目录")

    files = sorted(p for p in STAGE.rglob("*") if p.is_file())
    # 资产就是 installer/ 里的安装程序、免安装包和校验和。名字带版本号，所以按后缀扫
    # 而不是写死——写死过一次，结果漏传了安装包。安卓用的插件不挂到 Windows 发布页上。
    assets = []
    for path in sorted(RELEASE_DIR.glob("*")):
        if not path.is_file() or path.suffix.lower() not in (".exe", ".zip", ".txt"):
            continue
        low = path.name.lower()
        if low.startswith("onebot-recovery-fix") or low.startswith("iscc"):
            continue
        assets.append(path)

    missing: list[str] = []
    if not any("setup" in p.name.lower() for p in assets):
        missing.append("AstrBot-Setup-*.exe（先跑 ISCC 编译安装程序）")
    if not any("portable" in p.name.lower() for p in assets):
        missing.append("AstrBot-*-portable.zip（免安装包，build/make_release_assets.py）")
    if not any(p.name == "SHA256SUMS.txt" for p in assets):
        missing.append("SHA256SUMS.txt（build/make_release_assets.py 会生成）")

    print(f"仓库: {owner}/{name}  tag={args.tag}")
    print(f"源码: {len(files)} 个文件，共 {sum(p.stat().st_size for p in files)/1024:.0f} KB")
    print("资产:")
    for path in assets:
        print(f"   {path.name}  {path.stat().st_size/1048576:.1f} MB")
    if missing:
        for m in missing:
            print("   [缺] " + m)
        if not args.dry_run:
            raise SystemExit("资产不齐，拒绝发布（先补齐再跑，或用 --dry-run 看计划）")
    if args.dry_run:
        print("\n--dry-run：什么都没上传")
        return 0

    tok = token(args)
    status, me = api("GET", "/user", tok)
    print(f"token 属于: {me.get('login')}（若与 {owner} 不符，说明 token 没权限建这个仓库）")

    status, repo = api("GET", f"/repos/{owner}/{name}", tok, tolerate=(404,))
    if status == 404 or repo.get("message"):
        status, repo = api("POST", "/user/repos", tok, {
            "name": name,
            "description": args.description,
            "private": False,
            "has_issues": True,
            "has_wiki": False,
            "auto_init": False,
        })
        print(f"已创建公开仓库 {repo['full_name']}")
    else:
        print(f"仓库已存在：{repo['full_name']}（public={repo.get('private') is False}）")
    default_branch = repo.get("default_branch") or "main"

    # 一次 trees 调用写完全部源码：这台机器不能走 git push，逐个 contents API 会打 40+ 次请求
    base_sha = None
    # 刚建好的空仓库没有分支，而这个查询对空仓库回的是 **409 "Git Repository is empty"**（不是 404）；
    # 第一次发布就是被这个 409 当成致命错误拦下的，仓库建好了却没写上任何内容。
    status, ref = api("GET", f"/repos/{owner}/{name}/git/ref/heads/{default_branch}", tok, tolerate=(404, 409))
    if status == 200:
        base_sha = ref["object"]["sha"]
    if not base_sha:
        # 空仓库连 POST /git/trees 都会 409「Git Repository is empty」——Data API 不能凭空造第一个
        # tree。所以先用 Contents API 塞一个提交把分支建起来（就用将要上传的那份 README.md，
        # 后面的 trees 提交会把它覆盖成同内容），再走 trees。
        readme = STAGE / "README.md"
        _, boot = api("PUT", f"/repos/{owner}/{name}/contents/README.md", tok, {
            "message": "初始化仓库（发布脚本 bootstrap 第一个提交）",
            "content": base64.b64encode(readme.read_bytes()).decode(),
            "branch": default_branch,
        })
        base_sha = (boot.get("commit") or {}).get("sha")
        print(f"空仓库已 bootstrap：{default_branch}@{(base_sha or '')[:8]}")
        status, ref = api("GET", f"/repos/{owner}/{name}/git/ref/heads/{default_branch}", tok, tolerate=(404, 409))
        if status == 200:
            base_sha = ref["object"]["sha"]
    commit_message = f"发布 {args.tag}：桌面版构建脚本、启动器源码与验收套件"
    if args.squash:
        # 把本次 staging 写成**没有父提交的根提交**，分支和 tag 一起硬指过去。
        # 只适用于刚建的、没有别人在用的仓库：这条会重写公开历史，旧提交里的内容（比如
        # 我中间那几版错成 base64 的源码）就不再是 main 的历史了。
        _, tree = api("POST", f"/repos/{owner}/{name}/git/trees", tok, {"tree": tree_entries(tok, owner, name, files)})
        _, commit = api("POST", f"/repos/{owner}/{name}/git/commits", tok, {
            "message": commit_message,
            "tree": tree["sha"],
            "author": commit_author(owner),
            "committer": commit_author(owner),
        })
        api("PATCH", f"/repos/{owner}/{name}/git/refs/heads/{default_branch}", tok, {"sha": commit["sha"], "force": True})
        print(f"已压成单一根提交：{default_branch}@{commit['sha'][:8]}")
        status, tag_ref = api("GET", f"/repos/{owner}/{name}/git/ref/tags/{args.tag}", tok, tolerate=(404,))
        if status == 200:
            api("PATCH", f"/repos/{owner}/{name}/git/refs/tags/{args.tag}", tok, {"sha": commit["sha"], "force": True})
        else:
            api("POST", f"/repos/{owner}/{name}/git/refs", tok, {"ref": f"refs/tags/{args.tag}", "sha": commit["sha"]})
        print(f"tag {args.tag} 已移到同一提交")
        base_sha = commit["sha"]
    elif base_sha:
        _, head = api("GET", f"/repos/{owner}/{name}/git/commits/{base_sha}", tok)
        same_message = (head.get("message") or "").strip() == commit_message.strip()
        # message 相同不代表内容相同：改完脚本会重新组包，必须逐文件比 blob 哈希
        if same_message and not tree_out_of_date(tok, owner, name, base_sha, files):
            print(f"源码已是本次提交（{default_branch}@{base_sha[:8]}），跳过重复写入")
            head = None
        if head:
            _, tree = api("POST", f"/repos/{owner}/{name}/git/trees", tok, {"tree": tree_entries(tok, owner, name, files)})
            _, commit = api("POST", f"/repos/{owner}/{name}/git/commits", tok, {
                "message": commit_message,
                "tree": tree["sha"],
                "parents": [base_sha],
                "author": commit_author(owner),
                "committer": commit_author(owner),
            })
            api("PATCH", f"/repos/{owner}/{name}/git/refs/heads/{default_branch}", tok, {"sha": commit["sha"]})
            print(f"源码已写入 {default_branch}@{commit['sha'][:8]}")
    else:
        _, tree = api("POST", f"/repos/{owner}/{name}/git/trees", tok, {"tree": tree_entries(tok, owner, name, files)})
        _, commit = api("POST", f"/repos/{owner}/{name}/git/commits", tok, {
            "message": commit_message,
            "tree": tree["sha"],
            "author": commit_author(owner),
            "committer": commit_author(owner),
        })
        api("POST", f"/repos/{owner}/{name}/git/refs", tok, {"ref": f"refs/heads/{default_branch}", "sha": commit["sha"]})
        print(f"源码已写入 {default_branch}@{commit['sha'][:8]}")

    body = release_body(assets)
    # Release 可能上一次已经建好了（脚本中途断过），重复 POST 会 422；先查、有就复用并把正文刷成最新的
    status, release = api("GET", f"/repos/{owner}/{name}/releases/tags/{args.tag}", tok, tolerate=(404,))
    if status == 200 and release.get("upload_url"):
        api("PATCH", f"/repos/{owner}/{name}/releases/{release['id']}", tok, {"body": body})
        print(f"Release 已存在，复用并刷新正文: {release.get('html_url')}")
    else:
        _, release = api("POST", f"/repos/{owner}/{name}/releases", tok, {
            "tag_name": args.tag,
            "name": f"AstrBot 桌面版 {args.tag}",
            "body": body,
            "draft": False,
            "prerelease": False,
            "target_commitish": default_branch,
        })
        print(f"Release 已创建: {release.get('html_url')}")
    upload_url = release["upload_url"].split("{")[0]
    have = release_assets(tok, upload_url)
    for path in assets:
        size = path.stat().st_size
        digest = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
        item = have.get(path.name)
        # 断点续传：远端已经有同名、同大小、同 sha256 的资产就别再传一遍 325 MB 了
        if item and int(item.get("size") or -1) == size and str(item.get("digest") or "") == digest:
            print(f"   跳过 {path.name}（远端 sha256 已一致 {digest[7:19]}…）")
            continue
        if item:
            print(f"   远端同名项大小/摘要不符，删除后重传 {path.name}")
            api("DELETE", item["url"], tok, tolerate=(404,))
        print(f"   上传 {path.name} ({size/1048576:.1f} MB)...", flush=True)
        info = upload_asset(tok, upload_url, path)
        got = int(info.get("size") or 0)
        if got != size:
            raise SystemExit(f"{path.name} 上传后大小不符：本地 {size} 远端 {got}")
        print(f"   完成 {info.get('browser_download_url')}")

    _, fresh = api("GET", f"/repos/{owner}/{name}/releases/tags/{args.tag}", tok)
    items = fresh.get("assets") or []
    print(f"\n核对：Release 资产 {len(items)} 个 / 计划 {len(assets)} 个")
    by_name = {str(a.get("name")): a for a in items}
    bad = 0
    for path in assets:
        size = path.stat().st_size
        digest = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
        item = by_name.get(path.name) or {}
        ok = int(item.get("size") or -1) == size and str(item.get("digest") or "") == digest
        bad += 0 if ok else 1
        print(f"  {'OK  ' if ok else 'BAD '} {path.name}  远端 {item.get('size')} 字节 / 摘要 {str(item.get('digest'))[:23]}")
    print("页面: " + str(fresh.get("html_url")))
    if bad:
        print("有资产的远端摘要与本地不一致，别把这份当成品发出去")
        return 1
    return 0


def release_body(assets) -> str:
    lines = [
        "AstrBot 4.29.3 的 Windows 桌面版构建（**非官方**，上游：https://github.com/AstrBotDevs/AstrBot）。",
        "",
        "- 原生启动器（深色圆角窗口 + 托盘）+ 无控制台窗口核心 + 内嵌管理面板",
        "- QQ 接入：面板里点「扫码登录」，不用手抄 AppID/AppSecret",
        "- 插件与依赖默认走国内源（PyPI 阿里云镜像 + GitHub 归档加速前缀，失败自动回退直连）",
        "- 启动器自带纠错：端口占用自动顺延、面板启动超时看门狗、崩溃退避重启、故障分类给建议、一键导出脱敏诊断包",
        "- 修好了 OneBot / NapCat「重连上但再也收不到消息」的问题：直接改在程序里，不需要您再装任何插件",
        "",
        "## 下载前必读",
        "",
        "- 安装程序与包里的两个 exe 都由本次构建**自签**代码签名（随包附公钥 astrbot-signing.cer，可核对有没有被改过），",
        "  但自签证书没有权威机构背书，Windows SmartScreen 仍会提示一次「未知发布者」。这不是文件被篡改。",
        "- 二选一：运行 `AstrBot-Setup-4.29.3.exe`（安装版，简中/繁中/英文三套界面），",
        "  或解压 `AstrBot-4.29.3-desktop-portable.zip` 后双击里面的 `AstrBot.exe`（免安装，数据都在同目录 data\\ 里）。",
        "- 首次启动窗口里会显示面板初始密码，登录后请先改掉。",
        "- 校验：下载 SHA256SUMS.txt 后 `certutil -hashfile 文件名 SHA256` 比对。",
        "",
        "## 本版验证过的项",
        "",
        "见仓库内 build/README.txt 第八节；OneBot 掉线修复的复现与验证脚本在 test/verify_onebot_recovery.py。",
        "",
        "## 资产",
        "",
    ]
    for path in assets:
        lines.append(f"- `{path.name}` — {path.stat().st_size/1048576:.1f} MB")
    return "\n".join(lines)


if __name__ == "__main__":
    sys.exit(main())
