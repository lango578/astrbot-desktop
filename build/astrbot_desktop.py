"""AstrBot Windows desktop entry point.

Pins the data directory next to the executable, keeps the WebUI on loopback by
default, and opens the dashboard once the port answers.
"""

from __future__ import annotations

import argparse
import asyncio
import atexit
import json
import os
import shutil
import socket
import sys
import threading
import time
import traceback
import webbrowser
from pathlib import Path

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 6185

# 插件仓库下载的默认加速源。选它是有实测的：这台机器直连 GitHub 分支归档
# 21 秒拿不到一个字节，走这个前缀 2.7 秒且与直连的 tag 归档逐字节一致
# （核对过程在 test/verify_plugin_sources.py）。data\desktop.conf 里
# 写 plugin_mirror= 可以换成别的，plugin_mirror= 留空就是直连。
DEFAULT_PLUGIN_MIRROR = "https://gh-proxy.com/"

# 插件依赖的 PyPI 源。上游默认也是这个值，这里显式钉住：默认值漂回 pypi.org
# 是没人会发现的慢（实测本机 pypi.org 11.3 秒、这个 0.31 秒）。
DEFAULT_PYPI_INDEX = "https://mirrors.aliyun.com/pypi/simple/"

# Held for the process lifetime so filelock keeps the single-instance lock.
_INSTANCE_LOCK = None

# QQ input/output adapters, by the name the panel shows and the module that
# implements it. Kept explicit so --self-check reports the real import failure
# instead of a generic "adapter missing".
QQ_ADAPTER_MODULES = (
    ("OneBot v11 (个人号: NapCat/Lagrange/go-cqhttp)", "astrbot.core.platform.sources.aiocqhttp.aiocqhttp_platform_adapter"),
    ("QQ 官方机器人 (Websocket)", "astrbot.core.platform.sources.qqofficial.qqofficial_platform_adapter"),
    ("QQ 官方机器人 (Webhook)", "astrbot.core.platform.sources.qqofficial_webhook.qo_webhook_adapter"),
    ("Satori (NapCat Satori 协议)", "astrbot.core.platform.sources.satori.satori_adapter"),
)

# Third-party pieces the frozen build has to carry for the QQ path to work.
RUNTIME_PROBES = (
    ("数据库驱动 aiosqlite", "aiosqlite"),
    ("QQ 语音 silk 编解码 pysilk", "pysilk"),
    ("音频处理 pydub", "pydub"),
    ("官方机器人 SDK botpy", "botpy"),
    ("OneBot SDK aiocqhttp", "aiocqhttp"),
    ("向量化 faiss", "faiss"),
    ("中文分词 jieba", "jieba"),
    ("ASGI 服务器 hypercorn", "hypercorn"),
    ("CA 证书 certifi", "certifi"),
)


