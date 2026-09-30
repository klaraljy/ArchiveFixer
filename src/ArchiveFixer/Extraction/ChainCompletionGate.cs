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
