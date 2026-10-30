// AstrBot 桌面版启动器的界面与命令行入口。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AstrBotDesktop
{
    internal static class Capture
    {
        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // 无控制台的窗口程序，验收时靠它把界面留成证据。
        public static bool ToPng(Control control, string path)
        {
            RECT rect;
            if (!GetWindowRect(control.Handle, out rect))
            {
                return false;
            }
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }
            using (Bitmap bmp = new Bitmap(width, height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        if (!PrintWindow(control.Handle, hdc, 2))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return true;
        }
    }

    internal sealed class FieldSpec
    {
        public string Key;
        public string Label;
        public string Default;
        public bool Wide;

        public FieldSpec(string key, string label, string defaultValue, bool wide)
        {
            Key = key;
            Label = label;
            Default = defaultValue;
            Wide = wide;
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly Dictionary<string, FieldSpec[]> TypeFields = new Dictionary<string, FieldSpec[]>();

        private readonly string _baseDir;
        private readonly LauncherSettings _settings;
        private readonly CoreController _core;
        private readonly string _autostartKey = "AstrBot";

        private RoundPanel _header;
        private RoundPanel _statusPill;
        private Label _statusText;
        private Label _urlLabel;
        private TextBox _logBox;
        private RoundPanel _passwordCard;
        private Label _passwordValue;
        private Label _passwordHint;
        private RoundButton _passwordReset;
        private bool _coreResetOnce;
        private Label _qqStatus;
        private ComboBox _typeBox;
        private InputBox _nameInput;
        private InputBox _idInput;
        private RoundPanel _fieldsPanel;
        private readonly Dictionary<string, InputBox> _fieldInputs = new Dictionary<string, InputBox>();
        private CheckBox _autostartCheck;
        private CheckBox _openPanelCheck;
        private CheckBox _lanCheck;
        private NotifyIcon _tray;
        private ToolTip _tips;
        private PanelView _panelView;
        // 打开内置面板时把窗口撑到这个宽度，切回概览就还原（0 表示这次没撑过）
        private const int PanelWideWidth = 1240;
        // 演练桩的面板口令（test/mock_napcat_client.py --panel-password 同一个值）
        private const string MockPanelPassword = "mock-panel-secret";
        private int _panelGrewTo;
        private RoundButton _tabOverview;
        private RoundButton _tabPanel;
        private RoundButton _scanButton;
        private OneBotController _onebot;
        private Label _obStatus;
        private InputBox _obUin;
        private InputBox _obPanelPassword;
        private Label _obPanelNote;
        private RoundButton _obStart;
        private RoundButton _obStop;
        private RoundButton _obQr;
        private RoundButton _obDownload;
        private OneBotInstaller _obInstaller;
        private BindDialog _obDialog;
        private System.Windows.Forms.Timer _obTimer;
        private bool _obBusy;
        private readonly object _logSync = new object();
        private readonly List<string> _logPending = new List<string>();
        private System.Windows.Forms.Timer _logTimer;
        private int _logLines;
        private bool _obWasAlive;
        // 面板探活的结果缓存：状态栏只读这个值，绝不在 UI 线程上自己探端口。
        private bool _obAlive;
        private bool _obDiscovering;
        private bool _obStartWhenFound;
        private bool _repairBusy;
        private string _obScratchApp = "";
        private int _obRestartAttempt;
        private BindSession _bindSession;
        private BindDialog _bindDialog;
        private bool _quitting;
        private bool _panelOpenedOnce;

        // 纠错机制：崩溃后退避重启、故障分类、脱敏诊断导出。
        private readonly RecoveryPolicy _recovery = new RecoveryPolicy();
        private System.Windows.Forms.Timer _recoveryTimer;
        private string _lastFault = "";
        public string LastDiagnosticsPath = "";
        public string LastRecoveryNote = "";
        public int FaultDialogShown;
        private const int StartupTimeoutMs = 120000;

        static MainForm()
        {
            TypeFields["aiocqhttp"] = new FieldSpec[]
            {
                new FieldSpec("ws_reverse_host", "监听地址", "127.0.0.1", false),
                new FieldSpec("ws_reverse_port", "监听端口", "6199", false),
                new FieldSpec("ws_reverse_token", "连接令牌（NapCat 里填同一个）", "", true),
            };
            TypeFields["qq_official"] = new FieldSpec[]
            {
                new FieldSpec("appid", "机器人 AppID", "", false),
                new FieldSpec("secret", "机器人 AppSecret", "", true),
                new FieldSpec("enable_group_c2c", "群与私聊（true/false）", "true", false),
            };
            TypeFields["qq_official_webhook"] = new FieldSpec[]
            {
                new FieldSpec("appid", "机器人 AppID", "", false),
                new FieldSpec("secret", "机器人 AppSecret", "", true),
                new FieldSpec("port", "回调端口", "6196", false),
            };
            TypeFields["satori"] = new FieldSpec[]
            {
                new FieldSpec("satori_endpoint", "Satori 事件地址", "ws://127.0.0.1:5140/satori/v1/events", true),
                new FieldSpec("satori_api_base_url", "Satori API 地址", "http://127.0.0.1:5140/satori/v1", true),
                new FieldSpec("satori_token", "令牌", "", false),
            };
        }

        public MainForm(string baseDir)
        {
            _baseDir = baseDir;
            _settings = LauncherSettings.Load(Path.Combine(baseDir, "data", "desktop.conf"));
            _core = new CoreController(baseDir);
            _core.Host = _settings.PanelHost;
            _core.Port = _settings.Port;
            _tips = new ToolTip();

            UiGuard.UiThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Text = "AstrBot 桌面版 · " + Build.Stamp;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 720);
            ClientSize = new Size(940, 800);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Icon = LoadIcon();
            try
            {
                Native.ApplyWindowStyle(this);
            }
            catch (Exception)
            {
            }

            BuildHeader();
            BuildLeft();
            BuildRight();
            BuildLog();
            BuildPanelView();
            BuildTray();

            _core.Line += OnCoreLine;
            _core.ProcessExited += OnCoreExited;
            Load += delegate
            {
                if (!AutoStart)
                {
                    return;
                }
                // 回填表单要在核心起来之前做：那时 cmd_config.json 只有这一个读者。
                ThreadPool.QueueUserWorkItem(delegate
                {
                    LoadExistingPlatform();
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new Action(StartCore));
                    }
                });
            };
        }

        private Icon LoadIcon()
        {
            try
            {
                string path = Path.Combine(_baseDir, "astrbot.ico");
                if (File.Exists(path))
                {
                    return new Icon(path);
                }
                string core = Path.Combine(_baseDir, "core", "AstrBotCore.exe");
                if (File.Exists(core))
                {
                    return Icon.ExtractAssociatedIcon(core);
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private void BuildHeader()
        {
            _header = new RoundPanel();
            _header.Radius = 12;
            _header.SetBounds(20, 18, ClientSize.Width - 40, 76);
            _header.BackColor = Theme.Surface;
            _header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_header);

            PictureBox logo = new PictureBox();
            logo.SetBounds(20, 18, 40, 40);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            try
            {
                string png = Path.Combine(_baseDir, "astrbot.png");
                if (File.Exists(png))
                {
                    // Image.FromFile 会为位图的生命周期持有文件句柄：整个运行期间这个 png 都锁着，
                    // 「一键修复」想还原它都动不了。读进内存再解，句柄立刻还。
                    logo.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(png)));
                }
                else if (Icon != null)
                {
                    logo.Image = Icon.ToBitmap();
                }
            }
            catch (Exception)
            {
            }
            _header.Controls.Add(logo);

            Label title = new Label();
            title.Text = "AstrBot 桌面版";
            title.Font = Theme.Title;
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.SetBounds(74, 16, 300, 30);
            _header.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "多平台机器人 · 面板 http://127.0.0.1:" + _settings.Port;
            sub.Font = Theme.Small;
            sub.ForeColor = Theme.Muted;
            sub.AutoSize = true;
            sub.SetBounds(76, 46, 460, 18);
            _header.Controls.Add(sub);
            _urlLabel = sub;

            _statusPill = new RoundPanel();
            _statusPill.Radius = 16;
            _statusPill.SetBounds(_header.Width - 190, 22, 170, 32);
            _statusPill.BackColor = Theme.Card;
            _statusPill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _header.Controls.Add(_statusPill);

            RoundPanel dot = new RoundPanel();
            dot.Name = "statusDot";
            dot.Radius = 5;
            dot.DrawBorder = false;
            dot.SetBounds(16, 12, 10, 10);
            dot.BackColor = Theme.Muted;
            _statusPill.Controls.Add(dot);

            _statusText = new Label();
            _statusText.Name = "statusText";
            _statusText.Text = "未启动";
            _statusText.ForeColor = Theme.Text;
            _statusText.Font = Theme.Body;
            _statusText.SetBounds(34, 7, 130, 18);
            _statusText.AutoSize = false;
            _statusPill.Controls.Add(_statusText);
        }

        private void BuildLeft()
        {
            RoundPanel left = new RoundPanel();
            left.Radius = 12;
            left.SetBounds(20, 106, 330, 500);
            left.BackColor = Theme.Surface;
            left.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            Controls.Add(left);

            Label cap = Theme.Caption("运行控制");
            cap.SetBounds(20, 18, 200, 16);
            left.Controls.Add(cap);

            Button open = Theme.Primary("打开管理面板");
            open.SetBounds(20, 42, 290, 44);
            open.Click += delegate { ShowView(true); };
            left.Controls.Add(open);

            Button toggle = Theme.Ghost("启动 / 停止");
            toggle.SetBounds(20, 98, 141, 38);
            toggle.Click += delegate { ToggleCore(); };
            left.Controls.Add(toggle);

            Button restart = Theme.Ghost("重启服务");
            restart.SetBounds(169, 98, 141, 38);
            restart.Click += delegate { RestartCore(); };
            left.Controls.Add(restart);

            Button data = Theme.Ghost("打开数据目录");
            data.SetBounds(20, 144, 141, 34);
            data.Click += delegate { OpenDataDir(); };
            left.Controls.Add(data);

            Button check = Theme.Ghost("打包自检");
            check.SetBounds(169, 144, 141, 34);
            check.Click += delegate { RunSelfCheckInteractive(); };
            left.Controls.Add(check);

            Button repair = Theme.Ghost("一键修复");
            repair.Name = "repair";
            repair.SetBounds(20, 352, 290, 38);
            repair.Click += delegate { RunRepairInteractive(); };
            left.Controls.Add(repair);

            // 免安装包 = 这份程序的完整副本。指一次，之后修复器随时能从这里按摘要补文件
            Button restoreZip = Theme.Ghost("从免安装包还原…");
            restoreZip.Name = "restoreZip";
            restoreZip.SetBounds(20, 396, 290, 30);
            restoreZip.Font = Theme.Small;
            restoreZip.Click += delegate { PickRestoreZip(); };
            left.Controls.Add(restoreZip);

            _passwordCard = new RoundPanel();
            _passwordCard.Radius = 10;
            _passwordCard.SetBounds(20, 194, 290, 96);
            _passwordCard.BackColor = Theme.Card;
            left.Controls.Add(_passwordCard);

            Label pwdCap = Theme.Caption("首次登录密码（用户名 astrbot）");
            pwdCap.SetBounds(14, 12, 262, 16);
            _passwordCard.Controls.Add(pwdCap);

            _passwordValue = new Label();
            _passwordValue.Text = "";
            _passwordValue.ForeColor = Theme.Warn;
            _passwordValue.Font = Theme.Mono;
            _passwordValue.SetBounds(14, 34, 190, 20);
            _passwordValue.AutoSize = false;
            _passwordCard.Controls.Add(_passwordValue);

            _passwordHint = new Label();
            _passwordHint.Text = "";
            _passwordHint.ForeColor = Theme.Muted;
            _passwordHint.Font = Theme.Small;
            _passwordHint.SetBounds(14, 62, 190, 28);
            _passwordHint.AutoSize = false;
            _passwordCard.Controls.Add(_passwordHint);

            _passwordReset = Theme.Ghost("重置口令");
            _passwordReset.Name = "passwordReset";
            _passwordReset.SetBounds(210, 62, 66, 26);
            _passwordReset.Click += delegate { ResetPanelPassword(); };
            _passwordCard.Controls.Add(_passwordReset);

            Button copy = Theme.Ghost("复制");
            copy.SetBounds(210, 32, 66, 26);
            copy.Click += delegate
            {
                if (_passwordValue.Text.Length == 0)
                {
                    return;
                }
                try
                {
                    Clipboard.SetText(_passwordValue.Text);
                    _tips.Show("已复制", copy, 1500);
                }
                catch (Exception exc)
                {
                    // 剪贴板被别的进程占着是常事，不该让整个程序崩掉。
                    _tips.Show("复制失败，请手动选中：" + exc.Message, copy, 4000);
                }
            };
            _passwordCard.Controls.Add(copy);

            Button hide = Theme.Ghost("改完隐藏");
            hide.SetBounds(196, 62, 80, 24);
            hide.Font = Theme.Small;
            hide.Click += delegate { _passwordCard.Visible = false; };
            _passwordCard.Controls.Add(hide);

            Label hint = Theme.Caption("打不开面板就先点「重启服务」。");
            hint.SetBounds(20, 306, 290, 16);
            left.Controls.Add(hint);

            Label hint2 = Theme.Caption("要接 QQ，用右边的表单填一次就行。");
            hint2.SetBounds(20, 326, 290, 16);
            left.Controls.Add(hint2);
        }

        private void BuildRight()
        {
            RoundPanel right = new RoundPanel();
            right.Radius = 12;
            right.SetBounds(366, 106, 554, 500);
            right.BackColor = Theme.Surface;
            right.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(right);

            Label cap = Theme.Caption("接入 QQ（保存后自动重启生效）");
            cap.SetBounds(20, 18, 320, 16);
            right.Controls.Add(cap);

            _typeBox = new RoundComboBox();
            _typeBox.SetBounds(20, 44, 514, 32);
            _typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _typeBox.Font = Theme.Body;
            foreach (string[] pair in PlatformIo.QqTypes)
            {
                _typeBox.Items.Add(pair[0]);
            }
            _typeBox.SelectedIndex = 0;
            _typeBox.SelectedIndexChanged += delegate { RebuildFields(); };
            right.Controls.Add(_typeBox);

            // 系统自带的下拉箭头是浅灰方块，跟深色卡片不搭：盖一个禁用的标签上去，
            // 禁用子窗口命中测试返回透明，点击照样落到下拉框上。
            Label chevron = new Label();
            chevron.Text = "▾";
            chevron.Font = Theme.Body;
            chevron.ForeColor = Theme.Muted;
            chevron.BackColor = Theme.Card;
            chevron.TextAlign = ContentAlignment.MiddleCenter;
            chevron.Enabled = false;
            chevron.Parent = _typeBox;
            chevron.BringToFront();
            chevron.Size = new Size(26, 30);
            chevron.Location = new Point(_typeBox.Width - 28, 1);
            chevron.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            Label nameLabel = Theme.Caption("备注名");
            nameLabel.SetBounds(20, 94, 60, 16);
            right.Controls.Add(nameLabel);

            _nameInput = Theme.Input("QQ 机器人");
            _nameInput.SetBounds(92, 88, 200, 30);
            right.Controls.Add(_nameInput);

            Label idLabel = Theme.Caption("标识");
            idLabel.SetBounds(306, 94, 40, 16);
            right.Controls.Add(idLabel);

            _idInput = Theme.Input("qq_onebot");
            _idInput.SetBounds(350, 88, 184, 30);
            right.Controls.Add(_idInput);

            _fieldsPanel = new RoundPanel();
            _fieldsPanel.Radius = 0;
            _fieldsPanel.DrawBorder = false;
            _fieldsPanel.SetBounds(20, 134, 514, 110);
            _fieldsPanel.BackColor = Theme.Surface;
            right.Controls.Add(_fieldsPanel);

            Button save = Theme.Primary("保存并连接");
            save.SetBounds(20, 258, 180, 40);
            save.Click += delegate { SavePlatform(true); };
            right.Controls.Add(save);

            Button disable = Theme.Ghost("断开这个平台");
            disable.SetBounds(212, 258, 160, 40);
            disable.Click += delegate { SavePlatform(false); };
            right.Controls.Add(disable);

            _scanButton = Theme.Ghost("扫码登录");
            _scanButton.Name = "scanLogin";
            _scanButton.SetBounds(384, 258, 150, 40);
            _scanButton.Click += delegate { StartScanLogin(); };
            right.Controls.Add(_scanButton);

            _qqStatus = new Label();
            _qqStatus.Text = "QQ 状态：未连接";
            _qqStatus.ForeColor = Theme.Muted;
            _qqStatus.Font = Theme.Small;
            _qqStatus.AutoSize = true;
            _qqStatus.SetBounds(20, 312, 500, 18);
            right.Controls.Add(_qqStatus);

            _autostartCheck = new CheckBox();
            _autostartCheck.Text = "开机自动启动";
            _autostartCheck.ForeColor = Theme.Text;
            _autostartCheck.Font = Theme.Small;
            _autostartCheck.AutoSize = true;
            _autostartCheck.SetBounds(20, 340, 120, 20);
            _autostartCheck.Checked = IsAutostartEnabled();
            _autostartCheck.CheckedChanged += delegate { SetAutostart(_autostartCheck.Checked); };

            _openPanelCheck = new CheckBox();
            _openPanelCheck.Text = "启动后自动开面板";
            _openPanelCheck.ForeColor = Theme.Text;
            _openPanelCheck.Font = Theme.Small;
            _openPanelCheck.AutoSize = true;
            _openPanelCheck.SetBounds(160, 340, 150, 20);
            _openPanelCheck.Checked = _settings.OpenPanelOnStart;
            _openPanelCheck.CheckedChanged += delegate
            {
                _settings.OpenPanelOnStart = _openPanelCheck.Checked;
                SaveSettings();
            };

            _lanCheck = new CheckBox();
            _lanCheck.Text = "允许局域网访问";
            _lanCheck.ForeColor = Theme.Text;
            _lanCheck.Font = Theme.Small;
            _lanCheck.AutoSize = true;
            _lanCheck.SetBounds(320, 340, 140, 20);
            _lanCheck.Checked = _settings.AllowLan;
            _lanCheck.CheckedChanged += delegate
            {
                _settings.AllowLan = _lanCheck.Checked;
                _core.Host = _settings.PanelHost;
                SaveSettings();
                if (_lanCheck.Checked)
                {
                    MessageBox.Show(this,
                        "勾选后面板对本机以外网卡开放（http://本机IP:" + _settings.Port + "）。\n"
                        + "请务必先改掉初始密码，最好再在面板「安全」里开启两步验证。",
                        "允许局域网访问", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            right.Controls.Add(_autostartCheck);
            right.Controls.Add(_openPanelCheck);
            right.Controls.Add(_lanCheck);

            BuildOneBotSection(right);

            RebuildFields();
        }

        // 个人号那条路：AstrBot 自己不含腾讯协议的登录，得有 OneBot 客户端在机器上跑着。
        // 这一段让启动器也能发现它、起停它、看它有没有登上号，并把它的登录二维码显示到
        // 我们自己的窗口里——原来这些都得人自己去翻客户端目录、开控制台盯着。
        private void BuildOneBotSection(Control parent)
        {
            _onebot = new OneBotController(_baseDir);
            _onebot.Line += delegate(string text) { AppendLine("[QQ接入] " + text); };

            Label cap = Theme.Caption("QQ 接入客户端（个人号）");
            cap.SetBounds(20, 372, 300, 18);
            parent.Controls.Add(cap);

            _obStatus = new Label();
            _obStatus.Name = "obStatus";
            _obStatus.ForeColor = Theme.Muted;
            _obStatus.Font = Theme.Small;
            _obStatus.AutoSize = true;
            _obStatus.SetBounds(20, 392, 520, 18);
            parent.Controls.Add(_obStatus);

            _obUin = Theme.Input("QQ 号（留空=扫码登录）");
            _obUin.Name = "obUin";
            _obUin.SetBounds(20, 414, 140, 26);
            _obUin.Text = _settings.OneBotUin;
            _obUin.Leave += delegate
            {
                _settings.OneBotUin = _obUin.Text.Trim();
                SaveSettings();
            };
            parent.Controls.Add(_obUin);

            RoundButton browse = Theme.Ghost("选目录");
            browse.Name = "obBrowse";
            browse.SetBounds(166, 414, 68, 26);
            browse.Click += delegate { PickOneBotDir(); };
            parent.Controls.Add(browse);

            _obStart = Theme.Primary("启动客户端");
            _obStart.Name = "obStart";
            _obStart.SetBounds(238, 414, 76, 26);
            _obStart.Click += delegate { StartOneBotClient(); };
            parent.Controls.Add(_obStart);

            _obStop = Theme.Ghost("停止");
            _obStop.Name = "obStop";
            _obStop.SetBounds(318, 414, 60, 26);
            _obStop.Click += delegate { StopOneBotClient(); };
            parent.Controls.Add(_obStop);

            _obQr = Theme.Ghost("登录二维码");
            _obQr.Name = "obQr";
            _obQr.SetBounds(382, 414, 76, 26);
            _obQr.Click += delegate { ShowOneBotQr(); };
            parent.Controls.Add(_obQr);

            _obDownload = Theme.Ghost("下载 NapCat");
            _obDownload.Name = "obDownload";
            _obDownload.SetBounds(462, 414, 72, 26);
            _obDownload.Click += delegate { AskOneBotInstall(); };
            parent.Controls.Add(_obDownload);

            // v4.18.41 的面板不再往配置文件里放明文令牌：要问登录状态/取二维码，
            // 得拿面板口令去 /api/auth/login 换一张临时凭证。没有这一行，程序就只能"看着你登录完什么也不做"。
            _obPanelPassword = Theme.Input("NapCat 面板口令");
            _obPanelPassword.Name = "obPanelPassword";
            _obPanelPassword.Box.UseSystemPasswordChar = true;
            _obPanelPassword.SetBounds(20, 444, 200, 26);
            _obPanelPassword.Text = _settings.OneBotPanelPassword;
            _obPanelPassword.Leave += delegate
            {
                _settings.OneBotPanelPassword = _obPanelPassword.Text;
                SaveSettings();
            };
            parent.Controls.Add(_obPanelPassword);

            RoundButton panelConnect = Theme.Ghost("连接面板");
            panelConnect.Name = "obPanelConnect";
            panelConnect.SetBounds(224, 444, 76, 26);
            panelConnect.Click += delegate { ConnectOneBotPanel(); };
            parent.Controls.Add(panelConnect);

            _obPanelNote = new Label();
            _obPanelNote.Name = "obPanelNote";
            _obPanelNote.ForeColor = Theme.Muted;
            _obPanelNote.Font = Theme.Small;
            _obPanelNote.AutoSize = true;
            _obPanelNote.SetBounds(306, 450, 240, 16);
            parent.Controls.Add(_obPanelNote);

            Label hint = new Label();
            hint.Name = "obHint";
            hint.Text = "个人号需要本机装好 QQ 客户端；登录态由客户端持有，账号风控风险自行承担。";
            hint.ForeColor = Theme.Muted;
            hint.Font = Theme.Small;
            hint.SetBounds(20, 476, 534, 18);
            parent.Controls.Add(hint);

            DiscoverOneBot();

            _obTimer = new System.Windows.Forms.Timer();
            _obTimer.Interval = 3000;
            _obTimer.Tick += delegate { TickOneBot(); };
            _obTimer.Start();
        }

        private void DiscoverOneBot()
        {
            // 找客户端要扫好几个目录（Program Files、桌面、各盘根），再读它的 webui.json。
            // 磁盘慢的时候这两步就是"一打开就未响应"——启动时正好撞在冷盘上，所以一律挪到后台。
            if (_obDiscovering || _onebot == null)
            {
                return;
            }
            _obDiscovering = true;
            OneBotController ob = _onebot;
            string baseDir = _baseDir;
            string configured = _settings.OneBotDir;
            ThreadPool.QueueUserWorkItem(delegate
            {
                OneBotClientInfo pick = null;
                try
                {
                    List<OneBotClientInfo> found = OneBotController.Discover(baseDir, configured);
                    foreach (OneBotClientInfo info in found)
                    {
                        if (pick == null || string.Equals(info.Root, configured, StringComparison.OrdinalIgnoreCase))
                        {
                            pick = info;
                        }
                    }
                    ob.Client = pick;
                    if (pick != null)
                    {
                        ob.LoadWebUiConfig();
                    }
                }
                catch (Exception)
                {
                }
                Action done = delegate
                {
                    _obDiscovering = false;
                    _obAlive = false;
                    RefreshOneBotStatus();
                    TickOneBot();
                    if (_obStartWhenFound && ob.Client != null)
                    {
                        _obStartWhenFound = false;
                        StartOneBotClient();
                    }
                };
                if (IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(done);
                    }
                    catch (Exception)
                    {
                        _obDiscovering = false;
                    }
                }
                else
                {
                    _obDiscovering = false;
                }
            });
        }

        // 后台扫描还没回来时，点「启动」不该把人堵死：记下意图，扫到就自动起。
        private void StartOneBotWhenFound()
        {
            _obStartWhenFound = true;
            AppendLine("[QQ接入] 正在后台找客户端目录，找到就自动启动…");
            DiscoverOneBot();
        }

        private void PickOneBotDir()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 QQ 接入客户端的目录（里面有 launcher-user.bat / napcat.mjs 这类文件）";
                if (_onebot.Client != null)
                {
                    dialog.SelectedPath = _onebot.Client.Root;
                }
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                _settings.OneBotDir = dialog.SelectedPath;
                SaveSettings();
                DiscoverOneBot();
                if (_onebot.Client == null)
                {
                    // 闸门不能把人堵死：认不出的目录也要说清认出了什么、缺什么
                    MessageBox.Show(this,
                        "这个目录里没有找到能认的客户端标志文件。\n"
                        + "启动器认：NapCat Shell（NapCatWinBootMain.exe + napcat.mjs）"
                        + "或 NapCat Framework（napimain.exe + napiloader.dll）。\n"
                        + "解压时多套了一层目录的话，进去那一层再选一次。",
                        "没认出来", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void StartOneBotClient()
        {
            if (_onebot.Client == null)
            {
                if (_obDiscovering)
                {
                    StartOneBotWhenFound();
                    return;
                }
                // 上一次扫过了，这次按用户点的意思再扫一遍（后台），扫到就自动起。
                StartOneBotWhenFound();
                return;
            }
            try
            {
                _settings.OneBotUin = _obUin.Text.Trim();
                SaveSettings();
                _onebot.Start(_settings.OneBotUin);
                _obRestartAttempt = 0;
                RefreshOneBotStatus();
            }
            catch (Exception exc)
            {
                string note = _onebot.LastError.Length > 0 ? _onebot.LastError : exc.Message;
                AppendLine("[QQ接入] 启动失败：" + note);
                MessageBox.Show(this,
                    "启动客户端失败：\n" + note
                    + "\n\n它需要管理员权限时会弹一次 UAC；拒绝授权就起不来（NapCat 要往 QQ 里注入）。",
                    "起不来", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 点「连接面板」：拿口令去换临时凭证，全程在后台线程（一次问答最坏堵几秒）。
        // 换到了顺手问一次登录状态，让他立刻看见"跟不上"变成"跟上了"。
        private void ConnectOneBotPanel()
        {
            _settings.OneBotPanelPassword = _obPanelPassword.Text;
            SaveSettings();
            _onebot.WebUiPassword = _obPanelPassword.Text;
            if (_obPanelNote != null)
            {
                _obPanelNote.Text = "正在问面板…";
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = _onebot.PanelLogin();
                string note = _onebot.PanelNote;
                if (ok)
                {
                    _onebot.PollLoginPhase();
                    PreparePersonalPlatform();
                }
                Action done = delegate
                {
                    if (_obPanelNote != null)
                    {
                        _obPanelNote.Text = note;
                        _obPanelNote.ForeColor = ok ? Theme.Ok : Theme.Warn;
                    }
                    RefreshOneBotStatus();
                    if (ok && _onebot.LoginPhase.Length > 0 && _onebot.LoginPhase != "online")
                    {
                        ShowOneBotQr();
                    }
                };
                if (IsHandleCreated)
                {
                    try { BeginInvoke(done); } catch (Exception) { }
                }
            });
        }

        // 面板连上之后，"下一步"该由我们铺：查一下 AstrBot 侧有没有 OneBot 平台，
        // 没有就把表单替他填好（含随机令牌）、把 NapCat 那边要填的参数复制到剪贴板，
        // 让状态行从"跟不上"变成"照这两步点完就通"。
        private void PreparePersonalPlatform()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "astrbot-platform-dump.ini");
            bool hasPersonal = false;
            string output = "";
            try
            {
                if (_core.RunOnce("--platform-dump \"" + tmp + "\"", out output, 60000) == 0 && File.Exists(tmp))
                {
                    foreach (PlatformEntry entry in PlatformIo.ParseDump(File.ReadAllText(tmp, Encoding.UTF8)))
                    {
                        string type = entry.Get("type");
                        if (type == "aiocqhttp" || type == "satori")
                        {
                            hasPersonal = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            if (hasPersonal)
            {
                AppendLine("[QQ接入] AstrBot 侧已经有 OneBot 平台了，等客户端连过来就行（还连不上就看右边那个令牌是否同一个）");
                return;
            }
            string token = Guid.NewGuid().ToString("N").Substring(0, 16);
            string pairing = "NapCat 面板里要填的：新建「Websocket（反向）」\n"
                + "  URL = ws://127.0.0.1:6199/ws\n"
                + "  令牌 = " + token + "\n"
                + "（AstrBot 这边的表单我已经替你填好了，点「保存并连接」就生效）";
            Action onUi = delegate
            {
                for (int i = 0; i < PlatformIo.QqTypes.Length; i++)
                {
                    if (PlatformIo.QqTypes[i][1] == "aiocqhttp")
                    {
                        _typeBox.SelectedIndex = i;
                        break;
                    }
                }
                _nameInput.Text = "qq_onebot";
                _idInput.Text = "qq_onebot";
                InputBox hostBox;
                if (_fieldInputs.TryGetValue("ws_reverse_host", out hostBox))
                {
                    hostBox.Text = "127.0.0.1";
                }
                InputBox portBox;
                if (_fieldInputs.TryGetValue("ws_reverse_port", out portBox))
                {
                    portBox.Text = "6199";
                }
                InputBox tokenBox;
                if (_fieldInputs.TryGetValue("ws_reverse_token", out tokenBox))
                {
                    tokenBox.Text = token;
                }
                try
                {
                    Clipboard.SetText("ws://127.0.0.1:6199/ws  令牌 " + token);
                }
                catch (Exception)
                {
                }
                AppendLine("[QQ接入] " + pairing);
                MessageBox.Show(this, pairing, "还差两步", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            if (IsHandleCreated)
            {
                try { BeginInvoke(onUi); } catch (Exception) { }
            }
        }

        private void StopOneBotClient()
        {
            _obWasAlive = false;
            _onebot.Stop();
            CloseOneBotQr();
            RefreshOneBotStatus();
        }

        private void OpenOneBotPanel()
        {
            if (_onebot == null || _onebot.Client == null)
            {
                return;
            }
            string url = "http://127.0.0.1:" + _onebot.WebPort + "/webui/";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }

        private void ShowOneBotQr()
        {
            if (_onebot.Client == null)
            {
                return;
            }
            if (_obDialog == null || _obDialog.IsDisposed)
            {
                _obDialog = new BindDialog();
                _obDialog.UseForClientLogin();
            }
            _obDialog.Show(this);
            FetchOneBotQr();
        }

        // 没装客户端时的可选下载。绝不自动装：这是把别人写的、能读 QQ 会话的代码放上他的机器，
        // 必须他明确点一次。版本与 sha256 固定在 OneBotInstaller 里，对不上就不装。
        private void AskOneBotInstall()
        {
            if (_obInstaller == null)
            {
                _obInstaller = new OneBotInstaller(ScratchAppDir(), _baseDir);
                _obInstaller.Line += delegate(string text) { AppendLine("[QQ接入] " + text); };
            }
            string note = "· 两种都要本机装过 QQ 客户端（NapCat 从注册表找 QQ.exe 才能工作）" + Environment.NewLine;
            note += "· 版本固定在 v" + OneBotInstaller.Version + "，压缩包按固定 SHA-256 逐字节核对，对不上就不装" + Environment.NewLine;
            note += "· 下载走加速前缀，失败自动退回直连；以校验结果为准，不看谁快" + Environment.NewLine;
            note += "· 装完落在本程序的 data" + Path.DirectorySeparatorChar + "onebot 目录里，删掉那个目录就等于卸载" + Environment.NewLine;
            note += "· 它能读这个 QQ 号的全部会话；个人号跑机器人违反腾讯服务条款，封号风险自担";
            OneBotInstallDialog dialog = new OneBotInstallDialog(note);
            OneBotVariant variant = null;
            try
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    variant = dialog.Chosen;
                }
            }
            finally
            {
                dialog.Dispose();
            }
            if (variant == null)
            {
                return;
            }
            _obDownload.Enabled = false;
            AppendLine("[QQ接入] 准备安装 " + variant.Display);
            OneBotVariant chosen = variant;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = "";
                try
                {
                    _obInstaller.Install(chosen, "");
                }
                catch (Exception exc)
                {
                    error = exc.Message;
                }
                Action done = delegate
                {
                    _obDownload.Enabled = true;
                    if (error.Length > 0)
                    {
                        AppendLine("[QQ接入] 安装没完成：" + error);
                        MessageBox.Show(this, error, "装不上", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    DiscoverOneBot();
                    AppendLine("[QQ接入] 装好了，点「启动客户端」就会拉起它（首次可能要同意一次 UAC）");
                };
                try
                {
                    BeginInvoke(done);
                }
                catch (Exception)
                {
                }
            });
        }

        // 下载器只有两段可验：字节校验与解压落盘。网络那段不在这里跑。
        // 故意把包改坏一个字节必须被拒——"能装上"在这种情况下是坏事。
        private void InstallDrill()
        {
            string localZip = (Environment.GetEnvironmentVariable("ASTRBOT_TEST_LOCAL_ZIP") ?? "").Trim().Trim('"');
            if (localZip.Length == 0 || !File.Exists(localZip))
            {
                LastTestReport += "NOTRUN 下载器演练（没给 ASTRBOT_TEST_LOCAL_ZIP）" + Environment.NewLine;
                return;
            }
            if (_obInstaller == null)
            {
                _obInstaller = new OneBotInstaller(ScratchAppDir(), _baseDir);
                _obInstaller.Line += delegate(string text) { AppendLine("[QQ接入] " + text); };
            }
            OneBotVariant shell = OneBotInstaller.Find("shell");
            string work = Path.Combine(Path.GetTempPath(), "astrbot-obzip-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            string good = Path.Combine(work, "NapCat.Shell.zip");
            string tampered = Path.Combine(work, "tampered.zip");
            string installed = "";
            try
            {
                File.Copy(localZip, good, true);
                int[] foundCount = new int[1];
                string[] installedBox = new string[1];
                // 解压整包是几十 MB 的真磁盘活，同样不许在这条线程上做。
                RunOffUiAndWait(delegate { installedBox[0] = _obInstaller.Install(shell, good); });
                installed = installedBox[0] == null ? "" : installedBox[0];
                string scratch = ScratchAppDir();
                RunOffUiAndWait(delegate { foundCount[0] = OneBotController.Discover(scratch, installed).Count; });
                Expect("固定版本装完后客户端目录能被认出来",
                    installed.Length > 0 && foundCount[0] == 1);
                Expect("随包落了「来源与许可说明」，写清上游与摘要",
                    File.Exists(Path.Combine(installed, "来源与许可说明.txt"))
                    && File.ReadAllText(Path.Combine(installed, "来源与许可说明.txt"), Encoding.UTF8).Contains("SHA-256"));
                string escapeProbe = Path.Combine(_baseDir, "data", "onebot", "..", "..", "Windows", "System32", "drivers", "etc", "hosts");
                Expect("解压没跑出目标目录（挡过路径穿越）", !File.Exists(Path.GetFullPath(escapeProbe)));

                byte[] bytes = File.ReadAllBytes(good);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(tampered, bytes);
                string rejection = "";
                try
                {
                    RunOffUiAndWait(delegate { _obInstaller.Install(shell, tampered); });
                }
                catch (Exception bad)
                {
                    rejection = bad.Message;
                }
                Expect("改坏一个字节就被拒装（不装也不回退到别的来源）", rejection.Contains("不一致"));

                // 随包的离线副本：断网、代理没开、GitHub 拿不全字节这三种情况下，
                // 「下载 NapCat」这条得照样能用。装完仍然要按同一串摘要核对，副本不免检。
                string bundled = _obInstaller.BundledZip(shell);
                Expect("包里带了离线副本 factory\\napcat\\NapCat.Shell.zip", bundled.Length > 0);
                if (bundled.Length > 0)
                {
                    string fromBundle = "";
                    try
                    {
                        fromBundle = _obInstaller.Install(shell, "");
                        Expect("不碰网络、只从随包副本装也能装上（装到 " + Path.GetFileName(fromBundle) + "）",
                            fromBundle.Length > 0 && Directory.Exists(fromBundle));
                        Expect("离线装完也落了来源与许可说明",
                            File.Exists(Path.Combine(fromBundle, "来源与许可说明.txt")));
                    }
                    catch (Exception bad)
                    {
                        Expect("从随包副本安装没抛异常（" + bad.Message + "）", false);
                    }
                    finally
                    {
                        if (fromBundle.Length > 0 && Directory.Exists(fromBundle))
                        {
                            Directory.Delete(fromBundle, true);
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                Expect("安装演练没抛意外异常（" + exc.GetType().Name + "：" + exc.Message + "）", false);
            }
            finally
            {
                try
                {
                    Directory.Delete(work, true);
                    if (installed.Length > 0 && Directory.Exists(installed))
                    {
                        Directory.Delete(installed, true);
                    }
                    string onebotRoot = Path.Combine(_baseDir, "data", "onebot");
                    if (Directory.Exists(onebotRoot) && Directory.GetFileSystemEntries(onebotRoot).Length == 0)
                    {
                        Directory.Delete(onebotRoot);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private void CloseOneBotQr()
        {
            if (_obDialog != null && !_obDialog.IsDisposed)
            {
                _obDialog.Close();
            }
            _obDialog = null;
        }

        private void FetchOneBotQr()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 面板各版本的令牌取法不一样，控制器里那几种都试过；拿不到就退回面板链接，
                // 绝不能把"没拿到"显示成"已登录"。
                string url = _onebot.FetchQrcodeUrl();
                Action done = delegate
                {
                    if (_obDialog == null || _obDialog.IsDisposed)
                    {
                        return;
                    }
                    if (url.Length == 0)
                    {
                        _obDialog.SetStatus("拿不到二维码");
                        AppendLine("[QQ接入] 客户端面板没回二维码，可能是已登录，也可能要它的面板令牌——点「客户端面板」自己看。");
                        OpenOneBotPanel();
                        return;
                    }
                    string png = Path.Combine(_baseDir, "data", "tmp", "napcat-login.png");
                    string output;
                    int code = _core.RunOnce("--qr \"" + url.Replace("\"", "") + "\" --qr-out \"" + png + "\"", out output, 15000);
                    if (code == 0 && File.Exists(png))
                    {
                        _obDialog.ShowQr(png);
                    }
                    _obDialog.SetStatus(StatusOfLogin(_onebot.LoginPhase));
                };
                try
                {
                    BeginInvoke(done);
                }
                catch (Exception)
                {
                }
            });
        }

        private static string StatusOfLogin(string phase)
        {
            if (phase == "online")
            {
                return "已登录，客户端在线";
            }
            if (phase == "qrcode_scanned")
            {
                return "已扫码，等手机上确认";
            }
            if (phase == "waiting_qrcode")
            {
                return "等手机 QQ 扫码";
            }
            return "正在问客户端要二维码…";
        }

        // 平台的反向 WS 端口与令牌是这条链能不能通的唯一凭据，保存时顺手同步给客户端配置
        private void SyncOneBotFromEntry(PlatformEntry entry)
        {
            int port;
            string raw = entry.Get("ws_reverse_port");
            if (raw.Length > 0 && int.TryParse(raw, out port) && port > 0)
            {
                _onebot.ReverseWsPort = port;
            }
            _onebot.ReverseWsToken = entry.Get("ws_reverse_token");
            RefreshOneBotStatus();
        }

        private void TickOneBot()
        {
            if (_onebot == null || _onebot.Client == null || _obBusy)
            {
                return;
            }
            // 端口探活和面板 HTTP 一律不在 UI 线程做：面板半死不活时（连得上但不回话），
            // 一次问答最坏能堵好几秒，每三秒堵一次就是"未响应"。
            // 同一时刻只允许一次探测在飞，别让慢响应把线程池占满。
            OneBotController ob = _onebot;
            bool dialogOpen = _obDialog != null && !_obDialog.IsDisposed;
            bool wantPhase = dialogOpen || ob.LoginPhase != "online";
            _obBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool alive = ob.PanelAlive();
                if (alive && wantPhase)
                {
                    ob.PollLoginPhase();
                }
                if (alive)
                {
                    ob.LoadWebUiConfig();
                }
                Action done = delegate
                {
                    _obBusy = false;
                    _obAlive = alive;
                    if (!alive)
                    {
                        _obWasAlive = false;
                        if (_settings.OneBotAutoStart && !_quitting && _obRestartAttempt < 3)
                        {
                            _obRestartAttempt++;
                            AppendLine("[QQ接入] 客户端面板不在线，自动重试 " + _obRestartAttempt + "/3");
                            StartOneBotClient();
                        }
                        RefreshOneBotStatus();
                        return;
                    }
                    if (!_obWasAlive)
                    {
                        _obWasAlive = true;
                        _obRestartAttempt = 0;
                        AppendLine("[QQ接入] 客户端面板已在线（端口 " + ob.WebPort + "）");
                    }
                    if (dialogOpen && _obDialog != null && !_obDialog.IsDisposed)
                    {
                        _obDialog.SetStatus(StatusOfLogin(ob.LoginPhase));
                    }
                    RefreshOneBotStatus();
                };
                if (IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(done);
                    }
                    catch (Exception)
                    {
                        _obBusy = false;
                    }
                }
                else
                {
                    _obBusy = false;
                }
            });
        }

        private void RefreshOneBotStatus()
        {
            if (_obStatus == null)
            {
                return;
            }
            string client;
            if (_onebot == null || _onebot.Client == null)
            {
                client = "客户端：没找到";
            }
            else if (_obAlive)
            {
                client = "客户端：在线（" + _onebot.Client.Flavor + "）";
            }
            else
            {
                client = "客户端：未运行";
            }
            string qq;
            if (_onebot == null)
            {
                qq = "";
            }
            else
            {
                qq = _onebot.LoginPhase == "online" ? " · QQ：已登录" : " · QQ：" + StatusOfLogin(_onebot.LoginPhase);
            }
            string bot = _core != null && _core.QQConnected ? " · AstrBot：已连上" : " · AstrBot：还没连上";
            // 面板没授权时"问不到登录状态"是原因，不能让他对着一个不动的状态猜
            string panel = _onebot == null ? "" : (_onebot.PanelAuthorized ? " · 面板：已授权"
                : (_onebot.WebUiPassword.Length == 0 ? " · 面板：要口令" : " · 面板：" + _onebot.PanelNote));
            _obStatus.Text = client + qq + bot + panel;
            _obStatus.ForeColor = client.Contains("在线") ? Theme.Ok : Theme.Muted;
        }

        private void BuildLog()
        {
            Label cap = Theme.Caption("运行日志");
            cap.SetBounds(20, 592, 120, 16);
            cap.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(cap);

            Button clear = Theme.Ghost("清空");
            clear.SetBounds(ClientSize.Width - 116, 584, 96, 26);
            clear.Font = Theme.Small;
            clear.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            clear.Click += delegate
            {
                lock (_logSync)
                {
                    _logPending.Clear();
                }
                _logBox.Text = "";
                _logLines = 0;
            };
            Controls.Add(clear);

            RoundButton diag = new RoundButton();
            diag.Name = "exportDiag";
            diag.Text = "导出诊断包";
            diag.UsePalette(Theme.Surface, Theme.Card, Theme.Line, Theme.Text);
            diag.Font = Theme.Small;
            diag.Radius = 6;
            diag.Height = 26;
            diag.SetBounds(ClientSize.Width - 232, 584, 108, 26);
            diag.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            diag.Cursor = Cursors.Hand;
            diag.Click += delegate { ExportDiagnostics(); };
            _tips.SetToolTip(diag, "把自检输出和日志尾部存成 data\\diagnostics 里的一个文件，密码/密钥会自动打码，发这个文件就能定位问题。");
            Controls.Add(diag);

            RoundPanel frame = new RoundPanel();
            frame.Radius = 8;
            frame.BackColor = Theme.Surface;
            frame.Padding = new Padding(2);
            frame.SetBounds(20, 618, ClientSize.Width - 40, 164);
            frame.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(frame);

            _logBox = new TextBox();
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Theme.Surface;
            _logBox.ForeColor = Theme.Muted;
            _logBox.Font = Theme.Mono;
            _logBox.BorderStyle = BorderStyle.None;
            _logBox.WordWrap = false;
            _logBox.Dock = DockStyle.Fill;
            frame.Controls.Add(_logBox);

            // 日志每 200 毫秒批量落一次屏；洪泛时这也是界面最忙的 200 毫秒，
            // 响应性断言就是照这个周期去量的
            _logTimer = new System.Windows.Forms.Timer();
            _logTimer.Interval = 200;
            _logTimer.Tick += delegate { FlushLog(); };
            _logTimer.Start();

            _watchdogTimer = new System.Windows.Forms.Timer();
            _watchdogTimer.Interval = 250;
            _watchdogTimer.Tick += delegate { WatchdogTick(); };
            _watchdogTimer.Start();
        }

        private int _uiBeat;
        // 看门狗：量的就是"消息循环隔多久才转一圈"。用户说「容易未响应」时，
        // 这一段能把到底是哪一步堵住的写在诊断包里，不用再靠猜。
        private System.Windows.Forms.Timer _watchdogTimer;
        private int _watchdogAt;
        private int _worstLag;
        private int _lastLag;
        private string _worstLagAt = "";
        private string _busyLabel = "";
        private string _lastUiLine = "";

        // 量一次「投一条消息到消息循环，到它真被执行」之间的延迟。
        // perLine=true 走的是以前那版的做法：每一行日志都单独 BeginInvoke 去 AppendText + ScrollToCaret；
        // perLine=false 走现在的队列 + 定时器批量刷。同一台机器同一个窗口里量出来的差值，
        // 就是「容易未响应」到底是为什么。
        private int MeasureUiLatency(bool perLine, int lines)
        {
            lock (_logSync)
            {
                _logPending.Clear();
            }
            _logBox.Text = "";
            _logLines = 0;
            for (int i = 0; i < lines; i++)
            {
                string text = "[洪泛] 模拟核心启动期连发的日志行 " + i;
                if (perLine)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _logBox.AppendText(text + Environment.NewLine);
                        _logBox.SelectionStart = _logBox.TextLength;
                        _logBox.ScrollToCaret();
                    }));
                }
                else
                {
                    AppendLine(text);
                }
            }
            _uiBeat = 0;
            int posted = Environment.TickCount;
            BeginInvoke(new Action(delegate { _uiBeat = Environment.TickCount; }));
            for (int i = 0; i < 40 && _uiBeat == 0; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            if (_uiBeat == 0)
            {
                return 99999;
            }
            return _uiBeat - posted;
        }

        private void BuildPanelView()
        {
            _panelView = new PanelView(Path.Combine(_baseDir, "data", "webview2"));
            // 底边停在 606：运行日志的标题在 592、输入框从 618 起，面板不许盖住它们。
            _panelView.Bounds = new Rectangle(366, 106, ClientSize.Width - 386, 500);
            _panelView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _panelView.Visible = false;
            _panelView.BindOpenBrowser(new Action(OpenPanel));
            Controls.Add(_panelView);

            _tabOverview = Theme.Ghost("概览");
            _tabOverview.SetBounds(_header.Width - 380, 23, 82, 30);
            _tabOverview.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _tabOverview.Click += delegate { ShowView(false); };
            _header.Controls.Add(_tabOverview);

            _tabPanel = Theme.Ghost("内置面板");
            _tabPanel.SetBounds(_header.Width - 290, 23, 100, 30);
            _tabPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _tabPanel.Click += delegate { ShowView(true); };
            _header.Controls.Add(_tabPanel);

            ShowView(_settings.LastView == "panel");
        }

        private void ShowView(bool panel)
        {
            _tabOverview.Active = !panel;
            _tabPanel.Active = panel;
            if (panel)
            {
                // 内置面板装的是整个 AstrBot 控制台，挤在 554 px 里等于把人家首页压成一条。
                // 左列（口令、启停）不能动，所以把窗口横向撑到 1240：面板拿到 ~854 px，切回去再还原。
                if (ClientSize.Width < PanelWideWidth)
                {
                    _panelGrewTo = ClientSize.Width;
                    ClientSize = new Size(PanelWideWidth, ClientSize.Height);
                }
                _panelView.Visible = true;
                _panelView.BringToFront();
                // 端口只认核心那一份，否则面板会导航到一个没在听的端口上。
                _panelView.Start("http://" + _settings.BrowserHost + ":" + _core.Port + "/");
                if (PanelView.AvailableVersion().Length == 0)
                {
                    // 没有内嵌内核就退回系统浏览器，别让人对着空白卡片。
                    OpenPanel();
                }
            }
            else
            {
                _panelView.Visible = false;
                if (_panelGrewTo > 0)
                {
                    int back = _panelGrewTo;
                    _panelGrewTo = 0;
                    ClientSize = new Size(back, ClientSize.Height);
                }
            }
            _settings.LastView = panel ? "panel" : "overview";
            SaveSettings();
        }

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Text = "AstrBot 桌面版";
            if (Icon != null)
            {
                _tray.Icon = Icon;
            }
            else
            {
                _tray.Icon = SystemIcons.Application;
            }
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(new MenuItem("打开管理面板", delegate { ShowView(true); }));
            menu.MenuItems.Add(new MenuItem("在浏览器中打开", delegate { OpenPanel(); }));
            menu.MenuItems.Add(new MenuItem("显示主窗口", delegate { Show(); WindowState = FormWindowState.Normal; }));
            menu.MenuItems.Add(new MenuItem("-"));
            MenuItem toggleItem = new MenuItem("启动 / 停止", delegate { ToggleCore(); });
            menu.MenuItems.Add(toggleItem);
            menu.MenuItems.Add(new MenuItem("退出 AstrBot", delegate { Quit(); }));
            _tray.ContextMenu = menu;
            _tray.DoubleClick += delegate
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            };
            _tray.Visible = true;
        }

        private void RebuildFields()
        {
            _fieldsPanel.Controls.Clear();
            _fieldInputs.Clear();
            string type = SelectedType();
            if (_scanButton != null)
            {
                bool scannable = type == "qq_official";
                _scanButton.Text = scannable ? "扫码登录" : "去面板创建";
                _tips.SetToolTip(_scanButton, scannable
                    ? "用手机 QQ 扫码：AppID 和密钥由核心直接写进配置，不用手抄，也不用登录面板。"
                    : "这种接入方式没有扫码登录（参数要在 NapCat / 官网那边拿），这里是把您送到面板的创建页。");
            }
            FieldSpec[] specs;
            if (!TypeFields.TryGetValue(type, out specs))
            {
                return;
            }
            int y = 0;
            foreach (FieldSpec spec in specs)
            {
                Label label = Theme.Caption(spec.Label);
                label.SetBounds(0, y + 7, 162, 16);
                _fieldsPanel.Controls.Add(label);

                InputBox input = Theme.Input(spec.Default);
                input.SetBounds(168, y, spec.Wide ? 346 : 180, 30);
                input.Text = spec.Default;
                _fieldsPanel.Controls.Add(input);
                _fieldInputs[spec.Key] = input;
                y += 36;
            }
        }

        private string SelectedType()
        {
            int index = _typeBox.SelectedIndex;
            if (index < 0 || index >= PlatformIo.QqTypes.Length)
            {
                return "aiocqhttp";
            }
            return PlatformIo.QqTypes[index][1];
        }

        private void SaveSettings()
        {
            try
            {
                _settings.Save();
            }
            catch (Exception)
            {
            }
        }

        private void LoadExistingPlatform()
        {
            // 首次使用根本没有配置文件，别为它多拉一个进程、白白拖慢启动。
            if (!File.Exists(Path.Combine(_baseDir, "data", "cmd_config.json")))
            {
                return;
            }
            string tmp = Path.Combine(Path.GetTempPath(), "astrbot-platform-dump.ini");
            try
            {
                string output;
                if (_core.RunOnce("--platform-dump \"" + tmp + "\"", out output, 60000) != 0)
                {
                    return;
                }
                if (!File.Exists(tmp))
                {
                    return;
                }
                List<PlatformEntry> entries = PlatformIo.ParseDump(File.ReadAllText(tmp, Encoding.UTF8));
                PlatformEntry picked = null;
                foreach (PlatformEntry entry in entries)
                {
                    if (TypeFieldsKeys(entry.Get("type")) && (picked == null || entry.Get("enable") == "true"))
                    {
                        picked = entry;
                    }
                }
                if (picked == null)
                {
                    return;
                }
                Action apply = delegate
                {
                    for (int i = 0; i < PlatformIo.QqTypes.Length; i++)
                    {
                        if (PlatformIo.QqTypes[i][1] == picked.Get("type"))
                        {
                            _typeBox.SelectedIndex = i;
                            break;
                        }
                    }
                    _nameInput.Text = picked.Get("name");
                    _idInput.Text = picked.Get("id");
                    foreach (KeyValuePair<string, InputBox> kv in _fieldInputs)
                    {
                        string val = picked.Get(kv.Key);
                        if (val.Length > 0)
                        {
                            kv.Value.Text = val;
                        }
                    }
                };
                if (IsHandleCreated)
                {
                    BeginInvoke(apply);
                }
            }
            catch (Exception)
            {
            }
        }

        private static bool TypeFieldsKeys(string type)
        {
            return TypeFields.ContainsKey(type);
        }

        private string ValidateEntry(string type, Dictionary<string, string> values)
        {
            string id = _idInput.Text.Trim();
            if (id.Length == 0)
            {
                return "先给这个平台起个标识名（例如 qq_onebot）。";
            }
            foreach (char c in id)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                {
                    return "标识只能用字母、数字、下划线和短横。";
                }
            }

            if (type == "qq_official" || type == "qq_official_webhook")
            {
                string appid = Get(values, "appid");
                string secret = Get(values, "secret");
                if (appid.Length == 0)
                {
                    return "QQ 官方机器人要填 AppID（QQ 开放平台后台的机器人 AppID）。";
                }
                foreach (char c in appid)
                {
                    if (!char.IsDigit(c))
                    {
                        return "AppID 应该是一串数字，检查一下是不是粘错了。";
                    }
                }
                if (secret.Length == 0)
                {
                    return "QQ 官方机器人要填 AppSecret。没有的话去面板里扫码创建，密钥会自动写回来。";
                }
            }
            else if (type == "aiocqhttp")
            {
                string port = Get(values, "ws_reverse_port");
                int parsed;
                if (port.Length > 0 && (!int.TryParse(port, out parsed) || parsed < 1 || parsed > 65535))
                {
                    return "监听端口要填 1-65535 之间的数字。";
                }
                if (Get(values, "ws_reverse_host").Length == 0)
                {
                    return "监听地址不能为空，本机 NapCat 就填 127.0.0.1。";
                }
            }
            else if (type == "satori")
            {
                string endpoint = Get(values, "satori_endpoint");
                if (!endpoint.StartsWith("ws://") && !endpoint.StartsWith("wss://"))
                {
                    return "Satori 事件地址要以 ws:// 或 wss:// 开头。";
                }
            }
            return "";
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            string val;
            return values.TryGetValue(key, out val) ? val.Trim() : "";
        }

        private void SavePlatform(bool enable)
        {
            string type = SelectedType();
            Dictionary<string, string> values = new Dictionary<string, string>();
            foreach (KeyValuePair<string, InputBox> kv in _fieldInputs)
            {
                values[kv.Key] = kv.Value.Text.Trim();
            }
            string problem = ValidateEntry(type, values);
            if (problem.Length > 0)
            {
                MessageBox.Show(this, problem, "还差一点", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string id = _idInput.Text.Trim();
            PlatformEntry entry = new PlatformEntry();
            entry.Set("type", type);
            entry.Set("id", id);
            entry.Set("name", _nameInput.Text.Trim().Length > 0 ? _nameInput.Text.Trim() : id);
            entry.Set("enable", enable ? "true" : "false");
            foreach (KeyValuePair<string, string> kv in values)
            {
                if (kv.Value.Length > 0)
                {
                    entry.Set(kv.Key, kv.Value);
                }
            }

            if (type == "aiocqhttp")
            {
                SyncOneBotFromEntry(entry);
            }

            SetBusy("正在写入平台配置...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                // 先停核心：它会把手上的配置写回 cmd_config.json，晚一步就把刚保存的内容盖掉。
                if (_core.IsRunning)
                {
                    _core.Stop();
                    for (int i = 0; i < 40 && _core.IsRunning; i++)
                    {
                        Thread.Sleep(150);
                    }
                }
                string ini = Path.Combine(Path.GetTempPath(), "astrbot-platform-write.ini");
                File.WriteAllText(ini, PlatformIo.BuildIni(entry), Encoding.UTF8);
                string output;
                int code = _core.RunOnce("--platform-file \"" + ini + "\"", out output, 60000);
                bool written = output.Contains("PLATFORM_WRITTEN");
                Action finish = delegate
                {
                    AppendLine("[启动器] " + (written
                        ? "已写入 " + entry.Get("id") + "，正在重启核心生效"
                        : "写入失败：" + output));
                    if (written)
                    {
                        StartCore();
                    }
                    else
                    {
                        SetState(CoreState.Failed);
                    }
                };
                if (IsHandleCreated)
                {
                    BeginInvoke(finish);
                }
                else
                {
                    finish();
                }
                try
                {
                    File.Delete(ini);
                }
                catch (Exception)
                {
                }
            });
        }

        private void StartCore()
        {
            if (_core.IsRunning || _quitting)
            {
                return;
            }
            // 人工点启动/重启就是把恢复预算清零，别被上一次自动恢复的次数卡住。
            CancelRecoveryTimer();
            // 体检要连探若干个端口（每个都有等待），放在 UI 线程上做就是点一下「启动」
            // 窗口灰掉半秒到几秒。先后台体检，回来了再启动。
            SetBusy("正在体检端口...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                int free = Preflight.PickPort(_core.Port);
                Action go = delegate
                {
                    AdoptFreePortWith(free);
                    LaunchCore();
                };
                if (IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(go);
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }

        // 端口已确定可用之后真正拉起核心
        private void LaunchCore()
        {
            if (_core.IsRunning)
            {
                return;
            }
            try
            {
                string extra = _coreResetOnce ? "--reset-password" : "";
                _coreResetOnce = false;
                _core.Start(extra);
                SetState(CoreState.Starting);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool up = _core.WaitForPort(StartupTimeoutMs);
                    Action done = delegate
                    {
                        if (up)
                        {
                            SetState(CoreState.Running);
                            if (_settings.OpenPanelOnStart && !_panelOpenedOnce)
                            {
                                _panelOpenedOnce = true;
                                ShowView(true);
                            }
                        }
                        else if (_core.IsRunning && !_quitting)
                        {
                            // 进程活着但面板始终不监听：这比崩掉更坑人，看门狗直接判失败重来。
                            HandleStartupTimeout();
                        }
                        else if (Recovering)
                        {
                            // 已经交给自动恢复了，这里别把状态灯从「自动恢复中」改回「未启动」。
                        }
                        else
                        {
                            SetState(_core.State);
                        }
                        RefreshPasswordCard();
                    };
                    if (IsHandleCreated)
                    {
                        BeginInvoke(done);
                    }
                });
            }
            catch (Exception exc)
            {
                MessageBox.Show(this, "核心程序起不来：\n" + exc.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetState(CoreState.Failed);
            }
        }

        private void ToggleCore()
        {
            if (_core.IsRunning)
            {
                CancelRecoveryTimer();
                _recovery.Reset();
                _core.Stop();
                SetState(CoreState.Stopped);
                AppendLine("[启动器] 已停止核心");
            }
            else
            {
                _recovery.Reset();
                StartCore();
            }
        }

        private void RestartCore()
        {
            AppendLine("[启动器] 正在重启核心...");
            CancelRecoveryTimer();
            _recovery.Reset();
            _core.Stop();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1200);
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(StartCore));
                }
            });
        }

        // 内置扫码登录：QQ 官方机器人的凭证由核心去拿、由核心写进配置。人不用手抄 AppSecret，
        // 也不用登录面板接口（那要先有密码，而初始密码他多半早就改过了）。
        private void StartScanLogin()
        {
            string type = SelectedType();
            if (type != "qq_official")
            {
                AppendLine("[启动器] 这种接入方式没有扫码登录，已打开面板的平台创建页。");
                ShowView(true);
                OpenPanel("/platforms");
                return;
            }
            if (_bindDialog != null && !_bindDialog.IsDisposed)
            {
                _bindDialog.Activate();
                return;
            }
            if (_core.IsRunning)
            {
                // 配置是扫码进程写的，核心同时持有同一份文件会打架；扫完自动再起来。
                AppendLine("[启动器] 先停一下核心：扫码要把平台配置写进文件。扫完会自动重启。");
                _core.Stop();
            }
            _scanButton.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1200);
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(OpenBindDialog));
                }
            });
        }

        private void OpenBindDialog()
        {
            BindDialog dialog = new BindDialog();
            BindSession session = new BindSession(_baseDir, _core.CorePath);
            _bindDialog = dialog;
            _bindSession = session;

            session.QrReady += delegate(string path) { dialog.ShowQr(path); };
            session.QrUrl += delegate(string url) { dialog.SetUrl(url); };
            session.StateText += delegate(string text) { dialog.SetStatus(text); };
            session.Completed += delegate(string info)
            {
                dialog.SetStatus("已写入配置，正在重启核心…");
                Action done = delegate
                {
                    AppendLine("[启动器] 扫码登录成功（" + info + "），正在重启核心。");
                    dialog.Close();
                    RestartCore();
                };
                if (IsHandleCreated)
                {
                    BeginInvoke(done);
                }
            };
            session.Failed += delegate(string reason) { dialog.SetStatus("没成：" + reason); };
            dialog.FormClosed += delegate
            {
                session.Cancel();
                if (_scanButton != null && !_quitting)
                {
                    _scanButton.Enabled = true;
                }
            };
            dialog.Show(this);
            session.Start("qq_official", _nameInput.Text, 300);
        }

        // ==== 纠错机制 ====

        private void AdoptFreePort()
        {
            AdoptFreePortWith(Preflight.PickPort(_core.Port));
        }

        private void AdoptFreePortWith(int free)
        {
            if (free == _core.Port)
            {
                return;
            }
            AppendLine("[启动器] 端口 " + _core.Port + " 被别的程序占着，自动改用 " + free + "（已记住，可在 data 目录的 desktop.conf 里改回）");
            _core.Port = free;
            _settings.Port = free;
            SaveSettings();
        }

        private void CancelRecoveryTimer()
        {
            if (_recoveryTimer != null)
            {
                _recoveryTimer.Stop();
            }
        }

        // 退避等待中：这期间核心「停止」是自家动作，别把状态灯改回「未启动」。
        private bool Recovering
        {
            get { return _recoveryTimer != null && _recoveryTimer.Enabled; }
        }

        private void HandleStartupTimeout()
        {
            AppendLine("[启动器] 核心进程活着，但面板在 " + (StartupTimeoutMs / 1000) + " 秒内没有开始监听，判定启动超时。");
            _core.Stop();
            HandleFault(FaultKind.Timeout, 0);
        }

        // 崩溃处置：认出故障 → 能就地纠的先纠 → 退避重启（最多 3 次）→ 兜不住才弹框问人。
        private void HandleFault(FaultKind kind, int code)
        {
            string tail = LogTail(80);
            string advice;
            if (kind == FaultKind.Timeout)
            {
                advice = "面板端口 " + _core.Port + " 一直没起来。可能是某个插件或平台适配器把启动卡住了。";
            }
            else
            {
                kind = Diagnose.Classify(tail, out advice);
            }
            if (kind == FaultKind.BadConfig && !Preflight.ConfigJsonInvalid(_baseDir))
            {
                // 配置文件本身是好的，那句 JSON 报错来自别处（插件自己解析缓存也会报），
                // 这时候隔离配置就是把人家的 QQ 配置搬家。
                advice = "";
                kind = FaultKind.Crashed;
            }
            _lastFault = Diagnose.Title(kind);
            string fix = ApplyCorrection(kind);
            LastRecoveryNote = _lastFault + (fix.Length > 0 ? " · " + fix : "");
            AppendLine("[启动器] 故障判定：" + _lastFault + (code != 0 ? "（退出码 " + code + "）" : ""));

            if (Diagnose.IsAutoFixable(kind) && _recovery.ShouldRetry())
            {
                int delay = _recovery.NextDelayMs();
                AppendLine("[启动器] " + (fix.Length > 0 ? fix + "；" : "") + "自动恢复中，" + (delay / 1000)
                    + " 秒后重启核心（第 " + _recovery.Attempts + "/" + RecoveryPolicy.MaxAttempts + " 次）");
                ShowRecovering(delay);
                return;
            }
            ShowFaultDialog(kind, advice, fix, tail);
        }

        // 只做能确定帮上忙的动作：坏配置挪开让核心重建、端口换个空闲的。
        // 缺依赖、目录不可写这类重启三次也是同一个错，不假装在修。
        private string ApplyCorrection(FaultKind kind)
        {
            if (kind == FaultKind.BadConfig)
            {
                string backup = Preflight.QuarantineConfig(_baseDir);
                return backup.Length > 0
                    ? "坏配置已备份为 " + Path.GetFileName(backup)
                    : "坏配置没能备份，直接重启试试";
            }
            if (kind == FaultKind.PortBusy || kind == FaultKind.Timeout)
            {
                int before = _core.Port;
                AdoptFreePort();
                return _core.Port != before ? "面板端口改用 " + _core.Port : "";
            }
            return "";
        }

        private void ShowRecovering(int delayMs)
        {
            if (_recoveryTimer == null)
            {
                _recoveryTimer = new System.Windows.Forms.Timer();
                _recoveryTimer.Tick += delegate
                {
                    _recoveryTimer.Stop();
                    if (!_quitting)
                    {
                        StartCore();
                    }
                };
            }
            _recoveryTimer.Interval = delayMs;
            _recoveryTimer.Start();
            Panel dot = _statusPill.Controls["statusDot"] as Panel;
            if (dot != null)
            {
                dot.BackColor = Theme.Warn;
            }
            _statusText.Text = "自动恢复中 " + _recovery.Attempts + "/" + RecoveryPolicy.MaxAttempts;
        }

        private void ShowFaultDialog(FaultKind kind, string advice, string fix, string tail)
        {
            FaultDialogShown++;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【" + Diagnose.Title(kind) + "】");
            if (_recovery.Attempts > 0)
            {
                sb.AppendLine("已自动重启 " + _recovery.Attempts + " 次还是没起来。");
            }
            else
            {
                sb.AppendLine("这类问题自动重启也解决不了。");
            }
            sb.AppendLine(advice.Length > 0 ? advice : "日志里没有认出明确的故障类型。先点「重试」，不行就点左边「导出诊断包」，把 data\\diagnostics 里的文件发给我。");
            if (fix.Length > 0)
            {
                sb.AppendLine("启动器已经做了：" + fix);
            }
            sb.AppendLine();
            sb.AppendLine("日志尾部（已脱敏）：");
            sb.Append(Trim(Redact.Text(tail), 700));
            DialogResult choice = MessageBox.Show(this, sb.ToString(), "AstrBot 出错了",
                MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
            if (choice == DialogResult.Retry)
            {
                _recovery.Reset();
                StartCore();
            }
        }

        private string LogTail(int lines)
        {
            List<string> all = new List<string>(_core.Snapshot());
            int start = all.Count > lines ? all.Count - lines : 0;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < all.Count; i++)
            {
                sb.AppendLine(all[i]);
            }
            return sb.ToString();
        }

        private string StateLine()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("状态: " + _core.State + " · 进程 " + (_core.IsRunning ? "存活 pid=" + _core.Pid : "已退出"));
            sb.AppendLine("面板: http://" + _settings.BrowserHost + ":" + _core.Port + " · 允许局域网=" + _settings.AllowLan);
            sb.AppendLine("QQ: " + (_core.QQConnected ? "已连接 " + _core.QQBotId : "未连接") + " · 累计消息 " + _core.QqMessageCount + " 条");
            sb.AppendLine("最近报错: " + (_core.LastError.Length > 0 ? _core.LastError : "无"));
            sb.AppendLine("最近故障: " + (_lastFault.Length > 0 ? _lastFault : "无"));
            sb.AppendLine("自动恢复: " + _recovery.Attempts + "/" + RecoveryPolicy.MaxAttempts);
            sb.AppendLine("构建戳: " + Build.Stamp + "（标题栏上同一串，别报错了版本）");
            sb.AppendLine("界面最坏卡顿: " + _worstLag + " ms · " + (_worstLagAt.Length > 0 ? _worstLagAt : "至今没堵住过"));
            sb.AppendLine("UI 线程上做过的 I/O: " + (UiGuard.Violations.Length > 0 ? UiGuard.Violations : "无（都挪到后台了）"));
            return sb.ToString();
        }

        private void ExportDiagnostics()
        {
            string tail = LogTail(400);
            string state = StateLine();
            SetBusy("导出诊断包中...");
            AppendLine("[启动器] 正在跑自检并导出诊断包（约半分钟）...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string selfCheck;
                _core.RunOnce("--self-check", out selfCheck, 180000);
                string path = Diagnostics.Write(_baseDir, selfCheck, tail, state);
                LastDiagnosticsPath = path;
                Action done = delegate
                {
                    SetState(_core.State);
                    AppendLine("[启动器] 诊断包已写出：" + path);
                    MessageBox.Show(this,
                        path
                        + "\n\n里面是自检输出 + 日志尾部 + 当前状态，密码/密钥/token 已经全部替换成 ***。"
                        + "\n把它发过来就能定位问题，不用截图。",
                        "诊断包已导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                if (IsHandleCreated)
                {
                    BeginInvoke(done);
                }
            });
        }

        // 一键修复：确认一次，然后整段跑在后台线程（每一项都要碰磁盘或网络）。
        // 指一份免安装包给修复器：里面是这份程序的完整副本，被吃掉的文件能按摘要一个个补回来
        private void PickRestoreZip()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选你手上那份 AstrBot-4.29.3-desktop-portable.zip";
                dialog.Filter = "免安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                _settings.RestoreZip = dialog.FileName;
                AppendLine("[修复] 已认下这份免安装包：" + Path.GetFileName(dialog.FileName)
                    + "（只读它，不改它；修复时会按清单里的摘要逐个核对）");
                SaveSettings();
            }
        }

        // 演练必须落在一次性目录里：这台机器的 data\onebot 现在真的装着用户的 NapCat，
        // 拿交付目录当演练场等于往人家的客户端上解压、删目录（上一轮就差点动了它）。
        private string ScratchAppDir()
        {
            if (_obScratchApp.Length == 0)
            {
                _obScratchApp = Path.Combine(Path.GetTempPath(),
                    "astrbot-obapp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path.Combine(_obScratchApp, "data"));
            }
            return _obScratchApp;
        }

        private void RunRepairInteractive()
        {
            if (_repairBusy)
            {
                AppendLine("[修复] 上一次还在跑，结论会一行行写在这里");
                return;
            }
            if (MessageBox.Show(this,
                "要现在跑一次一键修复吗？\n\n"
                + "· 会检查：程序文件、数据目录、配置文件、面板端口、核心服务、磁盘、内嵌面板内核、QQ 接入客户端\n"
                + "· 只动有把握的：补建缺失目录、把读坏的配置留档挪开、换到空闲端口、起/重启我们自己那个核心\n"
                + "· 不会做：替你删任何文件、杀别人的进程、重置面板口令（那会断开机器人 3~5 秒，得你自己点那个按钮）\n"
                + "· 修不了的会直说修不了（比如程序文件被杀毒软件吃了，只能重装），不假装修好\n\n"
                + "全程在后台跑，界面照常能动；每一项的结论都写进下面的日志。",
                "一键修复", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            _repairBusy = true;
            AppendLine("[修复] 开始体检");
            RepairRunner runner = new RepairRunner(_baseDir, _settings, BuildRepairHooks());
            Thread worker = new Thread(delegate()
            {
                string summary = "";
                try
                {
                    summary = runner.Run();
                }
                catch (Exception exc)
                {
                    summary = "修复器自己出错了：" + exc.Message;
                }
                string done = summary;
                if (IsHandleCreated)
                {
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            _repairBusy = false;
                            AppendLine("[修复] 结束：" + done);
                            RefreshOneBotStatus();
                            MessageBox.Show(this,
                                done + "\n\n每一项的明细都在下面的运行日志里（往上翻）。"
                                + "标了「需要你处理」的那些，我们不该替你动手。",
                                "一键修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }));
                    }
                    catch (Exception)
                    {
                        _repairBusy = false;
                    }
                }
                else
                {
                    _repairBusy = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // 面板到底答不答话，只问 HTTP，不看我们自己的状态字段（状态字段会说谎）
        // 还有没有"别的"启动器在跑（同路径、不同 PID）。有的话它名下那个核心不是孤儿，绝不能杀。
        private bool AnotherLauncherRunning()
        {
            int me = Process.GetCurrentProcess().Id;
            string mine;
            try
            {
                mine = Path.GetFullPath(Path.Combine(_baseDir, "AstrBot.exe"));
            }
            catch (Exception)
            {
                return true;    // 判不出来就当有人在跑，宁可不清
            }
            try
            {
                foreach (Process proc in Process.GetProcessesByName("AstrBot"))
                {
                    using (proc)
                    {
                        if (proc.Id == me)
                        {
                            continue;
                        }
                        try
                        {
                            if (string.Equals(Path.GetFullPath(proc.MainModule.FileName), mine,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private RepairHooks BuildRepairHooks()        {
            RepairHooks hooks = new RepairHooks();
            hooks.CoreRunning = delegate { return _core.IsRunning; };
            hooks.CorePid = delegate { return _core.Pid; };
            hooks.StopCore = delegate { StopCoreFromRepair(); };
            hooks.StartCore = delegate { StartCoreFromRepair(); };
            hooks.PanelAnswers = delegate { return PanelHttpAnswers(0); };
            hooks.RediscoverOneBot = delegate { DiscoverOneBotFromRepair(); };
            hooks.OneBotNote = delegate { return OneBotRepairNote(); };
            hooks.SaveSettings = delegate { SaveSettings(); };
            hooks.OwnCorePid = delegate { return _core.Pid; };
            hooks.OtherLauncherAlive = delegate { return AnotherLauncherRunning(); };
            hooks.RestoreZipPath = delegate { return _settings.RestoreZip; };
            hooks.Log = delegate(string text)
            {
                if (IsHandleCreated)
                {
                    try { BeginInvoke(new Action(delegate { AppendLine(text); })); } catch (Exception) { }
                }
            };
            return hooks;
        }

        private bool PanelHttpAnswers(int port)
        {
            int target = port > 0 ? port : _core.Port;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + target + "/");
                request.Timeout = 3000;
                request.UserAgent = "astrbot-desktop-repair";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return (int)response.StatusCode >= 200 && (int)response.StatusCode < 400;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void StopCoreFromRepair()
        {
            if (IsHandleCreated)
            {
                try { BeginInvoke(new Action(delegate { _core.Stop(); })); } catch (Exception) { }
            }
            for (int i = 0; i < 40 && _core.IsRunning; i++)
            {
                Application.DoEvents();
                Thread.Sleep(250);
            }
        }

        private void StartCoreFromRepair()
        {
            if (IsHandleCreated)
            {
                try { BeginInvoke(new Action(delegate { StartCore(); })); } catch (Exception) { }
            }
            for (int i = 0; i < 80 && !_core.IsRunning; i++)
            {
                Application.DoEvents();
                Thread.Sleep(250);
            }
        }

        // 修复器跑在后台线程，这里可以放心同步扫；扫完把刷新丢回 UI 线程
        private void DiscoverOneBotFromRepair()
        {
            try
            {
                List<OneBotClientInfo> found = OneBotController.Discover(_baseDir, _settings.OneBotDir);
                _onebot.Client = found.Count > 0 ? found[0] : null;
                if (_onebot.Client != null)
                {
                    _onebot.LoadWebUiConfig();
                    _obAlive = _onebot.PanelAlive();
                }
                else
                {
                    _obAlive = false;
                }
            }
            catch (Exception)
            {
            }
            if (IsHandleCreated)
            {
                try { BeginInvoke(new Action(RefreshOneBotStatus)); } catch (Exception) { }
            }
        }

        // 只有真在接个人号（配置里有 aiocqhttp / satori）才需要本机客户端，否则这条不该吓人
        private string OneBotRepairNote()
        {
            string cfg = Path.Combine(_baseDir, "data", "cmd_config.json");
            bool personalAccount = false;
            try
            {
                if (File.Exists(cfg))
                {
                    string text = File.ReadAllText(cfg, Encoding.UTF8);
                    personalAccount = text.Contains("aiocqhttp") || text.Contains("satori");
                }
            }
            catch (Exception)
            {
            }
            if (!personalAccount)
            {
                return "";
            }
            if (_onebot.Client == null)
            {
                return "没找到客户端目录";
            }
            return _obAlive ? "客户端在线（" + _onebot.Client.Flavor + "）" : "客户端没起来";
        }

        private void OpenPanel()
        {
            OpenPanel("");
        }

        private void OpenPanel(string path)
        {
            string url = "http://" + _settings.BrowserHost + ":" + _core.Port + path;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }

        private void OpenDataDir()
        {
            string dir = Path.Combine(_baseDir, "data");
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }

        private void RunSelfCheckInteractive()
        {
            SetBusy("自检中...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string output;
                int code = _core.RunOnce("--self-check", out output, 180000);
                Action done = delegate
                {
                    AppendLine(output);
                    SetBusy("");
                    MessageBox.Show(this, Trim(output, 1800), code == 0 ? "自检通过" : "自检有问题（代码 " + code + "）",
                        MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                };
                if (IsHandleCreated)
                {
                    BeginInvoke(done);
                }
            });
        }

        private static string Trim(string text, int max)
        {
            if (text.Length <= max)
            {
                return text;
            }
            return text.Substring(text.Length - max);
        }

        private void SetBusy(string text)
        {
            _busyLabel = text;
            _statusText.Text = text;
        }

        private void SetState(CoreState state)
        {
            Color dot = Theme.Muted;
            string label = "未启动";
            switch (state)
            {
                case CoreState.Starting:
                    dot = Theme.Warn;
                    label = "启动中";
                    break;
                case CoreState.Running:
                    dot = Theme.Ok;
                    label = "运行中";
                    break;
                case CoreState.Failed:
                    dot = Theme.Bad;
                    label = "异常退出";
                    break;
                default:
                    break;
            }
            Panel pill = _statusPill.Controls["statusDot"] as Panel;
            if (pill != null)
            {
                pill.BackColor = dot;
            }
            _statusText.Text = label;
            if (state == CoreState.Running)
            {
                // 起来了就恢复满预算：下次真崩了还能再自动试三次，而不是被上一次用完的次数卡住。
                _recovery.Reset();
                CancelRecoveryTimer();
            }
            _urlLabel.Text = "多平台机器人 · 面板 http://" + _settings.BrowserHost + ":" + _core.Port;
            if (state == CoreState.Running && _panelView != null && _panelView.Visible)
            {
                // 面板比核心慢几秒是常态：核心一起来就自动重导一次。
                _panelView.Navigate();
            }
            RefreshQqStatus();
            RefreshPasswordCard();
        }

        private void RefreshPasswordCard()
        {
            // 以前这张卡只在"核心第一次跑出初始口令"那一瞬间出现，之后永远隐藏：
            // 口令改过或重装过的人就再也看不到它，也不知道怎么找回 —— 用户反馈的
            // 「密码显示没了」就是这个。现在卡片常驻，三种状态各自说清楚。
            if (_core.InitialPassword.Length > 0)
            {
                _passwordValue.Text = _core.InitialPassword;
                _passwordValue.ForeColor = Theme.Warn;
                _passwordHint.Text = "登录后请去面板「安全」里改掉";
                _passwordHint.ForeColor = Theme.Muted;
                _passwordReset.Enabled = false;
                return;
            }
            if (File.Exists(Path.Combine(_baseDir, "data", "cmd_config.json")))
            {
                _passwordValue.Text = "已设置过，不在这里显示";
                _passwordValue.ForeColor = Theme.Muted;
                _passwordHint.Text = "忘了口令就点右边「重置口令」：生成新的并重启核心";
                _passwordHint.ForeColor = Theme.Muted;
                _passwordReset.Enabled = _core.State != CoreState.Starting;
                return;
            }
            _passwordValue.Text = "启动核心后显示在这里";
            _passwordValue.ForeColor = Theme.Muted;
            _passwordHint.Text = "口令只写在本机 data 目录里，不上网、不进日志";
            _passwordHint.ForeColor = Theme.Muted;
            _passwordReset.Enabled = false;
        }

        // 重置走的是「带 --reset-password 启一次核心」：口令由核心自己生成并写回配置，
        // 启动器不生成、也不抄写它 —— 那条一次性通道一旦当成 RunOnce 用会起一个真的服务，
        // 所以这里是正常启动路径，不是一次性调用。
        private void ResetPanelPassword()
        {
            string blank = Environment.NewLine + Environment.NewLine;
            string ask = "要重置面板口令吗？" + blank
                + "· 会重启一次核心（断开 3~5 秒，期间机器人不收发）" + Environment.NewLine
                + "· 旧口令立即失效，包括你在面板里改过的那一个" + Environment.NewLine
                + "· 新口令会显示在左边的卡片里，登录后建议再去「安全」改一次";
            if (MessageBox.Show(this, ask,
                "重置面板口令", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
            _coreResetOnce = true;
            if (_core.IsRunning)
            {
                AppendLine("[启动器] 正在重启核心以重置口令...");
                RestartCore();
            }
            else
            {
                StartCore();
            }
        }

        private void RefreshQqStatus()
        {
            if (_core.QQConnected)
            {
                _qqStatus.ForeColor = Theme.Ok;
                string who = _core.QQBotId.Length > 0 ? "（Bot " + _core.QQBotId + "）" : "";
                _qqStatus.Text = "QQ 状态：已连接" + who + " · 收到消息 " + _core.QqMessageCount + " 条"
                    + (_core.LastQqMessage.Length > 0 ? " · 最近：" + Shorten(_core.LastQqMessage, 24) : "");
                _tips.SetToolTip(_qqStatus, _core.QQDetail);
            }
            else if (_core.LastError.Length > 0)
            {
                _qqStatus.ForeColor = Theme.Bad;
                _qqStatus.Text = "QQ 状态：未连接 · 最近报错：" + Shorten(_core.LastError, 60);
                _tips.SetToolTip(_qqStatus, _core.LastError);
            }
            else
            {
                _qqStatus.ForeColor = Theme.Muted;
                _qqStatus.Text = "QQ 状态：未连接（NapCat / 官方机器人 连上后这里会变绿）";
                _tips.SetToolTip(_qqStatus, "");
            }
        }

        private static string Shorten(string text, int max)
        {
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private void OnCoreLine(string line)
        {
            if (line.Trim().Length == 0)
            {
                return;
            }
            // 核心是按行异步投递的，这条回调跑在读输出的线程上。以前直接改控件：
            // 内嵌面板开着时 SetState→Navigate 会碰 WebView2 的 COM 对象，
            // 整个启动器当场崩（--repairtest 开着面板起核心就是这么撞出来的）。
            if (IsHandleCreated && InvokeRequired)
            {
                string captured = line;
                try
                {
                    BeginInvoke(new Action(delegate { OnCoreLine(captured); }));
                }
                catch (Exception)
                {
                }
                return;
            }
            // HandleLine 已经先把 State 置成 Running 了，所以这里只能看日志内容，
            // 再判 State 就永远进不来（状态灯只能等端口探测，慢半拍）。
            if (line.Contains("AstrBot started"))
            {
                SetState(CoreState.Running);
            }
            if (line.Contains("Initial password"))
            {
                // 口令这行可能在状态变化之前到，等下一次 SetState 就晚了
                RefreshPasswordCard();
            }
            if (line.Contains("适配器已连接") || line.Contains("收到消息"))
            {
                RefreshQqStatus();
            }
            AppendLine(line);
        }

        private void OnCoreExited(int code)
        {
            Action done = delegate
            {
                if (Recovering)
                {
                    return;
                }
                SetState(_core.State);
                if (code != 0 && !_quitting)
                {
                    AppendLine("[启动器] 核心意外退出，代码 " + code + "。日志最后几行如上。");
                    HandleFault(FaultKind.None, code);
                }
            };
            if (IsHandleCreated)
            {
                BeginInvoke(done);
            }
        }

        private void AppendLine(string line)
        {
            if (line.Length == 0)
            {
                return;
            }
            // 只入队，不在生产者线程碰 UI。核心启动/重启时一次连发几百行，
            // 逐行 BeginInvoke + AppendText + ScrollToCaret 会把消息循环占满，
            // 窗口就挂着不动了（用户反馈的「容易未响应」主要是这里 + 后台探活占 UI 线程）。
            lock (_logSync)
            {
                _logPending.Add(line);
                _lastUiLine = line.Length > 90 ? line.Substring(0, 90) : line;
            }
        }

        // 攒着的日志一次写进文本框：一条消息画一批，而不是几百条消息各画一行。
        private void WatchdogTick()
        {
            int now = Environment.TickCount;
            if (_watchdogAt != 0)
            {
                int lag = now - _watchdogAt - _watchdogTimer.Interval;
                if (lag > 600)
                {
                    _lastLag = lag;
                    if (lag > _worstLag)
                    {
                        _worstLag = lag;
                        _worstLagAt = "当时在忙：" + (_busyLabel.Length > 0 ? _busyLabel : "无")
                            + " · 状态：" + _core.State + " · 最近一行：" + _lastUiLine;
                    }
                    AppendLine("[界面] 消息循环被堵 " + lag + " ms（" + (_busyLabel.Length > 0 ? _busyLabel : "没有进行中的动作") + "）");
                }
            }
            _watchdogAt = now;
        }

        private void FlushLog()
        {
            string[] batch;
            lock (_logSync)
            {
                if (_logPending.Count == 0)
                {
                    return;
                }
                // 一轮只刷这么多行：一次 AppendText 1200 行本身就要堵 2.6 秒
                // （批量只是把成本挪了个位置，没消掉），剩下的高度量分批写。
                int take = _logPending.Count > 120 ? 120 : _logPending.Count;
                batch = _logPending.GetRange(0, take).ToArray();
                _logPending.RemoveRange(0, take);
            }
            StringBuilder sb = new StringBuilder();
            foreach (string line in batch)
            {
                sb.Append(line);
                sb.Append(Environment.NewLine);
            }
            // 人在往上翻就别硬把他拉回底部
            bool atEnd = _logBox.SelectionStart >= _logBox.TextLength;
            _logBox.AppendText(sb.ToString());
            _logLines += batch.Length;
            if (_logLines > 700)
            {
                // 文本框越长，一次写入与裁剪都越贵；把上限压到 700 行、一次丢到 300 行，
                // 让"最贵的那一下"落在能接受的范围里
                int cut = _logBox.Text.IndexOf(Environment.NewLine, _logBox.TextLength / 2);
                if (cut > 0)
                {
                    _logBox.Select(0, cut + Environment.NewLine.Length);
                    _logBox.SelectedText = "";
                }
                _logLines = 400;
                atEnd = true;
            }
            if (atEnd)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        private bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    return key != null && key.GetValue(_autostartKey) != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (on)
                    {
                        key.SetValue(_autostartKey, "\"" + Application.ExecutablePath + "\" --minimized");
                    }
                    else
                    {
                        key.DeleteValue(_autostartKey, false);
                    }
                }
                AppendLine("[启动器] 开机自动启动：" + (on ? "已开启（--minimized 静默启动）" : "已关闭"));
            }
            catch (Exception exc)
            {
                MessageBox.Show(this, "改开机自启失败：" + exc.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Quit()
        {
            _quitting = true;
            CancelRecoveryTimer();
            _core.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_quitting && _settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(2000, "AstrBot 仍在运行", "机器人还在收发消息，双击托盘图标回到窗口。", ToolTipIcon.Info);
                return;
            }
            _core.Stop();
            base.OnFormClosing(e);
        }

        // 验收钩子：不靠鼠标自动化，逐项检查界面状态，供 --uitest 调用。
        public string LastTestReport = "";
        public bool AutoStart = true;
        private readonly List<string> _testFails = new List<string>();

        private Control FindVisible(string name)
        {
            Control[] hits = Controls.Find(name, true);
            foreach (Control hit in hits)
            {
                if (hit.Visible && hit.Parent != null)
                {
                    return hit;
                }
            }
            return hits.Length == 0 ? null : hits[0];
        }

        private static string Count(Control control)
        {
            return control == null ? "缺" : "有";
        }

        private static bool FitsInParent(Control control)
        {
            Rectangle area = control.Parent.ClientRectangle;
            return area.Contains(control.Location)
                && area.Contains(new Point(control.Right - 1, control.Bottom - 1));
        }

        private void Expect(string name, bool ok)
        {
            LastTestReport += (ok ? "PASS  " : "FAIL  ") + name + "\n";
            if (!ok)
            {
                _testFails.Add(name);
            }
        }

        private bool WaitState(CoreState want, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline && _core.State != want)
            {
                Application.DoEvents();
                Thread.Sleep(200);
            }
            return _core.State == want;
        }

        // 核心在日志线程里就把 State 改好了，界面那一步是排队执行的：
        // 断言状态灯/恢复计数前先把消息泵打一拍，否则读到的是上一刻的界面。
        private void Pump(int ms)
        {
            for (int waited = 0; waited < ms; waited += 100)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
        }

        private bool WaitPill(string fragment, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline && !_statusText.Text.Contains(fragment))
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            return _statusText.Text.Contains(fragment);
        }

        private static Control FindNamed(Control.ControlCollection controls, string name)
        {
            foreach (Control c in controls)
            {
                if (c.Name == name)
                {
                    return c;
                }
            }
            return null;
        }

        public bool PanelReady
        {
            get { return _core.State == CoreState.Running; }
        }

        public void ForceQuit()
        {
            _quitting = true;
            CancelRecoveryTimer();
            _core.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
            }
            Close();
        }

        // 测试里调一次性通道也必须走后台线程：RunOnce 要等核心子进程跑完，快慢说不准，
        // 在 UI 线程上调它，抓奸器抓到的是测试自己，而不是产品里的真问题。
        // 等的时候照常泵消息 —— 顺带证明这段时间界面没堵。
        private int RunCoreOffUiAndWait(string args, int timeoutMs, out string output)
        {
            string[] box = new string[1];
            int[] code = new int[1];
            bool[] finished = new bool[1];
            code[0] = -9;
            Thread worker = new Thread(delegate()
            {
                string text;
                code[0] = _core.RunOnce(args, out text, timeoutMs);
                box[0] = text;
                finished[0] = true;
            });
            worker.IsBackground = true;
            worker.Start();
            int waited = 0;
            while (!finished[0] && waited < timeoutMs + 8000)
            {
                Application.DoEvents();
                Thread.Sleep(25);
                waited += 45;
            }
            output = box[0] == null ? "" : box[0];
            return code[0];
        }

        // 后台扫描/探活还没回来就把临时目录删掉，只会看到一堆无谓的竞态。测试收尾等它们落地。
        private void WaitOneBotSettled()
        {
            int waited = 0;
            while ((_obDiscovering || _obBusy) && waited < 30000)
            {
                Application.DoEvents();
                Thread.Sleep(25);
                waited += 45;
            }
        }

        // 测试夹具自己要碰磁盘的那几笔（扫目录、装压缩包）也挪到后台再泵回来。
        // 断言本身留在 UI 线程：LastTestReport / _testFails 不是线程安全的，不能两个线程一起写。
        private void RunOffUiAndWait(Action job)
        {
            Exception[] failure = new Exception[1];
            Thread worker = new Thread(delegate()
            {
                try
                {
                    job();
                }
                catch (Exception exc)
                {
                    failure[0] = exc;
                }
            });
            worker.IsBackground = true;
            worker.Start();
            int waited = 0;
            while (worker.IsAlive && waited < 300000)
            {
                Application.DoEvents();
                Thread.Sleep(25);
                waited += 45;
            }
            worker.Join(5000);
            if (failure[0] != null)
            {
                throw failure[0];
            }
        }

        public void RunUiTest()
        {
            _testFails.Clear();
            Action<string, bool> expect = Expect;

            expect("顶部状态胶囊存在", _statusPill != null && _statusText != null);

            for (int i = 0; i < PlatformIo.QqTypes.Length; i++)
            {
                _typeBox.SelectedIndex = i;
                string type = PlatformIo.QqTypes[i][1];
                int wanted = TypeFields[type].Length;
                bool allVisible = _fieldInputs.Count == wanted;
                foreach (KeyValuePair<string, InputBox> kv in _fieldInputs)
                {
                    InputBox box = kv.Value;
                    Point center = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
                    bool clipped = _fieldsPanel.Region != null && !_fieldsPanel.Region.IsVisible(center);
                    if (!box.Visible || box.Height < 12 || clipped)
                    {
                        allVisible = false;
                    }
                }
                expect("类型 " + type + " 渲染并可见 " + wanted + " 个字段", allVisible);
            }

            PlatformEntry entry = new PlatformEntry();
            entry.Set("type", "aiocqhttp");
            entry.Set("id", "uitest_bot");
            entry.Set("name", "自检号");
            entry.Set("enable", "true");
            entry.Set("ws_reverse_host", "127.0.0.1");
            entry.Set("ws_reverse_port", "6199");
            List<PlatformEntry> parsed = PlatformIo.ParseDump(PlatformIo.BuildIni(entry));
            expect("平台配置块往返一致",
                parsed.Count == 1
                && parsed[0].Get("id") == "uitest_bot"
                && parsed[0].Get("ws_reverse_port") == "6199");

            _core.InitialPassword = "UITESTPWD123";
            RefreshPasswordCard();
            expect("密码卡片能显示初始密码", _passwordCard.Visible && _passwordValue.Text == "UITESTPWD123");

            SetState(CoreState.Running);
            Panel dot = _statusPill.Controls["statusDot"] as Panel;
            expect("运行状态灯为绿", dot != null && dot.BackColor == Theme.Ok && _statusText.Text == "运行中");

            _core.QQConnected = true;
            _core.QqMessageCount = 3;
            _core.LastQqMessage = "在吗";
            RefreshQqStatus();
            expect("QQ 已连接状态可见", _qqStatus.Text.Contains("已连接") && _qqStatus.Text.Contains("3"));

            // 每个适配器"连上了"的文案都不一样，逐条喂日志确认识别得对。
            _core.QQConnected = false;
            _core.QQBotId = "";
            _core.LastError = "";
            _core.HandleLine("[01:00:00] [Core] [INFO] [qqofficial_platform_adapter:263]: [QQOfficial] Websocket session starting.");
            expect("官方机器人 session starting 算已连接", _core.QQConnected);
            _core.HandleLine("[01:00:01] [Core] [INFO] [satori_adapter:274]: Satori 连接成功 - Bot 1: platform=qq, user_id=998877, user_name=测试号");
            expect("Satori 连接成功能抓到 Bot 号", _core.QQBotId == "998877");
            _core.HandleLine("[01:00:02] [Core] [INFO] [qo_webhook_adapter:233]: QQ 机器人官方 API 适配器已经被关闭");
            expect("关闭文案能把状态改回未连接", !_core.QQConnected);
            _core.HandleLine("[01:00:03] [Core] [ERROR] [platform.manager:300]: 官方机器人鉴权失败");
            expect("报错行会留作状态提示", _core.LastError.Length > 0);
            RefreshQqStatus();
            expect("未连接且有报错时状态变红", _qqStatus.ForeColor == Theme.Bad && _qqStatus.Text.Contains("鉴权失败"));

            SetState(CoreState.Failed);
            expect("异常状态灯为红", dot != null && dot.BackColor == Theme.Bad);

            bool before = _settings.AllowLan;
            _settings.AllowLan = !before;
            SaveSettings();
            LauncherSettings again = LauncherSettings.Load(_settings.FilePath);
            expect("设置能存回读", again.AllowLan == !before);
            _settings.AllowLan = before;
            SaveSettings();

            // desktop.conf 里的 plugin_mirror 由核心读：窗口运行期间有人在文件里加的键，
            // 下一次保存必须还在（否则「换了下载源」这件事会悄悄失效）。
            string rawConf = File.ReadAllText(_settings.FilePath, Encoding.UTF8);
            File.AppendAllText(_settings.FilePath, "plugin_mirror=https://mirror.invalid/" + Environment.NewLine, Encoding.UTF8);
            SaveSettings();
            string savedConf = File.ReadAllText(_settings.FilePath, Encoding.UTF8);
            expect("运行中加进 desktop.conf 的核心设置不会被保存丢掉",
                savedConf.Contains("plugin_mirror=https://mirror.invalid/"));
            File.WriteAllText(_settings.FilePath, rawConf, Encoding.UTF8);
            SaveSettings();
            expect("还原后测试值不留在配置里",
                !File.ReadAllText(_settings.FilePath, Encoding.UTF8).Contains("mirror.invalid"));

            AppendLine("UITEST log line");
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
            FlushLog();
            expect("日志区能追加内容", _logBox.Text.Contains("UITEST log line"));
            expect("核心程序路径可定位", Path.GetFileName(_core.CorePath) == "AstrBotCore.exe");

            // 真跑一次冻结核心的一次性通道：保存 QQ 配置走的就是这条路。
            string probe = Path.Combine(Path.GetTempPath(), "astrbot-uitest-dump.ini");
            string bridgeOutput;
            int bridgeCode = RunCoreOffUiAndWait("--platform-dump \"" + probe + "\"", 120000, out bridgeOutput);
            expect("启动器能调通冻结核心的配置通道",
                bridgeCode == 0 && bridgeOutput.Contains("PLATFORM_DUMPED"));

            // 内嵌面板：真起核心、真导航，确认 WebView2 在窗口里把面板加载出来。
            try
            {
                _core.Port = 6187;
                _core.Start("");
                DateTime bootDeadline = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < bootDeadline && _core.State != CoreState.Running)
                {
                    Application.DoEvents();
                    Thread.Sleep(200);
                }
                expect("启动核心后状态为运行中", _core.State == CoreState.Running);

                ShowView(true);
                DateTime navDeadline = DateTime.UtcNow.AddSeconds(40);
                while (DateTime.UtcNow < navDeadline && !_panelView.Navigated)
                {
                    Application.DoEvents();
                    Thread.Sleep(200);
                }
                expect("面板在窗口内加载完成（" + _panelView.Status + "）", _panelView.Navigated);
                expect("页签高亮跟随视图切换", _tabPanel.Active && !_tabOverview.Active);
                ShowView(false);
                expect("视图选择会记住",
                    LauncherSettings.Load(_settings.FilePath).LastView == "overview");
            }
            catch (Exception exc)
            {
                expect("内嵌面板流程可完成（" + exc.Message + "）", false);
            }

            // 手动停止不该被记成「异常退出」：taskkill 之后 Exited 一定带非零码。
            try
            {
                _core.Stop();
                for (int i = 0; i < 25; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(200);
                }
                expect("手动停止后不是异常退出（state=" + _core.State + " pill=" + _statusText.Text + "）",
                    _core.State == CoreState.Stopped && !_statusText.Text.Contains("异常"));
            }
            catch (Exception exc)
            {
                expect("手动停止流程可完成（" + exc.Message + "）", false);
            }

            // 内置扫码登录：协议解析、脱敏兜底、按钮随类型变、窗口状态与二维码显示。
            string kind;
            string payload;
            expect("扫码协议行能解析出二维码文件",
                BindSession.ParseLine("BIND_QR_FILE D:\\x\\bind-qq_official.png", out kind, out payload)
                && kind == "BIND_QR_FILE" && payload.EndsWith("bind-qq_official.png"));
            string junkKind;
            string junkPayload;
            expect("核心的普通日志行不会被当成状态",
                !BindSession.ParseLine("[12:00:00] [Core] [INFO] [platform.manager:120]: 平台已加载", out junkKind, out junkPayload));
            expect("扫码完成行带着平台标识",
                BindSession.ParseLine("BIND_DONE id=qq_official_102112 type=qq_official mode=add", out kind, out payload)
                && kind == "BIND_DONE" && payload.Contains("qq_official_102112"));
            expect("扫码失败行能变成人话",
                BindSession.ParseLine("BIND_FAIL RuntimeError: 二维码过期了，重新点一次就好", out kind, out payload)
                && kind == "BIND_FAIL" && payload.Contains("过期"));
            string leakKind;
            string leakPayload;
            BindSession.ParseLine("BIND_STATE secret=abcdef123456", out leakKind, out leakPayload);
            expect("万一协议行里带了密钥就整段丢掉",
                !leakPayload.Contains("abcdef123456") && leakPayload.Contains("已省略"));
            expect("扫码状态文案对得上界面",
                BindSession.StateLabel("pending").Contains("等待扫码") && BindSession.StateLabel("created").Contains("写入配置"));
            string tricky = BindSession.SafeName("a\" --bind-timeout 1\r\nb");
            expect("备注名里的引号换行不会拆坏命令行",
                !tricky.Contains("\"") && !tricky.Contains("\r") && !tricky.Contains("\n"));
            expect("备注名过长会被截住", BindSession.SafeName(new string('x', 120)).Length <= 40);

            int scanIndex = 0;
            for (int i = 0; i < PlatformIo.QqTypes.Length; i++)
            {
                _typeBox.SelectedIndex = i;
                if (PlatformIo.QqTypes[i][1] == "qq_official")
                {
                    scanIndex = i;
                    expect("官方机器人时扫码按钮写着扫码登录", _scanButton.Text.Contains("扫码"));
                }
                else
                {
                    expect("非扫码类型时按钮改成去面板创建（不把人堵死）", _scanButton.Text.Contains("面板"));
                }
            }
            _typeBox.SelectedIndex = scanIndex;

            BindDialog bindProbe = new BindDialog();
            bindProbe.SetStatus("等待扫码…");
            bindProbe.Show();
            for (int i = 0; i < 10; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            expect("扫码窗口能显示状态文字", bindProbe.StatusText.Contains("等待扫码"));
            bindProbe.ShowQr(Path.Combine(_baseDir, "no-such-file.png"));
            for (int i = 0; i < 6; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }
            expect("二维码文件不存在时不崩、也不假装显示", !bindProbe.QrShown);
            string logo = Path.Combine(_baseDir, "astrbot.png");
            if (File.Exists(logo))
            {
                bindProbe.ShowQr(logo);
                for (int i = 0; i < 8; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                expect("扫码窗口能显示二维码图片", bindProbe.QrShown);
                bindProbe.SetUrl("https://q.qq.com/qqbot/openclaw/connect.html?task_id=x");
                for (int i = 0; i < 6; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
            }
            bindProbe.Close();
            expect("扫码窗口关掉后不留在屏幕上", bindProbe.IsDisposed);
            // —— QQ 接入客户端（个人号）这一段 ——
            // 造两个假客户端目录。判据必须是真实标志文件：只看目录名的话，
            // 一个空的 D:\NapCat 就会被当成"客户端找到了"，然后把人引到不存在的地方。
            string obRoot = Path.Combine(Path.GetTempPath(), "astrbot-ob-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            // 夹具必须跟"这台机器上真装着一个客户端"隔离开：Discover 除了看指定目录，
            // 还会扫 appDir\data\onebot —— 用户装过 NapCat 之后那里就是有东西的（这才是常态）。
            // 拿一个空的临时目录当 appDir，断言数的才是夹具自己摆的那份。
            string obScratch = Path.Combine(obRoot, "appdir");
            Directory.CreateDirectory(obScratch);
            string obShell = Path.Combine(obRoot, "napcat-shell");
            string obFramework = Path.Combine(obRoot, "framework");
            string obEmpty = Path.Combine(obRoot, "empty");
            string obNested = Path.Combine(obRoot, "nested", "NapCat-v4");
            try
            {
                Directory.CreateDirectory(obShell);
                Directory.CreateDirectory(obFramework);
                Directory.CreateDirectory(obEmpty);
                Directory.CreateDirectory(obNested);
                File.WriteAllText(Path.Combine(obShell, "NapCatWinBootMain.exe"), "");
                File.WriteAllText(Path.Combine(obShell, "napcat.mjs"), "");
                File.WriteAllText(Path.Combine(obShell, "launcher-user.bat"), "@echo off");
                File.WriteAllText(Path.Combine(obShell, "qqnt.json"), "{\"version\":\"9.9.22-40990\"}");
                File.WriteAllText(Path.Combine(obFramework, "napimain.exe"), "");
                File.WriteAllText(Path.Combine(obFramework, "napiloader.dll"), "");
                File.WriteAllText(Path.Combine(obFramework, "napiLoader.bat"), "@echo off");
                File.WriteAllText(Path.Combine(obNested, "NapCatWinBootMain.exe"), "");
                File.WriteAllText(Path.Combine(obNested, "napcat.mjs"), "");
                File.WriteAllText(Path.Combine(obNested, "launcher.bat"), "@echo off");

                List<OneBotClientInfo> shellFound = new List<OneBotClientInfo>();
                List<OneBotClientInfo> fwFound = new List<OneBotClientInfo>();
                List<OneBotClientInfo> nested = new List<OneBotClientInfo>();
                List<OneBotClientInfo> emptyFound = new List<OneBotClientInfo>();
                // 扫描是真·磁盘活，留在这条线程上就成了我们自己抓的那种人；挪后台，断言照常在这儿做。
                RunOffUiAndWait(delegate
                {
                    shellFound = OneBotController.Discover(obScratch, obShell);
                    fwFound = OneBotController.Discover(obScratch, obFramework);
                    nested = OneBotController.Discover(obScratch, Path.Combine(obRoot, "nested"));
                    emptyFound = OneBotController.Discover(obScratch, obEmpty);
                });
                expect("能按真文件认出 NapCat Shell",
                    shellFound.Count == 1 && shellFound[0].Flavor == "NapCat Shell"
                    && shellFound[0].Entry.EndsWith("launcher-user.bat", StringComparison.OrdinalIgnoreCase));
                expect("客户端提示里带上它自己的目标 QQ 框架版本",
                    shellFound.Count == 1 && shellFound[0].Hint.Contains("9.9.22"));

                expect("Framework 版也认得（入口是 napiLoader.bat）",
                    fwFound.Count == 1 && fwFound[0].Flavor == "NapCat Framework"
                    && fwFound[0].Entry.EndsWith("napiLoader.bat", StringComparison.OrdinalIgnoreCase));

                expect("解压多套一层目录也找得到", nested.Count == 1);
                expect("只有启动脚本没有主程序时不算找到（不认错别人家的目录）",
                    emptyFound.Count == 0);

                // 反向 WS 那段 JSON 的字段名取自 v4.18.41 包里的 WebsocketClientConfigSchema，
                // 少一个字段就可能被它的校验层拒掉，所以逐条断言而不是"看着像"
                _onebot.ReverseWsPort = 6199;
                _onebot.ReverseWsToken = "napcat-token-1";
                string snippet = _onebot.BuildClientJson(_onebot.ReverseWsUrl(), _onebot.ReverseWsToken, "AstrBot 桌面版");
                bool allKeys = snippet.Contains("\"name\"") && snippet.Contains("\"enable\"") && snippet.Contains("\"url\"")
                    && snippet.Contains("\"messagePostFormat\"") && snippet.Contains("\"reportSelfMessage\"")
                    && snippet.Contains("\"reconnectInterval\"") && snippet.Contains("\"token\"")
                    && snippet.Contains("\"debug\"") && snippet.Contains("\"heartInterval\"");
                expect("生成的客户端配置带全 schema 里的九个字段", allKeys);
                expect("反向 WS 地址是本机回环加监听端口", _onebot.ReverseWsUrl() == "ws://127.0.0.1:6199/ws");
                expect("令牌原样写进片段（不截断不转义坏）", snippet.Contains("napcat-token-1"));

                expect("登录状态文案：online 说已登录", StatusOfLogin("online").Contains("已登录"));
                expect("登录状态文案：扫过码说等确认", StatusOfLogin("qrcode_scanned").Contains("确认"));
                expect("登录状态文案：拿不到阶段时不谎报", StatusOfLogin("").Contains("正在问"));

                // 状态行的诚实性：客户端没在线时绝不许显示"在线"
                // 先把 3 秒一跳的轮询停掉、把缓存的在线状态清零：这台机器上现在真跑着一个客户端，
                // 后台那一跳随时可能把 _obAlive 改成 true，那是在验"真装了客户端"，不是在验这条断言。
                if (_obTimer != null)
                {
                    _obTimer.Stop();
                }
                _obBusy = false;
                _obAlive = false;
                _onebot.Client = null;
                _onebot.LoginPhase = "";
                RefreshOneBotStatus();
                expect("没找到客户端时状态写「没找到」", _obStatus.Text.Contains("没找到"));
                OneBotClientInfo aliveProbe = new OneBotClientInfo();
                aliveProbe.Root = obShell;
                aliveProbe.Flavor = "NapCat Shell";
                aliveProbe.Entry = Path.Combine(obShell, "launcher-user.bat");
                _onebot.Client = aliveProbe;
                _onebot.WebHost = "127.0.0.1";
                _onebot.WebPort = 1;      // 一定连不通的端口
                RefreshOneBotStatus();
                expect("面板端口不通时不显示成在线", !_obStatus.Text.Contains("在线"));

                // FindNamed 只看直接子控件，这些按钮在右侧面板里，必须用递归查找；
                // 而且下面两条不许被 if 罩住——罩住之后"没找到"会变成"这条跳过了"，
                // 报告里看着一切正常，其实啥也没验。
                Control startBtn = FindVisible("obStart");
                Control stopBtn = FindVisible("obStop");
                Control qrBtn = FindVisible("obQr");
                Control uinBox = FindVisible("obUin");
                expect("个人号那段的按钮都在窗口里（"
                    + Count(startBtn) + "/" + Count(stopBtn) + "/" + Count(qrBtn) + "/" + Count(uinBox) + "）",
                    startBtn != null && stopBtn != null && qrBtn != null && uinBox != null);
                string[] names = new string[] { "obUin", "obBrowse", "obStart", "obStop", "obQr", "obDownload" };
                bool allFit = true;
                string badName = "";
                foreach (string name in names)
                {
                    Control hit = FindVisible(name);
                    if (hit == null || hit.Parent == null || !FitsInParent(hit))
                    {
                        allFit = false;
                        badName = name;
                        break;
                    }
                }
                expect("个人号这段的六个控件都完整落在面板内（" + (allFit ? "全部" : badName + " 被裁") + "）", allFit);
                expect("状态行没溢出面板底边",
                    _obStatus != null && _obStatus.Parent != null && _obStatus.Bottom <= _obStatus.Parent.ClientSize.Height);

                string qqExe = OneBotController.FindQqNtExe();
                expect("QQ 客户端判据不抛异常（有就是真存在的文件）",
                    qqExe.Length == 0 || File.Exists(qqExe));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(obRoot))
                    {
                        Directory.Delete(obRoot, true);
                    }
                }
                catch (Exception)
                {
                }
            }


            // —— 面板口令卡片与内嵌面板的可见性（用户反馈「打开面板后密码显示没了」） ——
            expect("口令卡片常驻显示（不再只在首次启动那一瞬出现）", _passwordCard.Visible);
            expect("内嵌面板从左列右边开始，不遮口令卡片",
                _passwordCard.Parent != null && _panelView.Bounds.Left >= _passwordCard.Parent.Bounds.Right);
            expect("内嵌面板不盖住运行日志", _panelView.Bounds.Bottom <= 618);
            ShowView(true);
            Pump(1600);
            expect("切到内置面板后口令卡片仍然看得见", _passwordCard.Visible && _passwordCard.Parent.Visible);
            expect("切到内置面板后左列按钮还在", FindVisible("obStart") != null && FindVisible("obStart").Parent.Visible);
            // 用户反馈「内置面板变得怎么小」：整个 AstrBot 控制台挤在 554 px 里等于压成一条，
            // 所以打开面板要把窗口撑开，且撑开不许把口令卡片或日志挤掉。
            expect("打开内置面板后窗口撑到 " + ClientSize.Width + " px 宽",
                ClientSize.Width >= PanelWideWidth && _panelView.Width >= 830);
            expect("撑开后面板仍不盖住运行日志", _panelView.Bounds.Bottom <= 618 && _panelView.Height >= 480);
            ShowView(false);
            Pump(200);
            expect("切回概览后窗口宽度还原", ClientSize.Width < PanelWideWidth);
            expect("口令卡片没口令时给的是解释而不是空白",
                _passwordValue.Text.Length > 0 && _passwordHint.Text.Length > 0);
            expect("重置口令按钮在卡片上", FindVisible("passwordReset") != null);

            // —— 界面响应性（用户反馈「容易未响应」的回归） ——
            _worstLag = 0;
            _lastLag = 0;
            int perLine = MeasureUiLatency(true, 1200);
            // 上面那一下是故意用旧写法灌的，看门狗必须能看见它——先等它跑一轮再取样，
            // 否则下面比的是"我造的对照组"，不是"新写法有没有把界面拖住"
            for (int i = 0; i < 24 && _lastLag == 0; i++)
            {
                Application.DoEvents();
                Thread.Sleep(120);
            }
            expect("旧写法的卡顿看门狗能看见（记到 " + _lastLag + " ms）", _lastLag >= perLine - 600);
            // 从此只看新写法与各段操作：每段前归零，历史最大值不参与判断
            _worstLag = 0;
            _lastLag = 0;
            int batched = MeasureUiLatency(false, 1200);
            FlushLog();
            expect("同样洪泛 1200 行：批量写确实比逐行投递快（逐行 " + perLine + " ms / 批量 " + batched + " ms）",
                batched < perLine && batched < 500);
            expect("日志按批写入且行数被裁住（当前 " + _logLines + " 行）", _logLines <= 2000);

            // 黑洞端口：连得上但永远不回话，就是客户端面板半死不活的样子。
            // 以前这种探活和问题问答都在 UI 线程上做，最坏一次堵好几秒。
            System.Net.Sockets.TcpListener hole = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            hole.Start();
            int holePort = ((System.Net.IPEndPoint)hole.LocalEndpoint).Port;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    while (true)
                    {
                        hole.AcceptTcpClient();
                    }
                }
                catch (Exception)
                {
                }
            });
            OneBotClientInfo fake = new OneBotClientInfo();
            fake.Flavor = "黑洞";
            _onebot.Client = fake;
            _onebot.WebHost = "127.0.0.1";
            _onebot.WebPort = holePort;
            _onebot.WebToken = "probe";
            _uiBeat = 0;
            int tick = Environment.TickCount;
            TickOneBot();
            TickOneBot();
            TickOneBot();
            BeginInvoke(new Action(delegate { _uiBeat = Environment.TickCount; }));
            for (int i = 0; i < 40 && _uiBeat == 0; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            int obLatency = _uiBeat == 0 ? 99999 : _uiBeat - tick;
            expect("面板半死不活时连撞三次轮询也不卡界面（" + obLatency + " ms，且在飞的活儿只做一次）",
                obLatency < 500 && _obBusy);
            hole.Stop();
            // 正常操作（洪泛 + 切页签 + 黑洞轮询）都不该触发看门狗
            expect("批量写与这些操作没让消息循环堵过（最坏卡顿 " + _worstLag + " ms）", _worstLag == 0);
            // 再看门狗本身要真的能看见堵：故意在 UI 线程上睡 900 毫秒
            _lastLag = 0;
            Thread.Sleep(950);
            for (int i = 0; i < 12; i++)
            {
                Application.DoEvents();
                Thread.Sleep(60);
            }
            expect("故意堵 950 毫秒时看门狗报得出来（" + _lastLag + " ms）", _lastLag >= 700);
            expect("卡顿记录会写进诊断包（含当时在忙什么）", StateLine().Contains("界面最坏卡顿"));
            expect("标题栏带构建戳（" + Build.Stamp + "）", Build.Stamp != "DEV" && Text.Contains(Build.Stamp));
            expect("诊断包带构建戳", StateLine().Contains("构建戳"));
            expect("这一路跑下来没人偷在 UI 线程上做 I/O（" +
                   (UiGuard.Violations.Length == 0 ? "无" : UiGuard.Violations) + "）",
                   UiGuard.Violations.Length == 0);
            _onebot.WebToken = "";
            _onebot.Client = null;
            _obBusy = false;
            _obWasAlive = false;
            DiscoverOneBot();
            WaitOneBotSettled();

LastTestReport += "fails=" + _testFails.Count + "\n";
        }

        // 端到端演练：用一个假客户端（test/mock_napcat_client.py）把这一段新代码真正跑一遍。
        // 只验我们自己的逻辑：发现→读面板配置→起进程→端口判活→问二维码→让核心画成 PNG→
        // 登录状态变 online→状态行说实话→停止后端口不再响应。
        // 真 NapCat 需要本机 QQ 与人工扫码，桩替不了那部分，也绝不该假装替了。
        public void RunOneBotTest()
        {
            _testFails.Clear();
            LastTestReport = "";
            _settings.OpenPanelOnStart = false;
            _panelOpenedOnce = true;

            string python = (Environment.GetEnvironmentVariable("ASTRBOT_TEST_PYTHON") ?? "").Trim().Trim('"');
            string mock = (Environment.GetEnvironmentVariable("ASTRBOT_TEST_MOCK") ?? "").Trim().Trim('"');
            int port = 6310;
            string rawPort = Environment.GetEnvironmentVariable("ASTRBOT_TEST_WEBUI_PORT");
            int parsedPort;
            if (rawPort != null && rawPort.Length > 0 && int.TryParse(rawPort, out parsedPort) && parsedPort > 0)
            {
                port = parsedPort;
            }

            Expect("测试用的 Python 与桩脚本就位（这两个由验收脚本用环境变量传进来）",
                python.Length > 0 && File.Exists(python) && mock.Length > 0 && File.Exists(mock));
            // 下载器是这整个 exe 里唯一直接用 HTTPS 的地方。csc 编出来的程序集没有
            // TargetFrameworkAttribute，.NET 默认只给 SSL3/TLS1.0，端点会拒握手 —— 这条守住别被改回去。
            Expect("入口打开了 TLS1.2（当前 " + ServicePointManager.SecurityProtocol + "）",
                ((int)ServicePointManager.SecurityProtocol & 0xC00) != 0);
            // 真问一头线上释放页：证明 TLS 与前缀这条路从这份 exe 里走得通；
            // 拿不到是这台机器的网络状态，不是代码的错，显式记 NOTRUN，不假装通过也不算红。
            long[] remoteSize = new long[1];
            RunOffUiAndWait(delegate { remoteSize[0] = OneBotInstaller.ProbeAsset(OneBotInstaller.Find("shell")); });
            if (remoteSize[0] > 0)
            {
                Expect("线上释放页 HEAD 拿得到（" +
                       (remoteSize[0] > 1048576 ? (remoteSize[0] / 1048576.0).ToString("0.0") + " MB" : "200，代理没给长度") + "）",
                       true);
            }
            else
            {
                LastTestReport += "NOTRUN  线上释放页 HEAD（本机到 GitHub 的路不通，跳过）" + Environment.NewLine;
            }
            if (_testFails.Count > 0)
            {
                LastTestReport += "fails=" + _testFails.Count + "\n";
                return;
            }

            string root = Path.Combine(Path.GetTempPath(), "astrbot-obtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string oldDir = _settings.OneBotDir;
            string oldUin = _settings.OneBotUin;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "config"));
                File.WriteAllText(Path.Combine(root, "NapCatWinBootMain.exe"), "");
                File.WriteAllText(Path.Combine(root, "napcat.mjs"), "");
                // 真机 v4.18.41 的 webui.json 里已经没有明文令牌了，只有口令的哈希与盐；
                // 桩必须照这个形状来，否则测的是我脑子里的那个客户端。
                File.WriteAllText(Path.Combine(root, "config", "webui.json"),
                    "{\"host\":\"127.0.0.1\",\"port\":" + port
                    + ",\"passwordHash\":\"64eb0e3cf039fb5369f0f1aa20121db9\",\"passwordSalt\":\"8f867b79a71fb2f0\","
                    + "\"mustChangePassword\":false}", new UTF8Encoding(false));
                string bat = Path.Combine(root, "launcher-user.bat");
                // 不带 BOM 写：带 BOM 时 cmd 认不出第一行的 @echo off，整条命令行会被回显进 stdout
                // （实测就因此把面板口令写进了日志）。口令也不走命令行——桩用同一个默认值。
                File.WriteAllText(bat,
                    "@echo off\r\nchcp 65001 >nul\r\n\"" + python + "\" \"" + mock + "\" --webui-port " + port
                    + " --online-after 5\r\n", new UTF8Encoding(false));

                List<OneBotClientInfo> found = new List<OneBotClientInfo>();
                string obScratchRoot = Path.Combine(Path.GetTempPath(), "astrbot-obempty");
                Directory.CreateDirectory(obScratchRoot);
                RunOffUiAndWait(delegate { found = OneBotController.Discover(obScratchRoot, root); });
                Expect("演练目录里的客户端能被认出来", found.Count == 1);
                if (found.Count != 1)
                {
                    LastTestReport += "fails=" + _testFails.Count + "\n";
                    return;
                }
                _onebot.Client = found[0];
                _settings.OneBotDir = found[0].Root;

                _onebot.WebHost = "0.0.0.0";
                _onebot.WebPort = 1;
                _onebot.WebToken = "";
                _onebot.WebUiPassword = "";
                _onebot.PanelAuthorized = false;
                _onebot.LoadWebUiConfig();
                Expect("从客户端自己的 webui.json 读到端口", _onebot.WebPort == port);
                Expect("配置文件里没有明文令牌时也走得通（真机就是这样）", _onebot.WebToken.Length == 0);

                RefreshOneBotStatus();
                Expect("起来之前状态行不说「在线」", !_obStatus.Text.Contains("在线"));

                try
                {
                    _onebot.Start("");
                    Expect("调用客户端启动脚本没抛异常", true);
                }
                catch (Exception exc)
                {
                    Expect("调用客户端启动脚本没抛异常（" + exc.GetType().Name + "）", false);
                }

                bool alive = false;
                for (int i = 0; i < 45 && !alive; i++)
                {
                    Pump(250);
                    alive = _onebot.PanelAlive();
                }
                Expect("客户端面板端口起来了（判活用端口，不看脚本返回值）", alive);

                // —— 面板鉴权这条链（真机 v4.18.41 起：路由在 /api 下，且必须用口令换凭证）——
                // 顺序要紧：桩服务这会儿才起来，在它起来之前问不到面板，测的就不是鉴权了。
                string qrNoAuth = _onebot.FetchQrcodeUrl();
                Expect("没填口令时不假装拿到二维码", qrNoAuth.Length == 0 && !_onebot.PanelAuthorized);
                _onebot.WebUiPassword = "definitely-wrong";
                Expect("错口令被认成口令不对（不是面板坏了）",
                        !_onebot.PanelLogin() && _onebot.PanelNote.Contains("口令不对"));
                _onebot.WebUiPassword = MockPanelPassword;
                Expect("对口令能换到临时凭证", _onebot.PanelLogin() && _onebot.PanelAuthorized);
                FlushLog();
                string leaked = "";
                foreach (string line in _logBox.Text.Split('\n'))
                {
                    if (line.IndexOf(MockPanelPassword, StringComparison.Ordinal) >= 0)
                    {
                        // 只报"哪一行漏了"，把口令本身打码：报告也是会被贴出去看的东西
                        leaked += "口令（行：" + line.Replace("\r", "").Trim()
                            .Replace(MockPanelPassword, "***") + "）、";
                    }
                    else if (line.IndexOf("Credential", StringComparison.Ordinal) >= 0)
                    {
                        leaked += "凭证（行：" + line.Replace("\r", "").Trim() + "）、";
                    }
                }
                Expect("口令与凭证都不会被写进日志（漏了：" + (leaked.Length == 0 ? "无" : leaked) + "）",
                    leaked.Length == 0);

                string qr = _onebot.FetchQrcodeUrl();
                Expect("从客户端面板拿到二维码链接", qr.Contains("wpa1.qq.com"));

                if (qr.Length > 0)
                {
                    string png = Path.Combine(_baseDir, "data", "tmp", "obtest-login.png");
                    if (File.Exists(png))
                    {
                        File.Delete(png);
                    }
                    string output;
                    int code = RunCoreOffUiAndWait("--qr \"" + qr + "\" --qr-out \"" + png + "\"", 20000, out output);
                    bool realPng = false;
                    if (File.Exists(png))
                    {
                        byte[] head = new byte[4];
                        using (FileStream fs = File.OpenRead(png))
                        {
                            fs.Read(head, 0, 4);
                        }
                        realPng = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
                    }
                    Expect("核心能把客户端给的链接画成 PNG（" + code + "）", code == 0 && realPng);
                }

                string phase = "";
                for (int i = 0; i < 40; i++)
                {
                    Pump(250);
                    phase = _onebot.PollLoginPhase();
                    if (phase == "online")
                    {
                        break;
                    }
                }
                Expect("登录状态能问出 online", phase == "online");
                RefreshOneBotStatus();
                Expect("状态行跟着变成已登录", _obStatus.Text.Contains("已登录"));

                ShowOneBotQr();
                Pump(600);
                Expect("二维码窗口能开出来", _obDialog != null && !_obDialog.IsDisposed);

                _onebot.Stop();
                bool down = false;
                for (int i = 0; i < 40 && !down; i++)
                {
                    Pump(250);
                    down = !_onebot.PanelAlive();
                }
                Expect("停止后面板端口不再响应（进程树按 PID 杀干净）", down);
                CloseOneBotQr();
                Expect("演练结束二维码窗口不残留", _obDialog == null || _obDialog.IsDisposed);

                InstallDrill();
            }
            finally
            {
                _settings.OneBotDir = oldDir;
                _settings.OneBotUin = oldUin;
                _onebot.Client = null;
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, true);
                    }
                }
                catch (Exception)
                {
                }
                DiscoverOneBot();
                WaitOneBotSettled();
            }
            LastTestReport += "fails=" + _testFails.Count + "\n";
        }

        // 纠错机制验收：判据单测 + 真注入三种故障（端口被占、核心被外部杀掉、配置文件写坏）。
        // 跑在打包目录自带的数据目录上，测完把配置文件还原，只清理本次产生的测试残留。
        // 一键修复的验收：先把故障注进来（证明"红"），跑修复，再证明"绿"。
        // 只测我们自己那一段：核心真起、面板真应答、端口真换、坏配置真留档；
        // 修不了的那类（程序文件缺失）要求它**如实说修不了**，而不是报成已修复。
        public void RunRepairTest()
        {
            _testFails.Clear();
            LastTestReport = "";
            _settings.OpenPanelOnStart = false;
            _panelOpenedOnce = true;

            string dataDir = Path.Combine(_baseDir, "data");
            string cfg = Path.Combine(dataDir, "cmd_config.json");
            string cfgSaved = Path.Combine(Path.GetTempPath(), "astrbot-repairtest-cmd_config.json");
            string readme = Path.Combine(_baseDir, "README.txt");
            string readmeHidden = readme + ".repairtest-hidden";
            int originalPort = _settings.Port;
            TcpListener blocker = null;
            string smallAside = Path.Combine(_baseDir, "factory", "small.aside");

            Directory.CreateDirectory(dataDir);
            if (File.Exists(cfg))
            {
                File.Copy(cfg, cfgSaved, true);
            }
            try
            {
                _core.Stop();
                for (int i = 0; i < 40 && _core.IsRunning; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(250);
                }

                File.WriteAllText(cfg, "{ 这串不是合法 JSON", new UTF8Encoding(false));
                bool[] bad = new bool[1];
                RunOffUiAndWait(delegate { bad[0] = Preflight.ConfigJsonInvalid(_baseDir); });
                Expect("注入的坏配置确实被判为坏（不注入就证明不了这条有用）", bad[0]);

                string tmpDir = Path.Combine(dataDir, "tmp");
                if (Directory.Exists(tmpDir))
                {
                    Directory.Delete(tmpDir, true);
                }
                Expect("注入的缺失目录确实没有", !Directory.Exists(tmpDir));

                if (File.Exists(readme))
                {
                    if (File.Exists(readmeHidden)) { File.Delete(readmeHidden); }
                    File.Move(readme, readmeHidden);
                }

                // 端口故障注在"确定空闲"的备用端口上：现场可能还留着上一轮没人管的孤儿核心
                // 占着 6185/6186，跟它抢不如换个地方注。顺带让"清孤儿"这一项被真数据考一次。
                bool[] injected = new bool[2];
                int sparePort = 0;
                RunOffUiAndWait(delegate
                {
                    sparePort = Preflight.PickPort(21900);
                    if (Preflight.PortFree(sparePort))
                    {
                        try
                        {
                            blocker = new TcpListener(IPAddress.Loopback, sparePort);
                            blocker.Start();
                            injected[0] = true;
                            _settings.Port = sparePort;
                            SaveSettings();
                        }
                        catch (Exception)
                        {
                            blocker = null;
                        }
                    }
                });
                Expect("端口占用确实注上了（备用端口 " + sparePort + "）", injected[0]);
                int orphansBefore = OrphanCoreCount();

                // 冗余副本这条：改坏一个"带出厂副本"的文件，修复器必须发现并还原回原摘要。
                // 这是"宁愿包大一点也不能出问题不能用"的那一半承诺，不验就是空话。
                string png = Path.Combine(_baseDir, "astrbot.png");
                string pngHash = "";
                bool pngBroken = false;
                RunOffUiAndWait(delegate
                {
                    if (!File.Exists(png)) { return; }
                    pngHash = RepairRunner.Sha256Of(png);
                    byte[] bytes = File.ReadAllBytes(png);
                    if (bytes.Length > 40)
                    {
                        bytes[20] ^= 0xFF;
                        File.WriteAllBytes(png, bytes);
                        pngBroken = RepairRunner.Sha256Of(png) != pngHash;
                    }
                });
                Expect("出厂副本这条测试先把文件改坏了（改不坏就证明不了还原）", pngBroken);

                // 第二层冗余：把出厂副本那一层藏起来，再删掉一个文件——修复器得从"免安装包"里补回来。
                // 没给包路径就显式记 NOTRUN，不算通过（这条测的是断网/被杀毒吃掉之后的最后一条路）。
                string restoreZip = (Environment.GetEnvironmentVariable("ASTRBOT_TEST_RESTORE_ZIP") ?? "").Trim().Trim('"');
                string ico = Path.Combine(_baseDir, "astrbot.ico");
                bool zipLayerArmed = false;
                string icoHash = "";
                if (restoreZip.Length > 0 && File.Exists(restoreZip) && File.Exists(ico))
                {
                    string small = Path.Combine(_baseDir, "factory", "small");
                    _settings.RestoreZip = restoreZip;
                    RunOffUiAndWait(delegate
                    {
                        try
                        {
                            icoHash = RepairRunner.Sha256Of(ico);
                            if (Directory.Exists(small) && !Directory.Exists(smallAside))
                            {
                                Directory.Move(small, smallAside);   // 藏掉第一层，逼它走第二层
                            }
                            File.Delete(ico);
                            zipLayerArmed = !File.Exists(ico);
                        }
                        catch (Exception)
                        {
                            zipLayerArmed = false;
                        }
                    });
                    Expect("藏掉出厂副本、删掉图标之后确实缺了（不缺就证明不了第二层）", zipLayerArmed);
                }
                else
                {
                    LastTestReport += "NOTRUN  免安装包这一层（没给 ASTRBOT_TEST_RESTORE_ZIP）" + Environment.NewLine;
                }

                // 只考核这一段：前面注入故障时的读写不该算到修复器头上
                UiGuard.Reset();
                RepairRunner runner = new RepairRunner(_baseDir, _settings, BuildRepairHooks());
                RunOffUiAndWait(delegate { runner.Run(); });

                List<RepairStep> steps = runner.Steps;
                RepairStep files = FindStep(steps, "程序文件");
                RepairStep dirs = FindStep(steps, "数据目录");
                RepairStep config = FindStep(steps, "配置文件");
                RepairStep port = FindStep(steps, "面板端口");
                RepairStep coreStep = FindStep(steps, "核心服务");
                RepairStep proof = FindStep(steps, "修完复验");

                string silent = StepsMissingFindings(steps);
                Expect("每一项都给了检测结论（没结论的：" + (silent.Length == 0 ? "无" : silent) + "）",
                    steps.Count >= 9 && silent.Length == 0);
                bool[] orphanCleared = new bool[1];
                RunOffUiAndWait(delegate { orphanCleared[0] = OrphanCoreCount() == 0; });
                Expect("没人管的孤儿核心被清掉了（跑之前 " + orphansBefore + " 个）", orphanCleared[0]);
                bool[] pngRestored = new bool[1];
                string pngCopy = Path.Combine(_baseDir, "astrbot.png");
                string wantHash = pngHash;
                RunOffUiAndWait(delegate
                {
                    pngRestored[0] = wantHash.Length > 0
                        && RepairRunner.Sha256Of(pngCopy) == wantHash;
                });
                RepairStep integrity = FindStep(steps, "包体完整性");
                Expect("被改坏的文件按摘要被发现、并从出厂副本还原回去了（"
                       + (integrity == null ? "没有这一项" : integrity.Found) + "）",
                       pngRestored[0] && integrity != null && integrity.Did.Contains("astrbot.png"));
                if (zipLayerArmed)
                {
                    bool[] icoBack = new bool[1];
                    string wantIco = icoHash;
                    string icoCopy = ico;
                    RunOffUiAndWait(delegate
                    {
                        icoBack[0] = File.Exists(icoCopy)
                            && wantIco.Length > 0 && RepairRunner.Sha256Of(icoCopy) == wantIco;
                    });
                    Expect("没有出厂副本的那个文件从免安装包补回来了（摘要一致）", icoBack[0]);
                }
                Expect("缺的程序文件被如实报成「需要你处理」，没假装修好",
                    files != null && files.NeedsUser && files.Advice.Contains("重装"));
                // 这几项判据都要读盘，一律挪到后台拿结论，断言本身留在 UI 线程
                bool[] ok = new bool[3];
                string tmpDirCopy = tmpDir;
                string dataDirCopy = dataDir;
                RunOffUiAndWait(delegate
                {
                    ok[0] = Directory.Exists(tmpDirCopy) && dirs != null && dirs.Fixed;
                    ok[1] = !Preflight.ConfigJsonInvalid(_baseDir) && HasBadBackup(dataDirCopy)
                        && config != null && config.Fixed;
                    ok[2] = !Preflight.ConfigJsonInvalid(_baseDir);
                });
                Expect("缺失目录补回来了", ok[0]);
                Expect("坏配置留了档（原文件挪走而不是删掉）", ok[1]);
                // 换端口之后，新端口上是"我们自己那个核心"在听（修复器随后就把它起了），
                // 所以判据是"不再是被占的那个旧端口，且新端口要么空、要么就是我们自己在应答"。
                bool[] portOk = new bool[1];
                int movedPort = _settings.Port;
                RunOffUiAndWait(delegate
                {
                    portOk[0] = movedPort != sparePort && port != null && port.Fixed
                        && (Preflight.PortFree(movedPort) || PanelHttpAnswers(movedPort));
                });
                Expect("端口从被占的那个换走了（" + sparePort + " → " + movedPort + "）", portOk[0]);
                Expect("核心被重新起起来、面板真的应答", coreStep != null && coreStep.Fixed);
                Expect("最后一步「修完复验」是绿的", proof != null && proof.Fixed);
                Expect("汇总文案给人看得懂：" + runner.Summary,
                    runner.Summary.Contains("修好") && runner.Summary.Contains("还需你处理"));
                Expect("修复器全程没在 UI 线程上做 I/O（" +
                       (UiGuard.Violations.Length == 0 ? "无" : UiGuard.Violations) + "）",
                       UiGuard.Violations.Length == 0);
            }
            catch (Exception exc)
            {
                Expect("修复验收没抛意外异常（" + exc.GetType().Name + "：" + exc.Message + "）", false);
            }
            finally
            {
                // 测试借走的东西一律还回去：藏起来的第一层、指过去的免安装包
                try
                {
                    string smallBack = Path.Combine(_baseDir, "factory", "small");
                    if (Directory.Exists(smallAside) && !Directory.Exists(smallBack))
                    {
                        Directory.Move(smallAside, smallBack);
                    }
                }
                catch (Exception)
                {
                }
                _settings.RestoreZip = "";
                if (blocker != null)
                {
                    try { blocker.Stop(); } catch (Exception) { }
                }
                if (File.Exists(readmeHidden))
                {
                    try
                    {
                        // 修复器可能已经按清单把 README 还原回来了；Move 不覆盖已存在的目标，
                        // 那样会留一个 .repairtest-hidden 在包里——测试残渣绝不能进交付物。
                        if (File.Exists(readme))
                        {
                            File.Delete(readme);
                        }
                        File.Move(readmeHidden, readme);
                    }
                    catch (Exception)
                    {
                    }
                }
                if (File.Exists(cfgSaved))
                {
                    try { File.Copy(cfgSaved, cfg, true); } catch (Exception) { }
                }
                else
                {
                    try { if (File.Exists(cfg)) { File.Delete(cfg); } } catch (Exception) { }
                }
                _settings.Port = originalPort;
                SaveSettings();
                _core.Stop();
                for (int i = 0; i < 20 && _core.IsRunning; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(250);
                }
                LastTestReport += "fails=" + _testFails.Count + "\n";
            }
        }

        private static RepairStep FindStep(List<RepairStep> steps, string name)
        {
            foreach (RepairStep step in steps)
            {
                if (step.Name == name)
                {
                    return step;
                }
            }
            return null;
        }

        private static string StepsMissingFindings(List<RepairStep> steps)
        {
            List<string> silent = new List<string>();
            foreach (RepairStep step in steps)
            {
                if (step.Found.Trim().Length == 0)
                {
                    silent.Add(step.Name);
                }
            }
            return string.Join("、", silent.ToArray());
        }

        // 现场还有几个"不是我们起的"核心进程（路径是我们这份、但不是当前管着的那个）
        private int OrphanCoreCount()
        {
            int own = _core.Pid;
            int count = 0;
            try
            {
                foreach (Process proc in Process.GetProcessesByName("AstrBotCore"))
                {
                    using (proc)
                    {
                        if (proc.Id == own)
                        {
                            continue;
                        }
                        try
                        {
                            string path = proc.MainModule != null ? Path.GetFullPath(proc.MainModule.FileName) : "";
                            string ours = Path.GetFullPath(Path.Combine(_baseDir, "core", "AstrBotCore.exe"));
                            if (string.Equals(path, ours, StringComparison.OrdinalIgnoreCase))
                            {
                                count++;
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return count;
        }

        private static bool HasBadBackup(string dataDir)
        {
            try
            {
                foreach (string file in Directory.GetFiles(dataDir, "cmd_config.bad-*.json"))
                {
                    if (new FileInfo(file).Length > 0)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        public void RunRecoveryTest()
        {
            _testFails.Clear();
            LastTestReport = "";
            _settings.OpenPanelOnStart = false;
            _panelOpenedOnce = true;
            int originalPort = _settings.Port;

            string advice;
            FaultKind busy = Diagnose.Classify("[Core] [ERROR] OSError: [WinError 10048] 通常每个套接字地址只允许使用一次", out advice);
            Expect("端口占用能被认出（" + Diagnose.Title(busy) + "）", busy == FaultKind.PortBusy && advice.Length > 0);
            FaultKind bad = Diagnose.Classify("json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)", out advice);
            Expect("配置读坏能被认出", bad == FaultKind.BadConfig && Diagnose.IsAutoFixable(bad));
            FaultKind miss = Diagnose.Classify("ModuleNotFoundError: No module named 'pillow'", out advice);
            Expect("缺依赖能被认出且不做无谓重启", miss == FaultKind.MissingDependency && !Diagnose.IsAutoFixable(miss));
            FaultKind conflict = Diagnose.Classify("RuntimeError: A conflict between plugin dependencies and the current runtime", out advice);
            Expect("依赖冲突能被认出", conflict == FaultKind.DependencyConflict && !Diagnose.IsAutoFixable(conflict));
            FaultKind plain = Diagnose.Classify("[Core] [INFO] AstrBot 正常退出", out advice);
            Expect("认不出来时归为未识别但仍会重试", plain == FaultKind.Crashed && Diagnose.IsAutoFixable(plain));

            string red = Redact.Text("Initial password: SUP3RSECRETPW\nsecret=abcdef123456\ntoken: zzz789012");
            Expect("诊断文本里的密码/密钥/token 全打码",
                !red.Contains("SUP3RSECRETPW") && !red.Contains("abcdef123456") && !red.Contains("zzz789012") && red.Contains("***"));

            RecoveryPolicy policy = new RecoveryPolicy();
            int d1 = policy.NextDelayMs();
            int d2 = policy.NextDelayMs();
            int d3 = policy.NextDelayMs();
            Expect("退避重启是 3s/10s/30s", d1 == 3000 && d2 == 10000 && d3 == 30000);
            Expect("自动恢复最多 3 次", !policy.ShouldRetry());
            policy.Reset();
            Expect("计数能清零（核心起来后不该继续欠着）", policy.ShouldRetry() && policy.Attempts == 0);

            string scratch = Path.Combine(Path.GetTempPath(), "astrbot-recov-" + DateTime.Now.ToString("HHmmss"));
            Directory.CreateDirectory(Path.Combine(scratch, "data"));
            string scratchCfg = Path.Combine(scratch, "data", "cmd_config.json");
            File.WriteAllText(scratchCfg, "{\"platforms\": [1, 2]}", Encoding.UTF8);
            Expect("合法配置不会被误判成坏的", !Preflight.ConfigJsonInvalid(scratch));
            File.WriteAllText(scratchCfg, "{\"platforms\": [", Encoding.UTF8);
            Expect("非法配置能在动手前判出来", Preflight.ConfigJsonInvalid(scratch));
            string quarantined = Preflight.QuarantineConfig(scratch);
            Expect("坏配置能挪开且原件保留成备份",
                quarantined.Length > 0 && File.Exists(quarantined) && !File.Exists(scratchCfg)
                && Path.GetFileName(quarantined).StartsWith("cmd_config.bad-"));

            Control exportBtn = FindNamed(Controls, "exportDiag");
            Expect("界面上有导出诊断包按钮", exportBtn != null && exportBtn.Text.Contains("诊断"));

            // 故障 1：面板端口被别的程序占住。体检应在启动前就顺延，而不是等核心崩了再补救。
            int blockerPort = 6191;
            TcpListener blocker = new TcpListener(IPAddress.Any, blockerPort);
            bool occupied = false;
            try
            {
                blocker.Start();
                occupied = true;
            }
            catch (Exception exc)
            {
                LastTestReport += "NOTE  测试端口 " + blockerPort + " 占不上：" + exc.Message + "\n";
            }
            if (occupied)
            {
                Expect("端口被占时体检能识别", !Preflight.PortFree(blockerPort));
                _core.Stop();
                WaitState(CoreState.Stopped, 10000);
                _core.Port = blockerPort;
                _settings.Port = blockerPort;
                _recovery.Reset();
                StartCore();
                bool up1 = WaitState(CoreState.Running, 120000);
                Pump(1200);
                Expect("端口被占会自动顺延并把核心起来（改用 " + _core.Port + "）", up1 && _core.Port != blockerPort);
                Expect("顺延后的端口会记住", LauncherSettings.Load(_settings.FilePath).Port == _core.Port);
                blocker.Stop();
                // 自己核心在听的端口必须判成「被占」——这证明体检看到的是真实状态；
                // 而「不会乱改」要用一个确定没人听的端口测，拿正在监听的端口测必红。
                Expect("核心在听的端口被判为被占", !Preflight.PortFree(_core.Port));
                int spare = Preflight.PickPort(21999);
                Expect("确定空闲的端口不会被顺延改动", spare == 21999 && Preflight.PortFree(21999));

                // 故障 2：核心被外部杀掉（不是用户点停止），要自动恢复而不是红灯到底。
                int victim = _core.Pid;
                _recovery.Reset();
                bool killed = KillFromOutside(victim);
                Expect("测试能把核心真杀掉（pid=" + victim + "）", killed);
                bool recovering = WaitPill("自动恢复中", 30000);
                Expect("被外部杀掉后状态灯显示自动恢复（" + _statusText.Text + "）", recovering);
                Expect("自动恢复把核心重新拉起来", WaitState(CoreState.Running, 120000));
                Pump(2000);
                Expect("起来后备退预算清零（attempts=" + _recovery.Attempts + "）", _recovery.Attempts == 0);
                Expect("自动恢复期间没有弹框打扰（" + LastRecoveryNote + "）", FaultDialogShown == 0);
                Expect("被外部杀掉不会误动配置文件",
                    Directory.GetFiles(Path.Combine(_baseDir, "data"), "cmd_config.bad-*.json").Length == 0);
            }

            // 故障 3：配置文件被存成非法 JSON，核心会直接崩在读配置那一步。
            string cfg = Path.Combine(_baseDir, "data", "cmd_config.json");
            string keep = Path.Combine(Path.GetTempPath(), "astrbot-recov-cfgkeep.json");
            DateTime drillStart = DateTime.Now.AddSeconds(-60);
            bool restored = false;
            if (occupied)
            {
                _core.Stop();
                WaitState(CoreState.Stopped, 15000);
                if (File.Exists(cfg))
                {
                    if (File.Exists(keep))
                    {
                        File.Delete(keep);
                    }
                    File.Move(cfg, keep);
                    restored = true;
                }
                File.WriteAllText(cfg, "{\"platforms\": [", Encoding.UTF8);
                _recovery.Reset();
                StartCore();
                bool caught = WaitPill("自动恢复中", 150000);
                Expect("坏配置被认成配置文件损坏并触发恢复（" + LastRecoveryNote + "）",
                    caught && LastRecoveryNote.Contains("配置文件损坏"));
                bool up3 = WaitState(CoreState.Running, 120000);
                Pump(1500);
                Expect("隔离坏配置后能重新起来（" + _statusText.Text + "）", up3);
                string[] backups = Directory.GetFiles(Path.Combine(_baseDir, "data"), "cmd_config.bad-*.json");
                Expect("坏配置留了可找回的备份", backups.Length > 0);
                string fresh = File.ReadAllText(cfg, Encoding.UTF8);
                Expect("核心用默认配置重建了 cmd_config.json", fresh.StartsWith("{") && fresh.Length > 200);
                foreach (string bak in backups)
                {
                    if (File.GetLastWriteTime(bak) >= drillStart)
                    {
                        File.Delete(bak);
                    }
                }
                if (restored)
                {
                    if (File.Exists(cfg))
                    {
                        File.Delete(cfg);
                    }
                    File.Move(keep, cfg);
                }
            }

            // 诊断包：走真实导出，确认落盘且内容里已经找不到明文口令。
            AppendLine("[启动器] UITEST Initial password: SUP3RSECRETPW");
            for (int i = 0; i < 15; i++)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            string diagPath = Diagnostics.Write(_baseDir, "自检输出（验收占位）", LogTail(60), StateLine());
            string diagText = File.Exists(diagPath) ? File.ReadAllText(diagPath, Encoding.UTF8) : "";
            Expect("诊断包能落盘（" + Path.GetFileName(diagPath) + "）",
                File.Exists(diagPath) && new FileInfo(diagPath).Length > 200);
            Expect("诊断包里的密码已打码", !diagText.Contains("SUP3RSECRETPW") && diagText.Contains("password: ***"));
            foreach (string old in Directory.GetFiles(Path.Combine(_baseDir, "data", "diagnostics"), "astrbot-diag-*.txt"))
            {
                if (File.GetLastWriteTime(old) >= drillStart)
                {
                    File.Delete(old);
                }
            }

            // 把测试期间顺延出去的端口还原，别把后面的验收步骤指到别的端口上。
            CancelRecoveryTimer();
            _recovery.Reset();
            _core.Stop();
            WaitState(CoreState.Stopped, 20000);
            _core.Port = originalPort;
            _settings.Port = originalPort;
            SaveSettings();

            if (_testFails.Count > 0)
            {
                LastTestReport += "\n---- 现场：判定=" + LastRecoveryNote + " · 端口=" + _core.Port
                    + " · 恢复=" + _recovery.Attempts + "/" + RecoveryPolicy.MaxAttempts
                    + " · 弹框=" + FaultDialogShown + " ----\n";
                LastTestReport += Redact.Text(LogTail(40));
            }

            LastTestReport += "fails=" + _testFails.Count + "\n";
        }

        // 外部杀掉：启动器 Stop() 会标记「计划内停止」，证明不了崩溃恢复，必须用 taskkill 从外面下手。
        private static bool KillFromOutside(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill.exe";
                psi.Arguments = "/PID " + pid + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal static class Program
    {
        // 这个 exe 是系统自带 csc 编出来的，没有 TargetFrameworkAttribute，.NET 于是按 4.0 的默认值
        // 只给 SSL3/TLS1.0 —— 现代 HTTPS 端点直接拒掉握手（表现是「下不到客户端压缩包：…无法…」）。
        // 任何一次 HTTPS 都会撞，所以在入口一次性打开 TLS1.2（Win10 起再带上 1.3）。
        public static void EnableModernTls()
        {
            SecurityProtocolType want = (SecurityProtocolType)(0xC0 | 0x300 | 0xC00); // Tls | Tls11 | Tls12
            if (Environment.OSVersion.Version.Major >= 10)
            {
                want = (SecurityProtocolType)((int)want | 0x3000); // Tls13
            }
            ServicePointManager.SecurityProtocol = want;
            ServicePointManager.Expect100Continue = false;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            EnableModernTls();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            bool minimized = false;
            string selfCheckFile = null;
            string uitest = null;
            string recoverytest = null;
            string onebottest = null;
            string repairtest = null;
            string portProbe = null;
            string screenshot = null;

            foreach (string arg in args)
            {
                if (arg == "--minimized")
                {
                    minimized = true;
                }
                else if (arg == "--no-webview")
                {
                    PanelView.ForceUnavailable = true;
                }
                else if (arg.StartsWith("--self-check-to"))
                {
                    selfCheckFile = arg.Substring(arg.IndexOf('=') + 1).Trim('"');
                }
                else if (arg.StartsWith("--uitest"))
                {
                    uitest = arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1).Trim('"') : "";
                }
                else if (arg.StartsWith("--port-probe"))
                {
                    portProbe = arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1).Trim('"') : "";
                }
                else if (arg.StartsWith("--recoverytest"))
                {
                    recoverytest = arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1).Trim('"') : "";
                }
                else if (arg.StartsWith("--onebottest"))
                {
                    onebottest = arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1).Trim('"') : "";
                }
                else if (arg.StartsWith("--repairtest"))
                {
                    repairtest = arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1).Trim('"') : "";
                }
                else if (arg.StartsWith("--screenshot"))
                {
                    screenshot = arg.IndexOf('=') >= 0 ? arg.Substring(arg.IndexOf('=') + 1).Trim('"') : "astrbot-live.png";
                }
                else if (arg == "--self-check")
                {
                    selfCheckFile = "";
                }
            }

            // 端口体检判定不占单实例互斥：它不开核心，只是就地试一下这个端口能不能用。
            if (portProbe != null)
            {
                return PortProbe(portProbe);
            }

            bool held;
            using (Mutex mutex = new Mutex(true, "AstrBotDesktopLauncher", out held))
            {
                if (!held)
                {
                    MessageBox.Show("已经有一个 AstrBot 启动器在运行了（看系统托盘）。", "AstrBot 桌面版",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (selfCheckFile != null)
                {
                    return SelfCheckHeadless(baseDir, selfCheckFile);
                }
                if (uitest != null)
                {
                    return UiTestMain(baseDir, uitest);
                }
                if (recoverytest != null)
                {
                    return RecoveryTestMain(baseDir, recoverytest);
                }
                if (onebottest != null)
                {
                    return OneBotTestMain(baseDir);
                }
                if (repairtest != null)
                {
                    return RepairTestMain(baseDir, repairtest);
                }
                if (screenshot != null)
                {
                    return LiveScreenshot(baseDir, screenshot);
                }

                MainForm form = new MainForm(baseDir);
                if (minimized)
                {
                    form.WindowState = FormWindowState.Minimized;
                    form.ShowInTaskbar = false;
                    form.Hide();
                }
                Application.Run(form);
                GC.KeepAlive(mutex);
                return 0;
            }
        }

        // 体检判定要看得到：PortFree/PickPort 的结论直接写出来，不用为了搞清「为什么没让开
        // 端口」再等一次十几分钟的注入式验收。退出码 0=空闲，1=被占。
        private static int PortProbe(string spec)
        {
            int port;
            string report = Path.Combine(Path.GetTempPath(), "astrbot-port-probe.txt");
            if (!int.TryParse(spec.Trim(), out port) || port < 1 || port > 65535)
            {
                File.WriteAllText(report, "PORT_PROBE_BAD_PORT " + spec + "\n", Encoding.UTF8);
                return 2;
            }
            bool answers = Preflight.SomebodyAnswers(port);
            bool bindable = Preflight.Bindable(port);
            bool free = Preflight.PortFree(port);
            int picked = Preflight.PickPort(port);
            StringBuilder sb = new StringBuilder();
            sb.Append("port=" + port + " answers=" + answers + " bindable=" + bindable + " free=" + free + " picked=" + picked);
            // 顺延不生效时，光看结论没用：把每一跳的两个判据都摊开，才知道是连上了还是绑不住。
            for (int offset = 0; offset < 4; offset++)
            {
                int candidate = port + offset;
                sb.Append(" | " + candidate + " a=" + Preflight.SomebodyAnswers(candidate)
                    + " b=" + Preflight.Bindable(candidate)
                    + " any=" + Preflight.BindableEverywhere(candidate));
            }
            sb.Append("\n");
            File.WriteAllText(report, sb.ToString(), Encoding.UTF8);
            return free ? 0 : 1;
        }

        private static int SelfCheckHeadless(string baseDir, string outFile)
        {
            CoreController core = new CoreController(baseDir);
            string output;
            int code = core.RunOnce("--self-check", out output, 180000);
            // 客户端那段只有启动器看得见（核心不知道 NapCat 装没装），补进同一份自检报告里，
            // 用户报"连不上"时就能分清是 AstrBot 的问题、客户端没装、还是号没登录
            output = output.TrimEnd() + Environment.NewLine + Environment.NewLine + OneBotSelfCheck(baseDir);
            if (outFile.Length > 0)
            {
                File.WriteAllText(outFile, output, Encoding.UTF8);
            }
            else
            {
                MessageBox.Show(TrimTail(output, 1800), code == 0 ? "自检通过" : "自检有问题",
                    MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            return code;
        }

        private static string OneBotSelfCheck(string baseDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[QQ 接入客户端]");
            LauncherSettings settings = LauncherSettings.Load(Path.Combine(baseDir, "data", "desktop.conf"));
            List<OneBotClientInfo> found = OneBotController.Discover(baseDir, settings.OneBotDir);
            if (found.Count == 0)
            {
                sb.AppendLine("  -- 没找到 OneBot 客户端：个人号需要它（NapCat 等）。"
                    + "官方机器人不需要这条，可在界面点「下载 NapCat」或自己装好后用「选目录」指认。");
            }
            foreach (OneBotClientInfo info in found)
            {
                sb.AppendLine("  OK   认出 " + info.Flavor + "，入口 " + Path.GetFileName(info.Entry)
                    + (info.NeedsAdmin ? "（需要管理员）" : "") + " @ " + info.Root);
            }
            string qq = OneBotController.FindQqNtExe();
            if (qq.Length > 0)
            {
                sb.AppendLine("  OK   本机 QQ 客户端在 " + Path.GetDirectoryName(qq));
            }
            else
            {
                sb.AppendLine("  !!  注册表里没找到 QQ 客户端：NapCat 要从注册表拿到 QQ.exe 才能工作，先装 QQ");
            }
            if (found.Count > 0)
            {
                OneBotController probe = new OneBotController(baseDir);
                probe.Client = found[0];
                probe.LoadWebUiConfig();
                sb.AppendLine(probe.PanelAlive()
                    ? "  OK   客户端面板在线（端口 " + probe.WebPort + "），可以直接点界面的二维码按钮登录"
                    : "  --  客户端面板端口 " + probe.WebPort + " 没在监听：还没启动，或它还在起来路上");
            }
            return sb.ToString();
        }

        private static int UiTestMain(string baseDir, string screenshotName)
        {
            string shot = Path.Combine(Path.GetTempPath(), screenshotName.Length > 0 ? screenshotName : "astrbot-uitest.png");
            MainForm form = new MainForm(baseDir);
            form.AutoStart = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(60, 60);
            form.Show();
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(80);
            }
            form.RunUiTest();
            Application.DoEvents();
            bool captured = Capture.ToPng(form, shot);
            string report = form.LastTestReport + "screenshot=" + (captured ? shot : "FAILED") + "\n";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "astrbot-uitest-report.txt"), report, Encoding.UTF8);
            bool failed = report.Contains("FAIL") || !captured;
            form.ForceQuit();
            return failed ? 1 : 0;
        }

        // 和 --uitest 一样：把窗口真开出来跑，报告落到临时文件，任何 FAIL 都反映到退出码。
        private static int OneBotTestMain(string baseDir)
        {
            MainForm form = new MainForm(baseDir);
            form.AutoStart = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(60, 60);
            form.Show();
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(80);
            }
            form.RunOneBotTest();
            Application.DoEvents();
            string report = form.LastTestReport;
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "astrbot-onebot-test-report.txt"), report, Encoding.UTF8);
            bool failed = report.Contains("FAIL");
            form.ForceQuit();
            return failed ? 1 : 0;
        }

        // --repairtest：跟纠错验收同一套路子（真起窗口、真注故障、PrintWindow 截图、报告落盘）
        private static int RepairTestMain(string baseDir, string screenshotName)
        {
            string shot = Path.Combine(Path.GetTempPath(),
                screenshotName.Length > 0 ? screenshotName : "astrbot-repair.png");
            MainForm form = new MainForm(baseDir);
            form.AutoStart = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(60, 60);
            form.Show();
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(80);
            }
            form.RunRepairTest();
            Application.DoEvents();
            bool captured = Capture.ToPng(form, shot);
            string report = form.LastTestReport + "screenshot=" + (captured ? shot : "FAILED") + "\n";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "astrbot-repair-report.txt"), report, Encoding.UTF8);
            bool failed = report.Contains("FAIL") || !captured;
            form.ForceQuit();
            return failed ? 1 : 0;
        }

        private static int RecoveryTestMain(string baseDir, string screenshotName)
        {
            string shot = Path.Combine(Path.GetTempPath(), screenshotName.Length > 0 ? screenshotName : "astrbot-recovery.png");
            MainForm form = new MainForm(baseDir);
            form.AutoStart = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(60, 60);
            form.Show();
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(80);
            }
            form.RunRecoveryTest();
            Application.DoEvents();
            bool captured = Capture.ToPng(form, shot);
            string report = form.LastTestReport + "screenshot=" + (captured ? shot : "FAILED") + "\n";
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "astrbot-recovery-report.txt"), report, Encoding.UTF8);
            bool failed = report.Contains("FAIL") || !captured;
            form.ForceQuit();
            return failed ? 1 : 0;
        }

        // 真实运行一屏：正常拉起核心，等面板就绪后截图，再干净退出。
        private static int LiveScreenshot(string baseDir, string name)
        {
            string path = Path.Combine(Path.GetTempPath(), name);
            MainForm form = new MainForm(baseDir);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(60, 60);
            form.Show();
            DateTime deadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < deadline && !form.PanelReady)
            {
                Application.DoEvents();
                Thread.Sleep(250);
            }
            for (int i = 0; i < 12; i++)
            {
                Application.DoEvents();
                Thread.Sleep(250);
            }
            bool ready = form.PanelReady;
            bool ok = Capture.ToPng(form, path);
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "astrbot-shot-report.txt"),
                (ok ? "shot=" + path : "screenshot FAILED") + "\npanelReady=" + ready,
                Encoding.UTF8);
            form.ForceQuit();
            return ok && ready ? 0 : 1;
        }

        private static string TrimTail(string text, int max)
        {
            if (text.Length <= max)
            {
                return text;
            }
            return text.Substring(text.Length - max);
        }
    }
}
