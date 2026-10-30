// 内嵌面板视图：用 WebView2 把 AstrBot 的 WebUI 直接装进启动器窗口。
// 没装 WebView2 运行时的机器要能退回浏览器，不能白屏。

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AstrBotDesktop
{
    internal sealed class PanelView : RoundPanel
    {
        private WebView2 _view;
        private Label _notice;
        private Button _openBrowser;
        private Button _retry;
        private readonly string _userDataFolder;
        private bool _started;
        private string _url = "";

        public bool Navigated;
        public string Status = "未加载";
        // 验收用：--no-webview 强制走退回分支，确认没运行时的机器不会白屏。
        public static bool ForceUnavailable;

        public static string AvailableVersion()
        {
            return ForceUnavailable ? "" : InstalledVersion();
        }

        public PanelView(string userDataFolder)
        {
            _userDataFolder = userDataFolder;
            Radius = 12;
            BackColor = Theme.Surface;

            _notice = new Label();
            _notice.ForeColor = Theme.Muted;
            _notice.Font = Theme.Body;
            _notice.AutoSize = false;
            _notice.BackColor = Color.Transparent;
            _notice.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(_notice);

            _openBrowser = Theme.Ghost("改用浏览器打开");
            _openBrowser.Visible = false;
            Controls.Add(_openBrowser);

            // 核心比面板慢几秒起来是常态，失败卡片必须能原地重试。
            _retry = Theme.Primary("重试");
            _retry.Visible = false;
            _retry.Click += delegate
            {
                if (_started)
                {
                    Navigate();
                }
            };
            Controls.Add(_retry);
        }

        // SDK 自己的探测，比查注册表可靠（GUID 写错就查不到了，我踩过）。
        public static string InstalledVersion()
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception)
            {
                return "";
            }
        }

        public void BindOpenBrowser(Action handler)
        {
            _openBrowser.Click += delegate { handler(); };
        }

        public void Start(string url)
        {
            _url = url;
            if (_started)
            {
                Navigate();
                return;
            }

            string version = AvailableVersion();
            if (version.Length == 0)
            {
                ShowFallback("这台机器没有 WebView2 运行时，面板改用浏览器打开。\n"
                    + "装一个 Evergreen 运行时后重启本程序即可内嵌显示。");
                return;
            }

            _started = true;
            Status = "正在初始化内核...";
            try
            {
                _view = new WebView2();
                _view.Dock = DockStyle.Fill;
                _view.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = _userDataFolder
                };
                Controls.Add(_view);
                _view.BringToFront();
                Task start = _view.EnsureCoreWebView2Async();
                start.ContinueWith(delegate(Task t)
                {
                    if (t.IsFaulted)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            _started = false;
                            ShowFallback("WebView2 初始化失败：" + RootMessage(t.Exception));
                        }));
                        return;
                    }
                    WireEvents();
                    Navigate();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception exc)
            {
                _started = false;
                ShowFallback("WebView2 初始化异常：" + exc.Message);
            }
        }

        private static string RootMessage(Exception exc)
        {
            Exception inner = exc;
            while (inner != null && inner.InnerException != null)
            {
                inner = inner.InnerException;
            }
            return inner == null ? "未知错误" : inner.Message;
        }

        private void WireEvents()
        {
            _view.NavigationCompleted += delegate(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                Navigated = e.IsSuccess;
                Status = e.IsSuccess ? "已加载" : "加载失败 (" + e.WebErrorStatus + ")";
                if (!e.IsSuccess)
                {
                    ShowFallback("面板没能加载：" + e.WebErrorStatus + "\n通常是核心还没起来，稍等几秒再点一次「内置面板」。");
                }
            };
            try
            {
                _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            }
            catch (Exception)
            {
            }
        }

        public void Navigate()
        {
            // WebView2 是套间绑定的：从别的线程连 .CoreWebView2 这个属性都不能碰（会抛
            // "CoreWebView2 can only be accessed from the UI thread"，没人接住就是整个进程崩）。
            if (IsHandleCreated && InvokeRequired)
            {
                try { BeginInvoke(new Action(Navigate)); } catch (Exception) { }
                return;
            }
            if (_view == null || _view.CoreWebView2 == null || _url.Length == 0)
            {
                return;
            }
            _notice.Visible = false;
            _openBrowser.Visible = false;
            _retry.Visible = false;
            _view.Visible = true;
            try
            {
                _view.CoreWebView2.Navigate(_url);
            }
            catch (Exception exc)
            {
                ShowFallback("导航失败：" + exc.Message);
            }
        }

        private void ShowFallback(string text)
        {
            if (_view != null)
            {
                _view.Visible = false;
            }
            _notice.Text = text;
            _notice.SetBounds(16, 40, Math.Max(80, Width - 32), 80);
            _notice.Visible = true;
            LayoutButtons();
            Status = "退回浏览器: " + text.Replace('\n', ' ');
        }

        private void LayoutButtons()
        {
            int left = Math.Max(16, (Width - 288) / 2);
            _retry.SetBounds(left, 132, 120, 36);
            _retry.Visible = true;
            _openBrowser.SetBounds(left + 136, 132, 152, 36);
            _openBrowser.Visible = true;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_notice != null && _notice.Visible)
            {
                _notice.SetBounds(16, 40, Math.Max(80, Width - 32), 80);
                LayoutButtons();
            }
        }
    }
}
