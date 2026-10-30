"""假 NapCat 客户端：只用来验启动器新增那条链，不代表 NapCat 的真实行为。

它按 NapCat v4.18.41 包里实测到的形状提供三样东西：
  · 一个 WebUI HTTP 端口：POST /GetQQLoginQrcode 回一个二维码链接，POST /CheckLoginStatus
    先是 waiting_qrcode、到点后变 online；
  · stdout 打几行带二维码链接和登录状态的日志（启动器也在这些行里认状态）；
  · 变 online 之后，像真客户端那样以 universal 角色反向连到 AstrBot 的
    ws://127.0.0.1:<port>/ws，并推进一条 /help，收到 AstrBot 的回包就证明整条链通了。

只监听 127.0.0.1，不外发任何流量。
"""

from __future__ import annotations

import argparse
import asyncio
import base64
import hashlib
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

QR_URL = "https://wpa1.qq.com/mock-task?_type=mock"


class State:
    def __init__(self, online_after: float):
        self.started = time.monotonic()
        self.online_after = online_after
        self.polls = 0

    @property
    def online(self) -> bool:
        return time.monotonic() - self.started >= self.online_after

    @property
    def phase(self) -> str:
        return "online" if self.online else "waiting_qrcode"


def make_handler(state: State, log, password: str):
    """照 v4.18.41 真面板的规矩演：路由全在 /api 下，除 /api/auth/login 外都要凭证。

    凭证 = base64(JSON)，由 sha256(口令 + ".napcat") 登录换来；没带或不对就是
    HTTP 200 + {"code":-1,"message":"Unauthorized"}（真面板也是 200 包错误，
    所以只看 HTTP 状态的实现会以为成功——这条形状必须照抄，不然测了个假的）。
    """
    issued = set()

    class Handler(BaseHTTPRequestHandler):
        def _json(self, payload: dict):
            body = json.dumps(payload).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _ok(self, data):
            self._json({"code": 0, "data": data, "message": "success"})

        def _err(self, message: str):
            self._json({"code": -1, "message": message})

        def _body(self) -> dict:
            length = int(self.headers.get("Content-Length") or 0)
            if not length:
                return {}
            try:
                return json.loads(self.rfile.read(length).decode("utf-8", "replace") or "{}")
            except Exception:  # noqa: BLE001
                return {}

        def _authorized(self) -> bool:
            auth = self.headers.get("Authorization") or ""
            token = auth.split(" ")[1] if len(auth.split(" ")) > 1 else ""
            if not token:
                query = urlparse(self.path).query
                token = parse_qs(query).get("webui_token", [""])[0]
            return token in issued

        def do_POST(self):  # noqa: N802
            path = urlparse(self.path).path
            body = self._body()
            state.polls += 1
            if path == "/api/auth/login":
                want = hashlib.sha256((password + ".napcat").encode()).hexdigest()
                if body.get("hash") != want:
                    self._err("token is invalid")
                    log("[mock-napcat] 登录被拒（口令不对）")
                    return
                cred = base64.b64encode(json.dumps({"Data": {"CreatedTime": int(time.time())}}).encode()).decode()
                issued.add(cred)
                self._ok({"Credential": cred})
                log("[mock-napcat] 面板口令正确，发了一张临时凭证")
                return
            if not path.startswith("/api/"):
                # 真面板对没有 /api 前缀的路径直接 404（Express 的 "Cannot POST /xxx"）
                self.send_error(404, "Cannot POST " + path)
                return
            if not self._authorized():
                self._err("Unauthorized")
                log("[mock-napcat] 没带凭证 → Unauthorized（真机上程序跟不上就是这个）")
                return
            if path == "/api/GetQQLoginQrcode":
                if state.online:
                    self._err("QQ Is Logined")
                    return
                log("[mock-napcat] 给二维码链接")
                self._ok({"qrcode": QR_URL})
            elif path == "/api/CheckLoginStatus":
                self._ok({
                    "isLogin": state.online,
                    "isOffline": not state.online,
                    "loginPhase": state.phase,
                    "qrLoginAccepted": not state.online,
                    "coreReady": state.online,
                    "qrcodeurl": "" if state.online else QR_URL,
                    "loginError": "",
                })
            else:
                self._err("unknown route " + path)

        def log_message(self, *args):  # 安静
            pass

    return Handler


