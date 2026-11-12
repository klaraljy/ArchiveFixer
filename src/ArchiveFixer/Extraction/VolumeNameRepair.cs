using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using static ArchiveFixer.Extraction.VolumeNamePlanner;
using static ArchiveFixer.Extraction.VolumeNameRestorer;

namespace ArchiveFixer.Extraction
{

    /// <summary>
    /// 「按建议改名并重试」的实现（纯逻辑 + 文件操作，不引用 WPF）。
    ///
    /// <para>建议名的推法与失败提示**共用同一份** <see cref="RawSplitStreamDetector.SuggestStandardFirstName"/>：
    /// ⛔ 界面上说的名字与真正改成的名字必须是同一个，否则用户点一下反而把文件改坏。</para>
    /// </summary>
    public static partial class VolumeNameRepair
    {
        public static IReadOnlyList<string?> EnumerateFileNamesInDirectory(string? filePath)
            => VolumeNamePlanner.EnumerateFileNamesInDirectory(filePath);

        public static IReadOnlyList<VolumeCandidate> EnumerateVolumeCandidatesInDirectory(string? filePath)
            => VolumeNamePlanner.EnumerateVolumeCandidatesInDirectory(filePath);

        public static IReadOnlyList<VolumeCandidate> EnumerateVolumeCandidatesNearby(string? filePath)
            => VolumeNamePlanner.EnumerateVolumeCandidatesNearby(filePath);

        public static IReadOnlyList<VolumeCandidate> EnumerateVolumeCandidatesNearby(
            string? filePath,
            IEnumerable<string>? extraPaths)
            => VolumeNamePlanner.EnumerateVolumeCandidatesNearby(filePath, extraPaths);

        public static VolumeNameRepairPlan Plan(string? currentPath, IEnumerable<string?>? fileNamesInDirectory)
            => VolumeNamePlanner.Plan(currentPath, fileNamesInDirectory);

        public static Task<VolumeNameRepairPlan> PlanByContentAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            ArchiveFixer.Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates = null,
            CancellationToken cancellationToken = default)
            => VolumeNamePlanner.PlanByContentAsync(
                currentPath, filesInDirectory, engine, workRootDirectory, passwordCandidates, cancellationToken);

        public static Task<VolumeNameRepairPlan> PlanByContentWithNearbyCandidatesAsync(
            string? currentPath,
            IEnumerable<VolumeCandidate>? filesInDirectory,
            ArchiveFixer.Engines.IArchiveEngine engine,
            string? workRootDirectory,
            IReadOnlyList<string>? passwordCandidates = null,
            CancellationToken cancellationToken = default)
            => VolumeNamePlanner.PlanByContentWithNearbyCandidatesAsync(
                currentPath, filesInDirectory, engine, workRootDirectory, passwordCandidates, cancellationToken);

        internal static bool TryDeriveSpannedStem(
            IReadOnlyList<string> ordered,
            string tailPath,
            int count,
            out string stem)
            => VolumeNamePlanner.TryDeriveSpannedStem(ordered, tailPath, count, out stem);

        public static bool HasSpannedZipTailNearby(string? anyFileInDirectory)
            => VolumeNamePlanner.HasSpannedZipTailNearby(anyFileInDirectory);

        public static bool TryStripSuffixJunk(string? fileName, out string cleaned)
            => VolumeNamePlanner.TryStripSuffixJunk(fileName, out cleaned);

        /// <summary>
        /// 池里的一份候选（路径 + 名字自报的卷号 + 实际字节数）。
        /// 单独一个类型只为了让下面那几段读起来不必到处写三元组。
        /// </summary>
        private readonly record struct GatherPiece(string Path, int Index, long Size);

        /// <summary>
        /// **跨层收卷**的结论（纯事实 + 一张计划；<see cref="ResolveCrossLayerVolumeGather"/> 一个字节都不动）。
        /// </summary>
        internal sealed class CrossLayerVolumeGather
        {
            /// <summary>
            /// 这一档适不适用：手上这一份是不是某组的成员之一（⛔ 不再要求它是**第 1 卷**，
            /// 用户 2026-10-17：「这一组该拼到哪一层」与"手上拿着哪一片"无关）。
            /// 不适用 ⇒ 调用方照旧走今天那条路（⛔ 一个字节都不动、也不免试）。
            /// </summary>
            public bool Applicable { get; init; }

            /// <summary>
            /// 这一组**收拢的目标那一份** = 第 1 卷（跨盘 ZIP 那一族是末片 <c>X.zip</c>）的完整路径。
            /// 空 = 还没判到那一步。调用方**必须**把开工起点换成它 —— 手上有可能是后续卷，
            /// 那一片会被搬进第 1 卷所在层（不改名的话原地不动）。
            /// </summary>
            public string? EntryPath { get; init; }

