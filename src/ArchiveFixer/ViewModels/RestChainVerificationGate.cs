using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Collections.Generic;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 链完整性缺口判据（从 `ExtractionCoordinator.RestHandlingStep.cs` 搬出的纯函数，2026-11-13）：
    /// 同一条续解链上，最终目录要"每一个落在这里的任务都通过可证完整"才算齐；
    /// 任一没通过 / 压根没有任务落在这里 ⇒ 返回说给用户听的原因（下游是**移动 / 删除用户源包**）。
    ///
    /// <para>⛔ 判据唯一出口仍是 <see cref="ResultCompletenessClassifier"/>；这里只做遍历与拼接，不碰任务状态。</para>
    /// </summary>
    internal static class RestChainVerificationGate
    {
        internal static string? Describe(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            bool anyLanding = false;

            foreach (ArchiveTask? candidate in RestHandlingStep.EnumerateChainTasks(rootTask, chainTasks))
            {
                /*
                 * 同一分卷组的后续卷**不算这一条链上的产物**（用户 2026-10-01）：它没有解压过、
                 * 没有产物、也没有自己的落点目录 —— 它的落点是用同一套路径算法**重算**出来的，
                 * 于是必然与首卷那一单重合，拿它去比"链上的输出校验"必然得出"链里有一层没过"，
                 * 整组的源包就一个都删不掉了（真机现象）。
                 */
                if (candidate.IsVolumeGroupFollower)
                {
                    continue;
                }

                if (!SafePathHelper.PathEquals(
                        OneClickCoordinator.ResolveContinuationOutputDirectory(candidate), destinationDirectory))
                {
                    continue;
                }

                anyLanding = true;

                /*
                 * ⚠ 2026-09-30（检验等级 L4）：判据从 <c>IsOutputVerified</c>（= "校验通过"，
                 * 含"拿不到清单只做了底线校验"那一档）**收紧**成"可证完整"——
                 * 唯一出口 <see cref="ResultCompletenessClassifier"/>。这一处是**链尾补搬/删源**的最后一道
                 * 前置判据，判不出 ⇒ 源包一个字节都不许动。
                 */
                ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(candidate);

                if (!completeness.AllowsSourceRemoval)
                {
                    return $"{candidate.LogName}：{completeness.Message}"
                           + $"（机器结论：{completeness.Evidence}）";
                }
            }

            return anyLanding
                ? null
                : $"链里没有一个任务把 {destinationDirectory} 当落点（没有产物被定稿到那里）";
        }
    }
}

