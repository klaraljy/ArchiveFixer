using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction
{

    /// <summary>一个暂存目录里的分卷组 + 它的判定结论（定稿闸门按文件查它）。</summary>
    internal sealed class StageVolumeGroupView
    {
        /// <summary>这一组的目录（绝对路径）。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>判定器给出的唯一结论。</summary>
        public VolumeGroupResolution Resolution { get; init; } = new();

        /// <summary>组的全部文件（绝对路径，含推定进来的候选）。</summary>
        public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 暂存树的一次分卷扫描：**已经认出来的组** + **逐目录的事实清单**（后者给"孤儿卷"复查用）。
    ///
    /// <para>为什么要带着事实清单：带卷标记的文件**不一定**落进某个组 ——
    /// <see cref="VolumeGroupDetector"/> 对"名字被伪装过"的桶还要过一道尺寸规律，
    /// 过不了就把整桶丢掉（2026-09-28 三层方案，防认错组）。丢掉之后那些文件在
    /// <see cref="PlanFinalLayout"/> 里就只剩"按扩展名像不像分卷"这一条老判据了 ——
    /// 而它们恰恰可能是**缺了兄弟的那一组**。所以那一档必须回头问判定器（见调用点）。</para>
    /// </summary>
    internal sealed class StageVolumeScan
    {
        /// <summary>认出来的组（每组一条结论）。</summary>
        public IReadOnlyList<StageVolumeGroupView> Groups { get; init; } = Array.Empty<StageVolumeGroupView>();

        /// <summary>逐目录的事实清单（键 = 目录绝对路径）。</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<VolumeGroupEntry>> EntriesByDirectory { get; init; } =
            new Dictionary<string, IReadOnlyList<VolumeGroupEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>这个文件的同目录事实清单（拿不到就返回空 —— 空清单在判定器里等于"判不出"）。</summary>
        public IReadOnlyList<VolumeGroupEntry> EntriesFor(string filePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(filePath);

                return !string.IsNullOrWhiteSpace(directory) &&
                       EntriesByDirectory.TryGetValue(directory, out IReadOnlyList<VolumeGroupEntry>? entries)
                    ? entries
                    : Array.Empty<VolumeGroupEntry>();
            }
            catch
            {
                return Array.Empty<VolumeGroupEntry>();
            }
        }
    }
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
}
