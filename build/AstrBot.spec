# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec: AstrBot 4.29.3 Windows desktop build.

Layout produced (onedir) — 这条命令是唯一的正确姿势，--distpath 不能省：

    venv\\Scripts\\python.exe -m PyInstaller --noconfirm --distpath dist-new build\\AstrBot.spec

    dist-new/AstrBot/
      AstrBot.exe          <- console entry, data dir is pinned next to it
      _internal/           <- frozen python + astrbot + WebUI dist + builtin plugins

`dist\\AstrBot` 是上一份被人真实双击过的安装（里面有过 data\\），PyInstaller 默认就往那里写会
把成品覆盖上去，所以 --distpath 是安全边界的一部分；build\\package_dist.py 另有闸门：
目标目录里有 data\\ 就拒绝运行（详见那边的 docstring，2026-10-29 那次误删就是这么定的）。
"""

import sys
from pathlib import Path

from PyInstaller.utils.hooks import (
    collect_data_files,
    collect_dynamic_libs,
    collect_submodules,
    copy_metadata,
)

BUILD_DIR = Path(SPECPATH).resolve()
ROOT = BUILD_DIR.parent
SRC = ROOT / "src" / "AstrBot-master"
DASHBOARD_DIST = ROOT / "dashboard-dist" / "dist"

if not SRC.is_dir():
    raise SystemExit(f"AstrBot source tree is missing: {SRC}")
if not (DASHBOARD_DIST / "index.html").is_file():
    raise SystemExit(f"Dashboard dist is missing: {DASHBOARD_DIST}")

# AstrBot is imported as source code, not installed, so make it importable for
# collect_submodules() and for the analysis pass.
if str(SRC) not in sys.path:
    sys.path.insert(0, str(SRC))

datas = [
    # Served offline from the location resolve_dashboard_dist() calls "bundled".
    (str(DASHBOARD_DIST), "astrbot/dashboard/dist"),
]

def is_source_junk(path: Path) -> bool:
    """Byte-compiled leftovers from a previous run, not application assets."""
    return "__pycache__" in path.parts or path.suffix.lower() in (".pyc", ".pyo")


# Non-python assets inside the package: t2i templates, faiss sqlite init, jieba
# stopwords, plugin i18n, skill markdown.
for path in sorted((SRC / "astrbot").rglob("*")):
    if path.is_file() and path.suffix.lower() != ".py" and not is_source_junk(path):
        dest_dir = path.relative_to(SRC).parent.as_posix()
        datas.append((str(path), dest_dir))

# Loose source files the runtime reads back off disk: star_manager imports each
# bundled plugin's main.py, stat_service parses astrbot/__init__.py for the
# version, the changelog tab lists changelogs/*.md, and STT health checks play
# samples/stt_health_check.wav. Frozen bytecode alone is not enough for these.
for path in sorted((SRC / "astrbot" / "builtin_stars").rglob("*.py")):
    datas.append((str(path), path.relative_to(SRC).parent.as_posix()))

datas.append((str(SRC / "astrbot" / "__init__.py"), "astrbot"))

for folder, dest in ((SRC / "changelogs", "changelogs"), (SRC / "samples", "samples")):
    for path in sorted(folder.rglob("*")):
        if path.is_file():
            datas.append((str(path), dest))

THIRD_PARTY_DATA = (
    "certifi",
    "jieba",
    "faiss",
    "reportlab",
    "markitdown",
    "python_ripgrep",
    "pysilk",
    "mcp",
    "hypercorn",
    "sqlmodel",
    "tzdata",
    # pip runs in-process for plugin installs and reads its vendored CA bundle by path.
    "pip",
    "setuptools",
    "distlib",
    # aiocqhttp serves its static page through import_name/pkg_resources, which
    # needs the package directory as real files inside the bundle.
    "aiocqhttp",
)
for pkg in THIRD_PARTY_DATA:
    try:
        datas += collect_data_files(pkg)
    except Exception as exc:
        print(f"[spec] no data files for {pkg}: {exc}")

binaries = []
for pkg in ("faiss", "pysilk", "python_ripgrep"):
    try:
        binaries += collect_dynamic_libs(pkg)
    except Exception as exc:
        print(f"[spec] no dynamic libs for {pkg}: {exc}")

# importlib.metadata lookups inside libraries and AstrBot's plugin installer.
METADATA_PACKAGES = (
    "hypercorn",
    "fastapi",
    "starlette",
    "pydantic",
    "pydantic-core",
    "mcp",
    "openai",
    "anthropic",
    "aiohttp",
    "quart",
    "opentelemetry-api",
    "opentelemetry-sdk",
    "python-telegram-bot",
    "lark-oapi",
    "slack-sdk",
    "pillow",
    "numpy",
    "pandas",
    "sqlalchemy",
    "pip",
    "setuptools",
    "packaging",
    "aiocqhttp",
    "qq-botpy",
    "silk-python",
)
for pkg in METADATA_PACKAGES:
    try:
        datas += copy_metadata(pkg)
    except Exception as exc:
        print(f"[spec] no metadata for {pkg}: {exc}")

# Every installed distribution's metadata must be visible, not just the list
# above: AstrBot checks importlib.metadata to decide whether a plugin's
# requirements are already satisfied. Missing dist-info makes it reinstall
# packages the core already ships (charset_normalizer, requests, ...), and its
# runtime-conflict guard then refuses to load the plugin at all.
import sysconfig  # noqa: E402

_site_dir = Path(sysconfig.get_paths()["purelib"])
if _site_dir.is_dir():
    for meta in sorted(_site_dir.glob("*.dist-info")):
        datas.append((str(meta), meta.name))

hiddenimports = []


def walk_module_names(pkg_dir: Path, prefix: str) -> list[str]:
    """List importable module names under a source tree without importing them.

    collect_submodules() imports each package while walking it, which fails on
    AstrBot modules that guard optional third-party dependencies. The names are
    only needed as hidden imports, so a file walk is enough.
    """
    names = []
    for path in sorted(pkg_dir.rglob("*.py")):
        rel = path.relative_to(pkg_dir).as_posix()
        if rel == "__init__.py":
            dotted = prefix
        elif path.name == "__init__.py":
            dotted = f"{prefix}.{rel[: -len('/__init__.py')].replace('/', '.')}"
        else:
            dotted = f"{prefix}.{rel[:-3].replace('/', '.')}"
        names.append(dotted)
    return [name for name in names if name]


# Platform adapters, provider sources, pipeline stages and builtin tools are
# imported through module-path strings at runtime, which the static analysis
# cannot follow. Pull in every astrbot module instead, except the bundled
# plugins: those are loaded from their main.py on disk and must stay loose.
hiddenimports += [
    name
    for name in walk_module_names(SRC / "astrbot", "astrbot")
    if not name.startswith("astrbot.builtin_stars")
]
try:
    hiddenimports += collect_submodules("hypercorn")
except Exception as exc:
    print(f"[spec] collect_submodules(hypercorn) failed: {exc}")

EXPLICIT_HIDDEN = (
    # main.py is imported by the desktop launcher.
    "main",
    "runtime_bootstrap",
    # Hypercorn resolves these two protocol backends by dotted name.
    "hypercorn.h11",
    "hypercorn.websocket",
    "hypercorn.asyncio",
    "wsproto",
    "h11",
    # QQ voice + audio pipeline, imported inside functions.
    "pysilk",
    "audioop",
    "pydub",
    # Provider SDKs that are only imported once the user enables them.
    "pkg_resources",
    "setuptools",
    "aiocqhttp",
    "botpy",
    "lark_oapi",
    "dingtalk_stream",
    "slack_sdk",
    "discord",
    "telegram",
    "wechatpy",
    "dashscope",
    "google.genai",
    "xinference_client",
    "shipyard",
    "shipyard.python",
    "shipyard.shell",
    "faiss",
    "rank_bm25",
    "jieba",
    "qrcode",
    "jwt",
    "pyotp",
    "apscheduler",
    "watchfiles",
    "psutil",
    "socks",
    "python_socks",
    # SQLAlchemy resolves dialect modules by dotted name and aiosqlite's driver
    # is only imported from that lazy path; without this the database never opens.
    "aiosqlite",
    "greenlet",
    "sqlalchemy.dialects.sqlite.aiosqlite",
    # Windows has no system tz database; zoneinfo falls back to the tzdata wheel.
    "tzdata",
)
for mod in EXPLICIT_HIDDEN:
    hiddenimports.append(mod)

hiddenimports = sorted(set(hiddenimports))

excludes = [
    "tkinter",
    "matplotlib",
    "scipy",
    "PyQt5",
    "PySide6",
    "IPython",
    "pytest",
    "notebook",
]

a = Analysis(
    [str(BUILD_DIR / "astrbot_desktop.py")],
    pathex=[str(SRC), str(BUILD_DIR)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excludes,
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="AstrBotCore",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    # No console window: the native launcher is the only face of this app, and it
    # reads the log stream over redirected pipes.
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=str(BUILD_DIR / "assets" / "astrbot.ico")
    if (BUILD_DIR / "assets" / "astrbot.ico").is_file()
    else None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="AstrBot",
)
