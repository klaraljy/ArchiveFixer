using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;

namespace ArchiveFixer.Services;

/// <summary>
/// **「这一单该不该发」的全部门槛**：本批生不生效（<see cref="IsActive"/>）＋ 收尾那一刻按顺序问的
/// 五道门槛（<see cref="TryAdmit"/>）。任何一道不过 ⇒ 一个字节都不发布，并如实写清为什么。
///
/// <para>为什么判定单独一类：它是这一档**唯一**的准入口，五个判据的顺序本身有意义
/// （取消 → 机器终态 → 触发事实 → 空间互斥 → 落点），判定不该和搬运、记账、日志措辞混在一处。
/// ⛔ 本类不动盘、不改任务终态、不改用户设置 —— 只回答"能不能发"。</para>
/// </summary>
internal sealed class PartialPublishAdmission
{
    private readonly IPartialPublishStepHost _host;
    private readonly PartialPublishLog _log;

    internal PartialPublishAdmission(IPartialPublishStepHost host, PartialPublishLog log)
    {
        _host = host;
        _log = log;
    }

    /// <summary>
    /// 本批「部分完成发布」生不生效（**唯一判据**）。
    ///
    /// <para>= ③页那个开关打开 **且** 本批不是「空间不足」模式。用户 2026-10-02 顾虑 2 点名的危险组合
    /// （空间不足 + 大容量 + 多份相同文件一起开）就靠这一条**硬互斥**挡住：那一档的全部价值是
    /// "定稿 + 校验通过后当场删源包回收空间"，而部分完成**天然回收不了源包**（缺的那些条目只在源包里）
    /// ⇒ 两者目的正好相反。⛔ 只让行为失效，**绝不去改用户的设置**（勾还勾着，只是这一轮不生效）。</para>
    ///
    /// <para>⚠ 它与收尾那道空间互斥（<see cref="TryAdmit"/> 里的 <c>SpaceTightThisBatch</c>）不是同一件事：
    /// 这一位还带着"用户开关"，那一档是收尾时的硬性拦截。两处都读批首钉死的
    /// <see cref="IPartialPublishStepHost.SpaceTightPolicy"/>，⛔ 不各自解释一遍。</para>
    /// </summary>
    internal bool IsActive => _host.Settings?.PartialPublishEnabled == true
        && !(_host.SpaceTightPolicy.Enabled && _host.SpaceTightPolicy.PartialContentOnFailureDrops);

    /// <summary>
    /// 收尾那一刻的准入：按顺序过六道门槛。返回 true 时 <paramref name="packageOutputRoot"/>
    /// = 这一单的成品目录（后面真搬运就用它，⛔ 不再自己读一遍 <c>task.OutputPath</c>）。
    /// </summary>
    /// <param name="task">这一单（终态、落点、文件名都从这里读）。</param>
    /// <param name="candidate">失败那一刻登记的候选（触发事实与错误类型在它上面）。</param>
    /// <param name="cancelled">
    /// 这一单是不是被用户取消的（判据 = **任务自己的取消令牌**，⛔ 不是比对中文文案）。
    ///
    /// <para>为什么必须单独传这一位：单层那条取消路径（<c>ProcessExtractAsync</c> 的
    /// <c>catch (OperationCanceledException)</c>）只落了中文状态、没落机器终态，而收尾那一步会把
    /// <c>Pending</c> 兜底改成 <c>Failed</c> ⇒ "取消"在终态上与"失败"分不开。拿终态当唯一判据的话，
    /// 用户按下取消之后，半成品照样会被摆进目标目录 —— 那正是他要停下来的东西。</para>
    /// </param>
    /// <param name="packageOutputRoot">过门之后的成品目录；没过门时是空串。</param>
    internal bool TryAdmit(
        ArchiveTask task,
        PartialPublishStep.PartialPublishCandidate candidate,
        bool cancelled,
        out string packageOutputRoot)
    {
        packageOutputRoot = string.Empty;

        if (cancelled)
        {
            return false;
        }

        /*
         * ⛔ 只认机器终态（判据是枚举，⛔ 不比中文）：失败 / 部分完成才谈得上"发布已解出的那部分"。
         * 成功走定稿那条路（早就有内容物落地了，不需要这一档）；取消 / 跳过一个字节都不许发布
         * —— 用户按了取消，他要的是"停下来"，不是"把半成品摆进目标目录"。
         */
        if (task.Outcome is not (TaskOutcome.Failed or TaskOutcome.PartiallyCompleted))
        {
            return false;
        }

        /*
         * ⛔ **触发判据 = 结构化事实，不是③页那个开关**（用户 2026-10-14 口径：这个功能淘汰，
         * 做成底层设计 —— 「就是『损坏压缩包的解压情况』，这种情况下会自动开启，
         * **而且用户不能操作**」）。
         *
         * 老实现读 `Settings.PartialPublishEnabled`：那个开关的 UI 在九页签那一刀已经删掉、
         * 三处值都是 false ⇒ **用户再也开不了"把已经解出来的留下"**（这一档等于死掉了）。
         * 现在：只有"引擎明说包损坏"（<see cref="PartialPublishFacts.IsDamagedArchive"/>，唯一出口）
         * 才自动发布；别的失败（密码错 / 缺卷 / 取消 / 越界…）照旧一个字节都不发布，并如实写一行为什么。
         */
        if (!candidate.Damaged)
        {
            _log.NotDamaged(task, candidate);

            return false;
        }

        // 硬互斥（见 IsActive）。写一行 INFO 说清为什么没生效，⛔ 设置一个字都不改。
        if (_host.SpaceTightThisBatch)
        {
            _log.SpaceTightBlocked(task);

            return false;
        }

        string outputRoot = task.OutputPath;

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            _log.NoOutputRoot(task);

            return false;
        }

        packageOutputRoot = outputRoot;

        return true;
    }
}
