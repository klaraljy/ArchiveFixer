using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 续解链收尾时把**过程物**归集进「其余物」的执行体（生命周期：链尾那一刻；失败模型：逐项尽力而为）。
///
/// <list type="number">
/// <item><description><b>搬什么</b>：<see cref="ChainInnerPackageMovePlanner.Plan"/> 只算计划，一个字节都不动；
/// 本类按计划逐个搬，目标被占就现场让位（<see cref="RestArtifactMover.TryMoveIntoRest"/>），⛔ 绝不覆盖。</description></item>
/// <item><description><b>没搬成的怎么办</b>：单项失败只写 WARN、原件留在原地；已经搬进去的同步任务路径账
/// （<c>TaskPathSync.ApplyMoves</c>，与源包搬运同一个出口），保证下一轮按分卷组搬源包时不丢片。</description></item>
/// <item><description><b>还留着什么</b>：<see cref="ReportLeftBehindInnerPackages"/> **无条件**点名
/// 「成品目录里还留着、但内容还没解出来」的内层包 —— 那一份不能当过程物清掉（清掉就是删内容）。</description></item>
/// </list>
///
/// <para>⚠ 失败模型是「逐项失败、整体继续」：任何一项搬不动都不影响这一单的结论（内容物已经定稿），
/// 只是其余物少一份；唯一会提前收手的是一开始就被取消。成功链在这里直接清空其余物
/// （<see cref="RestPurgeStep.PurgeRestAfterSuccess"/>）——全链成功才这么做，失败/部分完成原样留着。</para>
/// </summary>
internal sealed class ChainRestArtifactCollector
{
    private readonly IRestHandlingStepHost _host;
    private readonly RestPurgeStep _purge;

    internal ChainRestArtifactCollector(IRestHandlingStepHost host, RestPurgeStep purge)
    {
        _host = host;
        _purge = purge;
    }

    /// <summary>
    /// 把一条续解链里**被真正解过的内层包**搬进根任务的「其余物」（第 35 条）。
    ///
    /// <para>只搬"事实成立"的那些：属于这条链、是续解任务、任务成功 + 校验通过、
    /// 文件还在、且落在根任务的输出范围之内。搬完把其余物目录记到根任务上 ——
    /// 这样「删除操作」那一档才有对象可删（此前内层包不在其余物里，彻底删除等于空转）。</para>
    ///
    /// <para>任何一步失败只写 WARN（内层包留在原地，不影响任何结论）；已经被搬进其余物的跳过（幂等）。</para>
    /// </summary>
    internal async Task CollectAsync(
        ArchiveTask rootTask,
        IReadOnlyList<ArchiveTask>? chainTasks,
        CancellationToken cancellationToken,
        string? planAllowedRoot = null)
    {
        if (chainTasks == null || chainTasks.Count == 0)
        {
            return;
        }

        /*
         * 归到哪一份其余物：根任务的（内容物那一层目录下的「其余物」）。
         * 定稿时已经记下过就沿用那一条（⛔ 不许现场重算：那一层可能因为撞名变成了「其余物(1)」）。
         */
        string restDirectory = rootTask.RestDirectoryPath;

        if (string.IsNullOrWhiteSpace(restDirectory))
        {
            string baseDirectory = string.IsNullOrWhiteSpace(rootTask.ContentDirectoryPath)
                ? rootTask.OutputPath
                : rootTask.ContentDirectoryPath;

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return;
            }

            restDirectory = Path.Combine(baseDirectory, ProcessArtifactLayout.ArtifactDirectoryName);
        }

        string outputRoot = string.IsNullOrWhiteSpace(rootTask.OutputPath)
            ? rootTask.ContentDirectoryPath
            : rootTask.OutputPath;

        var planningWarnings = new List<string>();

        List<(string From, string To)> moves = await Task.Run(
            () => ChainInnerPackageMovePlanner.Plan(
                rootTask,
                chainTasks,
                restDirectory,
                outputRoot,
                ContentKeepRules.FromSettings(_host.Settings),
                out planningWarnings),
            cancellationToken);

        foreach (string warning in planningWarnings)
        {
            _host.AppendLog("WARN", warning);
        }

