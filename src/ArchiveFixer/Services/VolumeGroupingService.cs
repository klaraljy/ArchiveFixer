using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 把扫描结果里的分卷文件归成**一组分卷 = 一个任务**（AGENTS.md §9.3、设计.md §十六）。
    ///
    /// 为什么必须在扫描阶段做：
    /// 用户把 <c>volume.7z.001</c>、<c>volume.7z.002</c>… 拖进来时，如果每个文件都变成一个任务，
    /// 界面上会出现 N 行"同一个包"，用户点解压会得到 N 个失败（除第一卷外都解不开），
    /// 批量操作也会重复干活。所以这里把它们合成一行，只从第一卷启动。
    ///
    /// 注意：这里只做"归组"，不做"合并文件" —— 7z 自己认得 .001 这套命名，
    /// 我们只需要保证只拿第一卷去启动、并且知道缺了哪一卷。
    /// </summary>
    public sealed class VolumeGroupingService
    {
        /// <summary>
        /// 按分卷组整理任务列表。
        /// 返回的顺序尽量保持原顺序：遇到某组的首卷时输出该组，其余卷被吸收掉。
        /// </summary>
        public List<ArchiveTask> ApplyVolumeGrouping(IReadOnlyList<ArchiveTask>? tasks)
        {
            var result = new List<ArchiveTask>();

            if (tasks == null || tasks.Count == 0)
            {
                return result;
            }

            var candidates = new List<VolumeCandidate>(tasks.Count);

            foreach (ArchiveTask task in tasks)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
                {
                    continue;
                }

                candidates.Add(new VolumeCandidate
                {
                    Path = task.CurrentPath,
                    Size = TryGetFileSize(task.CurrentPath)
                });
            }

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(candidates);

            if (groups.Count == 0)
            {
                result.AddRange(tasks.Where(t => t != null));
                return result;
            }

            // 卷路径 → 该卷所属的组；以及 组键 → 组
            var groupByVolumePath = new Dictionary<string, VolumeGroup>(StringComparer.OrdinalIgnoreCase);
            var primaryByGroupKey = new Dictionary<string, ArchiveTask>(StringComparer.OrdinalIgnoreCase);

            foreach (VolumeGroup group in groups)
            {
                foreach (VolumeCandidate volume in group.Volumes)
                {
                    groupByVolumePath[Normalize(volume.Path)] = group;
                }
            }

            // 第一遍：给每个组找一个"代表任务"（优先用已经存在的第一卷任务）
            foreach (VolumeGroup group in groups)
            {
                string firstKey = Normalize(group.FirstVolumePath);

                ArchiveTask? primary = tasks.FirstOrDefault(
                    t => t != null && string.Equals(Normalize(t.CurrentPath), firstKey, StringComparison.OrdinalIgnoreCase));

                // 第一卷可能在扫描阶段被过滤掉了（例如按后缀白名单扫描，.001 不在名单里），
                // 这种情况也要把这一组补出来，否则用户会看到一个从 .002 开始的任务。
                primary ??= new ArchiveTask(group.FirstVolumePath);

                ApplyGroupInfo(primary, group);
                primaryByGroupKey[group.GroupKey] = primary;
            }

            // 第二遍：按原顺序输出。组的代表任务在被遇到时输出；被吸收的卷跳过。
            var emittedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.CurrentPath))
                {
                    continue;
                }

                string key = Normalize(task.CurrentPath);

                if (!groupByVolumePath.TryGetValue(key, out VolumeGroup? group))
                {
                    result.Add(task);
                    continue;
                }

                if (emittedGroups.Add(group.GroupKey))
                {
                    result.Add(primaryByGroupKey[group.GroupKey]);
                }
            }

            // 第三遍：首卷不在任务列表里的组（被过滤掉的情况）补到末尾
            foreach (VolumeGroup group in groups)
            {
                if (emittedGroups.Add(group.GroupKey))
                {
                    result.Add(primaryByGroupKey[group.GroupKey]);
                }
            }

            return result;
        }

        /// <summary>把分卷信息写到代表任务上。</summary>
        public void ApplyGroupInfo(ArchiveTask task, VolumeGroup group)
        {
            if (task == null || group == null)
            {
                return;
            }

            task.IsVolumeGroup = true;
            task.VolumeGroupKey = group.GroupKey;
            task.VolumePaths.Clear();

            foreach (VolumeCandidate volume in group.Volumes)
            {
                task.VolumePaths.Add(volume.Path);
            }

            task.MissingVolumeNames.Clear();
            task.MissingVolumeNames.AddRange(group.MissingVolumeNames);
            task.IsVolumeComplete = group.IsComplete;
            task.VolumeInfoText = BuildVolumeInfoText(group);
        }

        /// <summary>
        /// 给用户看的一句话，直接显示在任务行上（①页「分卷」列 + 悬停提示 + 详情窗）。
        ///
        /// <para><b>用户 2026-10-02 拍板</b>："名字上明确是同一组的分卷 ⇒ 一律只留一行，
        /// 并在那一行注明「这一组共 N 卷」" —— 所以这一档统一以「共 N 卷」开头，
        /// 让用户一眼看出"这一行代表的是几卷"（老写法只写"N 卷"，
        /// 而那一列在①页是折叠的，他根本读不到）。</para>
        ///
        /// <para>⛔ 卷数仍然是"**按名字找到的**这几卷"，不是"这一组一共就这么多"：
        /// 总数推不出来这件事不许藏（见 <see cref="VolumeGroup.Note"/> 与
        /// <c>VolumeGroupDetector</c> 类注释第 2 条）—— 所以完整那一档照样把 Note 带在括号里。</para>
        /// </summary>
        public static string BuildVolumeInfoText(VolumeGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            if (group.MissingVolumeNames.Count > 0)
            {
                return $"共 {group.Volumes.Count} 卷，缺 {string.Join("、", group.MissingVolumeNames)}";
            }

            if (group.Volumes.Count <= 1)
            {
                return "只找到 1 卷，可能不完整";
            }

            string note = string.IsNullOrWhiteSpace(group.Note) ? string.Empty : $"（{group.Note}）";

            return $"共 {group.Volumes.Count} 卷{note}";
        }

        private static long TryGetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return -1;
            }
        }

        private static string Normalize(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path);
        }
    }
}
