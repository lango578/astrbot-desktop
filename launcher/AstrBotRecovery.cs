// 启动器纠错：启动前端口体检、崩溃退避重启、故障分类、脱敏诊断导出。
// 全部在启动器侧，核心不用重打。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AstrBotDesktop
{
    internal enum FaultKind
    {
        None,
        PortBusy,
        BadConfig,
        ReadOnlyData,
        DependencyConflict,
        MissingDependency,
        Timeout,
        Crashed,
    }

    internal static class Diagnose
    {
        private sealed class Rule
        {
            public FaultKind Kind;
            public string[] Needles;
            public string Advice;

            public Rule(FaultKind kind, string[] needles, string advice)
            {
                Kind = kind;
                Needles = needles;
                Advice = advice;
            }
        }

        private static readonly Rule[] Rules = new Rule[]
        {
            new Rule(FaultKind.PortBusy,
                new string[] { "Address already in use", "only one usage of each socket address", "WinError 10048", "通常每个套接字地址" },
                "面板端口被别的程序占了。启动器会自动往后找空闲端口；要一直用固定端口就改 data\\desktop.conf 里的 port。"),
            new Rule(FaultKind.BadConfig,
                new string[] { "Expecting value", "JSONDecodeError" },
                "配置文件 data\\cmd_config.json 读坏了（多半是被别的编辑器存成了非法 JSON）。启动器已把它改名存成备份，重启后用默认配置重建；"
                + "要把原来的 QQ / 插件配置找回来，把备份文件里的内容复制回新的 cmd_config.json 就行。"),
            new Rule(FaultKind.ReadOnlyData,
                new string[] { "Permission denied", "Access is denied", "read-only", "拒绝访问" },
                "程序目录或 data 目录不可写。把整个 AstrBot 文件夹挪到普通目录（例如 D:\\AstrBot）再启动，别放在 Program Files 这种要管理员权限的位置。"),
            new Rule(FaultKind.DependencyConflict,
                new string[] { "conflict between plugin dependencies" },
                "插件依赖和程序自带模块重名。先点「打包自检」看 pip 那几项是否 OK；急用就把 data\\site-packages 里同名的包删掉再重启。"),
            new Rule(FaultKind.MissingDependency,
                new string[] { "ModuleNotFoundError", "ImportError" },
                "某个插件缺依赖。看它 requirements.txt 里的包名，用 runtime\\python.exe -m pip install --target data\\site-packages "
                + "-i https://mirrors.aliyun.com/pypi/simple/ 包名 装上（带上这个源，直连 pypi.org 国内很慢），再点重启。"),
        };

        public static FaultKind Classify(string log, out string advice)
        {
            advice = "";
            if (string.IsNullOrEmpty(log))
            {
                return FaultKind.Crashed;
            }
            foreach (Rule rule in Rules)
            {
                foreach (string needle in rule.Needles)
                {
                    if (log.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        advice = rule.Advice;
                        return rule.Kind;
                    }
                }
            }
            return FaultKind.Crashed;
        }

        public static string Title(FaultKind kind)
        {
            switch (kind)
            {
                case FaultKind.PortBusy: return "端口被占用";
                case FaultKind.BadConfig: return "配置文件损坏";
                case FaultKind.ReadOnlyData: return "目录不可写";
                case FaultKind.DependencyConflict: return "插件依赖冲突";
                case FaultKind.MissingDependency: return "插件缺依赖";
                case FaultKind.Timeout: return "启动超时";
                case FaultKind.Crashed: return "核心异常退出";
                default: return "未识别的故障";
            }
        }

        // 重启能不能解决：能就地纠（换端口、隔离坏配置）或属于偶发故障的才值得自动再试，
        // 缺依赖/不可写这类重试三次也还是同样的错，直接给人话建议。
        public static bool IsAutoFixable(FaultKind kind)
        {
            return kind == FaultKind.PortBusy
                || kind == FaultKind.BadConfig
                || kind == FaultKind.Timeout
                || kind == FaultKind.Crashed;
        }
    }

    internal sealed class Preflight
    {
        // 探法必须跟「面板实际怎么被访问」对上。三条一起看，一条都不能少：
        //  1) 真去连一下：有人应答就是被占了。但这条并不可靠——别人只 listen 不 accept 时，
        //     背压满了以后连接会被丢掉，看起来就像“没人应答”（实测这样骗过了顺延判定）。
        //  2) 绑 127.0.0.1：核心默认就绑这个地址。
        //  3) 绑 0.0.0.0：Windows 允许在别人占着 0.0.0.0 时再绑一个 127.0.0.1，所以光试 2)
        //     也会把被占的端口判成空闲；能绑上通配地址才算这个端口真是我们的。
        public static bool PortFree(int port)
        {
            return !SomebodyAnswers(port) && Bindable(port) && BindableEverywhere(port);
        }

        private static bool BindableAt(IPAddress address, int port)
        {
            try
            {
                TcpListener listener = new TcpListener(address, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool SomebodyAnswers(int port)
        {
            UiGuard.Note("端口体检（connect 探测要等回音）");
            // 这里刻意用 Socket 而不是 TcpClient：在 .NET Framework 上，被拒绝的连接
            // 根本不会给 TcpClient 的 AsyncWaitHandle 发信号，照「没信号=忙」写就把每个空闲
            // 端口都判成被占（实测踩过，顺延因此从不触发）。用 Socket 时没信号就按没应答算，
            // 真有隐身监听者的情况交给 Bindable 那一关兜住。
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    IAsyncResult ar = socket.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(250))
                    {
                        try
                        {
                            socket.Close();
                        }
                        catch (Exception)
                        {
                        }
                        return false;
                    }
                    socket.EndConnect(ar);
                    return socket.Connected;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool Bindable(int port)
        {
            return BindableAt(IPAddress.Loopback, port);
        }

        public static bool BindableEverywhere(int port)
        {
            return BindableAt(IPAddress.Any, port);
        }

        public static int PickPort(int preferred)
        {
            for (int offset = 0; offset < 10; offset++)
            {
                int candidate = preferred + offset;
                if (candidate > 65534)
                {
                    break;
                }
                if (PortFree(candidate))
                {
                    return candidate;
                }
            }
            return preferred;
        }

        // 只有确认真是 cmd_config.json 读坏了才动手隔离：日志里冒 "Expecting value" 的来源很多
        // （插件自己解析缓存也会报），光凭文案就改人家的配置文件是帮倒忙。
        // 用框架自带的 JavaScriptSerializer 只做语法判定，不理解字段，所以不会跟 AstrBot 的格式理解跑偏。
        public static bool ConfigJsonInvalid(string appDir)
        {
            string path = Path.Combine(appDir, "data", "cmd_config.json");
            if (!File.Exists(path))
            {
                return false;
            }
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception)
            {
                return false;
            }
            if (text.Trim().Length == 0)
            {
                return true;
            }
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.DeserializeObject(text);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // 把坏配置挪开，让核心用默认值重建。原名保留成备份，不删任何东西。
        public static string QuarantineConfig(string appDir)
        {
            string path = Path.Combine(appDir, "data", "cmd_config.json");
            if (!File.Exists(path))
            {
                return "";
            }
            string backup = Path.Combine(
                Path.GetDirectoryName(path),
                "cmd_config.bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            try
            {
                File.Move(path, backup);
                return backup;
            }
            catch (Exception)
            {
                return "";
            }
        }
    }

    internal sealed class RecoveryPolicy
    {
        private static readonly int[] BackoffMs = new int[] { 3000, 10000, 30000 };

        public int Attempts;
        public const int MaxAttempts = 3;

        public bool ShouldRetry()
        {
            return Attempts < MaxAttempts;
        }

        public int NextDelayMs()
        {
            int index = Attempts < BackoffMs.Length ? Attempts : BackoffMs.Length - 1;
            Attempts++;
            return BackoffMs[index];
        }

        public void Reset()
        {
            Attempts = 0;
        }
    }

    internal static class Redact
    {
        private static readonly Regex Secretive = new Regex(
            "(?i)(password|passwd|pwd|pbkdf2_password|jwt_secret|secret|app_secret|access_token|api[_-]?key|access[_-]?key|token|authorization)([\"'\\s:=]+)([^\\s\"',;}]{3,})",
            RegexOptions.Compiled);

        public static string Text(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "";
            }
            return Secretive.Replace(input, delegate(Match m)
            {
                return m.Groups[1].Value + m.Groups[2].Value + "***";
            });
        }
    }

    internal static class Diagnostics
    {
        public static string Write(string appDir, string selfCheck, string logTail, string stateLine)
        {
            string dir = Path.Combine(appDir, "data", "diagnostics");
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "astrbot-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("AstrBot 桌面版诊断包（已脱敏：密码 / 密钥 / token 一律替换成 ***）");
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("程序目录: " + appDir);
                sb.AppendLine();
                sb.AppendLine("==== 当前状态 ====");
                sb.AppendLine(Redact.Text(stateLine));
                sb.AppendLine();
                sb.AppendLine("==== 自检输出 ====");
                sb.AppendLine(Redact.Text(selfCheck));
                sb.AppendLine();
                sb.AppendLine("==== 日志尾部 ====");
                sb.AppendLine(Redact.Text(logTail));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
            catch (Exception exc)
            {
                return "导出失败：" + exc.Message;
            }
        }
    }
}