async def connect_as_onebot(url: str, token: str, self_id: str, wait: float) -> int:
    """变 online 后反向连 AstrBot，像 NapCat 那样用 universal 角色。"""
    import websockets

    target = f"{url}?access_token={token}" if token else url
    headers = {"X-Client-Role": "universal", "X-Self-ID": self_id}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    sent = False
    async with websockets.connect(target, max_size=None, additional_headers=headers) as ws:
        print("[mock-napcat] 已反向连上 AstrBot", flush=True)
        deadline = time.monotonic() + wait
        while time.monotonic() < deadline:
            if not sent:
                await ws.send(json.dumps({
                    "time": int(time.time()),
                    "self_id": int(self_id),
                    "post_type": "message",
                    "message_type": "private",
                    "sub_type": "friend",
                    "message_id": 7001,
                    "user_id": 12345,
                    "group_id": 0,
                    "sender": {"user_id": 12345, "nickname": "Mocker", "card": ""},
                    "message": [{"type": "text", "data": {"text": "/help"}}],
                    "raw_message": "/help",
                    "font": 0,
                }))
                sent = True
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=2.0)
            except asyncio.TimeoutError:
                continue
            try:
                frame = json.loads(raw)
            except (TypeError, json.JSONDecodeError):
                continue
            if "action" in frame:
                action = frame["action"]
                print(f"[mock-napcat] AstrBot 发来回包动作：{action}", flush=True)
                await ws.send(json.dumps({
                    "status": "ok", "retcode": 0, "data": {"message_id": 7002}, "echo": frame.get("echo"),
                }))
                if action in ("send_msg", "send_private_msg", "send_group_msg"):
                    print("[mock-napcat] PASS 整条链通了：客户端推进消息，AstrBot 回出了消息", flush=True)
                    return 0
    print("[mock-napcat] 没等到 AstrBot 的回包", flush=True)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(description="假 NapCat 客户端（WebUI + 反向 OneBot 连接）")
    parser.add_argument("--webui-port", type=int, default=6099)
    parser.add_argument("--online-after", type=float, default=6.0)
    parser.add_argument("--reverse-ws", default="")
    parser.add_argument("--token", default="")
    parser.add_argument("--self-id", default="90210")
    parser.add_argument("--hold", type=float, default=45.0)
    parser.add_argument("--panel-password", default="mock-panel-secret",
                    help="面板口令：登录要按真面板的算法 sha256(口令+\".napcat\") 算哈希")
    args = parser.parse_args()

    state = State(args.online_after)

    def log(line: str) -> None:
        print(line, flush=True)

    server = ThreadingHTTPServer(("127.0.0.1", args.webui_port), make_handler(state, log, args.panel_password))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    log(f"[mock-napcat] WebUI 监听 127.0.0.1:{args.webui_port}")
    log(f"[mock-napcat] 请使用手机QQ扫码: {QR_URL}")
    log("[mock-napcat] 登录状态 waiting_qrcode")

    deadline = time.monotonic() + 60
    while time.monotonic() < deadline and not state.online:
        time.sleep(0.4)
    log("[mock-napcat] 登录状态 online")

    code = 0
    if args.reverse_ws:
        try:
            code = asyncio.run(connect_as_onebot(args.reverse_ws, args.token, args.self_id, args.hold))
        except Exception as exc:  # noqa: BLE001
            log(f"[mock-napcat] 反向连接失败 {type(exc).__name__}: {str(exc)[:120]}")
            code = 1
    else:
        time.sleep(args.hold)

    server.shutdown()
    return code


if __name__ == "__main__":
    sys.exit(main())
