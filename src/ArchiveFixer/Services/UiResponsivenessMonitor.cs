using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// UI 线程最后活动标记。任意线程都可写；心跳计时器只读，用来在日志里回答
    /// “卡住的那一刻程序正在做什么”。
    /// </summary>
    public static class UiActivity
    {
        private static string _phase = "空闲";
        private static string _task = string.Empty;
        private static string _detail = string.Empty;

        public static void Mark(string phase, string task = "", string detail = "")
        {
            _phase = phase ?? string.Empty;
            _task = task ?? string.Empty;
            _detail = detail ?? string.Empty;
        }

        public static string Snapshot()
        {
            string task = _task.Length > 0 ? $" ｜ 任务={_task}" : string.Empty;
            string detail = _detail.Length > 0 ? $" ｜ 操作={_detail}" : string.Empty;
            return $"阶段={_phase}{task}{detail}";
        }
    }

    /// <summary>
    /// UI 响应心跳：DispatcherTimer 每 250ms 走一次。两次心跳之间超过 1.5 秒
    /// 就说明 UI 线程没有处理消息 —— 在日志里写 WARN，并带上最后活动标记。
    ///
    /// <para>它不会“防止”UI 卡住；防卡住靠把同步磁盘/CPU 活挪出 UI 线程。
    /// 这个探针只负责让用户日志能定位到卡在哪一段，不用每次开调试器。</para>
    /// </summary>
    internal sealed class UiResponsivenessMonitor : IDisposable
    {
        private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(1.5);

        private readonly DispatcherTimer _timer;
        private readonly Action<string> _warn;
        private readonly Stopwatch _sinceTick = Stopwatch.StartNew();
        private bool _disposed;

        private UiResponsivenessMonitor(Dispatcher dispatcher, Action<string> warn)
        {
            _warn = warn;

            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                (_, _) => OnTick(),
                dispatcher);
        }

        public static UiResponsivenessMonitor Start(Dispatcher? dispatcher, Action<string> warn)
        {
            var monitor = new UiResponsivenessMonitor(
                dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)),
                warn ?? throw new ArgumentNullException(nameof(warn)));

            monitor._timer.Start();
            return monitor;
        }

        private void OnTick()
        {
            if (_disposed)
            {
                return;
            }

            TimeSpan gap = _sinceTick.Elapsed;
            _sinceTick.Restart();

            if (gap >= StallThreshold)
            {
                try
                {
                    _warn(
                        $"[性能] UI 线程停顿 {gap.TotalMilliseconds:N0} 毫秒 ｜ "
                        + UiActivity.Snapshot());
                }
                catch
                {
                    // 探针失败不许影响主流程。
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
        }
    }
}
