using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 「删除操作」那一档的**唯一执行体**：按用户选的方式处理一个任务自己的 `其余物`
/// （解压侧只剩彻底删除；回收站档已整条下线）。
///
/// <para>⚠ 2026-09-25 第 32 条起它不再只服务"危险模式"（那套已整块退役）：③页的「删除操作」
/// 现在只剩「不动 / 彻底删除」两档，后者走这里，<b>五道门槛一个字不改</b>。</para>
///
/// <para><b>门槛（五条，缺一不可 —— 这是那条红线"失败不删任何东西"的落点）</b>：</para>
/// <list type="number">
/// <item><description>任务的**机器终态**是「完成」（<see cref="TaskOutcome.Succeeded"/>，也就是产物已定稿 +
/// 校验通过 + 源包处理没出问题）——「部分完成」「解压失败」「已取消」「已跳过」一律不动。
/// ⚠ 判据是**枚举**，不是 <c>Status</c> 那个中文文案（用户 2026-09-24 要求：删除的裁决只准看事实）；</description></item>
/// <item><description>输出校验通过（<see cref="ArchiveTask.OutputVerification"/> ==
/// <see cref="OutputVerificationOutcome.Passed"/>，同样读事实而不是文案）；</description></item>
/// <item><description>没被取消（调用方传进来的 <c>cancelled</c>）；</description></item>
/// <item><description>`其余物` 目录**是本次真的记下来的那一个**（<see cref="ArchiveTask.RestDirectoryPath"/>）——
/// 现场重算会在三处算错（内容物那层也叫「其余物」时的 `其余物(1)`、共享根下按包名分的那层、归集把目录整体搬走之后的落点），
/// 而算错的后果是把**别人的**其余物删掉；</description></item>
/// <item><description>它落在本任务自己的输出根之内（<see cref="ArchivePathGuard.IsInsideRoot"/>），
/// 而且路径形状确实是"其余物"（自己就是，或上一级是 —— 共用输出根模式是 `其余物\包名`）。</description></item>
/// </list>
///
/// <para><b>为什么不用 <c>MaintenanceCleanupService.CleanProcessArtifacts</c></b>：它按任务**现场**解析作用域
/// （<c>ResolveArtifactScope</c>），而危险模式动作的时刻是"源包刚被搬进其余物之后"——
/// 那时 <see cref="ArchiveTask.CurrentPath"/> 已经指向其余物里面的新位置，
/// 现场解析在共享输出根模式下会退化成"整个 `其余物\`"，一删就是同目录里所有包的过程物。
/// 所以这里只认定稿那一刻记下来的那一条路径（门槛 4），并用"必须在自己的输出根之内 + 形状必须是其余物"
/// 两道校验兜住（门槛 5），两条都过了才动手。</para>
///
/// <para><b>三段职责（2026-11-14 拆分，公开 API、门槛顺序与判据一个字没改）</b>：
/// 扫描/规划 = <see cref="RestPurgeTargetGate"/>（解析目标 + 读取型闸门 + 待删清单）；
/// 删除执行 = <see cref="RestPurgeDeleter"/>（空壳就地删 / 永久删除）；
/// 结果汇总/日志 = <see cref="RestPurgeReporter"/>（事实位 + 措辞）。
/// 本类只留**不可逆判据的公开入口**与两档的编排（调用的先后 = 原先的门槛顺序）。</para>
///
/// <para><b>线程</b>：全是磁盘活，调用方必须放后台线程。</para>
/// </summary>
public sealed class RestItemPurger
{
    private readonly RestPurgeTargetGate _gate;
    private readonly RestPurgeDeleter _deleter;

    /// <param name="executor">删除执行体（默认真实文件系统 / 回收站）。</param>
    /// <param name="probe">文件系统探测器（默认 Windows 实现）。</param>
    /// <param name="keepRules">
    /// 「内容物保留关键词」判据（用户 2026-10-04 的新功能；唯一出口 <see cref="ContentKeepRules"/>）。
    ///
    /// <para>⛔ 判据放在**执行体里**（不是各个调用点）：其余物的删除只有这一个类，
    /// 三处调用（链尾 / 任务收尾 / 链尾补搬）都要过它 —— 放调用点等于漏两处。
    /// 生产路径三处构造点都从设置里取（<c>ContentKeepRules.FromSettings</c>）；
    /// 传 <c>null</c> / 空列表 ⇒ **一个都不拦**（行为与加这条功能之前逐字相同）。</para>
    ///
    /// <para>命中之后的口径：**整份不删 + 一行点名**（用户要求"碰都不碰"，而"整目录删"与
    /// "里面有命中项"冲突时，兜底永远落在"什么都不做"那一档）。</para>
    /// </param>
    public RestItemPurger(
        IDeleteExecutor? executor = null,
        IDeleteFileSystemProbe? probe = null,
        ContentKeepRules? keepRules = null)
    {
        _deleter = new RestPurgeDeleter(
            executor ?? new ShellDeleteExecutor(),
            probe ?? WindowsDeleteFileSystemProbe.Instance);
        _gate = new RestPurgeTargetGate(keepRules ?? ContentKeepRules.Empty);
    }

