using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ArchiveFixer.Services;

/// <summary>
/// 定稿执行（移动）：按 <see cref="PlacementLayoutPlanner"/> 的计划把暂存产物搬进最终目录。
///
/// <para>四条硬要求原样保留（判据一个字都没动）：① 取消时把已经搬过去的搬回来；
/// ② 同名绝不默认覆盖（AutoRename 改名）；③ 单个文件搬不动只记一笔；
/// ④ 用户选了「覆盖」才走 <see cref="PathService.TryOverwriteLanding"/> 的两阶段落位。</para>
///
/// <para>本类只负责"搬"与"搬不成时怎么收场"：摘要/收尾日志在
/// <see cref="PlacementCommitLog"/>，落位结果换算在 <see cref="PlacementCommitSettlement"/>，
/// 取消回滚的实现在既有的 <see cref="FinalizeMoveSupport"/>（这里只触发，不重写判据）。</para>
/// </summary>
internal sealed class PlacementMoveExecutor
{
    private readonly IPlacementStepHost _host;
    private readonly PlacementLayoutPlanner _planner;
    private readonly PlacementCommitSettlement _settlement;

    internal PlacementMoveExecutor(
        IPlacementStepHost host,
        PlacementLayoutPlanner planner,
        PlacementCommitSettlement settlement)
    {
        _host = host;
        _planner = planner;
        _settlement = settlement;
    }