            /// <summary>要把这几卷**搬进入口那一层**（<c>CanRepair = true</c> 才有值）；执行体仍是
            /// <see cref="TryApply"/>（全成或全不成、绝不覆盖、失败倒序回滚）。</summary>
            public VolumeNameRepairPlan? Plan { get; init; }

            /// <summary>这一趟真要动的卷数（搬过来的 + 原地改名的；入口那一卷不算）。</summary>
            public int MovedCount { get; init; }

            /// <summary>入口那一层**已经自证完整** ⇒ 引擎直接解得开（既不用搬、也不该免试）。</summary>
            public bool CompleteBesideEntry { get; init; }

            /// <summary>
            /// **不许再拿单独一片去试**（用户 2026-10-05 拍板）：有硬证据说这一组凑不齐、
            /// 或者凑得齐却收不到入口这一层。判据只有 7z 起始头那条字节数证据（见方法注释的"免试"一节）。
            /// </summary>
            public bool ShouldSkipTrial { get; init; }

            /// <summary>能点名的缺卷（规范卷名；空 = "缺的是末卷之后的卷"，名字里看不出来）。</summary>
            public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();

            /// <summary>
            /// 这一趟**找卷用的基准**（用户 2026-10-05 口径：手上最浅的那一卷，⛔ 绝不是 001）。
            /// 空 = 这一档没跑到判基准那一步（不适用 / 判不出）。
            /// </summary>
            public string? AnchorPath { get; init; }

            /// <summary>给人看的一句话（收了哪几卷 / 为什么没收 / 缺哪几片）。</summary>
            public string Detail { get; init; } = string.Empty;
        }

        /// <summary>伪装名恢复入口；实现已移到 <see cref="VolumeNameRestorer"/>。</summary>
        public static IReadOnlyList<string> RestoreDisguisedInnerPackageNames(
            IReadOnlyList<string>? candidatePaths,
            Action<string, string>? log = null)
            => VolumeNameRestorer.RestoreDisguisedInnerPackageNames(candidatePaths, log);

        /// <summary>
        /// 照着计划**只改名字**。⛔ 绝无覆盖、绝无删除、绝不改内容：
        /// 目标名已存在就原地拒绝（<see cref="FileOperations.Move(string, string)"/> 在没有 overwrite 参数时
        /// 碰到已存在的目标会抛 —— 这里不用"先判断再移动"的写法当唯一防线，两道都在）。
        /// </summary>
        public static VolumeNameRepairResult TryApply(VolumeNameRepairPlan? plan)
        {
            if (plan == null || !plan.CanRepair)
            {
                return Failure(plan?.Reason ?? StatusText.VolumeRepairSourceMissing);
            }

            long sizeBefore;

            /*
             * 已经改成功的卷（旧名 → 新名）。整组改名必须"全成或全不成"：
             * 中途任何一卷失败，都要按这张表**倒序把名字改回去**，让盘上的状态与
             * "一组没改"这句话一致。
             * 真机现场（2026-09-30）：日志写「改名没成功」，盘上却已经出现 `111.part1.002` ——
             * 半改状态比不改更糟：7-Zip 按新基名去找后续卷，名字七零八落时整组都打不开。
             */
            var applied = new List<(string From, string To)>();

            try
            {
                if (!File.Exists(plan.CurrentPath))
                {
                    return Failure(StatusText.VolumeRepairSourceMissing);
                }

                if (string.Equals(plan.CurrentPath, plan.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(StatusText.VolumeRepairAlreadyStandard);
                }

                if (File.Exists(plan.TargetPath))
                {
                    return Failure(string.Format(StatusText.VolumeRepairTargetTakenFormat, plan.SuggestedFileName));
                }

                sizeBefore = new FileInfo(plan.CurrentPath).Length;

                FileOperations.Move(plan.CurrentPath, plan.TargetPath);

                applied.Add((plan.CurrentPath, plan.TargetPath));
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("分卷改名", "降级处理", ex);
                return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, ex.Message));
            }

            /*
             * 改完立刻核一遍：新名字在、旧名字没了、**字节数一个不差**。
             * 这三条是"只改了名字"的机器证据（改名本来不该动内容；对不上就说明有别的程序在动它，
             * 那时候必须如实报出来，而不是让后面的解压拿着一个说不清的文件去跑）。
             */
            try
            {
                if (!File.Exists(plan.TargetPath))
                {
                    return Undo(
                        applied,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, "改完之后新名字没找到"));
                }

