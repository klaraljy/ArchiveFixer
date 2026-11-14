using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace ArchiveFixer.Services;


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