def exe_dir() -> Path:
    """Directory holding the running executable (or this script while developing)."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def app_dir() -> Path:
    """Root of the installed app; the launcher lives here and data/ is created here.

    The packaged core runs from <app>/core/AstrBotCore.exe, so its parent is the
    install root. Running the script from build/ keeps that same directory.
    """
    here = exe_dir()
    if getattr(sys, "frozen", False) and here.name.lower() == "core":
        return here.parent
    return here


def suppress_child_console_windows() -> None:
    """Keep helper processes (ffmpeg, ripgrep, plugin subprocesses) from flashing.

    AstrBot launches them through subprocess without console flags, which shows a
    black window for every call once the app itself has no console. Callers that
    pass their own creationflags are left alone.
    """
    if os.name != "nt":
        return
    import subprocess

    original = subprocess.Popen.__init__
    no_window = getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000)

    def init(self, *args, **kwargs):  # noqa: ANN001 - mirrors Popen.__init__
        if not kwargs.get("creationflags"):
            kwargs["creationflags"] = no_window
        original(self, *args, **kwargs)

    subprocess.Popen.__init__ = init


def _point_pip_at_a_readable_ca_bundle() -> None:
    """Give in-process pip a CA bundle that exists on disk.

    Frozen pip resolves its vendored ``pip/_vendor/certifi/cacert.pem`` from a
    path that only lives inside the PYZ archive, so installing a plugin died with
    FileNotFoundError inside truststore. certifi's own copy is collected as a
    real file, so point every consumer at that one.
    """
    try:
        import certifi

        bundle = certifi.where()
    except Exception:
        return
    if not bundle or not os.path.isfile(bundle):
        return
    os.environ.setdefault("PIP_CERT", bundle)
    os.environ.setdefault("REQUESTS_CA_BUNDLE", bundle)
    os.environ.setdefault("SSL_CERT_FILE", bundle)


def configure_runtime() -> tuple[str, int]:
    """Apply desktop defaults through the environment variables AstrBot reads.

    Returns:
        The effective bind host and port.
    """
    root = app_dir()
    # ffmpeg.exe ships at the install root. pydub only looks it up on PATH, so
    # without this QQ voice conversion silently falls back to failing.
    os.environ["PATH"] = os.pathsep.join(
        [str(root), str(exe_dir()), os.environ.get("PATH", "")]
    )
    os.environ.setdefault("ASTRBOT_ROOT", str(root))
    os.environ.setdefault("ASTRBOT_DISABLE_METRICS", "1")
    # AstrBot's packaged-desktop mode: plugin dependencies get installed into
    # data\site-packages with --target instead of into the frozen bundle.
    # ASTRBOT_ROOT above still wins for the data location, so the app stays portable.
    os.environ.setdefault("ASTRBOT_DESKTOP_CLIENT", "1")
    # Marks this backend as app-managed: the WebUI refuses core self-update, which
    # would drop loose .py files into the frozen bundle.
    os.environ.setdefault("ASTRBOT_DESKTOP_MANAGED", "1")
    _point_pip_at_a_readable_ca_bundle()

    host = os.environ.get("DASHBOARD_HOST") or os.environ.get("ASTRBOT_DASHBOARD_HOST")
    if not host:
        host = DEFAULT_HOST
        os.environ["ASTRBOT_DASHBOARD_HOST"] = host

    port_raw = (
        os.environ.get("DASHBOARD_PORT")
        or os.environ.get("ASTRBOT_DASHBOARD_PORT")
        or str(DEFAULT_PORT)
    )
    try:
        port = int(port_raw)
        if not 0 < port < 65536:
            raise ValueError(port_raw)
    except ValueError:
        # A bad env var must not kill a windowed app that has no console to
        # show the traceback in.
        print(f"面板端口 {port_raw!r} 不可用，已回退到 {DEFAULT_PORT}。", file=sys.stderr)
        port = DEFAULT_PORT
    os.environ["ASTRBOT_DASHBOARD_PORT"] = str(port)
    return host, port


def wait_and_open_dashboard(host: str, port: int, timeout: float = 180.0) -> None:
    """Open the default browser once the dashboard port accepts connections."""
    probe_host = "127.0.0.1" if host in ("0.0.0.0", "::", "*") else host
    url = f"http://{probe_host if probe_host != '::1' else '[::1]'}:{port}"
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        family = socket.AF_INET6 if ":" in probe_host else socket.AF_INET
        with socket.socket(family, socket.SOCK_STREAM) as sock:
            sock.settimeout(0.5)
            if sock.connect_ex((probe_host, port)) == 0:
                webbrowser.open(url)
                return
        time.sleep(0.5)


def print_banner(host: str, port: int) -> None:
    from astrbot.core import logger

    probe_host = "127.0.0.1" if host in ("0.0.0.0", "::", "*") else host
    logger.info("AstrBot 桌面版数据目录: %s", app_dir() / "data")
    logger.info("管理面板: http://%s:%s （初始密码在启动日志里）", probe_host, port)


async def run_async(args: argparse.Namespace) -> None:
    """Start the core inside one event loop, mirroring main.py's wiring."""
    import main as astrbot_main

    from astrbot.core import LogBroker, LogManager, db_helper, logger
    from astrbot.core.initial_loader import InitialLoader

    webui_dir = await astrbot_main.check_dashboard_files(args.webui_dir)
    if webui_dir is None:
        logger.critical(
            "找不到可用的 WebUI 静态文件，管理面板将无法打开。请检查安装包是否完整。"
        )

    log_broker = LogBroker()
    LogManager.set_queue_handler(logger, log_broker)

    core_lifecycle = InitialLoader(db_helper, log_broker)
    core_lifecycle.webui_dir = webui_dir
    await core_lifecycle.start()


def desktop_conf() -> dict[str, str]:
    """Read the launcher's key=value file (data\\desktop.conf) without the core."""
    path = Path(os.environ.get("ASTRBOT_ROOT") or app_dir()) / "data" / "desktop.conf"
    values: dict[str, str] = {}
    try:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if line.startswith("#") or "=" not in line:
                continue
            key, _, val = line.partition("=")
            values[key.strip()] = val.strip()
    except OSError:
        pass
    return values


def plugin_mirror_prefix() -> str:
    """插件仓库加速前缀；desktop.conf 里 plugin_mirror= 覆盖，留空表示直连。"""
    conf = desktop_conf()
    if "plugin_mirror" in conf:
        return conf["plugin_mirror"].rstrip("/")
    return DEFAULT_PLUGIN_MIRROR


def apply_plugin_mirror() -> None:
    """让「按仓库地址装/更新插件」默认走国内可达的加速源，失败自动回退直连。

    面板里填了代理就用它的，不覆盖用户的选择。插件市场给的 download_url 是
    AstrBot 官方对象存储的地址，那种不走前缀（前缀只代理 github.com 归档）。
    """
    prefix = plugin_mirror_prefix()
    if not prefix:
        return

    import astrbot.core.zip_updater as zip_updater
    from astrbot.core import logger

    base = zip_updater._RepoZipUpdater
    original = base._download_repository
    if getattr(original, "_desktop_mirror", False):
        return

    async def download_with_mirror(self, target_path, repo_url, proxy=""):
        if proxy:
            return await original(self, target_path, repo_url, proxy)
        try:
            logger.info("插件下载走加速源: %s", prefix)
            return await original(self, target_path, repo_url, prefix)
        except Exception as exc:  # noqa: BLE001 - 加速源挂了不能连带插件也装不上
            logger.warning(
                "加速源下载失败(%s)，改直连 GitHub: %s", prefix, str(exc)[:120]
            )
            return await original(self, target_path, repo_url, "")

    download_with_mirror._desktop_mirror = True
    base._download_repository = download_with_mirror