                if (File.Exists(plan.CurrentPath))
                {
                    return Undo(
                        applied,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, "旧名字还在"));
                }

                long sizeAfter = new FileInfo(plan.TargetPath).Length;

                if (sizeAfter != sizeBefore)
                {
                    return Undo(
                        applied,
                        string.Format(
                            StatusText.VolumeRepairRenameFailedFormat,
                            plan.SuggestedFileName,
                            $"字节数变了（{sizeBefore} → {sizeAfter}）"));
                }
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("分卷改名", "降级处理", ex);
                return Failure(string.Format(StatusText.VolumeRepairRenameFailedFormat, plan.SuggestedFileName, ex.Message));
            }

            /*
             * 还有别的卷要改（网盘给整组缀了「删除」那种）：接着一卷一卷来。
             * ⛔ 每一卷都走与上面同一套判据（源在、目标未被占、字节数不差）；
             * 中途任何一卷失败 ⇒ 把**已经改过的全部改回去**（<see cref="Undo"/>），再如实报"一组没改、卡在哪一卷"。
             * ⛔ 绝不留半改状态 —— 旧口径是"已改的那几卷不会再动"，真机上正是它把用户的名字改成七零八落。
             */
            var rest = (plan.Items ?? Array.Empty<VolumeRepairItem>())
                .Where(i => !string.Equals(i.CurrentPath, plan.CurrentPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int done = 0;

            foreach (VolumeRepairItem item in rest)
            {
                try
                {
                    if (!File.Exists(item.CurrentPath))
                    {
                        return Undo(applied, $"{item.CurrentFileName}：{StatusText.VolumeRepairSourceMissing}");
                    }

                    if (string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.Exists(item.TargetPath))
                    {
                        return Undo(
                            applied,
                            string.Format(StatusText.VolumeRepairTargetTakenFormat, item.SuggestedFileName));
                    }

                    long before = new FileInfo(item.CurrentPath).Length;
                    FileOperations.Move(item.CurrentPath, item.TargetPath);

                    applied.Add((item.CurrentPath, item.TargetPath));

                    long after = new FileInfo(item.TargetPath).Length;

                    if (after != before)
                    {
                        return Undo(applied, $"字节数变了（{before} → {after}）");
                    }

                    done++;
                }
                catch (Exception ex)
                {
                    ArchiveFixer.Helpers.AppDiagnostics.Problem("分卷改名", "降级处理", ex);
                    return Undo(
                        applied,
                        string.Format(StatusText.VolumeRepairRenameFailedFormat, item.SuggestedFileName, ex.Message));
                }
            }

            return new VolumeNameRepairResult
            {
                Success = true,
                NewPath = plan.TargetPath,
                Message = done == 0
                    ? string.Format(StatusText.VolumeRepairDoneFormat, plan.CurrentFileName, plan.SuggestedFileName)
                    : string.Format(StatusText.VolumeRepairGroupDoneFormat, plan.Describe(), done + 1)
            };
        }

        /// <summary>
        /// 整组没改成 ⇒ 把**已经改过的名字倒序改回原名**，让盘上的状态与"一组没改"这句话一致。
        /// <para>回滚本身失败（原名被别的程序占了 / 文件被锁 / 新名字不见了）时**如实点名**，
        /// ⛔ 绝不让用户以为"没动过"。<c>why</c> = 卡在哪一卷、为什么。</para>
        /// </summary>
        private static VolumeNameRepairResult Undo(IReadOnlyList<(string From, string To)> applied, string why)
        {
            var stuck = new List<string>();

            for (int index = applied.Count - 1; index >= 0; index--)
            {
                (string from, string to) = applied[index];

                try
                {
                    if (!File.Exists(to))
                    {
                        stuck.Add(SafeFileName(to));
                        continue;
                    }

                    if (File.Exists(from))
                    {
                        // 原名又被占上了（别的程序插进来的）⇒ 不敢覆盖，只能如实点名。
                        stuck.Add(SafeFileName(to));
                        continue;
                    }

                    FileOperations.Move(to, from);
                }
                catch
                {
                    stuck.Add(SafeFileName(to));
                }
            }

            if (stuck.Count == 0)
            {
                return Failure(string.Format(StatusText.VolumeRepairRolledBackFormat, why));
            }

            return Failure(string.Format(
                StatusText.VolumeRepairRollbackIncompleteFormat,
                stuck.Count,
                string.Join("、", stuck)));
        }

        /// <param name="trialAttempted">这一份"不能改"的结论是不是**试开跑过之后**下的
        /// （只有真跑过试开才值得写一行日志说清结论，见 <see cref="VolumeNameRepairPlan.TrialAttempted"/>）。</param>
        /// <param name="byteBudgetMismatch">结论是不是 7z 起始头那条**字节数**证据给的（见
        /// <see cref="VolumeNameRepairPlan.ByteBudgetMismatch"/>：一次引擎都没调，但必须如实报出差的字节数）。</param>
        internal static VolumeNameRepairPlan Cannot(
            string path,
            string reason,
            bool trialAttempted = false,
            bool byteBudgetMismatch = false) => new()
            {
                CanRepair = false,
                Reason = reason,
                CurrentPath = path,
                CurrentFileName = SafeFileName(path),
                TrialAttempted = trialAttempted,
                ByteBudgetMismatch = byteBudgetMismatch
            };

        private static VolumeNameRepairResult Failure(string message) => new()
        {
            Success = false,
            Message = message
        };

        internal static string SafeFileName(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
