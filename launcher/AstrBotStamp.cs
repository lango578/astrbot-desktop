// 构建戳与 UI 线程 I/O 抓奸器。
//
// 构建戳：用户反馈「还是卡」时，第一个要确认的事实是他跑的是哪一份 —— 没有版本标识就只能靠猜。
// build\make_stamp.py 在每次编译前把这份文件重写一遍，标题栏与诊断包里都带着它。
//
// I/O 抓奸器：凡是"可能在 UI 线程上做网络或起进程"的地方都调 UiGuard.Note(...)。
// 它不改行为，只留证据：一旦有人在按钮处理里同步探端口、同步问面板，
// 诊断包里就会出现「UI 线程上做过的 I/O：…」这一行，归因不用再靠复现。
using System;
using System.Threading;

namespace AstrBotDesktop
{
    internal static class Build
    {
        public const string Stamp = "1030-91ad76c5";
    }

    internal static class UiGuard
    {
        private static readonly object Sync = new object();
        private static string _violations = "";

        public static int UiThread;

        public static bool OnUiThread()
        {
            return UiThread != 0 && Thread.CurrentThread.ManagedThreadId == UiThread;
        }

        public static void Note(string what)
        {
            if (!OnUiThread())
            {
                return;
            }
            lock (Sync)
            {
                if (_violations.IndexOf(what, StringComparison.Ordinal) < 0)
                {
                    _violations = _violations.Length == 0 ? what : _violations + " · " + what;
                }
            }
        }

        public static string Violations
        {
            get
            {
                lock (Sync)
                {
                    return _violations;
                }
            }
        }

        public static void Reset()
        {
            lock (Sync)
            {
                _violations = "";
            }
        }
    }
}
