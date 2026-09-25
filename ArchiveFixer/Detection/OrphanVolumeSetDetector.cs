using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 目录里"**缺首卷**的一大组分卷"（例如只有 <c>set.7z.002</c> / <c>set.7z.003</c>，
    /// 没有 <c>set.7z.001</c>）。
    ///
    /// <para><b>为什么单独要这么一个东西</b>（用户 2026-09-25 第 42 条，真 7z 探针实测）：
    /// 容器（`封面.jpg`）里装着的**正是**那一组的首卷 —— 后续卷在容器外面。
    /// 那种情况下引擎会报"这是后续卷、缺少首卷"，与事实**正好相反**（缺的不是首卷，
    /// 而是"名字对得上的后续卷"）；程序要么把两边凑起来，要么至少得把这句话说对。</para>
    ///
    /// <para>判据只有两条，全来自文件系统：①这一组里有卷号 ≥ 2 的文件；②按标准命名推出来的
    /// **首卷文件名在目录里不存在**。⛔ 不猜、不按体积配对、不看内容。</para>
    /// </summary>
    public sealed class OrphanVolumeSet
    {
        /// <summary>标准首卷文件名（例如 <c>set.7z.001</c>）—— 目录里**没有**这个文件。</summary>
        public string FirstVolumeName { get; init; } = string.Empty;

        /// <summary>已找到的后续卷文件名（按卷序升序）。</summary>
        public IReadOnlyList<string> ContinuationFileNames { get; init; } = Array.Empty<string>();

        /// <summary>卷号是否从 2 开始连续（<c>.002/.003/…</c>）；缺号时只影响描述口径。</summary>
        public bool IsContiguous { get; init; }

        /// <summary>一行给人看：<c>set.7z.002、set.7z.003（标准首卷名 set.7z.001）</c>。</summary>
        public string Describe() =>
            $"{string.Join("、", ContinuationFileNames)}（标准首卷名 {FirstVolumeName}{(IsContiguous ? string.Empty : "，卷号不连续")}）";
    }

    /// <summary>「缺首卷的分卷组」检测（纯逻辑，不引用 WPF、不碰文件系统）。</summary>
    public static class OrphanVolumeSetDetector
    {
        /// <summary>
        /// 从**一个目录里的文件名**里找出所有"缺首卷"的分卷组。
        ///
        /// <para>按"标准首卷名"分组（<see cref="VolumeGroupDetector.TryGetFirstVolumeName"/>）——
        /// 这样老式 zip（<c>x.zip</c> + <c>x.z01</c>）与新式 rar（<c>x.part1.rar</c>）也是同一套口径，
        /// 不必按族各写一遍。首卷文件**存在**的组不算孤儿（那是正常分卷组，交给既有分组逻辑）。</para>
        /// </summary>
        public static IReadOnlyList<OrphanVolumeSet> Find(IEnumerable<string?>? fileNamesInDirectory)
        {
            var result = new List<OrphanVolumeSet>();

            if (fileNamesInDirectory == null)
            {
                return result;
            }

            var names = fileNamesInDirectory
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();

            if (names.Count == 0)
            {
                return result;
            }

            var present = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            // 首卷名 → 后续卷（卷序, 文件名）
            var groups = new Dictionary<string, List<(int Index, string Name)>>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in names)
            {
                int? index = VolumeGroupDetector.TryGetVolumeIndex(name);

                if (index == null || index.Value < 2)
                {
                    continue;
                }

                string? firstVolumeName = VolumeGroupDetector.TryGetFirstVolumeName(name);

                if (string.IsNullOrWhiteSpace(firstVolumeName))
                {
                    continue;
                }

                if (!groups.TryGetValue(firstVolumeName!, out List<(int, string)>? list))
                {
                    list = new List<(int, string)>();
                    groups[firstVolumeName!] = list;
                }

                list.Add((index.Value, name));
            }

            foreach (KeyValuePair<string, List<(int Index, string Name)>> group in groups)
            {
                // 首卷就在目录里 → 不是孤儿（正常分卷组，缺不缺卷由既有分组逻辑去报）。
                if (present.Contains(group.Key))
                {
                    continue;
                }

                List<(int Index, string Name)> ordered = group.Value.OrderBy(item => item.Index).ToList();

                bool contiguous = ordered[0].Index == 2
                    && ordered.Select(item => item.Index).Distinct().Count() == ordered.Count
                    && ordered[^1].Index == ordered[0].Index + ordered.Count - 1;

                result.Add(new OrphanVolumeSet
                {
                    FirstVolumeName = group.Key,
                    ContinuationFileNames = ordered.Select(item => item.Name).ToList(),
                    IsContiguous = contiguous
                });
            }

            return result;
        }
    }
}
