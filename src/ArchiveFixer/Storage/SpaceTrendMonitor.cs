using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Storage
{
    /// <summary>一针空间采样：什么时候、那块盘还剩多少、当时在干什么。</summary>
    public sealed class SpaceTrendSample
    {
        public DateTime At { get; init; }

        /// <summary>可用字节数（&lt; 0 = 取不到）。</summary>
        public long FreeBytes { get; init; }

        /// <summary>为什么采这一针（"批首" / "每 5 秒" / "「x.7z」开工"…）——进日志用。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>一次**有意义的变化**（够大、够久才算，免得刷屏）。</summary>
    public sealed class SpaceTrendChange
    {
        public SpaceTrendSample Previous { get; init; } = new();

        public SpaceTrendSample Current { get; init; } = new();

        /// <summary>当前 − 上一针（负数 = 空间在变少）。</summary>
        public long DeltaBytes => Current.FreeBytes - Previous.FreeBytes;

        public TimeSpan Since => Current.At - Previous.At;
    }

    /// <summary>
    /// **空间变化侦察**（用户 2026-09-27："你要时刻弄空间检测" / "同样也要弄空间检查技术"）。
    ///
    /// <para>他的场景：源包会不会被删、内容物长到多大、盘什么时候见底 —— 这些只有把**可用空间随时间的变化**
    /// 记下来才看得清（只看"开工前"和"收工后"两个点，中间那一段是不是差点写满盘永远不知道）。</para>
    ///
    /// <para><b>三条纪律</b>：</para>
    /// <list type="number">
    /// <item><description><b>只观察、不判断</b>：它不参与放行、不参与调度 —— 拦人的仍然是每个任务启动前的空间门。
    /// 侦察一旦变成判据，就会出现"日志与行为两套口径"。</description></item>
    /// <item><description><b>只说有意义的变化</b>：默认要"变化 ≥ 64 MiB 且 ≥ 1%"才报一行，
    /// 否则一批几十个任务会刷出几百行同义反复（用户 2026-09-26 第 45 条的教训：一条提示刷 74 遍）。</description></item>
    /// <item><description><b>取不到就说取不到</b>：探测失败（盘没就绪 / 路径非法）时记 `< 0` 的样本，
    /// 报告里如实写"这几针取不到"，绝不拿 0 或上一次的数字充数。</description></item>
    /// </list>
    ///
    /// <para>本类是**纯逻辑**（不碰 WPF、不碰时钟）：采样由调用方喂进来（<see cref="Record"/>），
    /// 周期采样那个循环在 <see cref="RunAsync"/> 里，探测函数也由调用方给 —— 所以单测可以精确摆布。</para>
    /// </summary>
    public sealed class SpaceTrendMonitor
    {
        /// <summary>默认的"值得报一行"的阈值：64 MiB。</summary>
        public const long DefaultNotifyThresholdBytes = 64L * 1024 * 1024;

        /// <summary>默认的"值得报一行"的相对阈值：1%。</summary>
        public const double DefaultNotifyThresholdPercent = 1.0d;

        private readonly object _sync = new();
        private readonly List<SpaceTrendSample> _samples = new();
        private readonly Func<long?> _probe;
        private readonly long _thresholdBytes;
        private readonly double _thresholdPercent;

        private SpaceTrendSample? _lastNotified;

        /// <param name="probe">探测可用空间（返回 null = 取不到）。与解压管线用**同一个**探测函数。</param>
        /// <param name="thresholdBytes">"值得报一行"的绝对阈值（null = <see cref="DefaultNotifyThresholdBytes"/>）。</param>
        /// <param name="thresholdPercent">"值得报一行"的相对阈值（null = <see cref="DefaultNotifyThresholdPercent"/>）。</param>
        public SpaceTrendMonitor(
            Func<long?> probe,
            long? thresholdBytes = null,
            double? thresholdPercent = null)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _thresholdBytes = thresholdBytes is > 0 ? thresholdBytes.Value : DefaultNotifyThresholdBytes;
            _thresholdPercent = thresholdPercent is > 0 ? thresholdPercent.Value : DefaultNotifyThresholdPercent;
        }

        /// <summary>已经采了几针。</summary>
        public int SampleCount
        {
            get
            {
                lock (_sync)
                {
                    return _samples.Count;
                }
            }
        }

        /// <summary>
        /// 采一针（**唯一的入口**；周期循环与"某任务开工 / 收尾"都走它）。
        /// </summary>
        /// <param name="note">这一针的来由（进日志 / 进曲线）。</param>
        /// <param name="at">采样时刻（不传 = 现在；单测可注入）。</param>
        /// <param name="notify">
        /// 这一针要不要**单独报一行**（默认 true）。
        ///
        /// <para>⚠ 任务开工 / 收尾那两针传 false：它们的 note 里带**任务文件名**，而"成功的任务只留一行"
        /// 是第 45 条定下的日志纪律（`Item45LogAndPasswordTests` 用"含该文件名的行数 ≤ 3"钉着它）——
        /// 多报一行就多一行带文件名的记录，那条纪律当场失效。
        /// 曲线不受影响：这一针照样进采样表（最低点的注脚仍然是"「x.7z」收尾"），
        /// 只是不在过程里刷那一行。</para>
        /// </param>
        /// <returns>这一针**值得报一行**时返回变化详情，否则 null（已经记进采样表，只是不必写日志）。</returns>
        public SpaceTrendChange? Record(string note, DateTime? at = null, bool notify = true)
        {
            long? free = null;

            try
            {
                free = _probe();
            }
            catch
            {
                // 探测本身抛异常 = 取不到（与返回 null 同一口径）：绝不因此打断解压。
                free = null;
            }

            var sample = new SpaceTrendSample
            {
                At = at ?? DateTime.Now,
                FreeBytes = free ?? SpaceReservationLedger.UnknownAvailable,
                Note = note ?? string.Empty
            };

            lock (_sync)
            {
                _samples.Add(sample);

                if (sample.FreeBytes < 0)
                {
                    // 取不到的那一针不进"有意义的变化"比较（拿它做基准会把下一针算成一个巨大的变化）。
                    return null;
                }

                if (!notify)
                {
                    /*
                     * 只记不报：**刻意不动 `_lastNotified`** —— 下一次要报的那一针因此仍然对着
                     * "上次报过的那个数"算变化，用户看到的差值始终是"自上次报数以来"的净值。
                     */
                    return null;
                }

                SpaceTrendSample? baseline = _lastNotified;

                if (baseline == null || baseline.FreeBytes < 0)
                {
                    _lastNotified = sample;

                    // 第一针永远值得报（"现在还剩多少"是后面所有变化的基准）。
                    return baseline == null
                        ? new SpaceTrendChange { Previous = sample, Current = sample }
                        : null;
                }

                long delta = sample.FreeBytes - baseline.FreeBytes;
                long magnitude = Math.Abs(delta);
                double percent = baseline.FreeBytes > 0
                    ? magnitude * 100.0d / baseline.FreeBytes
                    : 0d;

                if (magnitude < _thresholdBytes || percent < _thresholdPercent)
                {
                    return null;
                }

                _lastNotified = sample;

                return new SpaceTrendChange { Previous = baseline, Current = sample };
            }
        }

        /// <summary>把一次变化说成一行日志。</summary>
        public static string DescribeChange(SpaceTrendChange change, string subject)
        {
            long delta = change.DeltaBytes;
            string direction = delta < 0 ? "少了" : "多了";
            double percent = change.Previous.FreeBytes > 0
                ? Math.Abs(delta) * 100.0d / change.Previous.FreeBytes
                : 0d;

            var builder = new StringBuilder();

            builder.Append("空间变化：");
            builder.Append(string.IsNullOrWhiteSpace(subject) ? "目标盘" : subject);
            builder.Append(" 可用 ");
            builder.Append(TaskSpaceEstimate.FormatSize(change.Current.FreeBytes));
            builder.Append($"（比上一针{direction} {TaskSpaceEstimate.FormatSize(Math.Abs(delta))}");

            if (percent >= 0.1d)
            {
                builder.Append($"，{percent:F1}%");
            }

            if (change.Since > TimeSpan.Zero && change.Previous.At != change.Current.At)
            {
                builder.Append($"，间隔 {DescribeSpan(change.Since)}");
            }

            builder.Append('）');

            if (!string.IsNullOrWhiteSpace(change.Current.Note) &&
                !string.Equals(change.Current.Note, change.Previous.Note, StringComparison.Ordinal))
            {
                builder.Append(" —— ").Append(change.Current.Note);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 批末的空间曲线（**这是这一批"空间到底怎么变的"的唯一交代**）：
        /// 起 / 最低 / 收 + 最多同时占用多少 + 采样与报告次数。
        /// </summary>
        public IReadOnlyList<string> DescribeReport()
        {
            var lines = new List<string>();

            SpaceTrendSample[] samples;
            SpaceTrendSample? first;
            SpaceTrendSample? last;
            SpaceTrendSample? low;
            SpaceTrendSample? high;

            lock (_sync)
            {
                samples = _samples.ToArray();

                if (samples.Length == 0)
                {
                    return lines;
                }

                (first, last, low, high) = ResolveExtremes(samples);
            }

            int unavailable = 0;

            foreach (SpaceTrendSample sample in samples)
            {
                if (sample.FreeBytes < 0)
                {
                    unavailable++;
                }
            }

            if (first == null || last == null || low == null || high == null)
            {
                lines.Add($"空间曲线：本批采了 {samples.Length} 针，但一针都没取到可用空间（探测一直失败）—— 这一批没有空间曲线可看。");
                return lines;
            }

            long used = Math.Max(0L, first.FreeBytes - low.FreeBytes);

            lines.Add(
                $"空间曲线（本批 {samples.Length} 针）：起 {TaskSpaceEstimate.FormatSize(first.FreeBytes)}"
                + $" → 最低 {TaskSpaceEstimate.FormatSize(low.FreeBytes)}（{low.At:HH:mm:ss}，{low.Note}）"
                + $" → 收 {TaskSpaceEstimate.FormatSize(last.FreeBytes)}"
                + $"，全程最多同时占用约 {TaskSpaceEstimate.FormatSize(used)}。");

            if (unavailable > 0)
            {
                lines.Add($"（其中 {unavailable} 针取不到可用空间 —— 如实记下，没有拿别的数字充数。）");
            }

            return lines;
        }

        /// <summary>
        /// 批末那条曲线**之后**再补一针真实可用空间时说的一句话（用户 2026-10-02 真机）。
        ///
        /// <para><b>为什么必须有它</b>：批末"收"那一针打的时候，这一批的源包 / 过程物**还在盘上**
        /// （其余物的删除发生在那之后）—— 于是"收"比真实可用少一大截。真机那批：曲线写
        /// 「起 31.85 GiB → 最低 20.55 GiB → 收 26.3 GiB」，随后 10 份其余物被彻底删除（合计约 6 GB）
        /// ⇒ 真实收尾约 32.4 GiB（那一批其实净省 0.5 GB，按日志读却像净吃掉 5.5 GB）。</para>
        ///
        /// <para>⚠ 调用方必须先自己采一针（<see cref="Record"/>，走的是**同一个探测函数**）；
        /// 这个方法只负责把它说成一行 —— 起 / 最低 与 <see cref="DescribeReport"/> 用同一份采样表、
        /// 同一套字（⛔ 不是第二套取数、也不是第二套曲线文案）。</para>
        /// </summary>
        /// <param name="note">这一针的来由（例如"其余物处理之后"）。</param>
        /// <returns>那一行；一次都没采到可用空间时返回 null（如实不写，绝不拿别的数字充数）。</returns>
        public string? DescribePostscript(string note)
        {
            SpaceTrendSample[] samples;

            lock (_sync)
            {
                samples = _samples.ToArray();
            }

            (SpaceTrendSample? first, SpaceTrendSample? last, SpaceTrendSample? low, _) = ResolveExtremes(samples);

            if (first == null || last == null || low == null)
            {
                return null;
            }

            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Models.StatusText.SpaceCurvePostscriptFormat,
                note ?? string.Empty,
                TaskSpaceEstimate.FormatSize(last.FreeBytes),
                TaskSpaceEstimate.FormatSize(first.FreeBytes),
                TaskSpaceEstimate.FormatSize(low.FreeBytes));
        }

        /// <summary>从采样表里取"起 / 收 / 最低 / 最高"（取不到可用空间的那几针跳过）。</summary>
        private static (SpaceTrendSample? First, SpaceTrendSample? Last, SpaceTrendSample? Low, SpaceTrendSample? High)
            ResolveExtremes(IReadOnlyList<SpaceTrendSample> samples)
        {
            SpaceTrendSample? first = null;
            SpaceTrendSample? last = null;
            SpaceTrendSample? low = null;
            SpaceTrendSample? high = null;

            foreach (SpaceTrendSample sample in samples)
            {
                if (sample.FreeBytes < 0)
                {
                    continue;
                }

                first ??= sample;
                last = sample;

                if (low == null || sample.FreeBytes < low.FreeBytes)
                {
                    low = sample;
                }

                if (high == null || sample.FreeBytes > high.FreeBytes)
                {
                    high = sample;
                }
            }

            return (first, last, low, high);
        }

        /// <summary>
        /// 周期采样循环（调用方用 <see cref="CancellationTokenSource"/> 停它）。
        ///
        /// <para>⚠ 这个循环**只为观察而生**：任何一针失败都只影响那一针，
        /// 绝不允许把异常抛回解压流程（所以整体 try/catch，取消正常退出）。</para>
        /// </summary>
        public async Task RunAsync(
            TimeSpan interval,
            Action<string> report,
            CancellationToken cancellationToken)
        {
            if (interval <= TimeSpan.Zero)
            {
                interval = TimeSpan.FromSeconds(5);
            }

            string note = $"每 {interval.TotalSeconds:0} 秒";

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    SpaceTrendChange? change = Record(note);

                    if (change != null && report != null)
                    {
                        report(DescribeChange(change, "目标盘"));
                    }
                }
                catch (Exception ex)
                {
                    // 侦察自己出问题绝不许影响解压：写一行就继续。
                    report?.Invoke("空间侦察这一针失败（不影响解压）：" + ex.Message);
                }
            }
        }

        private static string DescribeSpan(TimeSpan span)
        {
            if (span.TotalSeconds < 60)
            {
                return $"{span.TotalSeconds:F0} 秒";
            }

            if (span.TotalMinutes < 60)
            {
                return $"{span.TotalMinutes:F1} 分钟";
            }

            return $"{span.TotalHours:F1} 小时";
        }
    }
}
