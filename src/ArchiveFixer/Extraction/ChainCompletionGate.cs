using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 链尾「删除操作」的**第六道门槛**：这条续解链必须**整条都成功**（用户 2026-09-25 第 37 条真机故障）。
    ///
    /// <para><b>现场</b>：他那个 12.22 GiB 的容器（`1-20+IF1-3.7z`）第一层**成功**了、三个分卷也进了其余物；
    /// 下一层 `Code Complete-BZ.7z(删掉.001` 判「分卷缺失」**失败**。链尾那一档当时只看根任务自己
    /// （Succeeded + 校验通过 + 其余物在）→ 把那一份 12.22 GiB 的其余物**彻底删了**。
    /// 用户看到的是"20 G 的素材只出来 8 G"，而且那 12 G 连过程物都不在了（只剩源容器）。</para>
    ///
    /// <para>判据只读**机器事实**（终态枚举 + 校验枚举，⛔ 不比对中文文案）：链上每个续解任务都必须是
    /// <see cref="TaskOutcome.Succeeded"/>，而且**完整性要可证**（L4 唯一出口
    /// <see cref="ResultCompletenessClassifier"/> = 可证完整）。**"还没跑过"与"判不出完整性"同样算拦下** ——
    /// 链没跑完、或者机器手上没有证据，都不许删。</para>
    /// </summary>
    public static class ChainCompletionGate
    {
        /// <summary>
        /// 这条链上有没有"没成功"或"完整性判不出"的任务。返回 <c>null</c> = 整条链都可证完整
        /// （可以按档处理其余物）；否则返回一句话说明是谁拦下的（调用方写进 WARN，
        /// 让用户知道"为什么这次没删"）。
        /// </summary>
        public static string? DescribeBlocker(ArchiveTask? rootTask, IReadOnlyList<ArchiveTask>? chainTasks)
        {
            if (rootTask == null)
            {
                return "根任务为空（判不出这条链跑没跑完）";
            }

            foreach (ArchiveTask candidate in EnumerateContinuations(rootTask, chainTasks))
            {
                if (candidate.Outcome != TaskOutcome.Succeeded)
                {
                    return $"链上的「{candidate.FileName}」没有成功（机器终态：{candidate.Outcome}）";
                }

                /*
                 * ⚠ 2026-09-30（检验等级 L4）：从"校验通过"收紧成"可证完整"——
                 * 拿不到清单、只做了非空底线校验的那一档现在算**判不出**，同样不许删。
                 */
                ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(candidate);

                if (!completeness.AllowsSourceRemoval)
                {
                    return $"链上的「{candidate.FileName}」{completeness.Message}"
                           + $"（机器结论：{completeness.Evidence}）";
                }
            }

            return null;
        }

        /// <summary>链上的续解任务（根任务排除在外；同一任务只算一次）。</summary>
        private static IEnumerable<ArchiveTask> EnumerateContinuations(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            var seen = new HashSet<ArchiveTask>();
            string rootIdentity = rootTask.ChainRootIdentity;

            foreach (ArchiveTask? candidate in chainTasks ?? Array.Empty<ArchiveTask>())
            {
                if (candidate == null || ReferenceEquals(candidate, rootTask) || !seen.Add(candidate))
                {
                    continue;
                }

                if (!candidate.IsContinuationTask)
                {
                    continue;
                }

                /*
                 * ⛔ **跟班卷不算链上成员**（用户 2026-09-27 真机：111 / 333 明明解压成功、其余物却一删不掉）。
                 *
                 * 现场：一组分卷从首卷那一单启动，**后续卷那一单一律落「已跳过」**
                 * （`IsVolumeGroupFollower`，见 `ExtractionCoordinator.SkipWhenAnotherTaskOwnsThisVolumeGroup`）。
                 * 那是**设计**，不是失败 —— 可这道闸门只问"你是不是 Succeeded"，于是每一批都会
                 * 被自己组里的跟班卷拦下，日志写着「链上的「111.part2.rar」没有成功（机器终态：Skipped）」，
                 * 其余物一个字节都不删。116 与 333 两组因此全部卡住。
                 *
                 * "整条链都成功"要防的是**续解链断了**（2026-09-25 那次 12 GiB 其余物被删）；
                 * 跟班卷根本没解、也不该解 —— 它**不在链上**，排除它不放松任何一条红线。
                 */
                if (candidate.IsVolumeGroupFollower)
                {
                    continue;
                }

                /*
                 * 链身份按**路径**认（真机 2026-09-30：另一个目录里同名的包把这条链的链尾挡下了 ——
                 * 日志原文「333-Rar4.part1.rar：链尾的其余物不处理（链上的「111.part1.rar」没有成功）」）。
                 *
                 * ⚠ 只有"带了明确链身份、且与根不同"的才排除；链身份为空（旧路径 / 测试直接构造的任务）
                 * 一律按**本链成员**处理 —— 宁可多拦一次（不删），也绝不漏掉真链成员：漏掉就是 2026-09-25
                 * 那次 12 GiB 其余物被删的事故。
                 */
                if (!string.IsNullOrWhiteSpace(candidate.RootSourcePath)
                    && !string.Equals(candidate.RootSourcePath, rootIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return candidate;
            }
        }

        /// <summary>链上续解任务的个数（日志/测试用）。</summary>
        public static int CountContinuations(ArchiveTask? rootTask, IReadOnlyList<ArchiveTask>? chainTasks)
        {
            return rootTask == null
                ? 0
                : EnumerateContinuations(rootTask, chainTasks).Count();
        }
    }
}