    /// <summary>
    /// 执行定稿：按 <see cref="PlacementLayoutPlanner.PlanFinalLayout"/> 的计划把暂存产物搬进最终目录。
    ///
    /// **只允许在后台线程上跑**（几千个 File.Move、跨盘还是拷贝）。
    /// 四条硬要求：
    /// ① 取消时把已经搬过去的**搬回来**（最终目录不许留半成品，契约 §6 第 2 条）；
    /// ② 同名绝不**默认**覆盖（AutoRename 改名，记进 <see cref="StageCommitResult.RenamedCount"/>）；
    /// ③ 单个文件搬不动只记一笔，不拖垮其余文件（与 <see cref="ExtractionWorkspace"/> 的搬运口径一致）；
    /// ④ 用户明确选了「覆盖」时才覆盖，而且必须走 <see cref="PathService.TryOverwriteLanding"/>
    ///    的两阶段（先挪到临时名 → 落位 → 再删），并逐个落点写日志留痕。
    /// </summary>
    /// <param name="conflictAction">同名冲突档（<see cref="ConflictActions"/> 的值）。</param>
    /// <param name="conflictDecision">
    /// 用户对同名冲突的答案（Ask 档；非 Ask 档为 null）。为 null 且档位是 Ask 时，
    /// <see cref="PathService.ResolveConflict"/> 会退回保守档（跳过）—— 绝不替用户决定成覆盖。
    /// </param>
    internal StageCommitResult ExecuteFinalLayout(
        ArchiveTask task,
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        string conflictAction,
        ConflictDecision? conflictDecision,
        CancellationToken cancellationToken,
        bool verificationPassed = true,
        RecursionResult? recursion = null,
        bool stageFilesAreProducedContent = false)
    {
        var logEntries = new List<(string Level, string Message)>();

        if (string.IsNullOrWhiteSpace(stageDirectory) ||
            string.IsNullOrWhiteSpace(destinationDirectory) ||
            !Directory.Exists(stageDirectory))
        {
            return new StageCommitResult { Attempted = false };
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new StageCommitResult
            {
                Attempted = true,
                Cancelled = true,
                Message = "定稿开始前已取消"
            };
        }

        FinalizePlan plan;

        try
        {
            plan = _planner.PlanFinalLayoutForTask(
                task,
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                terminalLayout,
                recursion,
                stageFilesAreProducedContent);
        }
        catch (Exception ex)
        {
            string readFailure = $"读取暂存目录失败：{ex.Message}";

            logEntries.Add(("ERROR", $"{task.LogName}：{readFailure}"));

            return new StageCommitResult
            {
                Attempted = true,
                FailedCount = 1,
                Failures = new[] { readFailure },
                Message = readFailure,
                LogEntries = logEntries
            };
        }

        if (plan.Failed)
        {
            /*
             * ⚠ 2026-10-09（用户点名第二条红字）：**"分卷组不完整"这一档不是失败、也不写 ERROR**。
             * 用户口径：「连完整的都没有，这就不是程序的错误」＋「在最后一步之前碰到分卷缺失的就先跳过」。
             * 判据读**结构化标志**（`plan.BlockedByIncompleteVolumeGroup`），⛔ 不比中文文案；
             * ⛔ "什么都不动"一个字没放松（照旧不定稿、不搬、不删）。
             */
            string planFailure = plan.BlockedByIncompleteVolumeGroup
                ? "这一层里有分卷组还没凑齐（先跳过，等最后那一轮再判）：" + plan.FailureReason
                : "定稿布局规划失败：" + plan.FailureReason;

            logEntries.Add((
                plan.BlockedByIncompleteVolumeGroup ? "WARN" : "ERROR",
                $"{task.LogName}：{planFailure}"));

            return new StageCommitResult
            {
                Attempted = true,
                FailedCount = 1,
                Failures = new[] { planFailure },
                Message = planFailure,
                LogEntries = logEntries
            };
        }

        foreach (string warning in plan.Warnings)
        {
            logEntries.Add(("WARN", $"{task.LogName}：定稿提醒 —— {warning}"));
        }

        if (plan.Moves.Count == 0)
        {
            /*
             * 引擎什么都没写出来（空包）：没有可定稿的东西，也不该凭空造一个空目录给用户。
             *
             * ⚠ "只有 0 字节产物"要单独说清楚（用户 2026-09-24 要求）：它看起来也是"没有产物"，
             * 但成因完全不同（密码不对 / 数据不全时 7z 会写 0 字节的桩文件），
             * 写成"这个包本来就是空的"会把人引到错的方向 —— 那句话必须带上"校验未通过，产物视为无效"。
             */
            string emptyMessage = plan.ZeroByteProductsOnly
                ? "暂存目录里只有 0 字节产物 —— 校验未通过，产物视为无效：不定稿、不生成其余物、源包留在原地。"
                : "暂存目录里没有产物，没有需要定稿的内容。";

            logEntries.Add(("WARN", $"{task.LogName}：{emptyMessage}"));

            return new StageCommitResult
            {
                Attempted = true,
                Message = emptyMessage,
                LogEntries = logEntries
            };
        }

        bool destinationExisted = Directory.Exists(destinationDirectory);

        if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
        {
            string createFailure = $"最终目录不存在且创建失败：{destinationDirectory}";

            logEntries.Add(("ERROR", $"{task.LogName}：{createFailure}"));

            return new StageCommitResult
            {
                Attempted = true,
                FailedCount = plan.Moves.Count,
                Failures = new[] { createFailure },
                Message = createFailure,
                LogEntries = logEntries
            };
        }

        var moved = new List<(string From, string To)>();
        var failures = new List<string>();
        var overwritten = new List<string>();

        int movedContent = 0;
        int movedProcess = 0;
        int renamed = 0;
        int skippedContent = 0;
        int skippedProcess = 0;

        // 计划里 **内容物在前、其余物在后**：先让用户要的东西落位，再收拾过程物。
        foreach (PlannedMove move in plan.Moves)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                FinalizeMoveSupport.RollBackFinalLayout(moved, destinationDirectory, destinationExisted);

                logEntries.Add(("WARN", $"{task.LogName}：定稿被取消，已把 {moved.Count} 项退回暂存目录。"));

                return new StageCommitResult
                {
                    Attempted = true,
                    Cancelled = true,
                    Message = "定稿被取消，产物已退回暂存目录",
                    LogEntries = logEntries
                };
            }

