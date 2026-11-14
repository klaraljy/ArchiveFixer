using System;
using System.Diagnostics;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 直读解压的**进度节流器**：按"已写出字节 / 清单里的总原始字节"算百分比，≥ 每 250 ms 报一次。
///
/// <para>为什么单独一个类：几条写入路径（stored / deflate / AES）都要累计字节并按同一节奏上报，
/// 而"节流窗口 + 累计量 + 结束时的 100%"必须只有一份实现 —— 各处各写一遍迟早出现
/// "一条路报 100、另一条停在 99"。进度只是显示：接收端抛异常绝不许把解压带下水。</para>
///
/// <para>⛔ 不碰盘、不改任何结论：只读累计量。</para>
/// </summary>
internal sealed class EmbeddedZipProgressReporter
{
    /// <summary>进度最小间隔：250 ms（与既有进度口径一致，不刷日志也不至于看着像卡住）。</summary>
    private const int ProgressIntervalMs = 250;

    private readonly IProgress<int>? _progress;
    private readonly Stopwatch _stopwatch;
    private readonly long _totalBytes;
    private long _nextReportAtMs;
    private long _writtenBytes;

    private EmbeddedZipProgressReporter(IProgress<int>? progress, long totalBytes)
    {
        _progress = progress;
        _totalBytes = totalBytes;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>已写出的字节数（成功结论里的"解出 N 字节"用的就是它）。</summary>
    internal long WrittenBytes => _writtenBytes;

    /// <summary>开始一轮解压：起表并先报 0（总大小为 0 时也照样报 0，不除以零）。</summary>
    internal static EmbeddedZipProgressReporter Start(IProgress<int>? progress, long totalBytes)
    {
        var reporter = new EmbeddedZipProgressReporter(progress, totalBytes);

        reporter.ReportPercent(0);

        return reporter;
    }

    /// <summary>
    /// 记入本次写出的字节，并在节流窗口到期时上报一次。
    /// （<paramref name="deltaBytes"/> 为 0 也允许：语义与"写循环里每块都调一次"的原口径一致。）
    /// </summary>
    internal void Record(long deltaBytes)
    {
        _writtenBytes += deltaBytes;

        if (_progress == null)
        {
            return;
        }

        long elapsed = _stopwatch.ElapsedMilliseconds;

        if (elapsed < _nextReportAtMs)
        {
            return;
        }

        _nextReportAtMs = elapsed + ProgressIntervalMs;

        ReportPercent(_totalBytes <= 0 ? 0 : (int)Math.Min(100, _writtenBytes * 100 / _totalBytes));
    }

    /// <summary>收尾：无条件报 100。</summary>
    internal void Complete()
    {
        ReportPercent(100);
    }

    private void ReportPercent(int percent)
    {
        if (_progress == null)
        {
            return;
        }

        try
        {
            _progress.Report(percent < 0 ? 0 : percent > 100 ? 100 : percent);
        }
        catch
        {
            // 接收端炸了不该把解压带下水（进度只是显示）。
        }
    }
}
