// 一键修复：把「这台机器的哪几件事坏了、我们能不能修」讲清楚，并且真的去修有把握的那部分。
//
// 设计约束（都是被教训逼出来的）：
// · 每一项都是「检测 → 只有确认坏了才动手 → 动手后复验」，绝不"看起来没坏也顺手重装一遍"。
// · 修不了的绝不假装修好：二进制缺了只能让他重装（安装程序不动 data\），我们负责说清缺了什么。
// · 会断开机器人的动作（重置口令、杀别人的进程）一律不做成自动，只给一条明确的下一步。
// · 全部跑在后台线程：这里每一步都在碰磁盘或网络，放 UI 线程上就是"点了修复然后未响应"。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AstrBotDesktop
{
    internal sealed class RepairStep
    {
        public string Name = "";
        public string Found = "";
        public string Did = "";
        public bool Broken;
        public bool Fixed;
        public bool NeedsUser;
        public string Advice = "";
    }

    // 修复器不直接抓着窗体不放：这些钩子让 --repairtest 能把真故障注进来再验一遍。
    internal sealed class RepairHooks
    {
        public Func<bool> CoreRunning = delegate { return false; };
        public Func<int> CorePid = delegate { return 0; };
        public Action StopCore = delegate { };
        public Action StartCore = delegate { };
        public Func<bool> PanelAnswers = delegate { return false; };
        public Action RediscoverOneBot = delegate { };
        public Func<string> OneBotNote = delegate { return ""; };
        public Action SaveSettings = delegate { };
        public Action<string> Log = delegate { };
        // 修复器自己起的/正在管的那个核心不能当孤儿杀
        public Func<int> OwnCorePid = delegate { return 0; };
        // 还有别的启动器在跑时，它名下那个核心不是孤儿，绝不能杀
        public Func<bool> OtherLauncherAlive = delegate { return false; };
        // 用户自己指的"免安装包"路径（没指就返回空串，由修复器去常见位置找）
        public Func<string> RestoreZipPath = delegate { return ""; };
    }

    internal sealed class RepairRunner
    {
        // 免安装包的名字：它就是这份程序的完整副本，修复器第二层靠它
        public const string PortableZipName = "AstrBot-4.29.3-desktop-portable.zip";
        private readonly string _baseDir;
        private readonly LauncherSettings _settings;
        private readonly RepairHooks _hooks;
        private readonly List<RepairStep> _steps = new List<RepairStep>();

        public RepairRunner(string baseDir, LauncherSettings settings, RepairHooks hooks)
        {
            _baseDir = baseDir;
            _settings = settings;
            _hooks = hooks;
        }

        public List<RepairStep> Steps
        {
            get { return _steps; }
        }

        public string Summary
        {
            get
            {
                int fixedCount = 0;
                int needUser = 0;
                foreach (RepairStep step in _steps)
                {
                    if (step.Fixed) { fixedCount++; }
                    if (step.NeedsUser) { needUser++; }
                }
                return "修好 " + fixedCount + " 处，还需你处理 " + needUser + " 处，其余检查没发现问题";
            }
        }

        public string Run()
        {
            _steps.Clear();
            CheckKeyFiles();
            CheckPackageIntegrity();
            CheckDataDirs();
            CheckConfig();
            CheckOrphanCore();
            CheckPort();
            CheckCoreRunning();
            CheckDisk();
            CheckWebView2();
            CheckOneBotClient();
            CheckFinalProof();
            string summary = Summary;
            _hooks.Log(summary);
            return summary;
        }

        private RepairStep Begin(string name)
        {
            RepairStep step = new RepairStep();
            step.Name = name;
            _steps.Add(step);
            return step;
        }

        private static bool Missing(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                return !info.Exists || info.Length == 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // 一、包体自己：缺文件不是"修复"能变出来的，说清楚缺哪个、怎么重装（重装不碰 data\）
        private void CheckKeyFiles()
        {
            RepairStep step = Begin("程序文件");
            List<string> missing = new List<string>();
            string[] must = new string[]
            {
                "AstrBot.exe",
                Path.Combine("core", "AstrBotCore.exe"),
                Path.Combine("runtime", "python.exe"),
                "astrbot.ico",
                "README.txt",
            };
            foreach (string rel in must)
            {
                if (Missing(Path.Combine(_baseDir, rel)))
                {
                    missing.Add(rel);
                }
            }
            if (!Directory.Exists(Path.Combine(_baseDir, "core", "_internal")))
            {
                missing.Add(Path.Combine("core", "_internal") + "\\（整个依赖目录）");
            }
            if (missing.Count == 0)
            {
                step.Found = "都在";
                return;
            }
            step.Broken = true;
            step.NeedsUser = true;
            step.Found = "缺 " + missing.Count + " 项：" + string.Join("、", missing.ToArray());
            step.Advice = "用 AstrBot-Setup-4.29.3.exe 重装一次即可（它不会动 data\\，聊天记录和配置都留着）；"
                + "如果是被杀毒软件隔离了，请把它加进白名单再装。";
            _hooks.Log("[修复] 程序文件缺失：" + step.Found);
        }

        // 一·五、包体完整性：拿随包的清单（factory\MANIFEST-SHA256.txt）逐个文件核对。
        // 杀毒软件吃掉一个 dll、用户删错一个文件，光看"在不在"是看不出来的——摘要才对得上。
        // 能还原的（factory 里带副本的那些）就地还原；还原不了的把文件名一条条列出来。
        private void CheckPackageIntegrity()
        {
            RepairStep step = Begin("包体完整性");
            string manifest = Path.Combine(_baseDir, "factory", "MANIFEST-SHA256.txt");
            if (!File.Exists(manifest))
            {
                step.Found = "包里没带清单（老版本的包），这一项只能跳过";
                return;
            }
            List<string[]> entries = new List<string[]>();
            try
            {
                foreach (string raw in File.ReadAllLines(manifest, Encoding.UTF8))
                {
                    if (raw.Trim().Length == 0 || raw.StartsWith("#"))
                    {
                        continue;
                    }
                    string[] parts = raw.Split('\t');
                    if (parts.Length == 3)
                    {
                        entries.Add(parts);
                    }
                }
            }
            catch (Exception exc)
            {
                step.Found = "清单读不出来：" + exc.Message;
                return;
            }

            List<string> missing = new List<string>();
            List<string> changed = new List<string>();
            foreach (string[] entry in entries)
            {
                string rel = entry[0].Replace('/', Path.DirectorySeparatorChar);
                string path = Path.Combine(_baseDir, rel);
                if (!File.Exists(path))
                {
                    missing.Add(rel);
                    continue;
                }
                long want;
                if (long.TryParse(entry[1], out want) && new FileInfo(path).Length != want)
                {
                    changed.Add(rel);   // 大小就不对，不必再算摘要
                    continue;
                }
                if (!string.Equals(Sha256Of(path), entry[2], StringComparison.OrdinalIgnoreCase))
                {
                    changed.Add(rel);
                }
            }
            int checkedCount = entries.Count;
            if (missing.Count == 0 && changed.Count == 0)
            {
                step.Found = "逐个核对了 " + checkedCount + " 个文件，摘要全对";
                return;
            }
            step.Broken = true;
            step.Found = "核对 " + checkedCount + " 个文件：缺 " + missing.Count + " 个、被改 " + changed.Count + " 个";

            // 第一层：出厂副本（factory\bin、factory\small）里带的就够补最容易坏的那几件
            List<string> restored = new List<string>();
            List<string> trouble = new List<string>();
            List<string> pending = new List<string>();
            foreach (string rel in Combine(missing, changed))
            {
                string want = WantHash(entries, rel);
                if (want.Length == 0)
                {
                    trouble.Add(rel + "（清单里没有这一项，不知道正确摘要，不敢动）");
                    continue;
                }
                string copy = FactoryCopy(rel);
                if (copy.Length > 0 && string.Equals(Sha256Of(copy), want, StringComparison.OrdinalIgnoreCase))
                {
                    TryRestore(rel, copy, want, restored, trouble);
                    continue;
                }
                pending.Add(rel);
            }
            // 第二层：免安装包就是这份程序的完整副本。把它留在硬盘上（或指给修复器），
            // 任何一个文件被吃掉都能按摘要补回来——这才是"出问题也还能用"。
            if (pending.Count > 0)
            {
                string zip = RestoreZip();
                if (zip.Length > 0)
                {
                    RestoreFromZip(zip, pending, entries, restored, trouble);
                }
            }
            if (restored.Count > 0)
            {
                step.Did = "按摘要还原了 " + restored.Count + " 个：" + string.Join("、", restored.ToArray());
            }
            if (pending.Count > 0 || trouble.Count > 0)
            {
                step.NeedsUser = true;
                List<string> list = new List<string>();
                foreach (string rel in pending) { list.Add("补不上：" + rel); }
                foreach (string note in trouble) { list.Add("动不了：" + note); }
                int shown = Math.Min(6, list.Count);
                step.Advice = "这些还得靠你：" + string.Join("；", list.GetRange(0, shown).ToArray())
                    + (list.Count > shown ? " 等 " + list.Count + " 项" : "")
                    + "。把手里那份 AstrBot-4.29.3-desktop-portable.zip 放到启动器同目录，"
                    + "或点「从免安装包还原」指给修复器，就能按摘要一个个补回来（不动 data\\）；"
                    + "实在没有就重装一次（安装程序也不动 data\\）。";
            }
            step.Fixed = pending.Count == 0 && trouble.Count == 0;
            _hooks.Log("[修复] 包体核对：" + step.Found
                + (restored.Count > 0 ? "，已还原 " + restored.Count + " 个" : ""));
        }

        // 还原一个文件：先核对来源摘要，再落盘，落盘后再核对一次（覆盖动作本身也可能失败）
        private void TryRestore(string rel, string source, string want, List<string> restored, List<string> trouble)
        {
            string dst = Path.Combine(_baseDir, rel);
            bool runningImage = rel.Equals("AstrBot.exe", StringComparison.OrdinalIgnoreCase);
            if (rel.EndsWith("AstrBotCore.exe", StringComparison.OrdinalIgnoreCase) && _hooks.CoreRunning())
            {
                _hooks.Log("[修复] 要还原核心，先把它停下来");
                _hooks.StopCore();
            }
            try
            {
                if (runningImage)
                {
                    // 正在跑的那个镜像不能覆盖，但 Windows 允许改名：先挪开坏的，再把好的放回原位。
                    // 当前进程用的还是内存里的旧代码，下次启动才生效——所以这条要如实说。
                    string aside = dst + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Move(dst, aside);
                    File.Copy(source, dst);
                    if (string.Equals(Sha256Of(dst), want, StringComparison.OrdinalIgnoreCase))
                    {
                        restored.Add(rel + "（坏的那份留在 " + Path.GetFileName(aside) + "，下次启动生效）");
                        return;
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(source, dst, true);
                    if (string.Equals(Sha256Of(dst), want, StringComparison.OrdinalIgnoreCase))
                    {
                        restored.Add(rel);
                        return;
                    }
                }
                trouble.Add(rel + "（覆盖后摘要仍不对）");
            }
            catch (Exception exc)
            {
                trouble.Add(rel + (runningImage
                    ? "（正被你自己用着：托盘退出启动器，再点一次修复就能还原；"
                    : "（落盘失败：") + exc.Message + "）");
            }
        }

        // 去哪儿找那份完整副本：用户指的优先，其次是包旁边和常见的下载位置
        private string RestoreZip()
        {
            string picked = "";
            try
            {
                picked = _hooks.RestoreZipPath == null ? "" : _hooks.RestoreZipPath();
            }
            catch (Exception)
            {
                picked = "";
            }
            if (picked.Length > 0 && File.Exists(picked))
            {
                return picked;
            }
            List<string> homes = new List<string>();
            homes.Add(_baseDir);
            homes.Add(Path.Combine(_baseDir, ".."));
            homes.Add(Path.Combine(_baseDir, "..", ".."));
            try
            {
                homes.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                homes.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            }
            catch (Exception)
            {
            }
            foreach (string home in homes)
            {
                try
                {
                    string path = Path.Combine(home, PortableZipName);
                    if (File.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
                catch (Exception)
                {
                }
            }
            return "";
        }

        private void RestoreFromZip(string zip, List<string> pending, List<string[]> entries,
            List<string> restored, List<string> trouble)
        {
            _hooks.Log("[修复] 从免安装包补文件：" + Path.GetFileName(zip));
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zip))
                {
                    // 包里的名字带一层 AstrBot/ 前缀；先按直连名字找，找不到再用一份索引兜底
                    // （不同版本打包时那层前缀可能不一样，索引只认"去掉第一段之后的相对路径"）
                    Dictionary<string, string> index = null;
                    List<string> handled = new List<string>();
                    foreach (string rel in pending)
                    {
                        string key = rel.Replace(Path.DirectorySeparatorChar, '/');
                        ZipArchiveEntry entry = archive.GetEntry(ZipRootPrefix + key);
                        if (entry == null)
                        {
                            if (index == null)
                            {
                                index = BuildZipIndex(archive);
                            }
                            string fullName;
                            if (!index.TryGetValue(key, out fullName))
                            {
                                continue;
                            }
                            entry = archive.GetEntry(fullName);
                            if (entry == null)
                            {
                                continue;
                            }
                        }
                        string want = WantHash(entries, rel);
                        string tmp = Path.Combine(Path.GetTempPath(),
                            "astrbot-restore-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        try
                        {
                            entry.ExtractToFile(tmp, true);
                            if (!string.Equals(Sha256Of(tmp), want, StringComparison.OrdinalIgnoreCase))
                            {
                                trouble.Add(rel + "（免安装包里的这份摘要也不对，不敢用）");
                                handled.Add(rel);
                                continue;
                            }
                            TryRestore(rel, tmp, want, restored, trouble);
                            handled.Add(rel);
                        }
                        catch (Exception exc)
                        {
                            trouble.Add(rel + "（从包里抽出来失败：" + exc.Message + "）");
                        }
                        finally
                        {
                            try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch (Exception) { }
                        }
                    }
                    foreach (string rel in handled)
                    {
                        pending.Remove(rel);
                    }
                }
            }
            catch (Exception exc)
            {
                _hooks.Log("[修复] 免安装包读不开：" + exc.Message);
            }
        }

        private const string ZipRootPrefix = "AstrBot/";

        private static Dictionary<string, string> BuildZipIndex(ZipArchive archive)
        {
            Dictionary<string, string> index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry item in archive.Entries)
            {
                if (item.Length == 0)
                {
                    continue;
                }
                string name = item.FullName.Replace('\\', '/');
                int slash = name.IndexOf('/');
                string rel = slash > 0 ? name.Substring(slash + 1) : name;
                if (!index.ContainsKey(rel))
                {
                    index.Add(rel, name);
                }
            }
            return index;
        }

        private static List<string> Combine(List<string> a, List<string> b)
        {
            List<string> all = new List<string>();
            foreach (string item in a) { if (!all.Contains(item)) { all.Add(item); } }
            foreach (string item in b) { if (!all.Contains(item)) { all.Add(item); } }
            return all;
        }

        private static string WantHash(List<string[]> entries, string rel)
        {
            foreach (string[] entry in entries)
            {
                if (string.Equals(entry[0].Replace('/', Path.DirectorySeparatorChar), rel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return entry[2];
                }
            }
            return "";
        }

        private string FactoryCopy(string rel)
        {
            string file = Path.GetFileName(rel);
            string[] homes = new string[]
            {
                Path.Combine(_baseDir, "factory", "bin"),
                Path.Combine(_baseDir, "factory", "small"),
            };
            foreach (string home in homes)
            {
                string path = Path.Combine(home, file);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return "";
        }

        internal static string Sha256Of(string path)
        {
            using (var sha = new System.Security.Cryptography.SHA256CryptoServiceProvider())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        // 二、数据目录：这个我们建得回来
        private void CheckDataDirs()
        {
            RepairStep step = Begin("数据目录");
            List<string> made = new List<string>();
            string[] dirs = new string[]
            {
                Path.Combine(_baseDir, "data"),
                Path.Combine(_baseDir, "data", "tmp"),
                Path.Combine(_baseDir, "data", "webview2"),
                Path.Combine(_baseDir, "data", "plugins"),
            };
            foreach (string dir in dirs)
            {
                if (Directory.Exists(dir))
                {
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(dir);
                    made.Add(Path.GetFileName(dir));
                }
                catch (Exception exc)
                {
                    step.Broken = true;
                    step.NeedsUser = true;
                    step.Found = "建不出来：" + exc.Message;
                    step.Advice = "这一项多半是磁盘权限或盘满了，先确认 " + _baseDir + " 所在盘还能写。";
                    _hooks.Log("[修复] 数据目录建不出来：" + exc.Message);
                    return;
                }
            }
            if (made.Count == 0)
            {
                step.Found = "齐全";
                return;
            }
            step.Broken = true;
            step.Found = "缺 " + made.Count + " 个目录：" + string.Join("、", made.ToArray());
            step.Did = "补建了 " + string.Join("、", made.ToArray());
            List<string> still = new List<string>();
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) { still.Add(dir); }
            }
            step.Fixed = still.Count == 0;
            if (!step.Fixed)
            {
                step.NeedsUser = true;
                step.Advice = "补建后仍然缺：" + string.Join("、", still.ToArray());
            }
            _hooks.Log("[修复] " + step.Did + (step.Fixed ? "（已复验）" : "（复验没过）"));
        }

        // 三、配置文件语法：坏就留档挪开，让核心用默认值重建（沿用纠错那条已经验过的路）
        private void CheckConfig()
        {
            RepairStep step = Begin("配置文件");
            string cfg = Path.Combine(_baseDir, "data", "cmd_config.json");
            if (!File.Exists(cfg))
            {
                step.Found = "还没有配置文件（首次启动会自己生成）";
                return;
            }
            if (!Preflight.ConfigJsonInvalid(_baseDir))
            {
                step.Found = "语法正常";
                return;
            }
            step.Broken = true;
            string backup = Preflight.QuarantineConfig(_baseDir);
            if (backup.Length == 0)
            {
                step.NeedsUser = true;
                step.Found = "配置读坏了，但连备份都没挪动";
                step.Advice = "文件可能被别的程序占着：关掉编辑器/AstrBot 相关进程再点一次修复。";
                _hooks.Log("[修复] 坏配置没能挪开");
                return;
            }
            step.Found = "cmd_config.json 语法读坏了";
            step.Did = "已把坏配置留档为 " + Path.GetFileName(backup) + "，核心下次启动会用默认值重建";
            step.Fixed = !File.Exists(cfg) || !Preflight.ConfigJsonInvalid(_baseDir);
            _hooks.Log("[修复] 配置文件读坏，已留档重建：" + Path.GetFileName(backup));
        }

        private static bool TcpAnswers(string host, int port, int timeoutMs)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult begin = client.BeginConnect(host, port, null, null);
                    if (!begin.AsyncWaitHandle.WaitOne(timeoutMs, true))
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

        // 三·五、孤儿核心：启动器崩过/被强杀之后，它那个核心常常还活着并占着面板端口。
        // 下一次打开就看到"端口被占"，端口一路顺延，用户以为产品乱改设置。
        // 只动"路径确实是我们这份核心、且现场没有别的启动器在管它"的进程，按 PID 精确杀。
        private void CheckOrphanCore()
        {
            RepairStep step = Begin("残留的核心");
            int own = _hooks.OwnCorePid();
            if (_hooks.OtherLauncherAlive())
            {
                step.Found = "还有别的启动器在跑，它名下的核心不算残留";
                return;
            }
            string ourCore;
            try
            {
                ourCore = Path.GetFullPath(Path.Combine(_baseDir, "core", "AstrBotCore.exe"));
            }
            catch (Exception)
            {
                step.Found = "核心路径判不出来，跳过（宁可不杀）";
                return;
            }
            List<int> orphans = new List<int>();
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
                        string path;
                        try
                        {
                            path = proc.MainModule != null ? Path.GetFullPath(proc.MainModule.FileName) : "";
                        }
                        catch (Exception)
                        {
                            continue;   // 读不到模块路径的（权限不够、位数不符）一律不动
                        }
                        if (string.Equals(path, ourCore, StringComparison.OrdinalIgnoreCase))
                        {
                            orphans.Add(proc.Id);
                        }
                    }
                }
            }
            catch (Exception)
            {
                step.Found = "进程列表读不出来，跳过";
                return;
            }
            if (orphans.Count == 0)
            {
                step.Found = "没有没人管的核心在跑";
                return;
            }
            step.Broken = true;
            List<string> ids = new List<string>();
            foreach (int pid in orphans)
            {
                ids.Add(pid.ToString());
                try
                {
                    // 同 OneBot 那一条的规矩：只按 PID 杀我们认出来的那一棵进程树
                    ProcessStartInfo kill = new ProcessStartInfo();
                    kill.FileName = "taskkill.exe";
                    kill.Arguments = "/PID " + pid + " /T /F";
                    kill.UseShellExecute = false;
                    kill.CreateNoWindow = true;
                    Process.Start(kill);
                }
                catch (Exception)
                {
                }
            }
            step.Did = "停掉了没人管的旧核心（PID " + string.Join("、", ids.ToArray()) + "）";
            System.Threading.Thread.Sleep(1500);
            int left = 0;
            try
            {
                foreach (Process proc in Process.GetProcessesByName("AstrBotCore"))
                {
                    using (proc)
                    {
                        if (orphans.Contains(proc.Id))
                        {
                            left++;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            step.Fixed = left == 0;
            step.Found = left == 0
                ? "有 " + orphans.Count + " 个没人管的核心占着端口，已清掉"
                : "有 " + orphans.Count + " 个没清掉（权限不够），端口那一项会改用别的端口";
            if (!step.Fixed)
            {
                step.NeedsUser = true;
                step.Advice = "在任务管理器里结束 AstrBotCore.exe，或者重启一次电脑。";
            }
            _hooks.Log("[修复] " + step.Did);
        }

        // 四、端口：被占就换（换端口是有把握的；杀占端口的东西不是）
        private void CheckPort()
        {
            RepairStep step = Begin("面板端口");
            int port = _settings.Port;
            if (Preflight.PortFree(port))
            {
                step.Found = port + " 空着";
                return;
            }
            bool ours = _hooks.CoreRunning();
            if (ours && _hooks.CorePid() > 0 && TcpAnswers("127.0.0.1", port, 600))
            {
                step.Found = port + " 上就是我们自己的核心在听（正常）";
                return;
            }
            step.Broken = true;
            int picked = Preflight.PickPort(port);
            if (picked == port)
            {
                step.NeedsUser = true;
                step.Found = port + " 被别的程序占着，且找不到能用的端口";
                step.Advice = "重启一次电脑通常最省事；或者告诉我占端口的是哪个程序，我再看能不能绕。";
                _hooks.Log("[修复] 没有可用端口，留给用户处理");
                return;
            }
            _settings.Port = picked;
            _hooks.SaveSettings();
            step.Found = port + " 被别的程序占着（不是我们的核心）";
            step.Did = "端口从 " + port + " 换到 " + picked + "（已写回设置）";
            step.Fixed = Preflight.PortFree(picked);
            if (!step.Fixed)
            {
                step.NeedsUser = true;
                step.Advice = "换到的 " + picked + " 复验时又被占了，先关掉正在抢端口的程序再点一次。";
            }
            _hooks.Log("[修复] 端口被占，改用 " + picked);
        }

        // 五、核心到底跑没跑：没跑就起来，跑着但面板不回话就重启我们那一份
        private void CheckCoreRunning()
        {
            RepairStep step = Begin("核心服务");
            bool running = _hooks.CoreRunning();
            bool answers = running && _hooks.PanelAnswers();
            if (running && answers)
            {
                step.Found = "在跑，面板也有回音";
                return;
            }
            step.Broken = true;
            if (running && !answers)
            {
                int pid = _hooks.CorePid();
                _hooks.Log("[修复] 核心进程在（PID " + pid + "）但面板不回话，按 PID 停掉再起一次");
                _hooks.StopCore();
            }
            else
            {
                _hooks.Log("[修复] 核心没在跑，现在起它");
            }
            _hooks.StartCore();
            for (int i = 0; i < 60 && !_hooks.PanelAnswers(); i++)
            {
                System.Threading.Thread.Sleep(1000);
            }
            bool now = _hooks.PanelAnswers();
            step.Did = running && !answers ? "重启了核心" : "启动了核心";
            step.Found = now ? "核心已起来，面板应答正常" : "起完之后面板仍然不回话";
            step.Fixed = now;
            if (!now)
            {
                step.NeedsUser = true;
                step.Advice = "看下面日志里最后几行报错，或点「打包诊断」把包发出来；这一步我们不该瞎猜。";
            }
            _hooks.Log("[修复] " + step.Did + "：" + (now ? "面板已能应答" : "还是没回音"));
        }

        private void CheckDisk()
        {
            RepairStep step = Begin("磁盘空间");
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_baseDir));
                long free = drive.AvailableFreeSpace;
                if (free > 500L * 1024 * 1024)
                {
                    step.Found = (free / 1073741824.0).ToString("0.0") + " GB 可用";
                    return;
                }
                step.Broken = true;
                step.NeedsUser = true;
                step.Found = "只剩 " + (free / 1048576.0).ToString("0") + " MB";
                step.Advice = "聊天记录和插件依赖都要落盘，盘满会让核心写不进配置。清出 1 GB 再来一次；"
                    + "我们不会替你删任何东西。";
                _hooks.Log("[修复] 磁盘只剩 " + (free / 1048576.0).ToString("0") + " MB，需要你先清理");
            }
            catch (Exception)
            {
                step.Found = "这台机器读不出剩余空间，跳过";
            }
        }

        private void CheckWebView2()
        {
            RepairStep step = Begin("内嵌面板内核");
            string version = PanelView.AvailableVersion();
            if (version.Length > 0)
            {
                step.Found = "WebView2 Runtime " + version;
                return;
            }
            step.Broken = true;
            step.NeedsUser = true;
            step.Found = "没装 WebView2 Runtime";
            step.Advice = "不影响使用：点「打开面板」会自动用系统浏览器。想要内嵌在窗口里，"
                + "去微软官网装 Microsoft Edge WebView2 Runtime（Evergreen）即可，装完点一次修复就能看到。";
            _hooks.Log("[修复] 缺 WebView2 Runtime，已退回系统浏览器（要内嵌就装 Evergreen 版）");
        }

        private void CheckOneBotClient()
        {
            RepairStep step = Begin("QQ 接入客户端");
            _hooks.RediscoverOneBot();
            string note = _hooks.OneBotNote();
            if (note.Length == 0)
            {
                step.Found = "接的是官方机器人，不需要本机客户端";
                return;
            }
            step.Found = note;
            if (note.Contains("在线") || note.Contains("已登录"))
            {
                return;
            }
            step.Broken = true;
            step.NeedsUser = true;
            step.Advice = "个人号这条路必须本机跑着一个 OneBot 客户端：点「启动」把它带起来；"
                + "没装就点「下载 NapCat」（固定 v4.18.41，按 SHA-256 逐字节核对），"
                + "或者用浏览器自己下完点「选目录」指给启动器。";
            _hooks.Log("[修复] 客户端状态：" + note + " —— 需要你在右侧那一栏点一下");
        }

        // 最后一步是"修完到底行不行"的总复验：不看我们自己写了什么，看面板真答不答话
        private void CheckFinalProof()
        {
            RepairStep step = Begin("修完复验");
            bool answers = _hooks.PanelAnswers();
            step.Found = answers
                ? "面板在 " + _settings.Port + " 上应答正常"
                : "面板还是打不开";
            step.Fixed = answers;
            if (!answers)
            {
                step.Broken = true;
                step.NeedsUser = true;
                step.Advice = "点「重启服务」再等十秒；仍不行就点「打包诊断」，把那个文件发出来。";
            }
        }
    }
}
