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
            Func<string, bool>? probeOpens = null,
            ArchiveFixer.Detection.CardVolumeGroupReading? cardEvidence = null)
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
                /*
                 * **名字归不出组时，让底层识别卡参与分组**（用户 2026-10-17 总则：底层识别优先、
                 * 名字只作线索）。判据：卡上三条证据成立（首卷自述 + 体积规律 + 合计对上 ⇒ `Complete`），
                 * 且卡上排出 ≥2 个槽位 ⇒ 就按**卡的槽位**成组。
                 * ⛔ 只是"分组"换成卡：搬运本体、试开、同号两份那些闸门一条不落。
                 */
                if (cardEvidence is { Complete: true } && cardEvidence.Slots.Count >= 2)
                {
                    return new VolumeGatherPlan(
                        VolumeGatherAction.Gather,
                        $"名字归不出组，但底层识别卡可证完整（{cardEvidence.Reason}）",
                        cardEvidence.Slots.Select(slot => slot.Path).ToList());
                }

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

            /*
             * ⛔ 措辞不许说假话：`probeOpens == null` = **这一次根本没试开**（试开那一步还没接上），
             * 不能写成"且试开通过"（2026-10-17 日志里那条 `这一组可证完整（共 2 卷）且试开通过`
             * 就是这么来的 —— 那一趟一次引擎都没调）。
             */
            return new VolumeGatherPlan(
                VolumeGatherAction.Gather,
                probeOpens == null
                    ? $"这一组可证完整（共 {pieces.Count} 卷），试开那一步还没接上（⛔ 没试过就说没试过）"
                    : $"这一组可证完整（共 {pieces.Count} 卷）且试开通过",
                pieces);
        }

        /// <summary>
        /// **体积规律**：除末卷外，每一片都跟第 1 卷等大（末卷可以更小）。
        /// 判据只读盘上的字节数；拿不到 / 只有一片 ⇒ 判不过（⛔ 不猜）。
        /// </summary>
        /// <summary>
        /// **体积规律（用户口径「除末卷外等大」）**：整组里**最多只有一片**跟其余片不一样，
        /// 而且那一片只能**更小**（末卷 / 短片），不许更大。
        ///
        /// <para>⚠ 2026-10-17 实测订正：老写法逐片跟"第 1 卷"比 ⇒ 真机上短的那一片**不一定排在最后**
        /// （`10101010` 那一组的尺寸是 10,490,107 + 26,214,400 + 26,214,400，短的排在最前）
        /// ⇒ 27 次误判成"体积规律不过"。改成**众数**判据：取出现次数最多的那个尺寸，
        /// 与它不同的片子**最多一片**且必须更小。</para>
        /// </summary>
        /// <summary>
        /// **体积规律（用户口径「除末卷外等大」，末卷按族定）**：
        ///
        /// <list type="bullet">
        /// <item><description><b>数字 / part 族</b>（`X.7z.001`、`X.partN.rar`、`X.r00`…）：
        /// **N 最大的那一片是末卷**，它可以更小，其余片必须等大。</description></item>
        /// <item><description><b>跨盘 ZIP（WinRAR 压的）**：`.zip` 才是末卷**（名字上它排在 `.z01` 前后都不算），
        /// 而且**它最小**；`.z01/.z02/…` 必须等大。</description></item>
        /// </list>
        ///
        /// <para>⚠ 2026-10-17 用户两次纠正（逐字）：「其他的都是 00N 或者 partN 这种 N 最大的为末卷，
        /// 但是 zip（WinRAR），压出来的分卷 zip 为末卷，他的大小最小」。老写法（逐片比第 1 卷 / 众数）
        /// 都会把"短片排在前面"的假形状当成合法 ⇒ 改成**先按族定末卷，再检查其余片等大、末卷 ≤ 满片**。</para>
        /// </summary>
        private static bool SizesFollowVolumeRule(VolumeGroup group, out string reason)
        {
            reason = string.Empty;

            if (group.Volumes.Count < 2)
            {
                reason = "只有一片 ⇒ 判不出体积规律";
                return false;
            }

            var sizes = new List<long>();
            var names = new List<string>();

            foreach (VolumeCandidate volume in group.Volumes)
            {
                try
                {
                    sizes.Add(new FileInfo(volume.Path).Length);
                }
                catch
                {
                    reason = $"「{Path.GetFileName(volume.Path)}」的大小读不到";
                    return false;
                }

                names.Add(Path.GetFileName(volume.Path));
            }

            if (sizes.Any(size => size <= 0))
            {
                reason = "有一片读到 0 字节";
                return false;
            }

            int lastIndex = FindLastVolumeIndex(names);

            if (lastIndex < 0)
            {
                reason = "判不出哪一片是末卷（族/卷号都对不上）";
                return false;
            }

            long fullSize = -1;

            for (int index = 0; index < sizes.Count; index++)
            {
                if (index == lastIndex)
                {
                    continue;
                }

                if (fullSize < 0)
                {
                    fullSize = sizes[index];
                    continue;
                }

                if (sizes[index] != fullSize)
                {
                    reason = $"体积规律不过：除末卷外应当等大，但「{names[index]}」{sizes[index]} 字节 ≠ "
                        + $"「{names[lastIndex == 0 ? 1 : 0]}」{fullSize} 字节";
                    return false;
                }
            }

            if (fullSize > 0 && sizes[lastIndex] > fullSize)
            {
                reason = $"体积规律不过：末卷「{names[lastIndex]}」{sizes[lastIndex]} 字节比满片 {fullSize} 字节还大";
                return false;
            }

            return true;
        }

        /// <summary>
        /// **哪一片是末卷**（用户口径，按族定）：跨盘 ZIP（同组里有 `.zip` 且有 `.zNN`）⇒ `.zip`；
        /// 其余族 ⇒ 卷号 N 最大的那一片。判不出 ⇒ -1（⛔ 不猜）。
        /// </summary>
        private static int FindLastVolumeIndex(IReadOnlyList<string> names)
        {
            bool hasZipBody = false;
            bool hasSpannedPiece = false;
            int zipIndex = -1;

            for (int index = 0; index < names.Count; index++)
            {
                string name = names[index];

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    hasZipBody = true;
                    zipIndex = index;
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(
                        name,
                        @"\.z\d{2,}$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    hasSpannedPiece = true;
                }
            }

            if (hasZipBody && hasSpannedPiece)
            {
                return zipIndex;
            }

            int bestIndex = -1;
            int bestNumber = -1;

            for (int index = 0; index < names.Count; index++)
            {
                int? number = VolumeGroupDetector.TryGetVolumeIndex(names[index]);

                if (number is int value && value > bestNumber)
                {
                    bestNumber = value;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }
    }
}
