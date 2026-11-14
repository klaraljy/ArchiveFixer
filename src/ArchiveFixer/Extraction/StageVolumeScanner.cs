using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;


/// <summary>
/// 暂存树的分卷扫描器（第 19 轮③前置：从 <c>ExtractionCoordinator.Recursion.cs</c> 的嵌套类型搬出来）。
/// 定稿闸门开工前判一次，不是每个文件各判一次（那是 O(n²) 的目录枚举）。
/// </summary>
internal static class StageVolumeScanner
{
    /// <summary>
    /// 把暂存树里出现的每一组分卷判一次（**逐目录**判：判定器只在同目录里认组）。
    ///
    /// <para>调用时机 = 定稿闸门开工前一次（不是每个文件各判一次 —— 那是 O(n²) 的目录枚举）。
    /// 不做试开：这一步跑在同步的定稿路径上；试开那一档在解前预检里做，两边收的是**同一份结论类型**。</para>
    /// </summary>
    internal static StageVolumeScan ResolveStageVolumeGroups(string stageRoot)
    {
        var result = new List<StageVolumeGroupView>();
        var byDirectory = new Dictionary<string, List<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
        {
            try
            {
                string? directory = Path.GetDirectoryName(file);

                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                if (!byDirectory.TryGetValue(directory, out List<VolumeGroupEntry>? list))
                {
                    list = new List<VolumeGroupEntry>();
                    byDirectory[directory] = list;
                }

                list.Add(new VolumeGroupEntry
                {
                    Path = SafePathHelper.GetFullPathSafe(file),
                    Size = new FileInfo(file).Length,
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(file),
                    OrderIndex = list.Count
                });
            }
            catch
            {
                // 单个文件量不出事实（被占用 / 权限）⇒ 它进不了任何组，照老路当内容物处理。
            }
        }

        var resolver = new VolumeGroupResolver();

        foreach (KeyValuePair<string, List<VolumeGroupEntry>> pair in byDirectory)
        {
            IReadOnlyList<VolumeGroupEntry> entries = pair.Value;

            var candidates = entries
                .Select(e => new VolumeCandidate { Path = e.Path, Size = e.Size })
                .ToList();

            foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
            {
                VolumeGroupResolution resolution = resolver.Resolve(new VolumeGroupQuery
                {
                    AnchorPath = group.FirstVolumePath,
                    DirectoryEntries = entries,
                    AllowTrialOpen = false
                });

                result.Add(new StageVolumeGroupView
                {
                    DirectoryPath = pair.Key,
                    Resolution = resolution,
                    FilePaths = resolution.GroupFilePaths
                });
            }
        }

        return new StageVolumeScan
        {
            Groups = result,
            EntriesByDirectory = byDirectory.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<VolumeGroupEntry>)pair.Value,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>这个文件属于哪一组（不属于任何组返回 null）。判定器认进来的推定候选也算。</summary>
    internal static StageVolumeGroupView? FindStageVolumeGroup(
        IReadOnlyList<StageVolumeGroupView> groups,
        string filePath)
    {
        string full = SafePathHelper.GetFullPathSafe(filePath);

        foreach (StageVolumeGroupView group in groups)
        {
            foreach (string member in group.FilePaths)
            {
                if (string.Equals(member, full, StringComparison.OrdinalIgnoreCase))
                {
                    return group;
                }
            }
        }

        return null;
    }
}