def apply_onebot_session_recovery() -> None:
    """修 aiocqhttp 反向 WebSocket 的会话登记，让 NapCat 重连后真能接管。

    上游把 API 通道存在 `_wsr_api_clients[self_id]` 里，登记时按 self_id 覆盖，
    注销时也只按 self_id 删，不核对里面存的是不是自己这条连接。于是：

        A 连上（后来被系统冻结）→ B 用同一个 self_id 重连并登记 →
        A 的连接过一会儿才真正死掉 → A 的清理把 B 的登记删了

    结果面板上「适配器已连接」（事件通道是按对象 discard 的，那半边是对的），
    但出站 API 已经没有通道了：机器人看着在线，消息永远发不出去。
    安卓上就是 issue #8/#9 报的「挂后台一会儿 onebot 直接没了」，
    Windows 上断网重连、NapCat 重启同样会踩。

    改两处：注销必须认对象；新连接来了就把旧的那条主动关掉，
    而不是等 TCP 自己超时（手机上可能是几分钟，那几分钟里机器人是聋的）。

    ASTRBOT_DISABLE_SESSION_PATCH=1 关掉这份内建补丁——验收用它证明
    contrib/onebot-recovery-fix 那个插件单独也能修好（安卓走的就是插件这条路）。
    """
    if os.environ.get("ASTRBOT_DISABLE_SESSION_PATCH") == "1":
        return

    try:
        _apply_onebot_session_recovery()
    except Exception as exc:  # noqa: BLE001 - 补丁装不上也只该报警，绝不能挡启动
        print(f"[桌面版] 会话恢复补丁未生效：{type(exc).__name__}: {exc}", file=sys.stderr)


def find_wsr_session_class():
    """按接口找 aiocqhttp 里管反向 WS 会话的类。

    类名不硬编：这一层叫 CQHttp，上游历史上还用过别的名字——上一版我写死了 Bot，
    结果补丁没打上、还把核心启动砸了。找接口比记名字可靠。
    """
    import aiocqhttp

    for obj in vars(aiocqhttp).values():
        if (
            isinstance(obj, type)
            and hasattr(obj, "_add_wsr_api_client")
            and hasattr(obj, "_remove_wsr_api_client")
        ):
            return obj
    return None


def _apply_onebot_session_recovery() -> None:
    from quart import websocket as quart_ws
    import aiocqhttp

    bot = find_wsr_session_class()
    if bot is None:
        print("[桌面版] 会话恢复补丁跳过：aiocqhttp 里没有反向 WS 登记接口，请更新本程序", file=sys.stderr)
        return
    if getattr(bot._add_wsr_api_client, "_desktop_recovery", False):
        return

    def add_client(self) -> None:
        ws = quart_ws._get_current_object()
        self_id = quart_ws.headers["X-Self-ID"]
        previous = self._wsr_api_clients.get(self_id)
        if previous is not None and previous is not ws:
            async def retire() -> None:
                try:
                    await previous.close(code=1000, reason="replaced by a newer connection")
                except Exception:  # noqa: BLE001 - 旧连接本来就死了，关掉失败不该影响新连接
                    pass

            asyncio.ensure_future(retire())
        self._wsr_api_clients[self_id] = ws

    def remove_client(self) -> None:
        ws = quart_ws._get_current_object()
        self_id = quart_ws.headers["X-Self-ID"]
        # 只有存的确实是自己时才删：这一句就是整个 bug 的正解。
        if self._wsr_api_clients.get(self_id) is ws:
            self._wsr_api_clients.pop(self_id, None)

    add_client._desktop_recovery = True
    remove_client._desktop_recovery = True
    bot._add_wsr_api_client = add_client
    bot._remove_wsr_api_client = remove_client


def seed_first_run_config() -> None:
    """Pin the two download-related defaults before AstrBot writes its first config.

    Upstream ships log_file_enable off, which leaves a desktop user with nothing
    to send when the app misbehaves. Its PyPI default happens to be a domestic
    mirror today but can drift back to pypi.org, which measured 11.3 s here
    against 0.31 s for the mirror. Keys are only added while they are still
    absent-or-empty: once the core has loaded it persists every default, so a
    password or index chosen in the panel is never overwritten here.
    """
    config_path = data_dir() / "cmd_config.json"
    config: dict = {}
    if config_path.is_file():
        try:
            config = json.loads(config_path.read_text(encoding="utf-8-sig") or "{}")
        except (OSError, json.JSONDecodeError):
            return
        if "log_file_enable" in config and config.get("pypi_index_url"):
            return
    changed = False
    if "log_file_enable" not in config:
        config["log_file_enable"] = True
        changed = True
    if not config.get("pypi_index_url"):
        config["pypi_index_url"] = DEFAULT_PYPI_INDEX
        changed = True
    if not changed:
        return
    try:
        config_path.parent.mkdir(parents=True, exist_ok=True)
        tmp_path = config_path.parent / (config_path.name + ".tmp")
        tmp_path.write_text(json.dumps(config, ensure_ascii=False, indent=4), encoding="utf-8")
        os.replace(tmp_path, config_path)
    except OSError:
        pass


RUNNING_FILE = "running.json"


def runtime_dir() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    return Path(base) / "AstrBot"


def _read_running() -> list[dict]:
    path = runtime_dir() / RUNNING_FILE
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    return data if isinstance(data, list) else []


