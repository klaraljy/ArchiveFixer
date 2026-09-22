using System;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 一次归档操作的**统一进度**（AGENTS.md §3.1：统一进度是 M1 骨架的一部分）。
    ///
    /// <para>
    /// 为什么必须只有这一个类型：7-Zip 与 UnRAR 的进度输出形态完全不同
    /// （7z 是 <c>\r</c> 分隔的「百分比 条目序号 命令 条目名」，UnRAR 是同一行里用退格回退的
    /// 「Extracting 路径 … 12%」），但**上层只认这一个类型**。
    /// 各引擎的解析器只允许待在自己的目录里（§3.1 禁止项②），
    /// 归一化后的结果全部走这里。
    /// </para>
    ///
    /// <para>
    /// ⛔ 它<b>不</b>携带密码，也<b>不</b>携带完整输出：只有"到什么程度了"。
    /// </para>
    /// </summary>
    public sealed class ArchiveProgress
    {
        /// <summary>百分比未知（例如 7z 还在扫描阶段、或引擎这一行没给百分比）。</summary>
        public const int UnknownPercent = -1;

        /// <summary>0–100；未知时是 <see cref="UnknownPercent"/>。**不要**把未知当 0。</summary>
        public int Percent { get; init; } = UnknownPercent;

        /// <summary>当前正在处理的条目名（引擎给的是相对/绝对路径都可能；没有时为空串）。</summary>
        public string CurrentEntry { get; init; } = string.Empty;

        /// <summary>已处理字节；引擎不报时为 <see cref="UnknownBytes"/>。</summary>
        public long ProcessedBytes { get; init; } = UnknownBytes;

        /// <summary>总字节；引擎不报时为 <see cref="UnknownBytes"/>。</summary>
        public long TotalBytes { get; init; } = UnknownBytes;

        /// <summary>已处理条目数；引擎不报时为 <see cref="ArchiveProgress.UnknownCount"/>。</summary>
        public int ProcessedEntries { get; init; } = UnknownCount;

        /// <summary>"字节数未知"哨兵。用 -1 而不是 0：0 是"确实一个字节都没处理"。</summary>
        public const long UnknownBytes = -1;

        /// <summary>"条目数未知"哨兵。</summary>
        public const int UnknownCount = -1;

        /// <summary>是不是"只知道在动、不知道到哪了"（扫描 / 分析阶段）。</summary>
        public bool IsIndeterminate => Percent < 0;

        public override string ToString()
        {
            return IsIndeterminate
                ? $"进度未知（{CurrentEntry}）"
                : $"{Percent}%（{CurrentEntry}）";
        }
    }

    /// <summary>
    /// "这个引擎已经很久没有任何输出了"的通知（**只提示，绝不杀进程**）。
    ///
    /// <para>
    /// 对应的正是用户反复抱怨的"卡死"：界面只有"处理中/完成"两态，
    /// 而一个 780MB 的固实包在解压中途可能几十秒不产生任何输出 —— 看起来就是死了。
    /// 这里不做任何"自动处理"，只把事实告诉上层，由上层写日志 + 在任务上提示，
    /// 用户自己决定要不要点「取消当前」（取消语义只归用户，见不变量 9）。
    /// </para>
    /// </summary>
    public sealed class ArchiveStallNotice
    {
        /// <summary>距离最后一次引擎输出过去了多久。</summary>
        public TimeSpan Idle { get; init; }

        /// <summary>触发这条提示的阈值（默认 90 秒）。</summary>
        public TimeSpan Threshold { get; init; }

        /// <summary>触发时进程是否仍在运行（恒为 true —— 进程已退出时不会报这个）。</summary>
        public bool ProcessStillRunning { get; init; } = true;

        public override string ToString()
        {
            return $"已 {Idle.TotalSeconds:F0} 秒没有任何引擎输出（阈值 {Threshold.TotalSeconds:F0} 秒）";
        }
    }

    /// <summary>
    /// 引擎一次运行的"进度与卡住提示"接收端。
    ///
    /// <para>
    /// 为什么单独一个类型而不是直接给 <see cref="ArchiveRequest"/>：
    /// 运行器（<c>SevenZipProcessRunner</c> / <c>UnRarProcessRunner</c>）的公开方法收的是
    /// "路径 + 选项 + 令牌"，把它们全换成 <see cref="ArchiveRequest"/> 会动到大量既有调用点与测试。
    /// 这个类型只承载"往哪里报"，与请求本身的参数解耦。
    /// </para>
    ///
    /// <para>
    /// <see cref="Progress"/> 由引擎层**节流之后**调用（见 <see cref="ArchiveProgressReporter"/>），
    /// 所以接收端可以放心地往 UI 线程投递，不必自己再去重。
    /// </para>
    /// </summary>
    public sealed class EngineProgressContext
    {
        /// <summary>进度接收端；null = 不报进度（但仍然会从日志里剔除进度噪声）。</summary>
        public IProgress<ArchiveProgress>? Progress { get; init; }

        /// <summary>"长时间无输出"提示；null = 不提。</summary>
        public Action<ArchiveStallNotice>? Stalled { get; init; }

        /// <summary>多久没有输出算"长时间无响应"。默认 90 秒（见 <see cref="EngineOutputActivityMonitor.DefaultStallThreshold"/>）。</summary>
        public TimeSpan StallThreshold { get; init; } = EngineOutputActivityMonitor.DefaultStallThreshold;

        /// <summary>从 <see cref="ArchiveRequest"/> 取出接收端；请求没带任何跟踪需求时返回 null。</summary>
        public static EngineProgressContext? From(ArchiveRequest? request)
        {
            if (request == null)
            {
                return null;
            }

            if (request.Progress == null && request.Stalled == null)
            {
                return null;
            }

            return new EngineProgressContext
            {
                Progress = request.Progress,
                Stalled = request.Stalled,
                StallThreshold = request.StallThreshold
            };
        }
    }
}