        /*
         * ⛔ 2026-10-15：「留着没跑的内层包 ⇒ 如实点名」那一段**搬走了**（见
         * <see cref="ReportLeftBehindInnerPackages"/>）—— 它必须**无条件**跑，而本方法是"链尾要不要搬"
         * 那一个判据下的空跑段（普通档恒不执行）⇒ 长在这里就会被静默掉
         * （实测：`ChainRestSweepTests` 三条守门因此变红）。
         */
        if (moves.Count == 0)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _host.AppendLog("WARN", $"{rootTask.LogName}：链尾收内层包之前被取消，内层包留在原地（其余物不生成）。");
            return;
        }

        var failures = new List<string>();

        // 这一趟真正落下去的目标名（让位时也要看它，见下）。
        var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string from, string to) in moves)
        {
            try
            {
                /*
                 * ⛔ 计划是几秒前算出来的：目标名可能在这期间被占掉（并发收同一份其余物 / 用户自己动过文件）。
                 * 搬之前**再看一眼**，占了就现场再让一次位 —— ⛔ 仍然绝不覆盖。
                 *
                 * ⚠ 这是兜底，不是根因：同一份计划里两个同名内层包互相撞名，已经在
                 * `ChainInnerPackageMovePlanner.Plan` 里用 `reserved` 解决（真机 2026-10-01 那批就栽在那一处）。
                 *
                 * ⚠ 2026-10-12：这一段（让位 + 同卷改名/跨卷复制 + 绝不覆盖）**收成一个出口**
                 * <see cref="RestArtifactMover.TryMoveIntoRest"/> —— 「其余物 = 不处理」档那一支（`ReclaimLayerFilesToRest`）
                 * 也要用同一套动作，⛔ 不许各写一份。
                 */
                if (!RestArtifactMover.TryMoveIntoRest(from, to, movedTargets, out string target, out string? moveFailure))
                {
                    failures.Add($"{Path.GetFileName(from)}（{moveFailure}）");
                    _host.AppendLog("WARN", $"{rootTask.LogName}：内层包没能移入其余物：{from} —— {moveFailure}（它留在原地）");
                    continue;
                }

                /*
                 * 任务对象跟着改位置：续解扫描靠它把"刚搬走的内层包"排除掉。
                 *
                 * ⚠ 2026-10-02 补齐：这里原来**只改 CurrentPath**，而任务上指向同一个文件的地方有两处
                 * （另一处是分卷清单 `VolumePaths`）—— 只改一条就会在账上留下"盘上已不存在"的旧名字，
                 * 下一次按分卷组搬源包时 `SourcePackageMover.ResolveSourceGroup` 判"清单非空、却不含自己"
                 * ⇒ **整组一份都不搬**（用户 2026-10-02 收口后的保守档），源包留在原地还要解释一遍。
                 * 现在与"源包搬进其余物"（ApplySourceMoveResult）、"整组改名"（SyncTasksAfterVolumeRename）
                 * 走同一个出口：`TaskPathSync.ApplyMove`。
                 */
                TaskPathSync.ApplyMoves(
                    chainTasks,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = target });

                /*
                 * 一行说完，**不写两条完整绝对路径**（用户 2026-09-26 第 45 条：
                 * 真机上这一行是 200 字符上下 × 38 个包，而两条路径对用户没有信息量 ——
                 * 他关心的是"哪个内层包进了其余物"，不是它待在 H:\ 的哪一层）。
                 */
                _host.AppendLog(
                    "INFO",
                    $"{rootTask.LogName}：内层包已移入其余物 —— {Path.GetFileName(from)}"
                    + $"（{Path.GetFileName(Path.GetDirectoryName(target)) ?? string.Empty} 那一层）");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(from)}（{ex.Message}）");
                _host.AppendLog("WARN", $"{rootTask.LogName}：内层包没能移入其余物：{from} —— {ex.Message}（它留在原地）");
            }
        }

        if (moves.Count > failures.Count)
        {
            // 只有真的搬进去了才把其余物记到根任务上（否则删除档会去删一个不存在的目录）。
            rootTask.RestDirectoryPath = restDirectory;
        }

        /*
         * ===== **链内产出的片**也要集中进其余物（用户 2026-10-12 计划的第三个动作）=====
         *
         * 上面那一段只认**任务**（`candidate.IsContinuationTask`），而链内解出来的那一族片
         * **不是任务** ⇒ 「其余物 = 不处理」档下它们留在落地树里（基线 3 轮 ③✘：`run-4444` /
         * `run-11111111` / `run-19191919`），下一遍再导入就会被当成新原包。
         *
         * 名单来自**递归钩子每层报的** `GroupFiles`（`_host.ChainInnerLayerInputs`，只记账不动盘）。
         * ⛔ 搬的时机**只能在这里**（链尾、定稿之后）：实测（`run-4444`）挂在"逐层"那一刻去搬
         * 会让后续那一层的产物跟踪按路径找不到原件 ⇒ 内容物被重复解出来（`444(1)/(2).mp4`）。
         * ⛔ 只搬"确实还在盘上、且落在根任务输出范围之内"的；⛔ 判不出/搬不动 ⇒ 写一行、不动。
         */
        CollectLayerInputsIntoRest(rootTask, restDirectory, outputRoot);

        /*
         * ⛔ 2026-11-01 用户口径：过程物「先放进其余物，成功就彻底删」。
         * 只有**整条链全部成功**才在这里直接清空其余物（用户源包不在其中）；
         * 有跳过/失败/未展开的 ⇒ 留在其余物里，由链尾「删除操作」按用户选择处理。
         */
        if (ChainFullySucceeded(rootTask, chainTasks))
        {
            _purge.PurgeRestAfterSuccess(rootTask, planAllowedRoot, cancellationToken);
        }
    }

    /// <summary>
    /// 整条续解链是否**全部成功**。
    ///
    /// <para>⚠ 2026-11-01 真机：跟班卷（分卷组后续卷）按设计就是 `Skipped`，
    /// 把 Skipped 当失败会让成功链也不清空其余物（真机 `6191.exe` 5.1 GB 留在其余物里）。
    /// 只有失败/取消/部分完成/未处理才算否。</para>
    /// </summary>
    private static bool ChainFullySucceeded(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks)
    {
        if (rootTask == null || rootTask.Outcome != TaskOutcome.Succeeded)
        {
            return false;
        }

        return chainTasks == null
            || chainTasks.All(task => task != null
                && task.Outcome is TaskOutcome.Succeeded or TaskOutcome.Skipped);
    }

    /// <summary>
    /// **把链内"用过的那几个文件"集中进其余物**（链尾那一刻；名单由递归钩子逐层记账）。
    ///
    /// <para>只搬三条件同时成立的那些：① 还在盘上；② 落在根任务的**输出范围之内**（⛔ 不碰别处）；
    /// ③ 不在其余物目录里（幂等）。执行体 = <see cref="RestArtifactMover.TryMoveIntoRest"/>（与旁边那一段同一套让位/复制语义）。</para>
    /// </summary>
    internal void CollectLayerInputsIntoRest(ArchiveTask rootTask, string restDirectory, string outputRoot)
    {
        if (rootTask == null || _host.ChainInnerLayerInputs.Count == 0 || string.IsNullOrWhiteSpace(restDirectory))
        {
            return;
        }

        var movedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int moved = 0;
        var failures = new List<string>();

        foreach (string from in _host.ChainInnerLayerInputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (!File.Exists(from))
            {
                continue;
            }

            if (outputRoot.Length > 0
                && !ArchivePathGuard.IsInsideRoot(outputRoot, from, out _))
            {
                // 不在根任务的输出范围里 ⇒ 不是"这一条链的产物" ⇒ 一个字节都不动。
                continue;
            }

            if (ProcessArtifactLayout.IsInsideArtifactDirectory(from, Path.GetDirectoryName(restDirectory) ?? string.Empty))
            {
                continue;
            }

            if (RestArtifactMover.TryMoveIntoRest(from, Path.Combine(restDirectory, Path.GetFileName(from)), movedTargets, out _, out string? moveFailure))
            {
                moved++;
                continue;
            }

            failures.Add($"{Path.GetFileName(from)}（{moveFailure}）");
        }

        if (moved > 0)
        {
            rootTask.RestDirectoryPath = restDirectory;

            _host.AppendLog(
                "INFO",
                $"{rootTask.LogName}：链内那一族的片已集中进其余物 {moved} 个 —— {ProcessArtifactLayout.ArtifactDirectoryName}"
                + (failures.Count > 0 ? $"（{failures.Count} 个没搬成，仍在原处）" : string.Empty));
        }
    }

    /// <summary>
    /// **如实点名"成品目录里还留着的内层包"**（唯一出口；链尾那一站**无条件**调它）。
    ///
    /// <para><b>为什么不能当过程物清掉</b>（用户 2026-09-28 真机：`amb909\amb909\amb.7z.001..004`
    /// 一直留在成品目录里，他以为是"忘了搬进其余物"）：过程物（内层包）能清的前提是"它的内容物已经
    /// 解出来了" —— 这一单跑成功、内层包被判**已完成**才成立。而"这一轮根本没跑"的内层包，内容还在它
    /// 里面，清掉（其余物在删除档下是**彻底删除**）等于把唯一一份内容删了 ⇒ 只做一件事：**如实点名 +
    /// 告诉他怎么处理**（判据与搬运用同一套：分卷组取 `VolumePaths` 全卷，单文件取自己；只认在本单输出目录里的）。</para>
    ///
    /// <para>⚠ 2026-10-15：这一段原来长在 <see cref="CollectAsync"/> 里，
    /// 跟着"链尾要不要搬"那一个判据（`!_host.LayerReclaimThisBatch`）一起被跳过；而逐层回收现在**恒开**
    /// ⇒ 那一整段在普通档下永不执行 ⇒ 这句点名被**静默**掉（实测 `ChainRestSweepTests` 三条守门变红）。
    /// 判据与动作于是拆开：**诊断恒做**，搬照旧只在该搬的时候搬。</para>
    /// </summary>
    internal void ReportLeftBehindInnerPackages(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks)
    {
        if (rootTask == null || chainTasks == null || chainTasks.Count == 0)
        {
            return;
        }

        string outputRoot = string.IsNullOrWhiteSpace(rootTask.OutputPath)
            ? rootTask.ContentDirectoryPath
            : rootTask.OutputPath;

        try
        {
            var leftBehind = new List<string>();

            /*
             * ⛔ 判据与搬运那一边**同一套**（`ChainInnerPackageMovePlanner.Plan`）：只有"自己这一单成功且校验通过"
             * 的内层包才会被收进其余物 —— 在这里再点名它们就是自相矛盾的假警报。
             *
             * ⚠ 2026-10-04 起"根任务成功 ⇒ 链上内层包都会被收走"这半句**不再成立**：
             * 未解开过的内层包一条都不搬（用户当场推翻旧口径，见 ChainInnerPackageMovePlanner.Plan）。
             * 两边必须同时改（§9.5：同一件事只能有一个判据），否则又会出现
             * "警告说还留着、下一行就搬走删掉"那种自相矛盾。
             *
             * 真机 2026-10-01：这一行写着「成品目录里还留着 2 个内层包没被清理（它们的内容还没解出来，
             * 清掉就等于删内容）」，紧接着两行就把那两个搬进其余物并彻底删掉了 ——
             * 用户读到的是"程序自己跟自己打架"，而真正没被清掉的那两个（撞名搬失败的）它反倒没说是为什么。
             */
            foreach (ArchiveTask? candidate in chainTasks)
            {
                if (candidate == null ||
                    ReferenceEquals(candidate, rootTask) ||
                    !candidate.IsContinuationTask)
                {
                    continue;
                }

                bool candidateSucceeded =
                    candidate.Outcome == TaskOutcome.Succeeded &&
                    candidate.OutputVerification == OutputVerificationOutcome.Passed;

                // 会被收走的（**只有它自己成功那一种**）：不进"还留着"的名单。
                if (candidateSucceeded)
                {
                    continue;
                }

                IEnumerable<string> candidatePaths = candidate.IsVolumeGroup && candidate.VolumePaths.Count > 0
                    ? candidate.VolumePaths
                    : new[] { candidate.CurrentPath };

                foreach (string candidatePath in candidatePaths)
                {
                    if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                    {
                        continue;
                    }

                    /*
                     * 已经在「其余物」里的，正是“失败/部分完成时留下的最后一层过程物”这一档；
                     * 它已经按用户设置保留，不能再报成“还留在成品目录里、要加进任务列表”。
                     * 判据与搬运用同一个出口（ProcessArtifactLayout），⛔ 不按名字/样本名。
                     */
                    if (!string.IsNullOrWhiteSpace(outputRoot)
                        && ProcessArtifactLayout.IsInsideArtifactDirectory(
                            candidatePath,
                            Path.Combine(outputRoot, ProcessArtifactLayout.ArtifactDirectoryName)))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(rootTask.RestDirectoryPath)
                        && ArchivePathGuard.IsInsideRoot(rootTask.RestDirectoryPath, candidatePath, out _))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(outputRoot) &&
                        ArchivePathGuard.IsInsideRoot(outputRoot, candidatePath, out _))
                    {
                        leftBehind.Add(Path.GetFileName(candidatePath));
                    }
                }
            }

            if (leftBehind.Count > 0)
            {
                rootTask.RecordLeftBehindInnerPackages(leftBehind);

                _host.AppendLog(
                    "WARN",
                    $"{rootTask.LogName}：成品目录里还留着 {leftBehind.Count} 个内层包没被清理（它们的内容还没解出来，"
                    + $"清掉就等于删内容）：{string.Join("、", leftBehind.Take(5))}"
                    + $"{(leftBehind.Count > 5 ? " 等" : string.Empty)}"
                    + " —— 要解就把它们加进任务列表；删除档只清「内容物已经解出来」的过程物。");
            }
        }
        catch (Exception ex)
        {
            _host.AppendLog("WARN", $"{rootTask.LogName}：清点留在成品目录里的内层包失败（不影响这一单）：{ex.Message}");
        }
    }
}