    /// <summary>彻底删除那一档的理由（写进删除日志）。必须写清"为什么可以删"与"进了哪里"。</summary>
    public const string AutoPurgeReason =
        "删除操作=彻底删除：内容物已定稿并按落点策略排好、输出校验通过、未取消 —— 彻底删除本任务的其余物（源包 + 过程物，不进回收站）";

    /// <summary>
    /// 部分完成收尾那一档的理由（用户 2026-10-02：「我建议源包 + 已解出的内容物留着，其他的都删掉」）。
    /// </summary>
    public const string PartialPurgeReason =
        "部分完成收尾：已解出的内容物按「部分完成」发布进了目标目录，这条链的最外层源包仍在盘上（它是唯一能重建整条链的东西）"
        + " —— 所以其余物里「除它以外」的过程物（内层包、抠出来的内嵌归档副本）都删掉，盘上只留「源包 + 内容物」两份";

    /// <summary>
    /// **产出方那一份"已经落地的片"**被整组接手、且整组可证完整 + 校验通过 + 未取消之后，
    /// 彻底删除它的理由（用户 2026-10-10 拍板：与 `111.z01` 那一档一致 —— 不留）。
    ///
    /// <para>为什么它也是一条"删除操作"：这一片就是**这一组的源包之一**（第一大步的内容物 = 第二大步的原包），
    /// 组层那一份早已随源包档被收掉；产出方落点里那一份只是同一个字节的另一条名字。</para>
    /// </summary>
    public const string SettledPieceCopyPurgeReason =
        "删除操作=彻底删除：这一片已经随整组解开（接手它的那一单可证完整 + 校验通过 + 未取消）"
        + " —— 产出方落点里这一份是多余的名字，彻底删除（不进回收站）";

    /*
     * ⛔ 2026-10-15：这里原来是 `SettledPieceCopyRecycleReason`（同上，走「移入回收站」那一档时的理由）。
     * 回收站那一档整条下线 ⇒ 这一片（"产出方落点里多余的那条名字"）只剩"彻底删除"一个动作，
     * 那条常量与它的两处三元式一起删除。
     */

