// AstrBot 桌面版启动器
//
// 用系统自带的 csc.exe 编译，不引入任何第三方库（六个源文件一起编，含 AstrBotRecovery.cs 纠错、AstrBotBind.cs 扫码登录）：
//   csc /target:winexe /win32icon:build\assets\astrbot.ico /out:build\launcher\AstrBot.exe ^
//       launcher\AstrBotLauncher.cs launcher\AstrBotControls.cs launcher\AstrBotPanelView.cs ^
//       launcher\AstrBotLauncherUi.cs launcher\AstrBotRecovery.cs launcher\AstrBotBind.cs
//
// 职责：拉起 core\AstrBotCore.exe（无控制台窗口）、把它的日志流显示出来、
// 提供面板入口与 QQ 接入表单，关闭窗口只缩到托盘，退出时停掉核心进程。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AstrBotDesktop
{
    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Windows 11 20H1+: 深色标题栏 + 圆角窗口 + Mica 背景。老系统上调用失败就静默忽略。
        public static void ApplyWindowStyle(Form form)
        {
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(form.Handle, 20, ref dark, 4);
            }
            catch (Exception)
            {
            }
            try
            {
                int round = 2;
                DwmSetWindowAttribute(form.Handle, 33, ref round, 4);
            }
            catch (Exception)
            {
            }
            try
            {
                int mica = 3;
                DwmSetWindowAttribute(form.Handle, 38, ref mica, 4);
            }
            catch (Exception)
            {
            }
        }
    }

    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(20, 22, 26);
        public static readonly Color Surface = Color.FromArgb(28, 32, 38);
        public static readonly Color Card = Color.FromArgb(35, 41, 50);
        public static readonly Color Line = Color.FromArgb(48, 56, 68);
        public static readonly Color Text = Color.FromArgb(230, 233, 239);
        public static readonly Color Muted = Color.FromArgb(150, 160, 175);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color AccentDark = Color.FromArgb(56, 108, 210);
        public static readonly Color Ok = Color.FromArgb(53, 192, 138);
        public static readonly Color Warn = Color.FromArgb(240, 167, 46);
        public static readonly Color Bad = Color.FromArgb(228, 92, 92);

        public static Font Title = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        public static Font Body = new Font("Microsoft YaHei UI", 9.5f);
        public static Font Small = new Font("Microsoft YaHei UI", 8.5f);
        public static Font Mono = new Font("Consolas", 9f);

        public static RoundButton Primary(string text)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.UsePalette(Theme.Accent, Color.FromArgb(96, 156, 255), Theme.AccentDark, Color.White);
            b.Font = Body;
            b.Height = 40;
            b.Radius = 6;
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static RoundButton Ghost(string text)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.UsePalette(Theme.Surface, Theme.Card, Theme.Line, Theme.Text);
            b.Font = Body;
            b.Height = 32;
            b.Radius = 6;
            b.FlatAppearance.BorderColor = Theme.Line;
            b.FlatAppearance.BorderSize = 1;
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static InputBox Input(string placeholder)
        {
            InputBox box = new InputBox();
            box.Tag = placeholder;
            return box;
        }

        public static Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = Muted;
            l.Font = Small;
            l.AutoSize = true;
            return l;
        }

        public static GroupBox Section(string text)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.ForeColor = Muted;
            g.Font = Small;
            g.BackColor = Surface;
            g.FlatStyle = FlatStyle.Flat;
            return g;
        }

        public static Region Rounded(int width, int height, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(0, 0, d, d, 180f, 90f);
            path.AddArc(width - d, 0, d, d, 270f, 90f);
            path.AddArc(width - d, height - d, d, d, 0f, 90f);
            path.AddArc(0, height - d, d, d, 90f, 90f);
            path.CloseFigure();
            return new Region(path);
        }
    }

    internal sealed class LauncherSettings
    {
        public int Port = 6185;
        public bool AllowLan;
        public bool OpenPanelOnStart = true;
        public bool MinimizeToTray = true;
        // panel = 打开时直接进内置面板；overview = 停在概览页。
        public string LastView = "overview";
        // QQ 接入客户端（个人号那条路）：目录由人指或自动发现后记住，QQ 号用于快速登录
        public string OneBotDir = "";
        public string OneBotUin = "";
        public bool OneBotAutoStart;
        // 用户指给「一键修复」的免安装包路径（这份程序的完整副本，只读不改）
        public string RestoreZip = "";
        // NapCat 面板口令（v4.18.41 起面板不再往配置里放明文令牌，要拿口令换临时凭证）。
        // 只存在这份本机配置里，日志与诊断包一律打码。
        public string OneBotPanelPassword = "";
        public string PanelHost
        {
            get { return AllowLan ? "0.0.0.0" : "127.0.0.1"; }
        }
        // 浏览器永远走回环，绑 0.0.0.0 时本机也用它打开。
        public string BrowserHost
        {
            get { return "127.0.0.1"; }
        }

        public string FilePath;

        // desktop.conf 也是核心的配置文件（plugin_mirror= 那一行由核心读）。
        // 启动器只该改自己认识的键，把不认识的行原样带回去，否则用户在文件里加的
        // 下载源设置会在下一次点选「记住我」时凭空消失。
        private readonly List<string> _extras = new List<string>();

        private static bool KnownKey(string key)
        {
            return key == "port" || key == "allow_lan" || key == "open_panel"
                || key == "minimize_to_tray" || key == "view"
                || key == "onebot_dir" || key == "onebot_uin" || key == "onebot_autostart"
                || key == "restore_zip" || key == "onebot_panel_password";
        }

        public static LauncherSettings Load(string path)
        {
            LauncherSettings s = new LauncherSettings();
            s.FilePath = path;
            if (!File.Exists(path))
            {
                return s;
            }
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    if (line.Trim().Length > 0)
                    {
                        s._extras.Add(line);
                    }
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (!KnownKey(key))
                {
                    s._extras.Add(key + "=" + val);
                    continue;
                }
                if (key == "port")
                {
                    int parsed;
                    if (int.TryParse(val, out parsed) && parsed > 0 && parsed < 65536)
                    {
                        s.Port = parsed;
                    }
                }
                else if (key == "allow_lan")
                {
                    s.AllowLan = val == "true";
                }
                else if (key == "open_panel")
                {
                    s.OpenPanelOnStart = val == "true";
                }
                else if (key == "minimize_to_tray")
                {
                    s.MinimizeToTray = val == "true";
                }
                else if (key == "view")
                {
                    s.LastView = val == "panel" ? "panel" : "overview";
                }
                else if (key == "onebot_dir")
                {
                    s.OneBotDir = val;
                }
                else if (key == "onebot_uin")
                {
                    s.OneBotUin = val;
                }
                else if (key == "onebot_autostart")
                {
                    s.OneBotAutoStart = val == "true";
                }
                else if (key == "restore_zip")
                {
                    s.RestoreZip = val;
                }
                else if (key == "onebot_panel_password")
                {
                    s.OneBotPanelPassword = val;
                }
            }
            return s;
        }

        public void Save()
        {
            List<string> extras = UnknownLines(FilePath);
            foreach (string extra in _extras)
            {
                if (!extras.Contains(extra))
                {
                    extras.Add(extra);
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("port=" + Port);
            sb.AppendLine("allow_lan=" + (AllowLan ? "true" : "false"));
            sb.AppendLine("open_panel=" + (OpenPanelOnStart ? "true" : "false"));
            sb.AppendLine("minimize_to_tray=" + (MinimizeToTray ? "true" : "false"));
            sb.AppendLine("view=" + LastView);
            if (!string.IsNullOrEmpty(OneBotDir))
            {
                sb.AppendLine("onebot_dir=" + OneBotDir);
            }
            if (!string.IsNullOrEmpty(OneBotUin))
            {
                sb.AppendLine("onebot_uin=" + OneBotUin);
            }
            sb.AppendLine("onebot_autostart=" + (OneBotAutoStart ? "true" : "false"));
            if (RestoreZip.Length > 0)
            {
                sb.AppendLine("restore_zip=" + RestoreZip);
            }
            if (OneBotPanelPassword.Length > 0)
            {
                sb.AppendLine("onebot_panel_password=" + OneBotPanelPassword);
            }
            foreach (string extra in extras)
            {
                sb.AppendLine(extra);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        // 保存前重新读一遍磁盘：人可能是在启动器运行期间才往 desktop.conf 里加
        // plugin_mirror 的，只认载入时那份就会在下一次保存时把它抹掉。
        private static List<string> UnknownLines(string path)
        {
            List<string> extras = new List<string>();
            try
            {
                if (!File.Exists(path))
                {
                    return extras;
                }
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                    int eq = trimmed.IndexOf('=');
                    string key = eq > 0 ? trimmed.Substring(0, eq).Trim() : "";
                    if (key.Length > 0 && KnownKey(key))
                    {
                        continue;
                    }
                    if (!extras.Contains(line))
                    {
                        extras.Add(line);
                    }
                }
            }
            catch (Exception)
            {
            }
            return extras;
        }
    }

    internal sealed class PlatformEntry
    {
        public Dictionary<string, string> Fields = new Dictionary<string, string>();

        public string Get(string key)
        {
            string val;
            return Fields.TryGetValue(key, out val) ? val : "";
        }

        public void Set(string key, string val)
        {
            Fields[key] = val;
        }
    }

    internal static class PlatformIo
    {
        // 与 core 的 --platform-file / --platform-dump 约定的简易块格式：
        // [platform] 每行 key=value，值里的布尔写 true/false，嵌套结构前缀 ~json~
        public static string BuildIni(PlatformEntry entry)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[platform]");
            foreach (KeyValuePair<string, string> kv in entry.Fields)
            {
                sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            sb.AppendLine("[/platform]");
            return sb.ToString();
        }

        public static List<PlatformEntry> ParseDump(string text)
        {
            List<PlatformEntry> list = new List<PlatformEntry>();
            PlatformEntry current = null;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line == "[platform]")
                {
                    current = new PlatformEntry();
                    continue;
                }
                if (line == "[/platform]")
                {
                    if (current != null)
                    {
                        list.Add(current);
                    }
                    current = null;
                    continue;
                }
                if (current == null || line.Length == 0 || !line.Contains("="))
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                current.Fields[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return list;
        }

        public static readonly string[][] QqTypes = new string[][]
        {
            new string[] { "OneBot v11（个人号 QQ：NapCat / Lagrange / go-cqhttp）", "aiocqhttp" },
            new string[] { "QQ 官方机器人（Websocket，推荐）", "qq_official" },
            new string[] { "QQ 官方机器人（Webhook）", "qq_official_webhook" },
            new string[] { "Satori（NapCat 的 Satori 协议）", "satori" },
        };
    }

    internal enum CoreState
    {
        Stopped,
        Starting,
        Running,
        Failed,
    }

    internal sealed class CoreController
    {
        private readonly string _baseDir;
        private readonly string _coreExe;
        private Process _proc;
        private bool _intentionalStop;
        private readonly object _sync = new object();
        private readonly Queue<string> _lines = new Queue<string>();
        private const int MaxLines = 900;

        public event Action<string> Line;
        public event Action<int> ProcessExited;

        public string InitialPassword = "";
        public int Port = 6185;
        public string Host = "127.0.0.1";
        public bool QQConnected;
        public string QQDetail = "";
        public string QQBotId = "";
        public string LastError = "";
        public int QqMessageCount;
        public string LastQqMessage = "";
        public CoreState State = CoreState.Stopped;

        public CoreController(string baseDir)
        {
            _baseDir = baseDir;
            _coreExe = Path.Combine(baseDir, "core", "AstrBotCore.exe");
        }

        public string CorePath
        {
            get { return _coreExe; }
        }

        public bool IsRunning
        {
            get
            {
                Process proc = _proc;
                return proc != null && !proc.HasExited;
            }
        }

        // 供诊断包与验收测试定位真实进程（外部杀掉它才能证明自愈生效）。
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

        public void Start(string extraArgs)
        {
            if (IsRunning)
            {
                return;
            }
            if (!File.Exists(_coreExe))
            {
                State = CoreState.Failed;
                throw new FileNotFoundException("找不到核心程序", _coreExe);
            }

            QQConnected = false;
            QQDetail = "";
            QQBotId = "";
            LastError = "";
            QqMessageCount = 0;
            LastQqMessage = "";
            State = CoreState.Starting;
            _intentionalStop = false;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _coreExe;
            psi.WorkingDirectory = _baseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.Arguments = "--no-browser " + extraArgs;
            psi.EnvironmentVariables["ASTRBOT_ROOT"] = _baseDir;
            psi.EnvironmentVariables["ASTRBOT_DASHBOARD_HOST"] = Host;
            psi.EnvironmentVariables["ASTRBOT_DASHBOARD_PORT"] = Port.ToString();

            _proc = new Process();
            _proc.StartInfo = psi;
            _proc.EnableRaisingEvents = true;
            _proc.OutputDataReceived += OnData;
            _proc.ErrorDataReceived += OnData;
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
                // taskkill always leaves a non-zero code, so an intentional stop
                // must not be reported as a crash.
                bool planned = _intentionalStop || code == 0;
                State = planned ? CoreState.Stopped : CoreState.Failed;
                Action<int> handler = ProcessExited;
                if (handler != null)
                {
                    handler(planned ? 0 : code);
                }
            };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

        private void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null)
            {
                return;
            }
            // loguru colours the stream; a text box would show the escapes verbatim.
            HandleLine(Ansi.Replace(e.Data, ""));
        }

        // 各适配器"连上了"的文案完全不同，只能一条条认：
        // aiocqhttp 打「适配器已连接」，Satori 打「连接成功 - Bot ...」，
        // QQ 官方机器人只打「Websocket session starting」，压根没有"已连接"三个字。
        public void HandleLine(string line)
        {
            if (line.Length == 0)
            {
                return;
            }
            if (line.Contains("AstrBot started") || line.Contains("Running on http"))
            {
                State = CoreState.Running;
            }
            lock (_sync)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }

            Match pwd = Regex.Match(line, "Initial password:\\s*([A-Za-z0-9._\\-]{6,})");
            if (pwd.Success)
            {
                InitialPassword = pwd.Groups[1].Value;
            }
            if (line.Contains("适配器已连接") || line.Contains("连接成功") || line.Contains("Websocket session starting"))
            {
                QQConnected = true;
                QQDetail = TrimTail(line, 70);
            }
            if (line.Contains("适配器已被关闭") || line.Contains("适配器已经被关闭") || line.Contains("连接关闭"))
            {
                QQConnected = false;
            }
            if (line.Contains("Satori 连接成功"))
            {
                Match uid = Regex.Match(line, "user_id=([A-Za-z0-9_\\-]+)");
                if (uid.Success)
                {
                    QQBotId = uid.Groups[1].Value;
                }
            }
            if (line.Contains("[ERROR]") || line.Contains("[CRITICAL]"))
            {
                LastError = TrimTail(line, 90);
            }
            Match msg = Regex.Match(line, "\\[(?<id>[A-Za-z0-9_\\-]+\\((aiocqhttp|qq_official|qq_official_webhook|satori)\\))\\]\\s*(?<body>.*)$");
            if (msg.Success)
            {
                QqMessageCount++;
                LastQqMessage = msg.Groups["body"].Value;
            }

            Action<string> handler = Line;
            if (handler != null)
            {
                handler(line);
            }
        }

        private static string TrimTail(string line, int max)
        {
            int start = line.IndexOf("]: ");
            string body = start >= 0 ? line.Substring(start + 3) : line;
            body = body.Trim();
            return body.Length <= max ? body : body.Substring(0, max) + "...";
        }

        public void Stop()
        {
            _intentionalStop = true;
            Process proc = _proc;
            if (proc == null)
            {
                State = CoreState.Stopped;
                return;
            }
            try
            {
                if (!proc.HasExited)
                {
                    // 冻结进程收不到 Ctrl+C，用 taskkill 连子进程一起收拾干净。
                    Process kill = new Process();
                    kill.StartInfo.FileName = "taskkill.exe";
                    kill.StartInfo.Arguments = "/PID " + proc.Id + " /T /F";
                    kill.StartInfo.UseShellExecute = false;
                    kill.StartInfo.CreateNoWindow = true;
                    kill.Start();
                    kill.WaitForExit(8000);
                }
            }
            catch (Exception)
            {
            }
            State = CoreState.Stopped;
        }

        public bool WaitForPort(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (State == CoreState.Failed || State == CoreState.Stopped)
                {
                    return false;
                }
                using (TcpClient client = new TcpClient())
                {
                    try
                    {
                        IAsyncResult ar = client.BeginConnect("127.0.0.1", Port, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(400) && client.Connected)
                        {
                            client.EndConnect(ar);
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                Thread.Sleep(300);
            }
            return false;
        }

        // 一次性调用：跑核心的一条命令并把输出收回来（自检、写配置）。
        public int RunOnce(string args, out string output, int timeoutMs)
        {
            UiGuard.Note("等核心子进程跑完一次性通道（参数里那条命令要多久说不准）");
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _coreExe;
            psi.WorkingDirectory = _baseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.Arguments = args;
            psi.EnvironmentVariables["ASTRBOT_ROOT"] = _baseDir;

            StringBuilder sb = new StringBuilder();
            using (Process p = Process.Start(psi))
            {
                DataReceivedEventHandler handler = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (sb)
                        {
                            sb.AppendLine(e.Data);
                        }
                    }
                };
                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    output = sb.ToString();
                    return 124;
                }
                p.WaitForExit();
                output = sb.ToString();
                return p.ExitCode;
            }
        }
    }
}
