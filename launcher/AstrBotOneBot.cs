// QQ 接入客户端（个人号）的进程管理与状态判读。
//
// 为什么需要这一整块：AstrBot 自己不实现腾讯协议的登录，个人号必须有一个 OneBot 客户端
// （NapCat / Lagrange 这类）在机器上跑着，AstrBot 只是等它反向连过来。原来的界面里
// 「保存并连接」只负责 AstrBot 这一侧，客户端要人自己去找、自己启动、自己看它有没有登上去，
// 掉线了还得自己重启——这就是「启动器只能启动 astrbot，不能启动 QQ 接入」的空档。
//
// 这一版的边界（都是实测出来的，不是猜的）：
//   · 判据用真文件：Shell 认 NapCatWinBootMain.exe + napcat.mjs，Framework 认 napimain.exe + napiloader.dll。
//   · 启动一律调客户端自带的脚本，不重抄它的引导过程：它们的 .bat 里要读注册表找 QQ.exe、
//     要设 NAPCAT_* 四个环境变量、Shell 版还要求管理员。我们自己拼一遍，版本一升级就对不上，
//     而且坏的是别人的程序，排查起来两头不讨好。
//   · 客户端是否真的活着不能只看我们起的那个进程：脚本里用 `start` 把 QQ 拉起来后就返回了。
//     所以判活以它的 WebUI 端口能否连上为准，进程只用来记 PID 和输出。
//   · 反向 WS 的 JSON 字段名取自 v4.18.41 包里的 schema（websocketClients: name/enable/url/
//     messagePostFormat/reportSelfMessage/reconnectInterval/token/debug/heartInterval）。
//     文件不存在时**不凭空造**（配置主体是客户端自己生成的），只给可复制的片段 + 打开目录。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AstrBotDesktop
{
    internal sealed class OneBotClientInfo
    {
        public string Root = "";
        public string Flavor = "";
        public string Entry = "";
        public bool NeedsAdmin;
        public string Hint = "";

        public string Display
        {
            get
            {
                if (Root.Length == 0)
                {
                    return "";
                }
                return Flavor + " — " + Root;
            }
        }
    }

    internal sealed class OneBotController
    {
        private readonly string _appDir;
        private Process _proc;
        private bool _intentionalStop;
        private readonly object _sync = new object();
        private readonly Queue<string> _lines = new Queue<string>();
        private const int MaxLines = 600;

        public event Action<string> Line;
        public event Action<int> ProcessExited;

        public OneBotClientInfo Client;
        public string WebHost = "127.0.0.1";
        public int WebPort = 6099;
        public string WebToken = "";
        public string LoginPhase = "";
        public string QrcodeUrl = "";
        public string LastError = "";
        public string QqUin = "";
        // v4.18.41 的面板不再往 webui.json 里放明文令牌，改成口令登录换凭证。
        // 口令只留内存与设置文件里，绝不进日志、诊断包或错误文案。
        public string WebUiPassword = "";
        public bool PanelAuthorized;
        public string PanelNote = "";
        public string LoginError = "";
        private string _credential = "";
        private DateTime _credentialAt = DateTime.MinValue;

        // AstrBot 的 aiocqhttp 适配器监听的反向 WS；端口与令牌由启动器这边的平台配置决定
        public int ReverseWsPort = 6199;
        public string ReverseWsToken = "";

        public OneBotController(string appDir)
        {
            _appDir = appDir;
        }

        public string DataOneBotRoot
        {
            get { return Path.Combine(_appDir, "data", "onebot"); }
        }

        public string WebUiConfigPath
        {
            get
            {
                if (Client == null || Client.Root.Length == 0)
                {
                    return "";
                }
                return Path.Combine(Client.Root, "config", "webui.json");
            }
        }

        public bool IsRunning
        {
            get
            {
                return PanelAlive();
            }
        }

        public int Pid
        {
            get
            {
                Process proc = _proc;
                if (proc == null)
                {
                    return 0;
                }
                try
                {
                    return proc.HasExited ? 0 : proc.Id;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public IEnumerable<string> Snapshot()
        {
            lock (_sync)
            {
                return new List<string>(_lines);
            }
        }

        private void Emit(string text)
        {
            if (text == null || text.Length == 0)
            {
                return;
            }
            lock (_sync)
            {
                _lines.Enqueue(text);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }
            Action<string> handler = Line;
            if (handler != null)
            {
                handler(text);
            }
        }

        // 客户端自带的启动脚本会把真正的工作进程 detach 出去，输出经常不回我们的管道；
        // 所以两边都收：能看到就写日志，看不到也得靠端口判活，不许把「没输出」当成「没起来」。
        private void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            // 客户端的日志带 ANSI 颜色码，文本框里会显示成 [[32m 这种噪声
            HandleLine(AnsiCode.Replace(e.Data, ""));
        }

        public void HandleLine(string line)
        {
            if (line.Length == 0)
            {
                return;
            }
            Emit(line);
            Match qr = Regex.Match(line, "(https?://wpa[0-9]*\\.qq\\.com/[^\\s\"']+|https?://\\.?qr\\.qq\\.com/[^\\s\"']+)");
            if (qr.Success)
            {
                QrcodeUrl = qr.Value;
            }
            if (line.Contains("waiting_qrcode") || line.Contains("二维码"))
            {
                LoginPhase = "waiting_qrcode";
            }
            if (line.Contains("qrcode_scanned") || line.Contains("已扫描"))
            {
                LoginPhase = "qrcode_scanned";
            }
            if (line.Contains("online") || line.Contains("登录成功") || line.Contains("好友列表"))
            {
                LoginPhase = "online";
            }
        }

        private static readonly Regex AnsiCode = new Regex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

        public void Start(string uin)
        {
            LastError = "";
            if (Client == null || Client.Root.Length == 0)
            {
                LastError = "还没找到 QQ 接入客户端";
                throw new InvalidOperationException(LastError);
            }
            if (!File.Exists(Client.Entry))
            {
                LastError = "找不到启动脚本：" + Client.Entry;
                throw new FileNotFoundException(LastError, Client.Entry);
            }
            if (Client.NeedsAdmin && !IsAdmin())
            {
                LastError = "这个客户端脚本需要管理员权限（它要往 QQ 里注入），已经用 UAC 请求提权";
            }

            QqUin = uin == null ? "" : uin.Trim();
            _intentionalStop = false;

            ProcessStartInfo psi = new ProcessStartInfo();
            // 用 cmd 执行 .bat，工作目录设在客户端根目录——它的脚本里全是 %~dp0 相对路径
            psi.FileName = "cmd.exe";
            psi.Arguments = "/c \"" + Client.Entry + "\"" + (QqUin.Length > 0 ? " " + QqUin : "");
            psi.WorkingDirectory = Client.Root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            if (Client.NeedsAdmin)
            {
                // 提权后拿不到 stdout（UseShellExecute 必须为 true），所以这是"能起来"而不是"能看到输出"
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.CreateNoWindow = false;
                psi.RedirectStandardOutput = false;
                psi.RedirectStandardError = false;
            }

            _proc = new Process();
            _proc.StartInfo = psi;
            _proc.EnableRaisingEvents = true;
            if (psi.RedirectStandardOutput)
            {
                _proc.OutputDataReceived += OnData;
                _proc.ErrorDataReceived += OnData;
            }
            _proc.Exited += delegate
            {
                int code = 0;
                try
                {
                    code = _proc.ExitCode;
                }
                catch (Exception)
                {
                }
                Emit("[QQ接入] 启动脚本退出码 " + code + "（脚本本身常先返回，客户端由端口判活）");
                Action<int> handler = ProcessExited;
                if (handler != null)
                {
                    handler(_intentionalStop ? 0 : code);
                }
            };
            _proc.Start();
            if (psi.RedirectStandardOutput)
            {
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            Emit("[QQ接入] 已调用 " + Path.GetFileName(Client.Entry) + (QqUin.Length > 0 ? "（QQ 号 " + Tail(QqUin) + "）" : ""));
        }

        public void Stop()
        {
            _intentionalStop = true;
            Process proc = _proc;
            int target = Pid;
            if (proc == null || target == 0)
            {
                return;
            }
            try
            {
                // .NET Framework 的 Process.Kill 不带树，而且这里要杀的是 cmd→bat→node/QQ 这一串；
                // 只按 PID 精确杀我们自己起的进程树，绝不按镜像名乱杀（QQ.exe 可能是人正在用的窗口）。
                ProcessStartInfo kill = new ProcessStartInfo();
                kill.FileName = "taskkill.exe";
                kill.Arguments = "/PID " + target + " /T /F";
                kill.UseShellExecute = false;
                kill.CreateNoWindow = true;
                Process.Start(kill);
            }
            catch (Exception)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        public bool PanelAlive()
        {
            UiGuard.Note("客户端面板端口探活");
            if (WebPort <= 0)
            {
                return false;
            }
            string host = WebHost == null || WebHost.Length == 0 || WebHost == "::" ? "127.0.0.1" : WebHost;
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult begin = client.BeginConnect(host, WebPort, null, null);
                    if (!begin.AsyncWaitHandle.WaitOne(300, true))
                    {
                        return false;
                    }
                    client.EndConnect(begin);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public string ReverseWsUrl()
        {
            return "ws://127.0.0.1:" + ReverseWsPort + "/ws";
        }

        // 字段名逐条来自 v4.18.41 包里的 WebsocketClientConfigSchema；多一个少一个都可能是它的校验层拒绝加载
        public string BuildClientJson(string url, string token, string name)
        {
            string safe = name == null || name.Length == 0 ? "AstrBot 桌面版" : name.Replace("\"", "'");
            return "{\n"
                + "  \"name\": \"" + safe + "\",\n"
                + "  \"enable\": true,\n"
                + "  \"url\": \"" + url + "\",\n"
                + "  \"messagePostFormat\": \"array\",\n"
                + "  \"reportSelfMessage\": false,\n"
                + "  \"reconnectInterval\": 5000,\n"
                + "  \"token\": \"" + (token == null ? "" : token.Replace("\"", "'")) + "\",\n"
                + "  \"debug\": false,\n"
                + "  \"heartInterval\": 30000\n"
                + "}";
        }

        public void LoadWebUiConfig()
        {
            UiGuard.Note("读客户端的 webui.json");
            string path = WebUiConfigPath;
            if (path.Length == 0 || !File.Exists(path))
            {
                return;
            }
            try
            {
                Dictionary<string, object> parsed = ParseJson(File.ReadAllText(path, Encoding.UTF8));
                if (parsed == null)
                {
                    return;
                }
                object host;
                if (parsed.TryGetValue("host", out host) && host != null)
                {
                    WebHost = host.ToString();
                }
                object port;
                if (parsed.TryGetValue("port", out port) && port != null)
                {
                    int value;
                    if (int.TryParse(port.ToString(), out value) && value > 0)
                    {
                        WebPort = value;
                    }
                }
                object token;
                if (parsed.TryGetValue("token", out token) && token != null)
                {
                    // 这是客户端面板的令牌：只留在内存里发请求用，绝不打进日志或诊断包
                    WebToken = token.ToString();
                }
            }
            catch (Exception)
            {
            }
        }

        public string FetchQrcodeUrl()
        {
            string body = PostWebUi("/GetQQLoginQrcode", "");
            // 已经登录时这个接口不回二维码，回的是 "QQ Is Logined" —— 这不是失败，是好消息
            if (body.Length == 0)
            {
                return "";
            }
            if (body.Contains("QQ Is Logined"))
            {
                LoginPhase = "online";
                return "";
            }
            string url = Field(body, "data", "qrcode");
            if (url.Length == 0)
            {
                url = Field(body, "data", "qrcodeurl");
            }
            if (url.Length == 0)
            {
                // 老结构里可能直接是字符串，兜一次正则
                Match m = Regex.Match(body, "https?://[^\"'\\\\ ]+");
                url = m.Success ? m.Value : "";
            }
            QrcodeUrl = url;
            return url;
        }

        public string PollLoginPhase()
        {
            string body = PostWebUi("/CheckLoginStatus", "");
            if (body.Length == 0)
            {
                return LoginPhase;
            }
            // 真字段：{ isLogin, isOffline, loginPhase, qrLoginAccepted, coreReady, qrcodeurl, loginError }
            string phase = Field(body, "data", "loginPhase");
            string isLogin = Field(body, "data", "isLogin");
            string error = Field(body, "data", "loginError");
            if (error.Length > 0)
            {
                LoginError = error;
            }
            if (isLogin == "True" || isLogin == "true" || phase == "online" || phase == "succ")
            {
                LoginPhase = "online";
            }
            else if (phase == "qrcode_scanned" || phase == "initializing")
            {
                LoginPhase = "qrcode_scanned";
            }
            else if (phase.Length > 0)
            {
                LoginPhase = phase;
            }
            string qr = Field(body, "data", "qrcodeurl");
            if (qr.Length > 0 && QrcodeUrl.Length == 0)
            {
                QrcodeUrl = qr;
            }
            return LoginPhase;
        }

        // 从 {"code":0,"data":{"k":v}} 里取一个字段；取不到返回空串，不抛
        private static string Field(string json, string outer, string key)
        {
            try
            {
                Dictionary<string, object> top = ParseJson(json);
                if (top == null)
                {
                    return "";
                }
                object box;
                if (!top.TryGetValue(outer, out box))
                {
                    return "";
                }
                Dictionary<string, object> data = box as Dictionary<string, object>;
                if (data == null)
                {
                    return "";
                }
                object value;
                if (!data.TryGetValue(key, out value) || value == null)
                {
                    return "";
                }
                return value is bool ? (value.Equals(true) ? "true" : "false") : value.ToString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        // NapCat 各版本取面板令牌的方式不一样（query、Authorization、cookie 都出现过），
        // 而且没登录时也可能有缓存状态。所以三种都试，全失败就返回空串——界面还有
        // 「打开面板扫码」这条退路，不能把拿不到码写成报错或当成已登录。
        private sealed class Attempt
        {
            public string Query = "";
            public string Header;
            public string Cookie;
        }

        private bool CredentialFresh()
        {
            // 客户端自己认 1 小时（MAX_CREDENTIAL_VALID_SECONDS），我们提前到 40 分钟就重换
            return _credential.Length > 0
                && (DateTime.UtcNow - _credentialAt).TotalMinutes < 40;
        }

        // 用面板口令换一张凭证。算法是从真包里读出来的：sha256(口令 + ".napcat") 的十六进制。
        public bool PanelLogin()
        {
            PanelNote = "";
            if (WebUiPassword.Length == 0)
            {
                PanelAuthorized = false;
                PanelNote = "没填面板口令";
                return false;
            }
            string hash;
            using (System.Security.Cryptography.SHA256 sha =
                new System.Security.Cryptography.SHA256CryptoServiceProvider())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(WebUiPassword + ".napcat"));
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (byte b in digest)
                {
                    sb.Append(b.ToString("x2"));
                }
                hash = sb.ToString();
            }
            string host = WebHost == null || WebHost.Length == 0 || WebHost == "::" ? "127.0.0.1" : WebHost;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "http://" + host + ":" + WebPort + "/api/auth/login");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 4000;
                request.ReadWriteTimeout = 4000;
                byte[] payload = Encoding.UTF8.GetBytes("{\"hash\":\"" + hash + "\"}");
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(payload, 0, payload.Length);
                }
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    Dictionary<string, object> top = ParseJson(text);
                    object dataBox;
                    Dictionary<string, object> data = null;
                    if (top != null && top.TryGetValue("data", out dataBox))
                    {
                        data = dataBox as Dictionary<string, object>;
                    }
                    object cred;
                    if (data != null && data.TryGetValue("Credential", out cred) && cred != null
                        && cred.ToString().Length > 0)
                    {
                        _credential = cred.ToString();
                        _credentialAt = DateTime.UtcNow;
                        PanelAuthorized = true;
                        PanelNote = "面板已授权";
                        Emit("面板口令正确，已换到临时凭证（40 分钟一换，不落日志）");
                        return true;
                    }
                    _credential = "";
                    PanelAuthorized = false;
                    // 口令错与被限流要分开说：后者不该让人怀疑自己记错了密码
                    PanelNote = text.Contains("rate limit") ? "登录太频繁，被面板限流了（等一分钟再试）"
                        : text.Contains("invalid") ? "面板口令不对"
                        : "面板没给凭证：" + ShortBody(text);
                    Emit("面板登录没成功（" + PanelNote + "）");
                    return false;
                }
            }
            catch (Exception exc)
            {
                _credential = "";
                PanelAuthorized = false;
                PanelNote = "问不到面板（" + exc.GetType().Name + "）";
                return false;
            }
        }

        private static string ShortBody(string text)
        {
            if (text == null)
            {
                return "";
            }
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length > 60 ? flat.Substring(0, 60) : flat;
        }

        private string PostWebUi(string route, string extra)
        {
            return PostWebUi(route, extra, true);
        }

        // v4.18.41 实测：整条路由表挂在 /api 下（jn.use("/api", Ar)），
        // 少这个前缀一律 404（"Cannot POST /CheckLoginStatus"），而且除了 /api/auth/login
        // 之外全要带凭证 —— 所以这里既补前缀也带上登录换来的 Credential。
        private string PostWebUi(string route, string extra, bool allowRelogin)
        {
            UiGuard.Note("客户端面板 HTTP 问答（一次最坏堵几秒）");
            string host = WebHost == null || WebHost.Length == 0 || WebHost == "::" ? "127.0.0.1" : WebHost;
            string base_ = "http://" + host + ":" + WebPort + "/api" + route;

            List<Attempt> attempts = new List<Attempt>();
            if (CredentialFresh())
            {
                Attempt credHeader = new Attempt();
                credHeader.Header = "Bearer " + _credential;
                attempts.Add(credHeader);
                // 中间件也认 ?webui_token=，有些反代会吃掉 Authorization 头
                Attempt credQuery = new Attempt();
                credQuery.Query = "webui_token=" + Uri.EscapeDataString(_credential);
                attempts.Add(credQuery);
            }
            if (WebToken.Length > 0)
            {
                Attempt query = new Attempt();
                query.Query = "token=" + Uri.EscapeDataString(WebToken);
                attempts.Add(query);
                Attempt header = new Attempt();
                header.Header = "Bearer " + WebToken;
                attempts.Add(header);
                Attempt cookie = new Attempt();
                cookie.Cookie = "token=" + WebToken;
                attempts.Add(cookie);
            }
            Attempt bare = new Attempt();
            attempts.Add(bare);

            string lastAuthError = "";
            foreach (Attempt attempt in attempts)
            {
                try
                {
                    string target = attempt.Query.Length > 0 ? base_ + "?" + attempt.Query : base_;
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(target);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = 1200;
                    request.ReadWriteTimeout = 1200;
                    request.AllowAutoRedirect = false;
                    if (attempt.Header != null)
                    {
                        request.Headers["Authorization"] = attempt.Header;
                    }
                    if (attempt.Cookie != null)
                    {
                        request.CookieContainer = new CookieContainer();
                        request.CookieContainer.Add(new Cookie("token", WebToken, "/", host));
                    }
                    byte[] payload = Encoding.UTF8.GetBytes(extra.Length == 0 ? "{}" : extra);
                    request.ContentLength = payload.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(payload, 0, payload.Length);
                    }
                    using (WebResponse response = request.GetResponse())
                    {
                        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            string text = reader.ReadToEnd();
                            if (text.Length == 0)
                            {
                                continue;
                            }
                            // 客户端回的是 200 + {"code":-1,"message":"Unauthorized"}，
                            // 光看 HTTP 状态永远看不出没权限；凭证过期（1 小时）也走这条。
                            if (IsAuthError(text))
                            {
                                lastAuthError = text;
                                continue;
                            }
                            PanelAuthorized = true;
                            return text;
                        }
                    }
                }
                catch (WebException)
                {
                }
                catch (Exception)
                {
                }
            }
            if (allowRelogin && lastAuthError.Length > 0 && WebUiPassword.Length > 0)
            {
                // 有一次拿口令换凭证的机会，然后整条重试一遍（不循环，避免口令错时反复撞限流）
                if (PanelLogin())
                {
                    return PostWebUi(route, extra, false);
                }
            }
            if (lastAuthError.Length > 0)
            {
                PanelAuthorized = false;
            }
            return "";
        }

        private static bool IsAuthError(string text)
        {
            return text.Contains("Unauthorized")
                || text.Contains("Authorization Failed")
                || text.Contains("Token has been revoked")
                || text.Contains("Server token not initialized");
        }

        private static Dictionary<string, object> ParseJson(string text)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object parsed = serializer.DeserializeObject(text);
            Dictionary<string, object> map = parsed as Dictionary<string, object>;
            return map;
        }

        private static string Tail(string value)
        {
            return value.Length <= 4 ? value : "***" + value.Substring(value.Length - 2);
        }

        private static bool IsAdmin()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal principal = new System.Security.Principal.WindowsPrincipal(id);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 和 NapCat 自己的脚本用同一条判据：卸载键里的 UninstallString 所在目录就是 QQ 的安装目录
        public static string FindQqNtExe()
        {
            string[] keys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQ",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QQ"
            };
            foreach (string key in keys)
            {
                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (root == null)
                        {
                            continue;
                        }
                        object value = root.GetValue("UninstallString");
                        if (value == null)
                        {
                            continue;
                        }
                        string uninstall = value.ToString().Trim().TrimStart('"');
                        int dir = uninstall.LastIndexOf('\\');
                        if (dir <= 0)
                        {
                            continue;
                        }
                        string exe = uninstall.Substring(0, dir) + "\\QQ.exe";
                        if (File.Exists(exe))
                        {
                            return exe;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return "";
        }

        public static List<OneBotClientInfo> Discover(string appDir, string configured)
        {
            UiGuard.Note("扫描客户端目录（Program Files、桌面、各盘根都要走一遍，冷盘上好几秒）");
            List<OneBotClientInfo> found = new List<OneBotClientInfo>();
            List<string> roots = new List<string>();
            if (!string.IsNullOrEmpty(configured))
            {
                roots.Add(configured);
            }
            string local = Path.Combine(appDir, "data", "onebot");
            roots.Add(local);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NapCat"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NapCat"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NapCat"));
            roots.Add(@"C:\NapCat");
            roots.Add(@"D:\NapCat");

            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }
                    Collect(found, root, 0);
                    foreach (string child in Directory.GetDirectories(root))
                    {
                        // 下载解压通常多套一层目录（NapCat/、napcat-4.18.41/），限深 2 层就够
                        Collect(found, child, 1);
                    }
                }
                catch (Exception)
                {
                }
            }

            List<OneBotClientInfo> unique = new List<OneBotClientInfo>();
            foreach (OneBotClientInfo info in found)
            {
                bool dup = false;
                foreach (OneBotClientInfo other in unique)
                {
                    if (string.Equals(other.Root, info.Root, StringComparison.OrdinalIgnoreCase))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                {
                    unique.Add(info);
                }
            }
            return unique;
        }

        private static void Collect(List<OneBotClientInfo> found, string root, int depth)
        {
            if (depth > 2 || !Directory.Exists(root))
            {
                return;
            }
            OneBotClientInfo shell = Probe(root, "NapCat Shell", "NapCatWinBootMain.exe", "napcat.mjs", new string[] { "launcher-user.bat", "launcher.bat" });
            if (shell != null)
            {
                shell.NeedsAdmin = Path.GetFileName(shell.Entry) == "launcher.bat";
                found.Add(shell);
                return;
            }
            OneBotClientInfo framework = Probe(root, "NapCat Framework", "napimain.exe", "napiloader.dll", new string[] { "napiLoader.bat" });
            if (framework != null)
            {
                found.Add(framework);
            }
        }

        private static OneBotClientInfo Probe(string root, string flavor, string markerA, string markerB, string[] entries)
        {
            if (!File.Exists(Path.Combine(root, markerA)) || !File.Exists(Path.Combine(root, markerB)))
            {
                return null;
            }
            foreach (string entry in entries)
            {
                string path = Path.Combine(root, entry);
                if (File.Exists(path))
                {
                    OneBotClientInfo info = new OneBotClientInfo();
                    info.Root = root;
                    info.Flavor = flavor;
                    info.Entry = path;
                    info.Hint = ReadHint(root);
                    return info;
                }
            }
            return null;
        }

        private static string ReadHint(string root)
        {
            try
            {
                string qqnt = Path.Combine(root, "qqnt.json");
                if (File.Exists(qqnt))
                {
                    Dictionary<string, object> map = ParseJson(File.ReadAllText(qqnt, Encoding.UTF8));
                    object version;
                    if (map != null && map.TryGetValue("version", out version) && version != null)
                    {
                        return "目标 QQ 框架版本 " + version;
                    }
                }
            }
            catch (Exception)
            {
            }
            return "";
        }
    }
}
