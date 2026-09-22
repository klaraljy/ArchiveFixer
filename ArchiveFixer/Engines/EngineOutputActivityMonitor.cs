using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// "这个外部进程是不是已经很久没有任何输出了"的监视器（**只报警，不杀进程**）。
    ///
    /// <para>
    /// 为什么需要它：用户反复抱怨"卡死"，而界面只有"处理中/完成"两态。
    /// 真实场景里有一个 780MB 的固实包，7-Zip 在解同一个固实块时可能几十秒刷不出新进度，
    /// 或者磁盘被别的程序占满导致写入停滞 —— 这时"零输出 + 看起来没动静"与"真的死了"
    /// 在用户眼里完全一样。我们做的**不是**替用户判断，而是把"多久没有输出了"这个事实说出来，
    /// 让他自己决定要不要点「取消当前」（取消语义只归用户，见不变量 9）。
    /// </para>
    ///
    /// <para>
    /// ⛔ <b>本类型没有任何杀进程的能力</b>，也不持有 <c>Process</c> 对象 ——
    /// "不会误杀"在设计上就成立，不靠调用方自觉。
    /// </para>
    ///
    /// <para>
    /// 时间源可注入（<c>clock</c>），所以阈值判定可以被确定性地单测，不需要 sleep。
    /// 同一段沉默**只报一次**；一旦又有输出，下一段沉默可以再报一次。
    /// </para>
    /// </summary>
    public sealed class EngineOutputActivityMonitor
    {
        /// <summary>默认阈值：90 秒。低于这个数量级的沉默在真实解压里很常见，报出来只会变成噪声。</summary>
        public static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(90);

        /// <summary>看门狗默认巡查间隔。它只做几次减法，与进程输出量无关。</summary>
        public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

        /// <summary>巡查间隔的下限（测试会用到毫秒级阈值，别把巡查变成忙等）。</summary>
        public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(20);

        /// <summary>
        /// 按阈值算巡查间隔：默认 5 秒，但阈值比它还短时取阈值的一半
        /// （否则"阈值 1 秒、5 秒才看一眼"会让提示最迟晚 5 秒才出来）。
        /// </summary>
        public static TimeSpan ResolvePollInterval(TimeSpan stallThreshold)
        {
            if (stallThreshold <= TimeSpan.Zero)
            {
                return DefaultPollInterval;
            }

            TimeSpan half = TimeSpan.FromTicks(stallThreshold.Ticks / 2);

            if (half >= DefaultPollInterval)
            {
                return DefaultPollInterval;
            }

            return half < MinPollInterval ? MinPollInterval : half;
        }

        private readonly TimeSpan _stallThreshold;
        private readonly Action<ArchiveStallNotice>? _onStall;
        private readonly Func<DateTime> _clock;
        private readonly object _gate = new();

        private DateTime _lastActivity;
        private bool _stallReportedForThisEpisode;
        private bool _completed;
        private int _stallCount;

        public EngineOutputActivityMonitor(
            TimeSpan stallThreshold,
            Action<ArchiveStallNotice>? onStall = null,
            Func<DateTime>? clock = null)
        {
            _stallThreshold = stallThreshold > TimeSpan.Zero
                ? stallThreshold
                : DefaultStallThreshold;
            _onStall = onStall;
            _clock = clock ?? (() => DateTime.UtcNow);
            _lastActivity = _clock();
        }

        /// <summary>已经报过几次"长时间无输出"。</summary>
        public int StallCount
        {
            get
            {
                lock (_gate)
                {
                    return _stallCount;
                }
            }
        }

        /// <summary>距离最后一次输出过去了多久。</summary>
        public TimeSpan IdleFor
        {
            get
            {
                lock (_gate)
                {
                    return _clock() - _lastActivity;
                }
            }
        }

        /// <summary>引擎又吐了一点东西出来（**任何**输出都算，包括进度行）。</summary>
        public void MarkActivity()
        {
            lock (_gate)
            {
                _lastActivity = _clock();
                _stallReportedForThisEpisode = false;
            }
        }

        /// <summary>
        /// 巡查一次。返回 true 表示"这一次刚好判定为长时间无输出并报了出去"。
        ///
        /// <para>
        /// 看门狗循环只调用它；测试也用同一个入口 + 假时钟做确定性断言。
        /// </para>
        /// </summary>
        public bool CheckForStall()
        {
            if (_onStall == null)
            {
                return false;
            }

            TimeSpan idle;

            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }

                idle = _clock() - _lastActivity;

                if (idle < _stallThreshold || _stallReportedForThisEpisode)
                {
                    return false;
                }

                _stallReportedForThisEpisode = true;
                _stallCount++;
            }

            // 回调在锁外：它要写日志 / 投递到 UI，不该把巡查锁住，更不该因为它卡住而让看门狗停摆。
            try
            {
                _onStall(new ArchiveStallNotice
                {
                    Idle = idle,
                    Threshold = _stallThreshold,
                    ProcessStillRunning = true
                });
            }
            catch
            {
                // 提示发不出去不影响引擎继续跑。
            }

            return true;
        }

        /// <summary>
        /// 进程已经结束（正常退出 / 取消 / 超时）：此后不再报任何提示。
        /// 与 <see cref="ArchiveProgressReporter.Complete"/> 同一个作用 —— 掐断迟到回调。
        /// </summary>
        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
            }
        }

        /// <summary>
        /// 看门狗：按 <paramref name="pollInterval"/> 巡查，直到令牌取消。
        ///
        /// <para>
        /// 它由运行器在起进程之前启动、在返回之前 await 掉，所以**不可能**在运行器返回之后
        /// 还活着去碰上层状态。
        /// </para>
        /// </summary>
        public static async Task WatchAsync(
            EngineOutputActivityMonitor monitor,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            if (monitor == null)
            {
                return;
            }

            TimeSpan interval = pollInterval > TimeSpan.Zero
                ? pollInterval
                : DefaultPollInterval;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                    monitor.CheckForStall();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常收口路径。
            }
            catch
            {
                // 看门狗自身出问题绝不允许影响解压结论。
            }
        }
    }
}
