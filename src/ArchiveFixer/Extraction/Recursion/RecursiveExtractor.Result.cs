using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

public sealed partial class RecursiveExtractor
{        /// <summary>
         /// 组装最终结论。
         ///
         /// 这里集中处理三件容易搞混的事（规则 6/7/8）：
         /// ① <see cref="RecursionResult.Completed"/> 只在真正走完时为 true；
         /// ② 部分完成时产物**留在工作区**、Summary 里必须写清工作区在哪；
         /// ③ 全完成时才发布，且发布失败要如实说出来，不能因为"层都解完了"就假装成功。
         ///
         /// ⚠ 工作区清理**不在这里**：本方法只负责给出结论（删东西要看结论 + 取消令牌，
         /// 见 <see cref="FinalizeRun"/>）。发布失败时它会把 Completed 降成 false，
         /// 那个 false 正是"产物还压在工作区里、绝不能删"的判据。
         /// </summary>
    private RecursionResult BuildResult(
        RecursionStopReason stopReason,
        List<RecursionLayerReport> layers,
        RecursionDecisionRequest? decision,
        ExtractionWorkspace workspace,
        string finalOutputDirectory,
        string extraMessage,
        bool published,
        IReadOnlyList<string>? unexpandedNames = null,
        string passwordFallbackNote = "",
        Exception? failureException = null)
    {
        bool completed = stopReason == RecursionStopReason.Completed;
        bool partiallyCompleted = !completed && layers.Any(layer => layer.Success);

        /*
         * 一层都没解出来时指向工作区目录本身：那些情形（取消、第一次就失败）下产物还没有，
         * 但"工作区在哪"正是排查与重试最需要的信息，而这个目录在工作区建出来时就一定存在。
         */
        string finalOutputPath = workspace.Layers.Count > 0
            ? workspace.Layers[^1].OutputPath
            : workspace.TaskDirectory;

        string publishMessage = string.Empty;

        if (published)
        {
            /*
             * ⛔ 展开了内层包 ⇒ 走「**就地替换**」那一档（用户 2026-09-30 中午）：
             * 每个被解开的内层包在**它原来的位置**留下一个以它命名的文件夹
             * （<c>AAA\DDDD\内容物</c>、<c>AAA\BBBB\CCCCC\内容物</c>），真文件原地不动 ——
             * ⛔ 不再"把所有叶子层产物摊到发布目标根上"（那等于把它们都搬到顶层）。
             * 「中间层省不省」读同一个出口；**首层与末层不受它影响**。
             *
             * 判据读**唯一**那个出口（PackageLayerRules），本方法自己数层数会与定稿侧漂移。
             * 只解了一层（没有内层包）时照旧摊外壳：那一份口径有很多既有用例钉着，一个字都不改。
             */
            bool inPlace = PackageLayerRules.ExpandedInnerPackage(layers);

            WorkspacePublishResult publishResult = workspace.Publish(
                finalOutputDirectory,
                inPlaceInnerPackages: inPlace,
                alreadyReclaimedPaths: ReclaimedProcessArtifacts,
                intakeStaging: IsInsideWorkspaceRoot(finalOutputDirectory));

            publishMessage = publishResult.Message;

            /*
             * ⛔ 发布侧"判不出就没动"的那几件事**必须一条一条说出来**（2026-10-03 就地替换那一档）：
             * 典型是"这一组卷本该被替换掉，但同组有一片不在我们这一步搬进来的名单里（可能在别的目录 /
             * 名字认不出）⇒ 一个字节都不删"。不写出来，用户只会看到"产物里怎么还留着几个包"，
             * 而原因（我们不敢动）一个字都没有 —— 这正是 §9.5 那条"要删的动作判不出就什么都不做，
             * 而且要留证据"。⛔ 只是日志，不改任务结论（发布成功仍然是成功）。
             */
            foreach (string warning in publishResult.Warnings)
            {
                /*
                 * 前缀用**本次递归那个归档的文件名**（`BuildResult` 这一层拿不到任务的显示名，
                 * 调用方会在汇总那一行自己加任务名）—— 没有它，一批里几十个包的 WARN 会混在一起，
                 * 谁也认不出是哪一单。
                 */
                string label = Path.GetFileName(_lastRunArchivePath);

                Log("WARN", label.Length == 0 ? warning : $"{label}：{warning}");
            }

            if (publishResult.Success)
            {
                // 只有真的发出去了才改指向：发布失败时 FinalOutputPath 仍指向工作区里的产物，
                // 用户按这个路径还能找到东西。
                finalOutputPath = publishResult.DestinationPath;
            }
            else
            {
                completed = false;
                partiallyCompleted = true;
            }
        }

        WriteRecoveryReport(stopReason, layers, workspace, finalOutputPath, completed, partiallyCompleted);

        /*
         * 临时链接的最后一道兜底：正常路径上每一层跑完就删了（主循环里那一处），
         * 这里再兜一次（异常 / 提前 return 的那些路）—— ⛔ 绝不把我们的临时名字留在盘上。
         */
        RemoveCrossBoundaryLinks();

        /*
         * ⛔ 「这一趟有没有解出过东西」的**盘上那一半**必须在收尾这一刻数一次（见 ProducedAnyLayerOutput）：
         * 失败层的报告里文件数按定义是 0，可它的产物目录里可能真有东西；而工作区随后就要被清掉/发布，
         * 晚一步这份事实就没了。
         *
         * ⚠ 判据必须走**唯一出口** `ProducedContentGate`（⛔ 不是"目录里有文件就算"）：
         * 那一档的阈值与"每一条都失败"两条判据是**与错密码签名分开**用的 —— 实测（守门
         * `ExtractionPipelineFixTests.只解出一个文件时_仍按老口径继续试下一个候选`）：只留下**一个**文件时
         * 按老口径仍是「密码错误 / 失败」，而"目录里有没有文件"会把这种错密码签名读成"解出过内容物" ⇒
         * 状态被改成「部分完成」（当场红）。
         */
        bool anyLayerProductsOnDisk = layers.Any(layer =>
            ProducedContentGate.TryMeasure(
                layer.OutputPath,
                layer.ReportedSubItemErrors,
                layer.FailedEntryNames.Count,
                out int layerFiles,
                out _)
            && layerFiles >= ProducedContentGate.MinimumFiles);

        return new RecursionResult
        {
            StopReason = stopReason,
            Completed = completed,
            PartiallyCompleted = partiallyCompleted,
            Decision = decision,
            Layers = layers.ToList(),
            AnyLayerProductsOnDisk = anyLayerProductsOnDisk,
            FinalOutputPath = finalOutputPath,
            ConsumedVolumeSources = _consumedVolumeSources.ToList(),
            UnresolvedVolumePieces = _unresolvedVolumePieces.ToList(),
            UnexpandedNames = unexpandedNames?.ToList() ?? new List<string>(),
            FailureException = failureException,
            Summary = BuildSummary(
                stopReason,
                layers,
                workspace,
                finalOutputPath,
                completed,
                publishMessage,
                extraMessage,
                unexpandedNames ?? Array.Empty<string>(),
                passwordFallbackNote)
        };
    }

