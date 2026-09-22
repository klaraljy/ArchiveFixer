using System;
using System.Diagnostics;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎进度的**节流器**：把又密又碎的进度行压成"最多每 250ms 一条"。
    ///
    /// <para>
    /// 为什么必须节流（这是本项目踩过的坑）：7-Zip 的进度行在有输出时是**每次进度刷新一行**
    /// （实测 250MB 的包一次解压能刷出上百条），UnRAR 更细 —— 同一个文件内每 1% 就回退重写一次。
    /// 这些行原本只是打到控制台，现在要一路投递到 WPF 的绑定上；
    /// 不节流就是把这个历史性的"UI 线程被重活刷爆"重新演一遍。
    /// </para>
    ///
    /// <para><b>节流规则</b>（两条取其一，且都要过时间闸）：</para>
    /// <list type="number">
    /// <item><description>距上次上报 ≥ <see cref="MinInterval"/>（默认 250ms）且状态有变化；</description></item>
    /// <item><description>百分比涨了 ≥ <see cref="MinPercentDelta"/>（默认 1 个百分点）
    /// 且距上次上报 ≥ <see cref="MinPercentInterval"/>（默认 100ms）——
    /// 让慢速大包的百分比不会因为第一条规则而显得"一跳一跳"。</description></item>
    /// </list>
    ///
    /// <para><b>生命周期</b>：<see cref="Complete"/> 之后<b>一律丢弃</b>。
    /// 引擎运行器在进程退出 / 取消 / 超时之后、返回之前必定调用它，
    /// 所以"迟到回调"不可能落到已经收尾的任务上（这正是本项要求的红线之一）。</para>
    ///
    /// <para><b>不阻塞</b>：<see cref="Report"/> 只做常数级比较，不碰磁盘、不等锁以外的任何东西；
    /// 接收端抛出的任何异常都被吞掉 —— 进度上报失败绝不能影响解压本身。</para>
    /// </summary>
    public sealed class ArchiveProgressReporter : IDisposable
    {
        /// <summary>默认最短上报间隔。250ms ≈ 4 次/秒，肉眼连续、UI 无压力。</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>"百分比涨了 1 个点"这条快路径的最短间隔。</summary>
        public static readonly TimeSpan MinPercentInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>走快路径至少要有几个百分点的变化。</summary>
        public const int MinPercentDelta = 1;

        private readonly IProgress<ArchiveProgress>? _sink;
        private readonly TimeSpan _minInterval;
        private readonly TimeSpan _minPercentInterval;
        private readonly int _minPercentDelta;
        private readonly Func<long> _timestampProvider;

        private readonly object _gate = new();

        private bool _completed;
        private bool _hasReported;
        private long _lastReportTimestamp;
        private int _lastReportedPercent = ArchiveProgress.UnknownPercent;
        private string _lastReportedEntry = string.Empty;
        private int _reportedCount;

        public ArchiveProgressReporter(IProgress<ArchiveProgress>? sink)
            : this(sink, MinInterval, MinPercentInterval, MinPercentDelta, Stopwatch.GetTimestamp)
        {
        }

        /// <summary>
        /// 全参数构造（测试用）：时间源可注入，节流窗口可缩短，因此"高频序列只上报很少几次"
        /// 这件事可以被**确定性**地断言，而不需要靠 sleep 去撞时序。
        /// </summary>
        public ArchiveProgressReporter(
            IProgress<ArchiveProgress>? sink,
            TimeSpan minInterval,
            TimeSpan minPercentInterval,
            int minPercentDelta,
            Func<long>? timestampProvider)
        {
            _sink = sink;
            _minInterval = minInterval < TimeSpan.Zero ? TimeSpan.Zero : minInterval;
            _minPercentInterval = minPercentInterval < TimeSpan.Zero ? TimeSpan.Zero : minPercentInterval;
            _minPercentDelta = Math.Max(1, minPercentDelta);
            _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        }

        /// <summary>真的投递出去过几次（测试与诊断用；被节流丢掉的不算）。</summary>
        public int ReportedCount
        {
            get
            {
                lock (_gate)
                {
                    return _reportedCount;
                }
            }
        }

        /// <summary>是否已经收口（收口之后所有上报都会被丢弃）。</summary>
        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        /// <summary>
        /// 喂一条进度。返回 true 表示这一次真的投递出去了。
        ///
        /// ⚠ 只允许从读取进程输出的那条循环里调用，且**不允许**在这里做任何阻塞动作。
        /// </summary>
        public bool Report(ArchiveProgress? progress)
        {
            if (progress == null)
            {
                return false;
            }

            bool deliver;

            lock (_gate)
            {
                if (_completed)
                {
                    // 进程已经退出 / 已取消：迟到的一律丢弃（红线：不得再有迟到回调）。
                    return false;
                }

                long now = _timestampProvider();
                bool sameAsLast = progress.Percent == _lastReportedPercent &&
                                  string.Equals(progress.CurrentEntry, _lastReportedEntry, StringComparison.Ordinal);

                if (_hasReported && sameAsLast)
                {
                    // 状态没变（只是引擎又刷了一遍同样的进度）：连时间闸都不用看。
                    return false;
                }

                if (!_hasReported)
                {
                    deliver = true;
                }
                else
                {
                    long sinceLast = now - _lastReportTimestamp;

                    /*
                     * 上一次只报过"在处理谁、不知道到哪了"（扫描行 / UnRAR 的条目行）时，
                     * 这一条百分比必须能立刻补上 —— 否则最坏要等满 250ms，
                     * 一个跑得很快的任务会**一条百分比都报不出去**（实测踩到过）。
                     */
                    bool percentAdvanced = progress.Percent >= 0 &&
                                           (_lastReportedPercent < 0 ||
                                            progress.Percent - _lastReportedPercent >= _minPercentDelta);

                    deliver = sinceLast >= ToTimestampTicks(_minInterval) ||
                              (percentAdvanced && sinceLast >= ToTimestampTicks(_minPercentInterval));
                }

                if (!deliver)
                {
                    return false;
                }

                _hasReported = true;
                _lastReportTimestamp = now;
                _lastReportedPercent = progress.Percent;
                _lastReportedEntry = progress.CurrentEntry ?? string.Empty;
                _reportedCount++;
            }

            // 接收端在锁外调用：进度接收端自己也不许阻塞（约定见 IProgress 的用法），
            // 而且它抛异常绝不能把读取循环带崩。
            try
            {
                _sink?.Report(progress);
            }
            catch
            {
                // 进度上报失败与解压结论无关，静默丢弃。
            }

            return true;
        }

        /// <summary>
        /// 收口：此后所有上报都被丢弃。引擎运行器在返回之前**必定**调用一次
        /// （进程退出 / 取消 / 超时 / 异常四条路径都要经过这里）。
        /// </summary>
        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
            }
        }

        public void Dispose()
        {
            Complete();
        }

        private static long ToTimestampTicks(TimeSpan span)
        {
            double ticks = span.TotalSeconds * Stopwatch.Frequency;

            if (ticks <= 0)
            {
                return 0;
            }

            return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
        }
    }
}
