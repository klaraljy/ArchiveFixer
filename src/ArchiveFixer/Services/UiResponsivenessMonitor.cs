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
        private static int _stallCount;

        public static void Mark(string phase, string task = "", string detail = "")
        {
            _phase = phase ?? string.Empty;
            _task = task ?? string.Empty;
            _detail = detail ?? string.Empty;
        }

        /// <summary>当前没有实际任务在跑；空闲阶段的窗口停顿不是“卡死”。</summary>
        public static bool IsIdle => string.Equals(_phase, "空闲", StringComparison.Ordinal);

        /// <summary>记一次 UI 停顿（供自适应并发控制器读取；本身不改变任何行为）。</summary>
        public static void NoteUiStall() => System.Threading.Interlocked.Increment(ref _stallCount);

        /// <summary>取走并清零停顿计数（只有调度线程调用）。</summary>
        public static int TakeUiStallCount() => System.Threading.Interlocked.Exchange(ref _stallCount, 0);

        public static string Snapshot()
        {
            string task = _task.Length > 0 ? $" ｜ 任务={_task}" : string.Empty;
            string detail = _detail.Length > 0 ? $" ｜ 操作={_detail}" : string.Empty;
            return $"阶段={_phase}{task}{detail}";
        }
    }

    /// <summary>
    /// UI 响应心跳：DispatcherTimer 每 250ms 走一次。
    /// 超过 500ms 只作为控制器输入；超过 1.5 秒才写 WARN，且同一段最多 30 秒汇总一次。
    /// 空闲阶段不统计、不写日志。
    /// </summary>
    internal sealed class UiResponsivenessMonitor : IDisposable
    {
        private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan WarnThreshold = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan WarnInterval = TimeSpan.FromSeconds(30);

        private readonly DispatcherTimer _timer;
        private readonly Action<string> _warn;
        private readonly Stopwatch _sinceTick = Stopwatch.StartNew();
        private bool _disposed;
        private int _stallCount;
        private double _maxGapMs;
        private DateTime _lastWarnUtc = DateTime.MinValue;

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

            if (gap < StallThreshold || UiActivity.IsIdle)
            {
                return;
            }

            UiActivity.NoteUiStall();
            _stallCount++;
            _maxGapMs = Math.Max(_maxGapMs, gap.TotalMilliseconds);

            if (gap < WarnThreshold || DateTime.UtcNow - _lastWarnUtc < WarnInterval)
            {
                return;
            }

            _lastWarnUtc = DateTime.UtcNow;

            try
            {
                _warn(
                    $"[性能] UI 线程明显停顿：本次 {gap.TotalMilliseconds:N0} 毫秒，"
                    + $"本段累计 {_stallCount} 次 / 最大 {_maxGapMs:N0} 毫秒 ｜ "
                    + UiActivity.Snapshot());
                _stallCount = 0;
                _maxGapMs = 0;
            }
            catch
            {
                // 探针失败不许影响主流程。
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
        }
    }
}