    /// <summary>
    /// 一次运行的收尾：把结论交回调用方之前，按结论决定要不要清掉**本次运行自己建的那个**工作区。
    ///
    /// 只有"真的走完 + 没被取消"才清（AGENTS.md §6 第 13 条与单层路径
    /// <c>ExtractionCoordinator.CleanupTaskWorkspaceDirectory</c> 同一口径）：
    /// · <see cref="RecursionResult.Completed"/> 已经蕴含"产物发布成功"（发布失败时
    ///   <see cref="BuildResult"/> 会把它降成 false），所以这一条同时覆盖"产物已出去"这个前提；
    /// · 取消可能在最后一层解完之后才到（队列已经空了），那时结论仍是 Completed ——
    ///   按规则**不许清**，所以要在这里再看一眼令牌；
    /// · 失败 / 部分完成 / 等用户决定一律保留：那些目录里的中间产物是用户唯一的线索，
    ///   "失败能定位到层、能从那一层重试"这条能力本身就依赖它们还在（见类注释）。
    ///
    /// 为什么这条线放在本类而不是调用方：工作区是本类建出来的，谁来建谁负责收 ——
    /// 调用方（协调器）只拿到一个结论对象，它无从知道这次到底建了哪个目录。
    /// </summary>
    private void RetainProcessArtifactBeforeConsumption(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(RetainedArtifactDirectory)
            || string.IsNullOrWhiteSpace(archivePath)
            || !File.Exists(archivePath))
        {
            return;
        }

