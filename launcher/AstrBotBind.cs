// 扫码登录聊天平台：跑核心的一次性 --login-bind 进程，把它的 BIND_* 协议行翻译成界面状态。
//
// 为什么走进程而不是面板 HTTP 接口：那个接口要先登录，而启动器手里不一定有密码（初始密码
// 他改过就没有了）。核心进程本身就是配置的合法读者/写者，凭证只落 cmd_config.json。
// 这里也绝不把原始输出行往外贴：日志区会被显示、也会进诊断包，只转发解析过的安全字段。

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AstrBotDesktop
{
    internal sealed class BindSession
    {
        private readonly string _baseDir;
        private readonly string _coreExe;
        private Process _proc;
        private bool _finished;
        private bool _cancelled;

        public event Action<string> QrReady;
        public event Action<string> QrUrl;
        public event Action<string> StateText;
        public event Action<string> Completed;
        public event Action<string> Failed;

        public BindSession(string baseDir, string coreExe)
        {
            _baseDir = baseDir;
            _coreExe = coreExe;
        }

        public bool Running
        {
            get
            {
                Process proc = _proc;
                return proc != null && !proc.HasExited;
            }
        }

        // 备注名是用户输入，直接拼进命令行会让 " 把参数拆开（--bind-timeout 会被顶掉）。
        public static string SafeName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "";
            }
            StringBuilder sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (c == '"' || c == '\r' || c == '\n' || c == '\\')
                {
                    continue;
                }
                sb.Append(c);
            }
            string clean = sb.ToString().Trim();
            return clean.Length > 40 ? clean.Substring(0, 40) : clean;
        }

        // BIND_QR_FILE <路径> / BIND_QR_URL <链接> / BIND_STATE <状态> / BIND_DONE ... / BIND_FAIL <原因>
        public static bool ParseLine(string line, out string kind, out string payload)
        {
            kind = "";
            payload = "";
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }
            string text = line.Trim();
            if (!text.StartsWith("BIND_"))
            {
                return false;
            }
            int space = text.IndexOf(' ');
            kind = space < 0 ? text : text.Substring(0, space);
            payload = space < 0 ? "" : text.Substring(space + 1).Trim();
            // 兜底：万一哪天核心在协议行里带了密钥，宁可丢掉整行也不能显示出去。
            if (payload.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
                || payload.IndexOf("bind_key", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                payload = "（内容已省略）";
            }
            return true;
        }

        public static string StateLabel(string status)
        {
            if (status == "pending")
            {
                return "等待扫码…（用手机 QQ 扫左边的码）";
            }
            if (status == "created")
            {
                return "扫码成功，正在写入配置…";
            }
            return status;
        }

        public void Start(string botType, string name, int timeoutSeconds)
        {
            if (Running)
            {
                return;
            }
            _finished = false;
            _cancelled = false;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _coreExe;
            psi.WorkingDirectory = _baseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.Arguments = "--login-bind " + botType
                + " --bind-name \"" + SafeName(name) + "\""
                + " --bind-timeout " + timeoutSeconds;
            psi.EnvironmentVariables["ASTRBOT_ROOT"] = _baseDir;

            _proc = new Process();
            _proc.StartInfo = psi;
            _proc.EnableRaisingEvents = true;
            _proc.OutputDataReceived += OnLine;
            _proc.ErrorDataReceived += OnLine;
            _proc.Exited += delegate
            {
                if (_finished)
                {
                    return;
                }
                _finished = true;
                Action<string> fail = _cancelled ? Failed : Failed;
                if (fail != null)
                {
                    fail(_cancelled ? "已取消" : "扫码进程提前退出了，可以看日志再试一次");
                }
            };
            try
            {
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception exc)
            {
                _finished = true;
                if (Failed != null)
                {
                    Failed("起不动扫码进程：" + exc.Message);
                }
            }
        }

        public void Cancel()
        {
            _cancelled = true;
            Process proc = _proc;
            if (proc == null)
            {
                return;
            }
            try
            {
                if (!proc.HasExited)
                {
                    Process kill = new Process();
                    kill.StartInfo.FileName = "taskkill.exe";
                    kill.StartInfo.Arguments = "/PID " + proc.Id + " /T /F";
                    kill.StartInfo.UseShellExecute = false;
                    kill.StartInfo.CreateNoWindow = true;
                    kill.Start();
                    kill.WaitForExit(6000);
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            string kind;
            string payload;
            if (e.Data == null || !ParseLine(e.Data, out kind, out payload))
            {
                return;
            }
            if (kind == "BIND_QR_FILE")
            {
                if (QrReady != null)
                {
                    QrReady(payload);
                }
                return;
            }
            if (kind == "BIND_QR_URL")
            {
                if (QrUrl != null)
                {
                    QrUrl(payload);
                }
                return;
            }
            if (kind == "BIND_STATE")
            {
                if (StateText != null)
                {
                    StateText(StateLabel(payload));
                }
                return;
            }
            if (kind == "BIND_DONE")
            {
                _finished = true;
                if (Completed != null)
                {
                    Completed(payload);
                }
                return;
            }
            if (kind == "BIND_FAIL")
            {
                if (_finished)
                {
                    return;
                }
                _finished = true;
                if (Failed != null)
                {
                    Failed(Explain(payload));
                }
            }
        }

        // 代理在做 TLS 中间人时，链子上是它自签的证书，Python 一律报 CERTIFICATE_VERIFY_FAILED。
        // 这句原文对人没用：告诉他到底是什么挡住了、该怎么办。
        private static string Explain(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "扫码任务没建起来（没给出原因）";
            }
            if (raw.Contains("CERTIFICATE_VERIFY_FAILED") || raw.Contains("self-signed"))
            {
                return "这台机器的代理在替换 HTTPS 证书（中间人），扫码到不了 q.qq.com。\n"
                    + "两条路：① 把代理临时关掉再点一次扫码；② 在系统里信任你这个代理的根证书。\n"
                    + "（原始报错：" + raw + "）";
            }
            if (raw.Contains("ConnectError") || raw.Contains("Unable to connect") || raw.Contains("timed out"))
            {
                return "连不上腾讯的扫码接口（这台机器出不去外网，或者代理没开）。\n"
                    + "可以先在右边填 AppID / AppSecret 保存连接 —— 那条路不需要扫码。\n"
                    + "（原始报错：" + raw + "）";
            }
            return raw;
        }
    }

    // 扫码窗口：左边二维码，右边进度与说明，随时可以取消。
    internal sealed class BindDialog : Form
    {
        private readonly PictureBox _qr;
        private readonly Label _status;
        private readonly Label _hint;
        private readonly Label _cap;
        private readonly LinkLabel _link;
        private readonly RoundButton _cancel;
        private string _url = "";
        private bool _clientMode;

        public BindDialog()
        {
            Text = "扫码登录";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 330);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;

            _qr = new PictureBox();
            _qr.SetBounds(22, 22, 220, 220);
            _qr.SizeMode = PictureBoxSizeMode.Zoom;
            _qr.BackColor = Color.White;
            Controls.Add(_qr);

            _cap = Theme.Caption("用手机 QQ 扫这个码");
            _cap.SetBounds(262, 22, 150, 18);
            Controls.Add(_cap);

            _status = new Label();
            _status.Text = "正在准备二维码…";
            _status.ForeColor = Theme.Warn;
            _status.SetBounds(262, 48, 150, 60);
            _status.AutoSize = false;
            Controls.Add(_status);

            _hint = new Label();
            _hint.Text = "扫过后 QQ 会让你确认授权哪个机器人。\r\n凭证只写进本机 data\\cmd_config.json，不会显示在日志里。";
            _hint.ForeColor = Theme.Muted;
            _hint.Font = Theme.Small;
            _hint.SetBounds(262, 116, 150, 90);
            _hint.AutoSize = false;
            Controls.Add(_hint);

            _link = new LinkLabel();
            _link.Text = "码看不清楚？用浏览器打开授权页";
            _link.ForeColor = Theme.Muted;
            _link.Font = Theme.Small;
            _link.SetBounds(262, 212, 156, 32);
            _link.AutoSize = false;
            _link.Visible = false;
            _link.LinkClicked += delegate { OpenUrl(); };
            Controls.Add(_link);

            _cancel = Theme.Ghost("取消");
            _cancel.SetBounds(262, 272, 146, 34);
            _cancel.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(_cancel);

            try
            {
                Native.ApplyWindowStyle(this);
            }
            catch (Exception)
            {
            }
        }

        public string StatusText
        {
            get { return _status.Text; }
        }

        public bool QrShown
        {
            get { return _qr.Image != null; }
        }

        private void OnUi(Action action)
        {
            if (!IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke(action);
            }
            catch (Exception)
            {
            }
        }

        // 同一个窗口也用来显示「QQ 接入客户端」的登录二维码。文案不能照抄官方机器人那套：
        // 这张码是 NapCat 出的、扫它的是 QQ 本体的登录，凭证也不会写进 cmd_config.json。
        public void UseForClientLogin()
        {
            _clientMode = true;
            Text = "登录 QQ 接入客户端";
            _cap.Text = "用手机 QQ 扫这个码登录";
            _hint.Text = "这张码由客户端（NapCat 等）自己出，扫过之后它才有线上会话。\r\n状态跟着这个窗口变，登上了会显示「已登录」。";
        }

        private bool LooksDone(string text)
        {
            if (text.StartsWith("扫码成功"))
            {
                return true;
            }
            return _clientMode && (text.StartsWith("已登录") || text.StartsWith("在线"));
        }

        public void SetStatus(string text)
        {
            OnUi(new Action(delegate
            {
                _status.Text = text;
                _status.ForeColor = LooksDone(text) ? Theme.Ok : Theme.Warn;
            }));
        }

        public void ShowQr(string path)
        {
            OnUi(new Action(delegate
            {
                try
                {
                    if (File.Exists(path))
                    {
                        // 文件会被反复覆盖，读进内存再释放句柄，否则取消都删不掉临时文件。
                        using (Stream stream = File.OpenRead(path))
                        {
                            _qr.Image = Image.FromStream(stream);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }));
        }

        public void SetUrl(string url)
        {
            OnUi(new Action(delegate
            {
                _url = url == null ? "" : url;
                _link.Visible = _url.Length > 0;
            }));
        }

        private void OpenUrl()
        {
            if (_url.Length == 0)
            {
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }
    }
}
