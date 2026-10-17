using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>收拢决策：这一次要不要把这一组搬到入口包那一层。</summary>
    public enum VolumeGatherAction
    {
        /// <summary>不搬（一个字节都不动）。</summary>
        Leave,

        /// <summary>整组搬到入口包那一层。</summary>
        Gather
    }

    /// <summary>收拢计划（纯数据；⛔ 这里不动盘上任何东西）。</summary>
    public sealed record VolumeGatherPlan(
        VolumeGatherAction Action,
        string Reason,
        IReadOnlyList<string> Pieces)
    {
        public static VolumeGatherPlan Leave(string reason)
            => new(VolumeGatherAction.Leave, reason, Array.Empty<string>());
    }

    /// <summary>
    /// **分卷收拢的决策器**（用户 2026-10-17 拍板的四步里的 ②分组 + ③硬链接试开证实；
    /// 执行仍然交给既有搬运出口，⛔ 本类不动盘）。
    ///
    /// <para>判据顺序（与用户口径一致）：</para>
    /// <list type="number">
    /// <item><description><b>分组</b>：先问既有唯一出口 <see cref="VolumeGroupDetector.Group"/>（族 + 基名 + 卷号），
    /// 再看**体积规律**（除末卷外等大） —— 名字只作线索，⛔ 不靠名字族一刀切；</description></item>
    /// <item><description><b>不完整</b>：这一组缺口非空 ⇒ <see cref="VolumeGatherAction.Leave"/>
    /// （用户原话：「不完整 ⇒ 放回原来的位置」，也就是原地不动）；</description></item>
    /// <item><description><b>硬链接试开证实</b>：完整也要先试开（<c>probeOpens</c> = 调用方给的试开回调），
    /// 开不动 ⇒ 一样 <see cref="VolumeGatherAction.Leave"/>，并把"哪一条不过"写清楚；</description></item>
    /// <item><description>三条都过 ⇒ <see cref="VolumeGatherAction.Gather"/>，按卷序给出要搬的那几份。</description></item>
    /// </list>
    ///
    /// <para>⛔ 判不出（读不到、归不进任何一组、尺寸异常）一律 <see cref="VolumeGatherAction.Leave"/>：
    /// 这一档的兜底永远是"什么都不做"。</para>
    /// </summary>
    public static class VolumeGatherPlanner
    {
        /// <summary>体积规律允许的误差（除末卷外等大；0 = 严格等大）。</summary>
        public const long DefaultSizeTolerance = 0;

        public static VolumeGatherPlan Decide(
            IReadOnlyList<string>? candidatePaths,
            string? entryLayer,
            Func<string, bool>? probeOpens = null)
        {
            if (string.IsNullOrWhiteSpace(entryLayer))
            {
                return VolumeGatherPlan.Leave("没有入口包那一层（⛔ 判不出就不搬）");
            }

            if (candidatePaths == null || candidatePaths.Count == 0)
            {
                return VolumeGatherPlan.Leave("这一层没有候选片");
            }

            var candidates = new List<VolumeCandidate>();

            foreach (string path in candidatePaths.Where(File.Exists))
            {
                long size;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    continue;
                }

                if (size <= 0)
                {
                    continue;
                }

                candidates.Add(new VolumeCandidate { Path = path, Size = size });
            }

            if (candidates.Count == 0)
            {
                return VolumeGatherPlan.Leave("候选里一个能读到大小的都没有");
            }

            List<VolumeGroup> groups = VolumeGroupDetector.Group(candidates).ToList();

            if (groups.Count == 0)
            {
                return VolumeGatherPlan.Leave("一个分卷组都没归出来（同族同基名都对不上）");
            }

            // 先挑"至少两片"的那一组（单片判不出是不是一组，⛔ 不搬）。
            VolumeGroup? chosen = groups
                .Where(group => group.Volumes.Count >= 2)
                .OrderByDescending(group => group.Volumes.Count)
                .FirstOrDefault();

            if (chosen == null)
            {
                return VolumeGatherPlan.Leave("同族的候选只有一片 —— 判不出它是哪一组，⛔ 不搬");
            }

            if (!chosen.IsComplete)
            {
                string missing = chosen.MissingVolumeNames.Count > 0
                    ? string.Join("、", chosen.MissingVolumeNames)
                    : "判不出缺哪几片";

                return VolumeGatherPlan.Leave($"这一组不完整（缺：{missing}）⇒ 原地不动");
            }

            if (!SizesFollowVolumeRule(chosen, out string sizeReason))
            {
                return VolumeGatherPlan.Leave(sizeReason);
            }

            string firstVolume = chosen.FirstVolumePath;

            if (string.IsNullOrWhiteSpace(firstVolume) || !File.Exists(firstVolume))
            {
                return VolumeGatherPlan.Leave("找不到这一组的第 1 卷");
            }

            if (probeOpens != null && !probeOpens(firstVolume))
            {
                return VolumeGatherPlan.Leave("硬链接试开没通过（引擎打不开这一组）⇒ 整组不动");
            }

            var pieces = chosen.Volumes
                .Select(volume => volume.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            return new VolumeGatherPlan(
                VolumeGatherAction.Gather,
                $"这一组可证完整（共 {pieces.Count} 卷）且试开通过",
                pieces);
        }

        /// <summary>
        /// **体积规律**：除末卷外，每一片都跟第 1 卷等大（末卷可以更小）。
        /// 判据只读盘上的字节数；拿不到 / 只有一片 ⇒ 判不过（⛔ 不猜）。
        /// </summary>
        private static bool SizesFollowVolumeRule(VolumeGroup group, out string reason)
        {
            reason = string.Empty;

            if (group.Volumes.Count < 2)
            {
                reason = "只有一片 ⇒ 判不出体积规律";
                return false;
            }

            long firstSize;

            try
            {
                firstSize = new FileInfo(group.Volumes[0].Path).Length;
            }
            catch
            {
                reason = "第 1 卷的大小读不到";
                return false;
            }

            for (int index = 0; index < group.Volumes.Count - 1; index++)
            {
                long size;

                try
                {
                    size = new FileInfo(group.Volumes[index].Path).Length;
                }
                catch
                {
                    reason = $"第 {index + 1} 卷的大小读不到";
                    return false;
                }

                if (Math.Abs(size - firstSize) > DefaultSizeTolerance)
                {
                    reason = $"体积规律不过：第 {index + 1} 卷 {size} 字节 ≠ 第 1 卷 {firstSize} 字节";
                    return false;
                }
            }

            long lastSize;

            try
            {
                lastSize = new FileInfo(group.Volumes[^1].Path).Length;
            }
            catch
            {
                reason = "末卷的大小读不到";
                return false;
            }

            if (lastSize > firstSize)
            {
                reason = $"体积规律不过：末卷 {lastSize} 字节比满片 {firstSize} 字节还大";
                return false;
            }

            return true;
        }
    }
}