    /// <summary>
    /// **部分完成之后的"半份清理"**：其余物里除 <paramref name="keepPaths"/> 以外全删。
    ///
    /// <para><b>为什么是同一个执行体</b>：其余物的删除只有这一个类（AGENTS.md：「其余物的删除只有这一个执行体，
    /// 三处调用都要过它」）—— 新口径要删的只是"其余的项"，但那仍然是其余物，走别处等于又开了一个删删除体。</para>
    ///
    /// <para><b>门槛（与 <see cref="Purge"/> 同一套容器内校验，判据另立两条）</b>：</para>
    /// <list type="number">
    /// <item><description>`其余物` 目录**是本次真的记下来的那一个**（<see cref="ArchiveTask.RestDirectoryPath"/>），
    /// 形状必须是其余物、必须在自己输出根之内 —— 与 <see cref="Purge"/> 逐字相同
    /// （判据本体 <see cref="RestPurgeTargetGate.DescribeBlocker"/>）；</description></item>
    /// <item><description>第六道门槛（分卷半套，<see cref="Extraction.RestVolumeCompletenessGate"/>）照过（同上一处）；</description></item>
    /// <item><description>**要保留的东西里至少有一个此刻真的在盘上** —— 调用方传进来的判据是
    /// "这条链的最外层源包"。它不在 ⇒ 判不出 ⇒ **一个字节都不删**（源包是唯一能重建整条链的东西，
    /// 它都没了还清过程物，等于把最后一份可恢复路径也删掉）；</description></item>
    /// <item><description>要保留的那一项**在其余物里面**时按项保留（不会被当成"别的项"顺手删掉）
    /// —— 后两条的判据本体 <see cref="RestPurgeTargetGate.DescribeKeptItemsBlocker"/>。</description></item>
    /// </list>
    /// </summary>
    public RestPurgeOutcome PurgeExcept(
        ArchiveTask? task,
        IReadOnlyList<string>? keepPaths,
        DeletionPlan? plan = null)
    {
        if (task == null)
        {
            return RestPurgeReporter.Skip("没有任务，未删除任何东西");
        }

        string? allowedRootEvidence = null;

        if (plan != null)
        {
            if (plan.Cancelled)
            {
                return RestPurgeReporter.Skip("删除计划已取消，其余物一个字节都不删");
            }

            if (!RestPurgeTargetGate.PlanTargetsRestDirectory(task, plan))
            {
                return RestPurgeReporter.Skip("删除计划的目标不是本任务的其余物，一个字节都不删");
            }

            allowedRootEvidence = plan.AllowedRoot;
        }

        string name = RestPurgeTargetGate.ResolveName(task);

        string? gateBlocker = _gate.DescribeBlocker(
            task,
            allowedRootEvidence,
            out string directory,
            out string allowedRoot);

        if (gateBlocker != null)
        {
            return RestPurgeReporter.Skip(gateBlocker);
        }

        string? itemsBlocker = RestPurgeTargetGate.DescribeKeptItemsBlocker(
            name,
            directory,
            keepPaths,
            out List<string> keep,
            out List<string> targets);

        if (itemsBlocker != null)
        {
            return RestPurgeReporter.Skip(itemsBlocker);
        }

        /*
         * ===== 空壳就地删掉，不进回收站（用户 2026-10-03 真机）=====
         *
         * 与 <see cref="Purge"/> 那一档**同一份判据、同一条路**：按 keepPaths 排除之后剩下的
         * 如果**全是**空目录（递归一个文件都没有），就一个个就地删掉，⛔ 不再逐个条目送回收站
         * —— 那就是回收站里那堆 1 KB 同名「其余物」文件夹的另一半来源。
         *
         * ⛔ keepPaths 的语义一个字不改：要保留的那一份（这条链的最外层源包）怎么算、怎么排除，
         * 全在 <see cref="RestPurgeTargetGate.DescribeKeptItemsBlocker"/> 里，这里只决定"剩下这些怎么删"。
         * 目标里只要有一个是真文件，<see cref="RestPurgeDeleter.IsFileFreeDirectoryTree"/> 就对它返回
         * false ⇒ 整个 targets 照旧走永久删除。
         */
        if (targets.All(target => RestPurgeDeleter.IsFileFreeDirectoryTree(target)))
        {
            int removedCount = 0;

            foreach (string target in targets)
            {
                // 判据已证明这一个递归无文件 ⇒ 只剩空目录结构，就地删掉（不进回收站）。
                string? shellError = RestPurgeDeleter.TryDeleteEmptyShellDirectory(target);

                if (shellError != null)
                {
                    return RestPurgeReporter.Skip(
                        $"{name}：其余物里剩下的全是空壳，但就地删空目录失败（{target}：{shellError}）"
                        + $" —— 已就地删掉 {removedCount} / {targets.Count} 个空目录，没有送进回收站，"
                        + "没删掉的项仍在原处");
                }

                removedCount++;
            }

            string shellLine = RestPurgeReporter.DescribeKeptItemsShellRemoved(name, removedCount);

            return RestPurgeReporter.RemovedAsEmptyShell(
                directory,
                shellLine + RestPurgeReporter.DescribeKeptSuffix(keep),
                new[] { shellLine });
        }

        var requests = targets
            .Select(path => new DeleteRequest(path, PartialPurgeReason))
            .ToList();

        DeleteResult? result = _deleter.TryDeletePermanently(
            requests,
            allowedRoot,
            PartialPurgeReason,
            out string? deleteError);

        if (result == null)
        {
            return RestPurgeReporter.Skip(
                $"{name}：清理其余物里的过程物时出现意外错误（{deleteError}），一个字节都没删");
        }

        IReadOnlyList<string> logLines = RestPurgeReporter.ToLogLines(result);
        int entryCount = result.Outcomes.Sum(outcome => outcome.EntryCount);

        if (result.SuccessCount <= 0)
        {
            return RestPurgeReporter.FailedNotRemoved(
                directory,
                RestPurgeReporter.DescribeKeptItemsPurgeFailed(
                    name,
                    targets.Count,
                    RestPurgeReporter.DescribeFailureDetail(result)),
                logLines);
        }

        return RestPurgeReporter.Removed(
            directory,
            entryCount,
            result.FreedBytes,
            RestPurgeReporter.DescribeKeptItemsPurged(
                name,
                result.SuccessCount,
                targets.Count,
                entryCount,
                result.FreedBytes,
                keep),
            logLines);
    }