def _panel_alive(entry: dict) -> bool:
    """用「面板端口真在监听」判断实例活着，不用 pid：进程被强杀时标记会留下，端口不会。"""
    port = entry.get("port")
    if not isinstance(port, int):
        return False
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.settimeout(0.3)
        return probe.connect_ex(("127.0.0.1", port)) == 0


def register_running(port: int) -> None:
    """登记本实例正在运行，供写配置的一次性命令判断。

    单实例锁是按端口分的（两台不同端口本就该能并存），但 cmd_config.json 是全实例
    共用的那一份——所以写配置前必须问「有没有任何活着的实例」，否则换个端口、
    或者另开一台副本，就能绕过检查把正在跑的核心配置盖掉。
    """
    directory = runtime_dir()
    try:
        directory.mkdir(parents=True, exist_ok=True)
        entries = [e for e in _read_running() if _panel_alive(e) or e.get("pid") == os.getpid()]
        entries.append({"pid": os.getpid(), "port": port, "at": int(time.time())})
        (directory / RUNNING_FILE).write_text(json.dumps(entries), encoding="utf-8")
    except OSError as exc:
        print(f"无法登记运行标记（{exc}），写配置的并发检查会失效。", file=sys.stderr)


def unregister_running() -> None:
    try:
        entries = [e for e in _read_running() if e.get("pid") != os.getpid()]
        (runtime_dir() / RUNNING_FILE).write_text(json.dumps(entries), encoding="utf-8")
    except OSError:
        pass


def running_instance_port() -> int:
    """别的活实例的面板端口；没有就返回 0。"""
    for entry in _read_running():
        if entry.get("pid") == os.getpid():
            continue
        if _panel_alive(entry):
            return int(entry.get("port") or 0)
    return 0


def instance_lock(port: int):
    """Take a per-user, per-port single-instance lock.

    It lives under %LOCALAPPDATA% rather than next to the executable because the
    install folder may be read-only (Program Files), and two copies on the same
    machine would fight over the panel port anyway.
    """
    from filelock import FileLock, Timeout

    base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    directory = Path(base) / "AstrBot"
    try:
        # filelock does not create missing parent directories.
        directory.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        print(f"无法创建锁目录（{exc}），跳过单实例检查。", file=sys.stderr)
        return None
    lock = FileLock(directory / f"astrbot-{port}.lock", timeout=3)
    try:
        lock.acquire()
    except Timeout as exc:
        raise SystemExit("已经有一个 AstrBot 在运行了。请先在系统托盘里退出那个实例。") from exc
    except OSError as exc:
        print(f"无法创建单实例锁（{exc}），继续启动。", file=sys.stderr)
        return None
    return lock


def run(args: argparse.Namespace) -> None:
    host, port = configure_runtime()

    # Must happen before astrbot.core is imported: that import instantiates the
    # global config, which fills every missing default and writes the file back.
    seed_first_run_config()

    import main as astrbot_main

    astrbot_main.check_env()
    apply_plugin_mirror()
    apply_onebot_session_recovery()

    # Keep the lock referenced: filelock releases it once the object dies.
    global _INSTANCE_LOCK
    _INSTANCE_LOCK = instance_lock(port)
    register_running(port)
    atexit.register(unregister_running)

    print_banner(host, port)

    if not args.no_browser:
        threading.Thread(
            target=wait_and_open_dashboard,
            args=(host, port),
            daemon=True,
        ).start()

    asyncio.run(run_async(args))


def _report_module(label: str, module: str) -> int:
    import importlib

    try:
        importlib.import_module(module)
    except Exception as exc:  # noqa: BLE001 - the whole point is to report it
        cause = exc.__cause__ or exc
        print(f"  FAIL {label}: {type(cause).__name__}: {str(cause).splitlines()[0][:120]}")
        return 1
    print(f"  OK   {label}")
    return 0


