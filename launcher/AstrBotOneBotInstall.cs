// 没装客户端时的可选下载：固定版本 + 逐字节校验。
//
// 为什么允许下载第三方：个人号那条路必须有 OneBot 客户端，让"那人"自己去 GitHub 找版本、
// 解压、再装 QQ，比这个启动器难用多了。但这是把别人写的、能读 QQ 会话的代码放上他的机器，
// 所以边界钉死：
//   · 版本与 sha256 写死在这里，不是"最新可用版本"。这两个摘要 2026-10-29 从 GitHub API
//     的 assets[].digest 字段取到，并与本机下载的文件逐字节核对过。
//   · 校验不过就删掉、不装，也不回退到别的来源——内容对不上时"能装上"才是坏事。
//   · 国内优先走加速前缀，失败自动退回直连；前缀是别人的服务，只在字节校验通过后才作数。
//   · 装到 data\onebot\ 下，与程序本体分开；随包落一份来源说明，写清上游、版本、摘要与许可证。
//   · 绝不自动装：必须人点「下载」。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace AstrBotDesktop
{
    internal sealed class OneBotVariant
    {
        public string Key = "";
        public string Display = "";
        public string FileName = "";
        public string Sha256 = "";
        public string Note = "";
    }

    internal sealed class OneBotInstaller
    {
        public const string Version = "4.18.41";
        private const string Repo = "NapNeko/NapCatQQ";

        // 国内直连 GitHub 释放页经常拿不全字节，前缀失败再退回直连；顺序就是尝试顺序
        private static readonly string[] Prefixes = new string[] { "https://gh-proxy.com/", "" };

        public static readonly OneBotVariant[] Variants = new OneBotVariant[]
        {
            NewVariant("shell", "NapCat Shell（本机已装 QQ 客户端时用）", "NapCat.Shell.zip",
                "6801f93e533846cea05f4f531874a0e84371ab2331c5585b57cd7e3b869cf96b",
                "要往 QQ 进程里注入，首次启动可能弹一次 UAC。"),
            NewVariant("framework", "NapCat Framework（同样需要本机装过 QQ）", "NapCat.Framework.zip",
                "3c526f5789ff6cc56d78381eb10536c8273fe394d227187b27dea94b59965822",
                "入口是 napiLoader.bat，仍然从注册表里找 QQ.exe。"),
        };

        private readonly string _appDir;
        // 离线副本所在的目录：验收演练会把"装到哪"指到临时目录，但找随包副本必须看真正的程序目录，
        // 不然那条"断网也能装"的断言会去临时目录里找一个根本不存在的东西。
        private readonly string _bundleDir;

        public event Action<string> Line;

        public OneBotInstaller(string appDir)
            : this(appDir, appDir)
        {
        }

        public OneBotInstaller(string appDir, string bundleDir)
        {
            _appDir = appDir;
            _bundleDir = bundleDir;
        }

        private static OneBotVariant NewVariant(string key, string display, string file, string sha, string note)
        {
            OneBotVariant v = new OneBotVariant();
            v.Key = key;
            v.Display = display;
            v.FileName = file;
            v.Sha256 = sha;
            v.Note = note;
            return v;
        }

        public static OneBotVariant Find(string key)
        {
            foreach (OneBotVariant v in Variants)
            {
                if (v.Key == key)
                {
                    return v;
                }
            }
            return null;
        }

        // 只问一头：线上这个包现在还在不在、这条加速前缀通不通。拿不到不算错误，返回 -1 由调用方记 NOTRUN。
        // 有些代理回 200 但不给 Content-Length —— 那也算"通"，别让断言去比一个不存在的大小。
        public static long ProbeAsset(OneBotVariant variant)
        {
            foreach (string prefix in Prefixes)
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ReleaseUrl(variant, prefix));
                    request.Method = "HEAD";
                    request.UserAgent = "astrbot-desktop-client";
                    request.Timeout = 15000;
                    using (WebResponse response = request.GetResponse())
                    {
                        string len = response.Headers["Content-Length"];
                        long value;
                        if (long.TryParse(len, out value) && value > 0)
                        {
                            return value;
                        }
                        return 1;
                    }
                }
                catch (Exception)
                {
                }
            }
            return -1;
        }

        public string InstallRoot
        {
            get { return Path.Combine(_appDir, "data", "onebot"); }
        }

        public static string ReleaseUrl(OneBotVariant variant, string prefix)
        {
            return prefix + "https://github.com/" + Repo + "/releases/download/v" + Version + "/" + variant.FileName;
        }

        public string TargetDir(OneBotVariant variant)
        {
            return Path.Combine(InstallRoot, variant.Key == "shell" ? "NapCat.Shell" : "NapCat.Framework");
        }

        // 随包带的离线副本（build\make_factory.py 落的）。断网、代理没开、GitHub 拿不全字节
        // 这三种情况下，点「下载 NapCat」照样能装上——副本仍然按同一串摘要核对，不因为"是自己包的"就免检。
        public string BundledZip(OneBotVariant variant)
        {
            string path = Path.Combine(_bundleDir, "factory", "napcat", variant.FileName);
            return File.Exists(path) ? path : "";
        }

        // localZip 非空表示用已经下到本地的包（验收跑这条，不走网络）；正常使用时传空串。
        public string Install(OneBotVariant variant, string localZip)
        {
            if (variant == null)
            {
                throw new InvalidOperationException("不认识要装哪一种客户端");
            }

            string zip;
            bool fromBundle = false;
            if (!string.IsNullOrEmpty(localZip))
            {
                zip = localZip;
            }
            else
            {
                string bundled = BundledZip(variant);
                if (bundled.Length > 0)
                {
                    zip = bundled;
                    fromBundle = true;
                    Emit("用随包的离线副本：" + Path.GetFileName(bundled) + "（不用网也能装）");
                }
                else
                {
                    zip = Download(variant);
                }
            }            string sha = Sha256Of(zip);
            if (!string.Equals(sha, variant.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(localZip) && !fromBundle)
                {
                    TryDelete(zip);
                }
                throw new InvalidDataException(
                    (fromBundle ? "随包的副本" : "下载到的文件") + "与固定摘要不一致，拒绝安装\n期望 "
                    + variant.Sha256.Substring(0, 16) + "…\n实际 " + sha.Substring(0, 16) + "…");
            }
            Emit("字节校验通过：" + variant.FileName + " " + variant.VersionLabel());

            string target = TargetDir(variant);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
            Directory.CreateDirectory(target);
            Extract(zip, target);
            WriteProvenance(target, variant, sha);
            Emit("已装到 " + target);
            return target;
        }

        private string Download(OneBotVariant variant)
        {
            string last = "";
            foreach (string prefix in Prefixes)
            {
                string url = ReleaseUrl(variant, prefix);
                string path = Path.Combine(Path.GetTempPath(), "astrbot-" + variant.FileName);
                try
                {
                    Emit("正在下载 " + url);
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.UserAgent = "astrbot-desktop-client";
                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 120000;
                    using (WebResponse response = request.GetResponse())
                    using (Stream src = response.GetResponseStream())
                    using (FileStream dst = File.Create(path))
                    {
                        byte[] buffer = new byte[1 << 16];
                        long total = 0;
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            dst.Write(buffer, 0, read);
                            total += read;
                        }
                        Emit("下载完成 " + (total / 1048576.0).ToString("0.0") + " MB");
                    }
                    return path;
                }
                catch (Exception exc)
                {
                    last = exc.Message;
                    TryDelete(path);
                    Emit(prefix.Length == 0 ? "直连也没拿下来：" + last : "加速前缀没拿下来，退回直连再试");
                }
            }
            throw new InvalidOperationException("下不到客户端压缩包：" + last + "\n\n"
                + "「无法连接到远程服务器」通常是这台机器到 GitHub 的路断了：本地代理没开，"
                + "或 hosts 把 github.com 指到了 127.0.0.1。\n"
                + "三条路，任选一条：\n"
                + "· 把你的代理开起来，再点一次「下载 NapCat」；\n"
                + "· 用浏览器自己下载 v" + Version + " 的 " + variant.FileName + "，"
                + "解压后点「选目录」指给启动器（它照样按端口判活、按 PID 停止）；\n"
                + "· 暂时不接个人号，先用 QQ 官方机器人那条路（点「扫码登录」，不需要客户端）。\n"
                + "不管从哪条路拿到的包，启动器都只认这个 SHA-256：" + variant.Sha256.Substring(0, 16) + "…");
        }

        // zip 里的条目名是外部输入：`..\..\Windows\...` 这种路径穿越必须挡住
        private static void Extract(string zipPath, string target)
        {
            string rootFull = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("/") || entry.FullName.Length == 0)
                    {
                        continue;
                    }
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.Contains("../") || name.Contains(":\\"))
                    {
                        continue;
                    }
                    string dest = Path.GetFullPath(Path.Combine(rootFull, name));
                    if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string dir = Path.GetDirectoryName(dest);
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    entry.ExtractToFile(dest, true);
                }
            }
        }

        private static void WriteProvenance(string target, OneBotVariant variant, string sha)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("这个目录里的东西不是 AstrBot 桌面版自己写的，是从下面这里原样下载并解压的：");
            sb.AppendLine("  项目：https://github.com/" + Repo);
            sb.AppendLine("  版本：v" + Version);
            sb.AppendLine("  文件：" + variant.FileName);
            sb.AppendLine("  SHA-256：" + sha + "（安装时与本文件记录的一致，否则不会装）");
            sb.AppendLine();
            sb.AppendLine("它是独立项目，许可证以它仓库里的 LICENSE 为准（NapCat 采用 AGPL-3.0 并附额外条款，");
            sb.AppendLine("细节请看上游仓库）。这个启动器只是把它放到本机并帮你起停，");
            sb.AppendLine("修改它的代码需要按它的条款同时提供对应源码；要改就去找上游。");
            sb.AppendLine();
            sb.AppendLine("它做的事是替 AstrBot 登录 QQ 并转发消息——它能读这个 QQ 号的全部会话内容。");
            sb.AppendLine("用个人号跑机器人违反腾讯的服务条款，账号被限制或封禁的风险由使用者承担。");
            sb.AppendLine("不想用了：删掉这个目录，或者在启动器里停止后不再启动。");
            File.WriteAllText(Path.Combine(target, "来源与许可说明.txt"), sb.ToString(), Encoding.UTF8);
        }

        public static string Sha256Of(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }

        private void Emit(string text)
        {
            Action<string> handler = Line;
            if (handler != null)
            {
                handler(text);
            }
        }
    }

    internal static class OneBotVariantExtensions
    {
        public static string VersionLabel(this OneBotVariant variant)
        {
            return "v" + OneBotInstaller.Version + " sha256=" + (variant.Sha256.Length >= 12 ? variant.Sha256.Substring(0, 12) : "");
        }
    }

    // 下载确认窗口。不用 MessageBox 凑： MessageBoxButtons 那几套固定按钮在这台机器的
    // 中文界面里会显示成「中止/重试/忽略」，人根本看不出哪个是我要装的那个客户端。
    internal sealed class OneBotInstallDialog : Form
    {
        public OneBotVariant Chosen;

        public OneBotInstallDialog(string note)
        {
            Text = "下载 QQ 接入客户端";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(452, 300);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;

            Label head = new Label();
            head.Text = "个人号需要一个 OneBot 客户端。选一个装到本程序的 data\\onebot 目录下：";
            head.ForeColor = Theme.Text;
            head.SetBounds(22, 20, 408, 34);
            Controls.Add(head);

            Label risk = new Label();
            risk.Text = note;
            risk.ForeColor = Theme.Muted;
            risk.Font = Theme.Small;
            risk.SetBounds(22, 60, 408, 108);
            Controls.Add(risk);

            RoundButton shell = Theme.Primary("装 NapCat Shell");
            shell.Name = "pickShell";
            shell.SetBounds(22, 182, 200, 36);
            shell.Click += delegate
            {
                Chosen = OneBotInstaller.Find("shell");
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(shell);

            RoundButton framework = Theme.Ghost("装 NapCat Framework");
            framework.Name = "pickFramework";
            framework.SetBounds(230, 182, 200, 36);
            framework.Click += delegate
            {
                Chosen = OneBotInstaller.Find("framework");
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(framework);

            RoundButton cancel = Theme.Ghost("暂不下载");
            cancel.Name = "pickCancel";
            cancel.SetBounds(22, 230, 120, 32);
            cancel.Click += delegate
            {
                Chosen = null;
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(cancel);

            try
            {
                Native.ApplyWindowStyle(this);
            }
            catch (Exception)
            {
            }
        }
    }
}
