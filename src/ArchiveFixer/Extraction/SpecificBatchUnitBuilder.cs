using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;

public static class SpecificBatchUnitBuilder
{
    public static IReadOnlyList<SpecificBatchUnit> Build(IEnumerable<ArchiveTask>? tasks)
    {
        var groups = new Dictionary<string, List<ArchiveTask>>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveTask task in tasks ?? Enumerable.Empty<ArchiveTask>())
        {
            if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                continue;
            }

            string name = Path.GetFileName(task.CurrentPath);
            string? firstVolume = LegacyVolumeNames.TryGetFirstVolumeName(name);
            string key = firstVolume == null
                ? task.CurrentPath
                : Path.Combine(Path.GetDirectoryName(task.CurrentPath) ?? string.Empty, firstVolume);

            if (!groups.TryGetValue(key, out List<ArchiveTask>? group))
            {
                group = new List<ArchiveTask>();
                groups[key] = group;
            }

            group.Add(task);
        }

        var units = new List<SpecificBatchUnit>();

        foreach ((string key, List<ArchiveTask> group) in groups)
        {
            string firstName = LegacyVolumeNames.TryGetFirstVolumeName(
                Path.GetFileName(group[0].CurrentPath)) ?? string.Empty;

            ArchiveTask entry = group.FirstOrDefault(task => string.Equals(
                                      Path.GetFileName(task.CurrentPath),
                                      firstName,
                                      StringComparison.OrdinalIgnoreCase))
                                  ?? group
                                      .OrderBy(task => LegacyVolumeNames.TryGetIndex(
                                          Path.GetFileName(task.CurrentPath)) ?? int.MaxValue)
                                      .ThenBy(task => task.CurrentPath, StringComparer.OrdinalIgnoreCase)
                                      .First();

            List<ArchiveTask> followers = group
                .Where(task => !ReferenceEquals(task, entry))
                .ToList();

            units.Add(new SpecificBatchUnit
            {
                Entry = entry,
                Followers = followers,
                SourceBytes = group.Sum(Measure)
            });
        }

        return units;
    }

    private static long Measure(ArchiveTask task)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(task.CurrentPath) && File.Exists(task.CurrentPath)
                ? new FileInfo(task.CurrentPath).Length
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