def self_check() -> int:
    """Verify the packaged build can actually serve QQ, without starting it."""
    host, port = configure_runtime()

    import main as astrbot_main

    astrbot_main.check_env()

    from astrbot.core.config.default import VERSION
    from astrbot.core.desktop_runtime import is_desktop_managed_backend
    from astrbot.core.utils.astrbot_path import (
        get_astrbot_builtin_plugin_path,
        get_astrbot_path,
    )

    probe_host = "127.0.0.1" if host in ("0.0.0.0", "::", "*") else host
    print(f"AstrBot 桌面版自检 (core v{VERSION})")
    print(f"  Python   : {sys.version.split()[0]}  frozen={getattr(sys, 'frozen', False)}")
    print(f"  程序目录 : {app_dir()}")
    print(f"  数据目录 : {data_dir()}")
    print(f"  面板地址 : http://{probe_host}:{port}")
    print(
        f"  运行姿态 : 桌面托管={is_desktop_managed_backend()}（面板里的「更新核心」应被拒绝） "
        f"指标上报={'关' if os.environ.get('ASTRBOT_DISABLE_METRICS') == '1' else '开'}"
    )

    failures = 0
    print("\n[QQ 平台适配器]")
    for label, module in QQ_ADAPTER_MODULES:
        failures += _report_module(label, module)

    print("\n[聊天接入登录]")
    for label, module in LOGIN_BIND_MODULES:
        failures += _report_module(label, module)
    failures += _report_module("二维码生成 qrcode", "qrcode")
    print(f"  OK   命令行入口: --login-bind {' / --login-bind '.join(BIND_TYPES)}")

    print("\n[OneBot 会话恢复]")
    try:
        apply_onebot_session_recovery()
        bot = find_wsr_session_class()

        if bot is None:
            print("  FAIL aiocqhttp 里找不到反向 WS 会话接口（版本变了？重连后机器人会变聋）")
            failures += 1
        else:
            add = bot._add_wsr_api_client
            remove = bot._remove_wsr_api_client
            desktop = getattr(add, "_desktop_recovery", False) and getattr(remove, "_desktop_recovery", False)
            plugin = getattr(add, "_onebot_recovery_patched", False) and getattr(
                remove, "_onebot_recovery_patched", False
            )
            if desktop or plugin:
                source = "+".join(
                    [name for name, on in (("内建", desktop), ("插件", plugin)) if on]
                )
                print(f"  OK   NapCat 重连接管已生效（来源：{source}，类 {bot.__name__}）")
            else:
                print("  FAIL 会话恢复补丁没打上，重连后机器人会变聋（内建被关且未装插件）")
                failures += 1
    except Exception as exc:  # noqa: BLE001 - 报出来，别让它悄悄失效
        print(f"  FAIL 会话恢复补丁不可用: {type(exc).__name__}: {str(exc)[:120]}")
        failures += 1

    print("\n[关键依赖]")
    for label, module in RUNTIME_PROBES:
        failures += _report_module(label, module)

    print("\n[插件依赖安装]")
    failures += _report_module("pip 包管理器", "pip")
    bundle = os.environ.get("PIP_CERT", "")
    if bundle and os.path.isfile(bundle):
        print(f"  OK   pip 的 CA 证书包: {bundle}")
    else:
        print(f"  FAIL pip 的 CA 证书包不可用: {bundle or '未设置'}（装插件会在 truststore 里报 FileNotFoundError）")
        failures += 1
    desktop_mode = os.environ.get("ASTRBOT_DESKTOP_CLIENT") == "1"
    print(
        f"  {'OK  ' if desktop_mode else 'FAIL'} 桌面模式（插件依赖装进 data\\site-packages）: {desktop_mode}"
    )
    if not desktop_mode:
        failures += 1
    print("\n[插件下载源]")
    index_url = ""
    try:
        from astrbot.core import astrbot_config

        index_url = str(astrbot_config.get("pypi_index_url") or "")
    except Exception:  # noqa: BLE001 - 取不到就按未配置处理，下面照样报
        index_url = ""
    if not index_url:
        index_url = DEFAULT_PYPI_INDEX
        print(f"  WARN 配置里 pypi_index_url 是空的，按国内源处理: {index_url}")
    else:
        print(f"  OK   插件依赖 PyPI 源: {index_url}")
    if index_url.startswith("https://pypi.org"):
        print("  WARN 这是境外源，国内装插件依赖会慢到像卡死：面板「高级设置」里把 pypi_index_url 换成国内镜像")
    mirror = plugin_mirror_prefix()
    if mirror:
        print(f"  OK   插件仓库加速前缀: {mirror}（失败自动回退直连；desktop.conf 里 plugin_mirror= 可换可关）")
    else:
        print("  WARN 插件仓库加速未启用：按仓库地址装插件要直连 GitHub，国内常失败")

    print("  ...  pip 试装（走一次索引往返，等价于装插件依赖）")
    pip_code, pip_output = pip_dry_run(index_url)
    if pip_code == 0:
        print("  OK   pip 试装通过，插件依赖可以正常安装")
    else:
        last = [line for line in pip_output.strip().splitlines() if line.strip()]
        print(f"  FAIL pip 试装失败（退出码 {pip_code}）: {(last[-1] if last else '无输出')[:140]}")
        failures += 1

    print("\n[随包文件]")
    checks = (
        ("WebUI 静态文件", Path(get_astrbot_path()) / "astrbot" / "dashboard" / "dist" / "index.html"),
        ("内置插件 astrbot", Path(get_astrbot_builtin_plugin_path()) / "astrbot" / "main.py"),
        ("内置插件 builtin_commands", Path(get_astrbot_builtin_plugin_path()) / "builtin_commands" / "main.py"),
    )
    for label, path in checks:
        if path.is_file():
            print(f"  OK   {label}: {path}")
        else:
            print(f"  FAIL {label}: 缺少 {path}")
            failures += 1

    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        print(f"  OK   ffmpeg: {ffmpeg}")
    else:
        print("  WARN ffmpeg 不在 PATH：QQ 语音/非 wav 音频转换会失败（把 ffmpeg.exe 放在 AstrBot.exe 同目录即可）")

    print("\n结果:", "全部通过" if not failures else f"{failures} 项失败")
    return 1 if failures else 0


INT_CONFIG_KEYS = frozenset({"port", "ws_reverse_port", "api_port", "callback_port"})
BOOL_CONFIG_KEYS = frozenset(
    {
        "enable",
        "use_markdown",
        "is_sandbox",
        "unified_webhook_mode",
        "enable_group_c2c",
        "enable_guild_direct_message",
        "satori_auto_reconnect",
        "active_send_mode",
    }
)
JSON_MARKER = "~json~"


