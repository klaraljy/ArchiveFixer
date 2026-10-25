using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 空间不足排队单位：一个单一完整包，或一整组分卷。
    /// 排序键 = 这个单位的**源包总体积**（分卷按整组相加），不是解压后净占用。
    /// </summary>
    public sealed class SpaceQueueUnit
    {
        public string Key { get; init; } = string.Empty;

        public ArchiveTask Entry { get; init; } = null!;

        public IReadOnlyList<ArchiveTask> Tasks { get; init; } = Array.Empty<ArchiveTask>();

        public long SourceBytes { get; init; }
    }

    public static class SpaceQueueUnitBuilder
    {
        public static IReadOnlyList<SpaceQueueUnit> Build(IEnumerable<ArchiveTask>? tasks)
        {
            var units = new List<SpaceQueueUnit>();
            var byGroup = new Dictionary<string, List<ArchiveTask>>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                if (task.IsVolumeGroup && !string.IsNullOrWhiteSpace(task.VolumeGroupKey))
                {
                    if (!byGroup.TryGetValue(task.VolumeGroupKey, out List<ArchiveTask>? group))
                    {
                        group = new List<ArchiveTask>();
                        byGroup[task.VolumeGroupKey] = group;
                    }

                    group.Add(task);
                }
                else
                {
                    units.Add(new SpaceQueueUnit
                    {
                        Key = task.CurrentPath,
                        Entry = task,
                        Tasks = new[] { task },
                        SourceBytes = Measure(task)
                    });
                }
            }

            foreach ((string key, List<ArchiveTask> group) in byGroup)
            {
                ArchiveTask entry = group.FirstOrDefault(task => !task.IsVolumeGroupFollower) ?? group[0];

                units.Add(new SpaceQueueUnit
                {
                    Key = key,
                    Entry = entry,
                    Tasks = group,
                    SourceBytes = group.SelectMany(task => task.GetSnapshotPaths()).Distinct(StringComparer.OrdinalIgnoreCase).Sum(MeasurePath)
                });
            }

            return units
                .OrderByDescending(unit => unit.SourceBytes)
                .ThenBy(unit => unit.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static long Measure(ArchiveTask task)
        {
            return task.GetSnapshotPaths()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Sum(MeasurePath);
        }

        private static long MeasurePath(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? new FileInfo(path).Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
