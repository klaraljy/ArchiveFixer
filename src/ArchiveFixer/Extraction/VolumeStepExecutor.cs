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
        /// <summary>
        /// 这一组**收场时**对入口层里组片的处置（`修改日志.md` 2026-10-22 ㈣，逐条照做）：
        /// 解开成功且校验通过 ⇒ 入口层里的组片按过程物删；失败 / 未校验 ⇒ 一片都不删、全留在入口层；
        /// 用户导入的原件由调用方给的唯一判据决定（⛔ 这里不比路径、不比名字）。
        /// </summary>
        /// <returns>真删掉的片数。</returns>
        public static int ApplySecondStepGroupPieceHandling(
            IEnumerable<string>? pieces,
            string? entryLayerDirectory,
            bool solvedAndVerified,
            Func<string, bool>? isUserImportedSource,
            Action<string, string>? log = null)
        {
            if (!solvedAndVerified)
            {
                log?.Invoke(
                    "INFO",
                    "这一组没解开（或没通过校验）—— 入口层里的组片一片都不删，留在入口包的位置等补密码/补卷。");
                return 0;
            }

            return VolumeGroupEntryLayer.PurgeSolvedGroupPiecesInEntryLayer(
                pieces,
                entryLayerDirectory,
                isUserImportedSource,
                log);
        }

        /// <summary>
        /// 合成组入口的**去重键** = <c>源层 + 基名</c>（`修改日志.md` 2026-10-22 ㈡ 逐字口径）。
        /// 用源层而不是族：同一基名在不同层是两组；族判得出/判不出一律不影响"是不是同一个入口"。
        /// ⛔ 键只用于去重，不当任何判据（判据仍在 VolumeGroupUnit / 内容卡上）。
        /// </summary>
        public static string BuildSyntheticGroupEntryKey(string? sourceLayer, string? baseName)
        {
            string layer = (sourceLayer ?? string.Empty).Trim();

            if (layer.Length > 0)
            {
                try
                {
                    layer = Path.GetFullPath(layer);
                }
                catch
                {
                    // 取不到全路径 ⇒ 用原样文本（判不出就按原样比，⛔ 不猜）。
                }
            }

            return layer + "|" + (baseName ?? string.Empty).Trim();
        }

    }
}
