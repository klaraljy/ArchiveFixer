using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection
{
    /// <summary>「缺首卷的分卷组」检测（纯逻辑，不引用 WPF、不碰文件系统）。</summary>
    public static class OrphanVolumeSetDetector
    {
        /// <summary>
        /// 从**一个目录里的文件名**里找出所有"缺首卷"的分卷组。
        ///
        /// <para>按"标准首卷名"分组（<see cref="LegacyVolumeNames.TryGetFirstVolumeName"/>）——
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
                int? index = LegacyVolumeNames.TryGetIndex(name);

                if (index == null || index.Value < 2)
                {
                    continue;
                }

                string? firstVolumeName = LegacyVolumeNames.TryGetFirstVolumeName(name);

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