            bool isProcessArtifact = plan.ProcessArtifactSources.Contains(move.From);

            try
            {
                // 计划里的一条可能是**文件**，也可能是**整个目录**（判定表 2/3/4 会整棵搬）。
                bool isDirectory = Directory.Exists(move.From) && !File.Exists(move.From);

                /*
                 * ⚠ 2026-10-09 真机 `EEEE`（用户最在意的那条「为什么有两个 `111.zip`」）：
                 * **目标位上已经是同一份物理文件 ⇒ 不必再搬**（⛔ 不删、⛔ 不改名，直接跳过这一条）。
                 *
                 * 现场（这一条**两个改动缺一不可**：上面把"本链产出"判成内容物之后，
                 * 它就会来定稿落地，于是撞上这个形状）：接片给"**本单自己暂存目录**"里那一份
                 * **建的是硬链接**（`ShouldMovePieceIntoGroup` 那一档只建链接、绝不搬走，
                 * 真机行 146 的 `moveIntoGroup=False` 就是它）⇒ `111\111\111.zip` 与暂存里的
                 * `111.zip` **是同一个 FileId**。定稿再搬一次，只会在同一个目录里多出一个
                 * `111(1).zip` —— 用户看到的"两个"就从"其余物里一个"变成"`(1)` 一个，症状根本不消失。
                 *
                 * 判据只读事实（唯一出口 <see cref="FileIdentity.IsSamePhysicalFile"/>：卷序列号 + 文件索引），
                 * ⛔ 不比名字、⛔ 不看大小。**一个字节都不动**：数据本来就已经在目标位上（同一条记录）。
                 */
                if (!isDirectory && FileIdentity.IsSamePhysicalFile(move.From, move.To))
                {
                    logEntries.Add((
                        "INFO",
                        $"{task.LogName}：{Path.GetFileName(move.To)} 已经在目标位上、而且是「同一份文件」"
                        + "（接片时建的硬链接）—— 不必再搬一次，也不留多余的名字。"));

                    continue;
                }

                /*
                 * ⚠ 2026-10-09 真机 `EEEE` 的另一半（同一个"两个 `111.zip`"的另一形状）：
                 * **这一片已经由接片搬进"这一组的落点层"了 ⇒ 产出方自己落点里这一份是多余名**。
                 *
                 * 与上面那一条的区别（真机行 161 实测）：那一档 `moveIntoGroup=True` ⇒ 接片对
                 * 层产物用的是**移动**（不是建链接），所以两边**不是同一个 FileId**，
                 * `IsSamePhysicalFile` 判不出来 —— 只能按"组层是不是已经有这个名字"判。
                 *
                 * 判据两条事实，缺一不可：① 这一份是**分卷片**（`IsVolumePartFile`）；
                 * ② 这一组的**落点层**（唯一出口 `ResolveBatchGroupDirectory`）里已经有同名的一份。
                 * ⇒ 那才是它的正位（整组在那一层拼、在那一层解），产出方落点里这一份就是
                 * 用户点名过的"没人认领的名字"（`SiblingFolderVolumeGatherTests` ③ 那条用例钉的就是它）。
                 * ⛔ 数据一个字节都不丢：正位那一份已经在了。
                 */
                if (!isDirectory && StageFilePredicates.IsVolumePartFile(move.From))
                {
                    string pieceName = Path.GetFileName(move.From);

                    /*
                     * ⚠ **读"接片那一刻定下来的层"，⛔ 不在这里重算**（2026-10-11 实测）：
                     * 这一档原来转调 `ResolveBatchGroupDirectory` 的 1 参版，而它当年靠"先到先得"那条
                     * 兜底才答得出层；兜底按用户口径删掉之后它答不出 ⇒ 这道闸门**静默失效**
                     * ⇒ 本单的工作区那一份又被发布到产出方落点里，成了**没人认领的多余名**
                     * （实测残留逐字 `…\out-…\111(2)_\111.z01`；守门用例
                     * `SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
                     * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它）。
                     * 层只有一个出口：接片那一档算过的那个（`_groupLayerByBaseName`）。
                     */
                    string groupLayer = _host.TryResolveGroupLayer(
                        FileNameHelper.GetArchiveBaseName(pieceName),
                        out string rememberedLayer)
                        ? rememberedLayer
                        : string.Empty;

                    if (groupLayer.Length > 0 && File.Exists(Path.Combine(groupLayer, pieceName)))
                    {
                        logEntries.Add((
                            "INFO",
                            $"{task.LogName}：{pieceName} 已经由接片搬进「{Path.GetFileName(groupLayer)}」"
                            + "这一组的落点层了 —— 产出方这里这一份是多余的名字，不落地（一个字节都不丢）。"));

                        continue;
                    }
                }

                if (!SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(move.To)))
                {
                    failures.Add($"{Path.GetFileName(move.To)}（无法创建目标目录）");
                    continue;
                }

