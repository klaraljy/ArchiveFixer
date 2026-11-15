using ArchiveFixer.Security;
using System.Collections.Generic;

namespace ArchiveFixer.Storage;

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
    /// 目标盘默认保留余量（空间门专用）。
    ///
    /// <para>2026-11-06 从 512 MiB 下调到 64 MiB：小说 / TXT / 小文件的实际解压远小于 512 MiB，
    /// 固定 512 MiB 会把大量本来能跑的小任务误拦。`ResourceBudgetOptions` 的硬预算默认值
    /// 仍是各自那一档，不复用这里。</para>
    ///
    /// <para>测试边界用 <c>SpaceReserveOverride</c> 注入更小的值；生产是否开放配置与最终公式
    /// 仍待空间单位讨论。</para>
    /// </summary>
    public static long DefaultReserveBytes => 64L * 1024 * 1024;

    /// <summary>
    /// 判断能否为一个任务预留 <paramref name="requiredBytes"/> 字节。
    ///
    /// <para>⚠ <paramref name="requiredBytes"/> 取
    /// <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>（内容物 + 过程物 = "这次要从可用空间里
    /// 新写多少"），⛔ **不是** <see cref="TaskSpaceEstimate.PeakBytes"/>：峰值里那份源包
    /// 本来就在盘上、从来不在"可用空间"里，加进需求就是把源包算两遍
    /// （2026-09-29 真机：17.7 GiB 的三卷包在 33.45 GiB 可用的盘上被判"整盘都放不下"）。</para>
    /// </summary>
    /// <param name="requiredBytes">需求（取 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>）。</param>
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
    /// <param name="availableIsBookValue">
    /// <paramref name="availableBytes"/> 是不是**账本上的数**（批首那一针，之后只在"有任务等空间"
    /// 时才重探）而不是此刻刚探到的。
    ///
    /// <para>⚠ 这个开关**只影响放行那一行的措辞**（用户 2026-10-02 真机：同一分钟里
    /// 「空间门通过……可用 31.85 GiB」与空间趋势的「可用 26.91 GiB」差了 5 GiB，
    /// 他按日志读成"程序把盘算错了"）。⛔ 判据、比较、数字来源一个字都没动 ——
    /// 传进来的还是同一个 <paramref name="availableBytes"/>。</para>
    /// </param>
    public static SpaceGateDecision Check(
        long requiredBytes,
        long? availableBytes,
        long reserveBytes,
        long alreadyReservedBytes = 0,
        long reclaimableBytes = 0,
        bool availableIsBookValue = false)
    {
        long required = requiredBytes > 0 ? requiredBytes : 0L;
        long reserve = reserveBytes > 0 ? reserveBytes : 0L;
        long reserved = alreadyReservedBytes > 0 ? alreadyReservedBytes : 0L;
        long total = TaskSpaceEstimate.SaturatingSum(required, reserved);

        if (!availableBytes.HasValue)
        {
            return new SpaceGateDecision
            {
                Allowed = false,
                RequiredBytes = total,
                AvailableBytes = -1,
                ReserveBytes = reserve,
                ShortfallBytes = total,
                Reason = $"没能取到目标盘可用空间（需要 {TaskSpaceEstimate.FormatSize(total)}），"
                         + "空间未知不算够：本次不启动这个任务",
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

            /*
             * 两个数（需要 / 可用）都取既有口径，⛔ 一个都没动；变的只是"这个可用是哪来的"这句话 ——
             * 账本那条路（并发解压）与打包那条路（刚探到）**必须说得不一样**，
             * 否则用户会拿一个批首的数去比同一分钟的空间趋势（2026-10-02 真机）。
             */
            Reason = availableIsBookValue
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.SpaceGatePassedBookValueFormat,
                    TaskSpaceEstimate.FormatSize(total),
                    TaskSpaceEstimate.FormatSize(available),
                    TaskSpaceEstimate.FormatSize(remaining),
                    TaskSpaceEstimate.FormatSize(reserve))
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Models.StatusText.SpaceGatePassedFormat,
                    TaskSpaceEstimate.FormatSize(total),
                    TaskSpaceEstimate.FormatSize(available),
                    TaskSpaceEstimate.FormatSize(remaining),
                    TaskSpaceEstimate.FormatSize(reserve))
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
            "先清理「其余物」和残留工作区（③「清理方式」页把最后一层过程物设为彻底删除）：把上一个包的过程物清掉，空间立刻回来"
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
