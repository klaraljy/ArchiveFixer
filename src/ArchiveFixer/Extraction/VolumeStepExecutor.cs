using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **第二大步执行体**（用户 2026-10-23 口径）：顺序固定为
    /// ① 组分析（基名 = 第一个点之前 + 大小分布）→ ② 内容驱动改名 → ③ 单层解压 → ④ 收片。
    ///
    /// <para>本类只实现 ① 与 ② 两个纯动作，③④ 由协调器在拿到事实后调既有单层路完成。
    /// ⛔ 第一大步不改分卷名：改名入口**只有** <see cref="PlanRenameAsync"/>（内部只走
    /// <see cref="VolumeNameRepair.PlanByContentAsync"/>，名字路只作它自己的兜底、不在这里另开一套）。</para>
    /// </summary>
    public static class VolumeStepExecutor
    {
        /// <summary>
        /// ① 组分析：种子 = 第一大步登记的交接事实 + 任意候选片；根 = 本批产物树 + 用户源目录。
        /// 判据在 <see cref="GroupAnalysis"/>（基名 + 大小分布，⛔ 不分族）。
        /// </summary>
        public static IReadOnlyList<GroupAnalysis.GroupCandidate> Analyze(
            IEnumerable<string>? seeds,
            IEnumerable<string>? scanRoots) =>
            GroupAnalysis.Analyze(seeds, scanRoots);

        /// <summary>
        /// ② 内容驱动改名：只问 <see cref="VolumeNameRepair.PlanByContentAsync"/>
        /// （族/卷号来自内容 + 硬链接试开做证据）。⛔ 这里不调名字路
        /// （<see cref="VolumeNameRepair.Plan(string?, IEnumerable{string?}?)"/>）——
        /// 名字只在该方法的内部兜底里出现，第二大步不应该再开第二套判据。
        /// </summary>
        /// <param name="entryPath">这一组的入口片（第 1 片 / 末片，按族）。</param>
        /// <param name="engine">用于试开的引擎（与解压同一套）。</param>
        /// <param name="probeWorkRoot">试开用的工作区根（跨盘时判不出 ⇒ 不试开）。</param>
        /// <param name="passwords">试开时要试的密码候选（只活在内存）。</param>
        public static async Task<VolumeNameRepairPlan?> PlanRenameAsync(
            string? entryPath,
            IArchiveEngine engine,
            string? probeWorkRoot,
            IReadOnlyList<string>? passwords,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(entryPath) || !File.Exists(entryPath) || engine == null)
            {
                return null;
            }

            IReadOnlyList<VolumeCandidate> candidates =
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(entryPath);

            return await VolumeNameRepair.PlanByContentAsync(
                    entryPath,
                    candidates,
                    engine,
                    probeWorkRoot,
                    passwords ?? Array.Empty<string>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