        try
        {
            string restDirectory = Path.Combine(
                RetainedArtifactDirectory,
                ProcessArtifactLayout.ArtifactDirectoryName);
            Directory.CreateDirectory(restDirectory);

            string target = Path.Combine(restDirectory, Path.GetFileName(archivePath));

            if (!File.Exists(target))
            {
                File.Copy(archivePath, target, overwrite: false);
            }

            RetainedProcessArtifactPath = target;
        }
        catch
        {
            // 留不下不影响解压结论；收尾那一档会按原有路径继续。
        }
    }

    private string? ResolveRetainedArtifactRoot(string outputRoot)
    {
        if (!string.IsNullOrWhiteSpace(RetainedArtifactDirectory))
        {
            return RetainedArtifactDirectory;
        }

        string? parent = Path.GetDirectoryName(outputRoot);
        return string.IsNullOrWhiteSpace(parent)
            ? null
            : Path.Combine(parent, "kept");
    }

    private static string? ResolveWorkspaceOutputRoot(string workspaceRoot)
    {
        string? cursor = workspaceRoot;

        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if (string.Equals(Path.GetFileName(cursor), ".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(cursor);
            }

            cursor = Path.GetDirectoryName(cursor);
        }

        return null;
    }

    private RecursionResult FinalizeRun(
        RecursionResult result,
        ExtractionWorkspace workspace,
        string taskLabel,
        CancellationToken cancellationToken)
    {
        if (_limits.KeepDeepestProcessArtifact
            && result.Completed
            && result.Layers.Count > 0)
        {
            RecursionLayerReport? deepest = result.Layers.LastOrDefault(layer => layer.Success && layer.Depth > 0);

            string? outputRoot = ResolveWorkspaceOutputRoot(workspace.RootDirectory);

            if (deepest != null
                && !string.IsNullOrWhiteSpace(outputRoot)
                && Directory.Exists(outputRoot)
                && File.Exists(deepest.ArchivePath))
            {
                string? retainedRoot = ResolveRetainedArtifactRoot(outputRoot);

                if (string.IsNullOrWhiteSpace(retainedRoot))
                {
                    return result;
                }

                string retained = Path.Combine(
                    retainedRoot,
                    "其余物",
                    Path.GetFileName(deepest.ArchivePath));

                Directory.CreateDirectory(Path.GetDirectoryName(retained)!);

                if (!File.Exists(retained))
                {
                    try
                    {
                        File.Copy(deepest.ArchivePath, retained, overwrite: false);
                        Log(
                            "INFO",
                            $"{taskLabel}：空间不足模式 —— 最后一层过程物已留下：{Path.GetFileName(retained)}");
                    }
                    catch
                    {
                        // 留不下不影响结论。
                    }
                }
            }
        }

        if (result.Completed && !cancellationToken.IsCancellationRequested)
        {
            CleanupWorkspace(workspace, taskLabel, "递归解压已完成且产物已发布");
            CleanupSupersededWorkspaces(taskLabel);
        }

        return result;
    }

    /*
     * ===== 失败收尾段（RecursiveResultBuilder）的宿主出口 =====
     *
     * 这一段过去全部写在本类实例方法里，直接用私有字段；搬类之后只把**它本来就在用**的
     * 那几个出口按 internal 露出来（行为一个字不改：清法、报告、停因观察口、写穿字段都同一份）。
     * ⛔ 不新增判据、不新增状态。
     */

    /// <summary>交给换引擎兜底与清单恢复的主引擎（与候选循环用的是**同一个**实例）。</summary>
    internal IArchiveEngine Engine => _engine;

    /// <summary>递归上限：点名"哪几条候选没试到"时要用每层密码尝试上限。</summary>
    internal RecursionLimits Limits => _limits;

    /// <summary>
    /// 换引擎兜底那句结论（"已换 WinRAR 再试过一遍" / "没找到 WinRAR，所以没换引擎再试"）。
    ///
    /// <para>⚠ **写穿到实例字段**、不放进收尾段的局部量：本方法可能被
    /// <see cref="TryCarveEmbeddedInnerArchiveAsync"/> 递归重入，外层收尾时整份回写会把
    /// 内层刚写下的那句话覆盖掉（同一层里两句话只有一句成立）。</para>
    /// </summary>
    internal string PasswordFallbackNote
    {
        get => _passwordFallbackNote;
        set => _passwordFallbackNote = value;
    }

    /// <summary>
    /// 「本层已在更精确的分支里报过停因」——同样**写穿实例字段**（理由同上：重入时外层不许覆盖）。
    /// 调用方 <see cref="ExtractAsync"/> 读一次后会清回 false。
    /// </summary>
    internal void MarkExactStopReasonReported()
    {
        _exactStopReasonReported = true;
    }

    /*
     * ===== 候选循环（LayerExecutor）的宿主出口 =====
     *
     * 同样只把循环本来就在用的两笔账按方法露出来：账本仍是本类这两个字段
     * （谁建谁负责清，见 ExtractAsync 里那两处 Clear），⛔ 不复制第二份。
     */

    /// <summary>跨盘 ZIP 那一档建出来的临时链接（收尾要删干净：留着会被当成内容物发布）。</summary>
    internal void AddCrossBoundaryLinks(IReadOnlyList<string> linkedPaths)
    {
        _crossBoundaryLinks.AddRange(linkedPaths);
    }

    /// <summary>"这一组的内容已由谁解出"的记账（借来用过的用户源片）。</summary>
    internal void AddConsumedVolumeSources(IReadOnlyList<string> linkedSources)
    {
        _consumedVolumeSources.AddRange(linkedSources);
    }

}