    /// <summary>
    /// 试着删除一个任务的其余物。**任何一条门槛不成立都返回"没动"**，并给出原因（不抛异常）。
    /// </summary>
    /// <param name="task">目标任务。</param>
    /// <param name="cancelled">这一刻是不是已经被取消（用户按了「取消当前」/「停止后续」）。</param>
    /// <param name="completenessEvidence">
    /// **完整性证据的来源**。默认（null）= 本任务自己那一份（<see cref="ResultCompletenessClassifier.Classify"/>）。
    ///
    /// <para>唯一需要换来源的形状（真机 CCCC 2026-10-06 18:13，用户两次追问的 48.35 MB
    /// `111\111\其余物\111.zip`）：某一单只出**过程物**（它解出来的入口包就是下一层的输入）、
    /// 自己因此停在中途 ⇒ **它自己的完整性永远判不出来**，而那一组后来由**另一个当场核过清单、
    /// 可证完整**的消费方解开 ⇒ 该按证据的是**消费方那一份**。</para>
    ///
    /// <para>⛔ 换的只是"证据从哪来"，**六道门槛一条都不绕过**；⛔ 这条证据只用于这一次删除，
    /// 不许写回任务自己的账（伪造记录是更坏的事）。</para>
    /// </param>
    /// <param name="allowedRootEvidence">
    /// **"本任务输出范围"这一道门槛的锚点**（默认 null = 用本任务自己的 `CollectedPath` / `OutputPath`）。
    ///
    /// <para>同上那一格：过路层那一单被收场时 `OutputPath` 已经被**显式清空**
    /// （它是跟班卷，没有自己的成品目录）⇒ 老写法会拒绝删除（"不在本任务的输出目录（）之内"）。
    /// 调用方用**唯一落点出口**现算一份它那条链的落点目录传进来即可
    /// —— 其余物正是建在那一层底下。</para>
    /// </param>
    public RestPurgeOutcome Purge(
        ArchiveTask? task,
        bool cancelled,
        ResultCompletenessVerdict? completenessEvidence = null,
        string? allowedRootEvidence = null,
        ArchiveTask? completenessEvidenceTask = null)
    {
        if (task == null)
        {
            return RestPurgeReporter.Skip("没有任务，未删除任何东西");
        }

        if (completenessEvidenceTask != null
            && !string.Equals(
                completenessEvidenceTask.ChainRootIdentity,
                task.ChainRootIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            return RestPurgeReporter.Skip(
                $"{task.FileName}：完整性证据来自另一条链"
                + $"（证据链根 {completenessEvidenceTask.ChainRootIdentity}，本任务链根 {task.ChainRootIdentity}）"
                + "—— 其余物一个字节都不删");
        }

        string name = RestPurgeTargetGate.ResolveName(task);

        if (cancelled)
        {
            return RestPurgeReporter.Skip($"{name}：已取消 —— 其余物一个字节都不删（失败 / 取消 / 校验不通过一律不动）");
        }

        if (task.Outcome != TaskOutcome.Succeeded)
        {
            return RestPurgeReporter.Skip(
                $"{name}：任务的机器终态不是「完成」（当前：{task.Outcome}）—— " +
                $"其余物一个字节都不删（部分完成 / 失败 / 取消 / 跳过一律不动）");
        }

        /*
         * ⚠ 判据只准看**事实**（用户 2026-09-24 要求）：这里读的是校验那一刻写下的枚举，
         * ⛔ 不再用 `task.Status == "解压成功"` 这种中文状态字符串 ——
         * 真机日志里出现过"状态写着解压成功、校验却已判否"的那一帧，
         * 万一那种任务走到这里，用户的源包就会被永久删掉。
         *
         * ⚠ 2026-09-30（检验等级 L4）：判据从"校验通过"**收紧**成"可证完整"——
         * 唯一出口 <see cref="ResultCompletenessClassifier"/>。老口径下"拿不到清单、
         * 只做了非空底线校验"也算通过（`Verified = true`），于是**没有任何证据**的这一档
         * 照样拿到了删源包的通行证；现在它落在"判不出"⇒ 一个字节都不删。
         */
        ResultCompletenessVerdict completeness = completenessEvidence
            ?? ResultCompletenessClassifier.Classify(task);

        if (!completeness.AllowsSourceRemoval)
        {
            return RestPurgeReporter.Skip(
                $"{name}：{completeness.Message}（机器结论：{completeness.Evidence}），其余物一个字节都不删");
        }

        // 门槛 4/5 + 第六道门槛（半套分卷）+「内容物保留关键词」：判据全在扫描/规划那一层。
        string? gateBlocker = _gate.DescribeBlocker(
            task,
            allowedRootEvidence,
            out string directory,
            out string allowedRoot);

        if (gateBlocker != null)
        {
            return RestPurgeReporter.Skip(gateBlocker);
        }

        /*
         * ===== 空壳就地删掉，不进回收站（用户 2026-10-03 真机）=====
         *
         * 判据唯一出口 <see cref="RestPurgeDeleter.IsFileFreeDirectoryTree"/>：**递归地一个文件都没有**
         * （只剩空目录结构，含嵌套空目录）。⛔ 排在全部门槛**通过之后** ——
         * 这一条只决定"怎么删"，绝不决定"能不能删"：越界 / 形状不像 / 半套分卷照旧 Skip、什么都不做。
         */
        if (RestPurgeDeleter.IsFileFreeDirectoryTree(directory))
        {
            string? shellError = RestPurgeDeleter.TryDeleteEmptyShellDirectory(directory);

            if (shellError != null)
            {
                return RestPurgeReporter.Skip(
                    $"{name}：其余物里只剩下空壳（0 个文件），但就地删空目录失败（{shellError}）"
                    + " —— 没有送进回收站，盘上可能还剩一部分空目录");
            }

            string shellLine = RestPurgeReporter.DescribeDirectoryShellRemoved(name);

            return RestPurgeReporter.RemovedAsEmptyShell(directory, shellLine, new[] { shellLine });
        }

        DeleteResult? result = _deleter.TryDeletePermanently(
            new[] { new DeleteRequest(directory, AutoPurgeReason) },
            allowedRoot,
            AutoPurgeReason,
            out string? deleteError);

        if (result == null)
        {
            return RestPurgeReporter.Skip($"{name}：删除其余物时出现意外错误（{deleteError}），源包与过程物都还在");
        }

        IReadOnlyList<string> logLines = RestPurgeReporter.ToLogLines(result);
        int entryCount = result.Outcomes.FirstOrDefault()?.EntryCount ?? 0;

        if (result.SuccessCount <= 0)
        {
            return RestPurgeReporter.FailedNotRemoved(
                directory,
                RestPurgeReporter.DescribeDirectoryPurgeFailed(
                    name,
                    RestPurgeReporter.DescribeFailureDetail(result)),
                logLines);
        }

        return RestPurgeReporter.Removed(
            directory,
            entryCount,
            result.FreedBytes,
            RestPurgeReporter.DescribeDirectoryPurged(name, directory, entryCount, result.FreedBytes),
            logLines);
    }

    /// <summary>
    /// 唯一删除计划的执行入口：目标/允许根/取消事实都从 <see cref="DeletionPlan"/> 读，
    /// 执行体不再自己猜目标；计划与任务其余物对不上就一个字节都不删。
    /// </summary>
    public RestPurgeOutcome Purge(
        ArchiveTask? task,
        DeletionPlan plan,
        ResultCompletenessVerdict? completenessEvidence = null,
        ArchiveTask? completenessEvidenceTask = null)
    {
        if (task == null || plan == null)
        {
            return RestPurgeReporter.Skip("没有任务或删除计划，未删除任何东西");
        }

        if (string.IsNullOrWhiteSpace(task.RestDirectoryPath))
        {
            return RestPurgeReporter.Skip("任务没有其余物目录，未删除任何东西");
        }

        if (!RestPurgeTargetGate.PlanTargetsRestDirectory(task, plan))
        {
            return RestPurgeReporter.Skip("删除计划的目标不是本任务的其余物，一个字节都不删");
        }

        return Purge(
            task,
            plan.Cancelled,
            completenessEvidence: completenessEvidence,
            allowedRootEvidence: plan.AllowedRoot,
            completenessEvidenceTask: completenessEvidenceTask);
    }

    /// <summary>
    /// 路径形状校验（**薄转发**）：最后一段是「其余物 / 过程物」，**或者**上一级是
    /// （共用输出根模式是 <c>&lt;根&gt;\其余物\&lt;包基名&gt;</c>，最后一段是包名）。
    /// 判据本体只有一处：<see cref="RestPurgeTargetGate.LooksLikeArtifactDirectory"/>。
    /// </summary>
    internal static bool LooksLikeArtifactDirectory(string? directory)
    {
        return RestPurgeTargetGate.LooksLikeArtifactDirectory(directory);
    }
}