def data_dir() -> Path:
    """Mirror get_astrbot_data_path() without importing the core."""
    return Path(os.environ["ASTRBOT_ROOT"]) / "data"


def _decode_config_value(key: str, raw: str):
    if raw.startswith(JSON_MARKER):
        return json.loads(raw[len(JSON_MARKER) :])
    if key in BOOL_CONFIG_KEYS:
        return raw.strip().lower() in {"1", "true", "yes", "on"}
    if key in INT_CONFIG_KEYS and raw.strip().lstrip("-").isdigit():
        return int(raw)
    return raw.strip()


def _encode_config_value(value) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (dict, list)):
        return JSON_MARKER + json.dumps(value, ensure_ascii=False)
    return str(value)


def read_platform_file(path: str) -> dict:
    """Parse the launcher's [platform] block into a platform config entry."""
    entry: dict = {}
    in_block = False
    for line in Path(path).read_text(encoding="utf-8-sig").splitlines():
        stripped = line.strip()
        if stripped == "[platform]":
            in_block = True
            continue
        if stripped == "[/platform]":
            break
        if not in_block or not stripped or stripped.startswith("#") or "=" not in stripped:
            continue
        key, _, raw = stripped.partition("=")
        entry[key.strip()] = _decode_config_value(key.strip(), raw)

    if not entry.get("type"):
        raise ValueError("平台配置缺少 type")
    entry.setdefault("id", entry["type"])
    entry.setdefault("name", entry["id"])
    entry.setdefault("enable", True)
    if entry["type"] == "aiocqhttp":
        entry.setdefault("ws_reverse_host", "127.0.0.1")
        entry.setdefault("ws_reverse_port", 6199)
    return entry


def upsert_platform_entry(entry: dict) -> str:
    """写一条平台配置进 cmd_config.json（同 id 覆盖），返回 add 或 update。

    Raises:
        RuntimeError: 配置文件读不了或写不回去。
    """
    config_path = data_dir() / "cmd_config.json"
    config: dict = {}
    if config_path.is_file():
        try:
            config = json.loads(config_path.read_text(encoding="utf-8-sig") or "{}")
        except (OSError, json.JSONDecodeError) as exc:
            raise RuntimeError(f"配置文件读不了：{exc}") from exc

    platforms = config.get("platform")
    if not isinstance(platforms, list):
        platforms = []
    mode = "add"
    for index, item in enumerate(platforms):
        if isinstance(item, dict) and item.get("id") == entry.get("id"):
            platforms[index] = entry
            mode = "update"
            break
    else:
        platforms.append(entry)
    config["platform"] = platforms

    try:
        config_path.parent.mkdir(parents=True, exist_ok=True)
        tmp_path = config_path.parent / (config_path.name + ".tmp")
        tmp_path.write_text(json.dumps(config, ensure_ascii=False, indent=4), encoding="utf-8")
        os.replace(tmp_path, config_path)
    except OSError as exc:
        raise RuntimeError(f"配置写不回去：{exc}") from exc
    return mode


def apply_platform_file(path: str) -> int:
    """Upsert one platform entry into data/cmd_config.json."""
    busy = running_instance_port()
    if busy:
        # 运行中的核心持有这份配置，写完它自己一回写就把我们盖掉了
        print(f"PLATFORM_FAILED 已经有一个 AstrBot 在运行（面板端口 {busy}）。请先在启动器/托盘里退出那个实例再改配置。")
        return 4
    try:
        entry = read_platform_file(path)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"PLATFORM_FAILED {exc}")
        return 1
    try:
        mode = upsert_platform_entry(entry)
    except RuntimeError as exc:
        print(f"PLATFORM_FAILED {exc}")
        return 1
    print(f"PLATFORM_WRITTEN id={entry['id']} type={entry['type']} mode={mode}")
    return 0


# 平台自带的扫码登录：省掉「去官网建机器人 → 复制 AppID/密钥 → 粘回面板」这一串
# 最容易出错的手抄。凭证只落在 cmd_config.json 里，stdout 上只出现状态和 appid。
BIND_TYPES = ("qq_official", "weixin_oc")

LOGIN_BIND_MODULES = (
    ("QQ 官方机器人扫码登录", "astrbot.core.platform.sources.qqofficial.login_registration"),
    ("微信客服扫码登录", "astrbot.core.platform.sources.weixin_oc.login_registration"),
)


def _bind_render_qr(text: str, path: Path) -> bool:
    try:
        import qrcode

        path.parent.mkdir(parents=True, exist_ok=True)
        qrcode.make(text).save(str(path))
        return True
    except Exception as exc:  # noqa: BLE001 - 画不出码还有链接可以点，不该卡住登录
        print(f"BIND_QR_ERROR {type(exc).__name__}: {str(exc)[:120]}", flush=True)
        return False