                string target = move.To;

                // 覆盖档由 TryOverwriteLanding 自己把新条目搬过去（它要先腾位），这里不再搬第二次。
                bool landed = false;

                if (File.Exists(target) || Directory.Exists(target))
                {
                    /*
                     * 目标是**最终目录自己**时不许覆盖：那等于把刚落位的产物连同目录一起顶掉，
                     * 而且是用户完全没预期的一次删除。规划器现在不会给出这种条目
                     * （计划里永远是 destDir\子项），这里是最后一道保险。
                     */
                    bool isDestinationRoot = SafePathHelper.PathEquals(target, destinationDirectory);

                    ConflictResolution resolution = isDestinationRoot
                        ? new ConflictResolution(ConflictChoice.AutoRename, target, true, false)
                        : _host.Paths.ResolveConflict(
                            target,
                            isDirectory ? ConflictTargetKind.Directory : ConflictTargetKind.File,
                            conflictAction,
                            conflictDecision);

                    if (resolution.Choice == ConflictChoice.Skip)
                    {
                        // 「跳过」：这一项不动，已有文件一个字节都不改（新产物留在暂存目录）。
                        if (isProcessArtifact)
                        {
                            skippedProcess++;
                        }
                        else
                        {
                            skippedContent++;
                        }

                        logEntries.Add(("WARN", $"{task.LogName}：同名冲突，按你的选择跳过：{target}"));
                        continue;
                    }

                    if (resolution.Choice == ConflictChoice.Overwrite && !isDestinationRoot)
                    {
                        /*
                         * 「覆盖」= 两阶段落位（AGENTS.md §6 第 3 条）：
                         * 先把占位者挪到临时名 → 新条目落位 → 落位成功后才删掉那个临时名。
                         * **禁止"先 File.Delete 再 Move"**：中间任何失败都会让用户想保留的那一份
                         * 永久消失，而新条目还没到位。
                         */
                        if (!_host.Paths.TryOverwriteLanding(
                                move.From,
                                target,
                                isDirectory,
                                out string overwriteError,
                                out string overwriteNote))
                        {
                            failures.Add($"{Path.GetFileName(move.From)}（{overwriteError}）");
                            continue;
                        }

                        // 真正覆盖前要留痕：哪个落点被顶掉了必须写进日志（AGENTS.md §6 第 3 条 / 用户要求）。
                        overwritten.Add(target);
                        landed = true;

                        logEntries.Add((
                            "WARN",
                            $"{task.LogName}：同名冲突，按你的选择覆盖：{target}（{overwriteNote}）"));
                    }
                    else
                    {
                        // 自动重命名：换成 名字(1)，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                        string renamedTarget = resolution.TargetPath;

                        if (!string.Equals(renamedTarget, target, StringComparison.OrdinalIgnoreCase))
                        {
                            renamed++;
                        }

                        logEntries.Add(("INFO", $"{task.LogName}：同名冲突，改名落位（绝不覆盖）：{renamedTarget}"));

                        target = renamedTarget;
                    }
                }

