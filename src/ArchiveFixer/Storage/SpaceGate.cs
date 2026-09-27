using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage
{
    /// <summary>空间门（<see cref="SpaceGate"/>）的结论。</summary>
    public sealed class SpaceGateDecision
    {
        /// <summary>放不放行。false = **不启动**这个任务（用户 2026-09-22 需求：空间不足时不启动）。</summary>
        public bool Allowed { get; init; }

        /// <summary>本次判断用的需求字节数（含已经在跑的其它任务的预留）。</summary>
        public long RequiredBytes { get; init; }

        /// <summary>目标盘可用字节数；取不到时为 -1（<see cref="ProbeFailed"/> 为 true）。</summary>
        public long AvailableBytes { get; init; }

        /// <summary>要求保留的余量字节数。</summary>
        public long ReserveBytes { get; init; }

        /// <summary>差多少字节（放行时为 0）。</summary>
        public long ShortfallBytes { get; init; }

        /// <summary>中文结论，**一定带具体数字**（需要 X、可用 Y、差 Z）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>建议动作（清理 `其余物` / 换输出盘 / 开「空间不足」模式 / 降并发）。</summary>
        public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();

        /// <summary>可用空间**取不到**（盘未就绪 / 路径非法 / 权限）。</summary>
        public bool ProbeFailed => AvailableBytes < 0;

        /// <summary>是不是"空间不足"被拦下（区别于"探测失败"）。</summary>
        public bool ShortOfSpace => !Allowed && !ProbeFailed;

        /// <summary>一行日志形态：把结论与建议拼成一句话（建议之间用分号）。</summary>
        public string ToLogLine()
        {
            return Suggestions.Count == 0
                ? Reason
                : Reason + " 建议：" + string.Join("；", Suggestions) + "。";
        }
    }

    /// <summary>
    /// **解压之前**的空间门（用户 2026-09-22 需求第 1 条）。
    ///
    /// <para><b>它与 <c>Security/ResourceBudget</c> 的分工</b>：预算回答"这个包该不该解"（硬上限：
    /// 单文件 / 文件数 / 总量 / 展开比 / 盘空间），本类回答"现在能不能开这个任务"。
    /// 两者的盘空间判断共用 <see cref="SpaceChecker"/> 的取数口径（唯一一处 DriveInfo），不另写一份。</para>
    ///
    /// <para><b>为什么以前说拦没拦住</b>：旧实现里空间不足只写一条 <c>WARN</c>，任务照跑，
    /// 用户看到的是"提示完了照样解压"。现在：</para>
    /// <list type="bullet">
    /// <item><description>放不下 → <see cref="SpaceGateDecision.Allowed"/> = false，调用方**不启动**该任务，
    /// 状态落「磁盘空间不足」，并如实报出需要 / 可用 / 差多少；</description></item>
    /// <item><description>放得下 → 放行，但"放得下"这个结论**不写进任务状态**（空间随跑随变，
    /// 把它当成结论会让用户以为后面一定不会满）。</description></item>
    /// </list>
    ///
    /// <para><b>探测失败（取不到可用空间）的口径</b>：<b>不拦</b>，但一定写一条 WARN，
    /// 而且这句话里绝不出现"空间充足" —— 取不到就是取不到，不能把"不知道"说成"够"。
    /// 之所以不拦：取不到通常意味着盘没就绪 / 路径异常，那种情况解压本身会以更具体的原因失败，
    /// 把它报成"空间不足"是指错方向（同一个理由见 <c>ResourceBudget.CheckFreeSpace</c>）。</para>
    /// </summary>
    public static class SpaceGate
    {
        /// <summary>
        /// 目标盘默认保留余量。**不在这里写数字**：与 <c>ResourceBudgetOptions.MinFreeSpaceReserveBytes</c>
        /// 同源（那个设置项将来若可配，这一处会跟着变，不会分叉）。
        /// </summary>
        public static long DefaultReserveBytes =>
            new Security.ResourceBudgetOptions().MinFreeSpaceReserveBytes;

        /// <summary>
        /// 判断能否为一个任务预留 <paramref name="requiredBytes"/> 字节。
        /// </summary>
        /// <param name="requiredBytes">需求（通常取 <see cref="TaskSpaceEstimate.PeakBytes"/>）。</param>
        /// <param name="availableBytes">目标盘可用字节数；null = 取不到。</param>
        /// <param name="reserveBytes">要保留的余量（负数按 0）。</param>
        /// <param name="alreadyReservedBytes">
        /// **已经在跑**的任务的峰值之和。并发下这一项是必须的：只看"现在还剩多少"会让四个任务
        /// 同时通过检查、一起去吃同一块盘（用户点名的反例：5G + 6G 两个大包并行排上）。
        /// </param>
        /// <param name="reclaimableBytes">
        /// 这个任务完成时能立刻收回的字节数（源包 + 过程物；开「空间不足」模式时它真的会被删掉）。
        /// 它只用于**建议文案**（"开那个模式可以少要多少"），绝不参与放行判断 ——
        /// 判断必须按"这些字节此刻还在盘上"来算。
        /// </param>
        public static SpaceGateDecision Check(
            long requiredBytes,
            long? availableBytes,
            long reserveBytes,
            long alreadyReservedBytes = 0,
            long reclaimableBytes = 0)
        {
            long required = requiredBytes > 0 ? requiredBytes : 0L;
            long reserve = reserveBytes > 0 ? reserveBytes : 0L;
            long reserved = alreadyReservedBytes > 0 ? alreadyReservedBytes : 0L;
            long total = TaskSpaceEstimate.SaturatingSum(required, reserved);

            if (!availableBytes.HasValue)
            {
                return new SpaceGateDecision
                {
                    Allowed = true,
                    RequiredBytes = total,
                    AvailableBytes = -1,
                    ReserveBytes = reserve,
                    ShortfallBytes = 0,
                    Reason = $"没能取到目标盘可用空间（需要 {TaskSpaceEstimate.FormatSize(total)}），"
                             + "本次不做空间门判断 —— 这不代表空间充足",
                    Suggestions = new[] { "确认目标盘已就绪（外接盘 / 网络盘拔掉时取不到可用空间）" }
                };
            }

            long available = availableBytes.Value > 0 ? availableBytes.Value : 0L;

            /*
             * 分两步比，不写 required + reserved + reserve > available：
             * 三个都接近 long.MaxValue 时那个加法会溢出成负数，"严重不足"就变成了"充足"
             * （同一个坑见 SpaceChecker.HasEnoughSpace 的注释）。
             */
            if (total > available)
            {
                long shortfall = total - available;

                return Block(
                    total,
                    available,
                    reserve,
                    shortfall,
                    $"磁盘空间不足：这个任务需要 {TaskSpaceEstimate.FormatSize(total)}"
                    + (reserved > 0 ? $"（其中 {TaskSpaceEstimate.FormatSize(reserved)} 是已经在跑的任务占的）" : string.Empty)
                    + $"，目标盘可用 {TaskSpaceEstimate.FormatSize(available)}，差 {TaskSpaceEstimate.FormatSize(shortfall)}",
                    reclaimableBytes);
            }

            long remaining = available - total;

            if (remaining < reserve)
            {
                long shortfall = reserve - remaining;

                return Block(
                    total,
                    available,
                    reserve,
                    shortfall,
                    $"磁盘空间不足：这个任务需要 {TaskSpaceEstimate.FormatSize(total)}"
                    + (reserved > 0 ? $"（其中 {TaskSpaceEstimate.FormatSize(reserved)} 是已经在跑的任务占的）" : string.Empty)
                    + $"，目标盘可用 {TaskSpaceEstimate.FormatSize(available)}，"
                    + $"解压过程中会低于要保留的 {TaskSpaceEstimate.FormatSize(reserve)}，还差 {TaskSpaceEstimate.FormatSize(shortfall)}",
                    reclaimableBytes);
            }

            return new SpaceGateDecision
            {
                Allowed = true,
                RequiredBytes = total,
                AvailableBytes = available,
                ReserveBytes = reserve,
                ShortfallBytes = 0,
                Reason = $"空间门通过：需要 {TaskSpaceEstimate.FormatSize(total)}，"
                         + $"可用 {TaskSpaceEstimate.FormatSize(available)}，"
                         + $"余下 {TaskSpaceEstimate.FormatSize(remaining)}（保留 {TaskSpaceEstimate.FormatSize(reserve)}）"
            };
        }

        /// <summary>
        /// 拦下时的那几句话。建议动作按用户能立刻做的顺序排：
        /// ① 清 `其余物`（能立刻腾出空间，而且是他本来就要删的东西）；
        /// ② 换输出盘 / 换落点；③ 开①页那个「空间不足」模式（说明它为什么能省，并**同时**说明它会永久删源包）；
        /// ④ 降并发（并发越高，同时在盘上的峰值越大）。
        /// </summary>
        private static SpaceGateDecision Block(
            long required,
            long available,
            long reserve,
            long shortfall,
            string reason,
            long reclaimableBytes)
        {
            var suggestions = new List<string>
            {
                "先清理「其余物」（③「清理与删除」页 →「删除其余物…」）：把上一个包的源包与过程物清掉，空间立刻回来"
            };

            string tightModeHint = reclaimableBytes > 0
                ? $"开①页的「空间不足」模式可以少要 {TaskSpaceEstimate.FormatSize(reclaimableBytes)}"
                : "开①页的「空间不足」模式可以边解边回收源包占的空间";

            suggestions.Add(
                tightModeHint
                + "（每个包校验通过后立刻永久删除它的源包；只对这一次运行有效，不写设置。"
                + "⚠ 它删的是你的源包，删了不可恢复）");

            suggestions.Add("换一个空间更大的输出盘：设置 → 输出位置 → 「指定位置」");
            suggestions.Add($"把「最大并发解压数」调小（当前判断里已经算进了同时在跑的任务）：差 {TaskSpaceEstimate.FormatSize(shortfall)}");

            return new SpaceGateDecision
            {
                Allowed = false,
                RequiredBytes = required,
                AvailableBytes = available,
                ReserveBytes = reserve,
                ShortfallBytes = shortfall,
                Reason = reason,
                Suggestions = suggestions
            };
        }

        /// <summary>把字节数说成人话（与 <c>SpaceChecker</c> 同一口径，只用于展示）。</summary>
        internal static string FormatSize(long bytes)
        {
            return TaskSpaceEstimate.FormatSize(bytes);
        }
    }

    /// <summary>
    /// **空间预留账本**：并发解压时"已经许出去多少空间"的唯一记账处。
    ///
    /// <para><b>为什么必须有它</b>（用户点名的反例：一开始就把 5G+6G 两个大包并行排上）：
    /// 没有账本时，每个任务启动前看到的都是"现在还剩 10G"，于是 5G 和 6G 两个都通过了检查，
    /// 一起开跑，最后双双写满磁盘。有了账本，"还剩下多少"= 可用 − 已预留，
    /// 第二个大包会在启动前就被拦下（差多少也算得出来）。</para>
    ///
    /// <para><b>释放与刷新</b>：任务结束时按**当初预留的那个数字**释放（不是按实际用量），
    /// 同时用最新探测到的可用空间刷新账本 —— 「空间不足」模式下源包真的被删掉，多出来的空间会因此
    /// 出现在下一次判断里，这正是那个模式"越跑越宽松"的原因。</para>
    ///
    /// <para>本类是纯数据（无 IO、无 WPF），磁盘探测由调用方喂进来，所以可以被单测精确摆布。</para>
    /// </summary>
    public sealed class SpaceReservationLedger
    {
        private readonly object _sync = new();

        private long _availableBytes;
        private long _reservedBytes;

        /// <param name="availableBytes">目标盘可用字节数（null = 取不到）。</param>
        /// <param name="reserveBytes">要保留的余量；null 时取 <see cref="SpaceGate.DefaultReserveBytes"/>。</param>
        public SpaceReservationLedger(long? availableBytes, long? reserveBytes = null)
        {
            _availableBytes = availableBytes ?? UnknownAvailable;
            ReserveBytes = reserveBytes ?? SpaceGate.DefaultReserveBytes;
        }

        /// <summary>可用空间取不到时的哨兵（-1，与 <see cref="SpaceGateDecision.AvailableBytes"/> 同一口径）。</summary>
        public const long UnknownAvailable = -1L;

        /// <summary>要保留的余量。</summary>
        public long ReserveBytes { get; }

        /// <summary>最近一次知道的可用字节数（-1 = 取不到）。</summary>
        public long AvailableBytes
        {
            get
            {
                lock (_sync)
                {
                    return _availableBytes;
                }
            }
        }

        /// <summary>已经许给"正在跑"的任务的峰值之和。</summary>
        public long ReservedBytes
        {
            get
            {
                lock (_sync)
                {
                    return _reservedBytes;
                }
            }
        }

        /// <summary>账面上**还能给新任务用**的字节数：可用 − 已预留 − 余量。取不到可用空间时返回 -1。</summary>
        public long RemainingBytes
        {
            get
            {
                lock (_sync)
                {
                    return ComputeRemaining(_availableBytes, _reservedBytes, ReserveBytes);
                }
            }
        }

        /// <summary>用最新探测值刷新可用空间（null = 这次没探到，**保留上一次的值**，不假装是 0）。</summary>
        public void RefreshAvailable(long? latestAvailableBytes)
        {
            if (!latestAvailableBytes.HasValue)
            {
                return;
            }

            lock (_sync)
            {
                _availableBytes = latestAvailableBytes.Value > 0 ? latestAvailableBytes.Value : 0L;
            }
        }

        /// <summary>
        /// 申请预留。<see cref="SpaceGateDecision.Allowed"/> 为 true 时**已经记账**，
        /// 调用方必须在任务结束时用同一个数字调 <see cref="Release"/>（成功 / 失败 / 跳过都要调）。
        /// </summary>
        public SpaceGateDecision TryReserve(long peakBytes, long reclaimableBytes = 0)
        {
            long required = peakBytes > 0 ? peakBytes : 0L;

            lock (_sync)
            {
                SpaceGateDecision decision = SpaceGate.Check(
                    required,
                    _availableBytes >= 0 ? _availableBytes : null,
                    ReserveBytes,
                    _reservedBytes,
                    reclaimableBytes);

                if (decision.Allowed && !decision.ProbeFailed)
                {
                    // 探测失败时不记账：那时 required 根本没法比较，记进去只会污染后面的判断。
                    _reservedBytes = TaskSpaceEstimate.SaturatingSum(_reservedBytes, required);
                }

                return decision;
            }
        }

        /// <summary>释放预留（按当初申请的数字）。负数与超量都被夹住，绝不让账面变成负数。</summary>
        public void Release(long peakBytes)
        {
            long released = peakBytes > 0 ? peakBytes : 0L;

            lock (_sync)
            {
                _reservedBytes = _reservedBytes > released ? _reservedBytes - released : 0L;
            }
        }

        /// <summary>
        /// 任务开工之后（一般是拿到条目清单、算出了精确峰值的那一刻）**改预留**。
        ///
        /// <para>
        /// 为什么要改而不是"开始时大致估一个就算了"：估小了会让"这些任务同时达到峰值"的总和超过可用空间 ——
        /// 正是并发把磁盘写满的那条路径；估大了会白白拦下本来能跑的任务。
        /// 变更走的是**差值**：<see cref="TryReserve"/> 看到的 <c>已预留</c> 里已经含本任务那一份，
        /// 所以 <c>差值 + 已预留</c> 恰好等于"新峰值 + 别的任务的预留"，判断口径与开工时完全一致。
        /// </para>
        /// </summary>
        /// <param name="currentReservation">本任务当前占着的预留（开工时申请的数字）。</param>
        /// <param name="newRequiredBytes">按精确清单算出来的新峰值。</param>
        /// <returns>新峰值下的空间门结论。放行时调用方要把预留改成 <paramref name="newRequiredBytes"/>。</returns>
        public SpaceGateDecision Adjust(long currentReservation, long newRequiredBytes, long reclaimableBytes = 0)
        {
            long current = currentReservation > 0 ? currentReservation : 0L;
            long updated = newRequiredBytes > 0 ? newRequiredBytes : 0L;

            if (updated <= current)
            {
                // 估大了：把多占的还回去，再按新数字给一次结论（这时只会更宽松）。
                Release(current - updated);
            }
            else
            {
                SpaceGateDecision decision = TryReserve(updated - current, reclaimableBytes);

                if (!decision.Allowed)
                {
                    // 没能加上：预留保持原值，让调用方按"空间不足"处理（它会用原值释放）。
                    return decision;
                }
            }

            lock (_sync)
            {
                return SpaceGate.Check(
                    updated,
                    _availableBytes >= 0 ? _availableBytes : null,
                    ReserveBytes,
                    0,
                    reclaimableBytes);
            }
        }

        /// <summary>一句话把账面说清楚（进日志：用户要能回答"当时为什么跳过了它"）。</summary>
        public string Describe()
        {
            lock (_sync)
            {
                if (_availableBytes < 0)
                {
                    return "目标盘可用空间取不到，本次不做空间门判断";
                }

                return $"可用 {TaskSpaceEstimate.FormatSize(_availableBytes)}"
                       + $"，已经在跑的任务预留 {TaskSpaceEstimate.FormatSize(_reservedBytes)}"
                       + $"，保留 {TaskSpaceEstimate.FormatSize(ReserveBytes)}"
                       + $"，还能排 {TaskSpaceEstimate.FormatSize(ComputeRemaining(_availableBytes, _reservedBytes, ReserveBytes))}";
            }
        }

        private static long ComputeRemaining(long available, long reserved, long reserve)
        {
            if (available < 0)
            {
                return UnknownAvailable;
            }

            // 两步减，理由同 SpaceGate.Check：三个大数相加会溢出成负数。
            long afterReserved = available > reserved ? available - reserved : 0L;

            return afterReserved > reserve ? afterReserved - reserve : 0L;
        }

        /// <summary>把字节数说成人话（展示用）。</summary>
        internal static string FormatSize(long bytes) => TaskSpaceEstimate.FormatSize(bytes);
    }
}