def _bind_flows(bot_type: str):
    """每个类型的「起任务 / 轮询 / 拼平台条目」三元组，其余流程共用。"""
    if bot_type == "qq_official":
        from astrbot.core.platform.sources.qqofficial import login_registration as mod

        async def start():
            reg = await mod.request_qqofficial_login_qr({})
            return reg.qrcode, reg.interval, reg

        async def poll(state):
            return await mod.poll_qqofficial_login_once(
                platform_config={}, task_id=state.task_id, bind_key=state.bind_key
            )

        def finish(result, name):
            return {
                "id": f"qq_official_{result['appid']}",
                "name": name or "QQ 官方机器人",
                "type": "qq_official",
                "enable": True,
                "appid": str(result["appid"]),
                "secret": str(result["secret"]),
            }

        return start, poll, finish

    from astrbot.core.platform.sources.weixin_oc import login_registration as mod

    async def start():
        reg = await mod.request_weixin_oc_login_qr({})
        return reg.qrcode_img_content or reg.qrcode, reg.interval, reg

    async def poll(state):
        return await mod.poll_weixin_oc_login_once(qrcode=state.qrcode)

    def finish(result, name):
        entry = {
            "id": f"weixin_oc_{str(result.get('weixin_oc_account_id') or '')[:12]}",
            "name": name or "微信客服",
            "type": "weixin_oc",
            "enable": True,
        }
        for key in ("weixin_oc_token", "weixin_oc_account_id", "weixin_oc_base_url"):
            if result.get(key):
                entry[key] = str(result[key])
        return entry

    return start, poll, finish


def login_bind(bot_type: str, name: str, timeout: float = 300.0) -> int:
    """扫码登录一个聊天平台，成功后直接把它写进配置。

    启动器不需要登录面板接口（那要密码），也不需要把密钥显示给人看：它读这里的
    BIND_* 行画二维码、报进度，写完配置再重启核心。
    """
    if bot_type not in BIND_TYPES:
        print(f"BIND_FAIL 暂不支持用扫码登录的类型 {bot_type}（可选 {'/'.join(BIND_TYPES)}）", flush=True)
        return 2

    _, port = configure_runtime()
    seed_first_run_config()
    busy = running_instance_port()
    if busy:
        # 扫码结果要写进 cmd_config.json：核心在跑就会和它抢这份文件
        print(f"BIND_FAIL 已经有一个 AstrBot 在运行（面板端口 {busy}）。请先在启动器里停止核心再扫码。", flush=True)
        return 3
    try:
        lock = instance_lock(port)
    except SystemExit as exc:
        print(f"BIND_FAIL {exc}", flush=True)
        return 3

    start, poll, finish = _bind_flows(bot_type)

    async def drive() -> int:
        qr_text, interval, state = await start()
        print(f"BIND_QR_URL {qr_text}", flush=True)
        png = data_dir() / "tmp" / f"bind-{bot_type}.png"
        if _bind_render_qr(qr_text, png):
            print(f"BIND_QR_FILE {png}", flush=True)
        deadline = time.monotonic() + timeout
        beats = 0
        while True:
            await asyncio.sleep(max(float(interval or 2), 1.0))
            if time.monotonic() > deadline:
                raise RuntimeError(f"等扫码超过 {int(timeout)} 秒，重新点一次就好")
            result = await poll(state)
            status = str(result.get("status") or "")
            if status == "pending":
                beats += 1
                if beats % 15 == 1:
                    print("BIND_STATE pending", flush=True)
                continue
            if status == "expired":
                raise RuntimeError("二维码过期了，重新点一次就好")
            if status == "denied":
                raise RuntimeError("您在手机上取消了登录")
            if status != "created":
                raise RuntimeError(str(result.get("message") or f"没认出的状态：{status}"))
            entry = finish(result, name)
            mode = upsert_platform_entry(entry)
            print("BIND_STATE created", flush=True)
            print(f"BIND_DONE id={entry['id']} type={bot_type} mode={mode}", flush=True)
            return 0

    try:
        return asyncio.run(drive())
    except Exception as exc:  # noqa: BLE001 - 界面上要看到人话，不是 traceback
        print(f"BIND_FAIL {type(exc).__name__}: {str(exc)[:200]}", flush=True)
        return 1
    finally:
        try:
            if lock is not None:
                lock.release()
        except Exception:  # noqa: BLE001 - 锁由进程退出兜底
            pass


def dump_platforms(path: str) -> int:
    """Write every configured platform entry out for the launcher to prefill."""
    config_path = data_dir() / "cmd_config.json"
    platforms: list = []
    if config_path.is_file():
        try:
            platforms = json.loads(config_path.read_text(encoding="utf-8-sig") or "{}").get("platform", [])
        except json.JSONDecodeError as exc:
            print(f"PLATFORM_FAILED 配置文件不是合法 JSON: {exc}")
            return 1

    with open(path, "w", encoding="utf-8") as handle:
        for item in platforms:
            if not isinstance(item, dict):
                continue
            handle.write("[platform]\n")
            for key, value in item.items():
                handle.write(f"{key}={_encode_config_value(value)}\n")
            handle.write("[/platform]\n")
    print(f"PLATFORM_DUMPED count={len(platforms)} file={path}")
    return 0