                if (!landed)
                {
                    if (isDirectory)
                    {
                        Directory.Move(move.From, target);
                    }
                    else
                    {
                        File.Move(move.From, target);
                    }
                }

                moved.Add((move.From, target));

                if (isProcessArtifact)
                {
                    movedProcess++;
                }
                else
                {
                    movedContent++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(move.From)}（{ex.Message}）");
            }
        }

        /*
         * 抠出来的内嵌归档：它是从源文件按偏移再生出来的派生数据，契约 §3.2 把它列为其余物。
         * 它**不在暂存区里**（在任务工作区根下，是解压的输入），所以不在上面的计划里，单独搬一次。
         * 为什么默认不搬：见 MoveCarvedArtifactIntoProcessDirectory 的说明。
         */
        if (_host.MoveCarvedArtifactIntoProcessDirectory && task.EmbeddedArchiveOffset > 0)
        {
            string carvedPath = _host.BuildEmbeddedArchivePath(task);

            if (File.Exists(carvedPath))
            {
                try
                {
                    string processRoot = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
                        ? Path.Combine(destinationDirectory, ProcessArtifactLayout.ArtifactDirectoryName)
                        : plan.ProcessArtifactDirectory;

                    string carvedTarget = Path.Combine(processRoot, Path.GetFileName(carvedPath));

                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(carvedTarget));

                    if (File.Exists(carvedTarget))
                    {
                        carvedTarget = SafePathHelper.AutoRenameFilePath(carvedTarget);
                        renamed++;
                    }

                    File.Move(carvedPath, carvedTarget);

                    movedProcess++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(carvedPath)}（抠出的中间归档：{ex.Message}）");
                }
            }
        }

        string processDirectory = string.IsNullOrWhiteSpace(plan.ProcessArtifactDirectory)
            ? Path.Combine(destinationDirectory, ProcessArtifactLayout.ArtifactDirectoryName)
            : plan.ProcessArtifactDirectory;

        /*
         * 摘要与收尾日志的唯一出口：`summary` 同时是日志那一行（"定稿完成 —— …"）与①页
         * 「错误信息」列（StageCommitResult.Message）的来源 —— 一处出口、两个消费点。
         */
        string summary = PlacementCommitLog.BuildSummary(
            plan,
            destinationDirectory,
            processDirectory,
            movedProcess,
            renamed,
            overwritten.Count,
            skippedContent + skippedProcess,
            failures.Count,
            failures);

        string contentNote = PlacementCommitLog.BuildContentNote(plan.ContentFileCount, verificationPassed);

        PlacementCommitLog.AppendCompletionEntries(
            logEntries,
            task.LogName,
            plan,
            summary,
            contentNote,
            failures.Count,
            overwritten);

        List<string> producedFiles = PlacementCommitSettlement.CollectProducedFiles(plan, moved);

        return new StageCommitResult
        {
            Attempted = true,
            PlannedContentCount = plan.PlannedContentCount,
            MovedContentCount = movedContent,
            MovedProcessCount = movedProcess,
            ContentFileCount = plan.ContentFileCount,
            ProcessArtifactDirectory = processDirectory,
            ContentDirectory = _settlement.ResolveContentLayerDirectory(destinationDirectory, processDirectory),
            ProcessArtifactBytes = plan.ProcessArtifactTotalSize,
            RenamedCount = renamed,
            SkippedCount = skippedContent + skippedProcess,
            SkippedContentCount = skippedContent,
            OverwrittenCount = overwritten.Count,
            OverwrittenPaths = overwritten,
            FailedCount = failures.Count,
            Failures = failures,
            Message = summary,
            ProducedFiles = producedFiles,
            LogEntries = logEntries
        };
    }
}