def pip_dry_run(index_url: str = "") -> tuple[int, str]:
    """Run a real in-process pip index round-trip, the way plugin installs do.

    Plugin installs failed silently in the frozen build because pip resolved its
    vendored CA bundle to a path that only exists inside the PYZ archive. This
    exercises that exact code path without touching any plugin, against the same
    index the installer will use: probing pypi.org while the app installs from a
    mirror would make the check measure a path nobody takes.
    """
    import contextlib
    import io

    try:
        from pip._internal.main import main as pip_main
    except Exception as exc:  # noqa: BLE001
        return 1, f"无法导入 pip：{type(exc).__name__}: {exc}"

    args = [
        "install",
        "--dry-run",
        "--no-deps",
        "--disable-pip-version-check",
    ]
    if index_url:
        args += ["-i", index_url]
    args.append("six")

    buffer = io.StringIO()
    code = 1
    try:
        with contextlib.redirect_stdout(buffer), contextlib.redirect_stderr(buffer):
            code = pip_main(args)
    except SystemExit as exc:
        code = int(exc.code or 0)
    except Exception as exc:  # noqa: BLE001
        buffer.write(f"{type(exc).__name__}: {exc}")
        code = 1
    return code, buffer.getvalue()


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="AstrBot", description="AstrBot 桌面版")
    parser.add_argument("--webui-dir", default=None, help="指定 WebUI 静态文件目录")
    parser.add_argument("--host", default=None, help="面板监听地址，默认 127.0.0.1")
    parser.add_argument("--port", type=int, default=None, help="面板端口，默认 6185")
    parser.add_argument(
        "--no-browser", action="store_true", help="启动后不自动打开浏览器"
    )
    parser.add_argument(
        "--reset-password", action="store_true", help="重置并在日志中打印面板初始密码"
    )
    parser.add_argument(
        "--self-check",
        action="store_true",
        help="只做检查：QQ 适配器、关键依赖、随包文件是否齐全，不启动服务",
    )
    parser.add_argument(
        "--platform-file",
        default=None,
        metavar="文件",
        help="读取 [platform] 配置块并写入 data/cmd_config.json（供启动器调用），不启动服务",
    )
    parser.add_argument(
        "--platform-dump",
        default=None,
        metavar="文件",
        help="把当前所有平台配置以 [platform] 块的形式导出到文件，不启动服务",
    )
    parser.add_argument(
        "--login-bind",
        default=None,
        metavar="类型",
        help=f"扫码登录一个聊天平台并写进配置，不启动服务（可选 {'/'.join(BIND_TYPES)}）",
    )
    parser.add_argument("--bind-name", default="", metavar="名字", help="扫码登录成功后写进去的备注名")
    parser.add_argument(
        "--bind-timeout",
        type=float,
        default=300.0,
        metavar="秒",
        help="最多等多久扫码，默认 300 秒",
    )
    parser.add_argument(
        "--qr",
        default="",
        metavar="文本",
        help="把任意文本画成二维码 PNG（给 NapCat 登录码这类外部链接用），配 --qr-out",
    )
    parser.add_argument("--qr-out", default="", metavar="路径", help="--qr 的输出 PNG 路径")
    return parser.parse_args(argv)


def render_qr(text: str, out: str) -> int:
    """一次性小工具：启动器自己不画二维码（免得多带一个库），把码的渲染统一交给核心。"""
    if not out:
        print("QR_ERROR 必须同时给 --qr-out", flush=True)
        return 2
    path = Path(out)
    if not _bind_render_qr(text, path):
        return 1
    print(f"QR_OK {path}", flush=True)
    return 0


def force_utf8_streams() -> None:
    """Stop the console log sink from raising when stdout is redirected.

    AstrBot's startup banner contains emoji. With stdout pointed at a file on a
    zh-CN Windows machine the stream codec is cp936, the record fails to encode,
    and the initial dashboard password never reaches the user.
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, OSError, ValueError):
            pass


def main() -> int:
    force_utf8_streams()
    suppress_child_console_windows()
    args = parse_args(sys.argv[1:])
    if args.host:
        os.environ["ASTRBOT_DASHBOARD_HOST"] = args.host
    if args.port:
        os.environ["ASTRBOT_DASHBOARD_PORT"] = str(args.port)
    if args.reset_password:
        os.environ["ASTRBOT_RESET_DASHBOARD_PASSWORD"] = "1"

    if args.self_check:
        return self_check()
    if args.platform_file:
        configure_runtime()
        return apply_platform_file(args.platform_file)
    if args.platform_dump:
        configure_runtime()
        return dump_platforms(args.platform_dump)
    if args.login_bind:
        return login_bind(args.login_bind, args.bind_name, args.bind_timeout)
    if args.qr:
        return render_qr(args.qr, args.qr_out)

    try:
        run(args)
    except SystemExit as exc:
        code = exc.code if isinstance(exc.code, int) else (0 if exc.code is None else 1)
        if code:
            print(exc, file=sys.stderr)
        return code
    except KeyboardInterrupt:
        print("\nAstrBot 已退出。")
        return 0
    except Exception:
        traceback.print_exc()
        return 1
    return 0


def should_wait_for_enter() -> bool:
    """Only pause for a key press when a real console is attached.

    The launcher starts this process with pipes and no window, so waiting there
    would leave an invisible process blocked on input.
    """
    if os.name != "nt" or not getattr(sys, "frozen", False):
        return False
    try:
        return bool(sys.stdin and sys.stdin.isatty() and sys.stdout and sys.stdout.isatty())
    except (OSError, ValueError):
        return False


if __name__ == "__main__":
    exit_code = main()
    if should_wait_for_enter():
        print("\nAstrBot 已退出，按回车关闭窗口。")
        try:
            input()
        except (EOFError, OSError):
            pass
    sys.exit(exit_code)
