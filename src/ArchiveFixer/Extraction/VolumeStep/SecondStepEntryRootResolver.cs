using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction.VolumeStep
{
    /// <summary>
    /// 第二大步「目标工作区根」的唯一解析出口。
    ///
    /// <para>事实链（顺序固定，⛔ 不猜、不落盘根、不复制）：</para>
    /// <list type="number">
    /// <item><description>入口片自己的任务（当前路径/原路径/卷路径命中）⇒ <c>OutputPath</c>；</description></item>
    /// <item><description>账本里该片的 <c>ProducedByTaskId</c> ⇒ 生产者任务的 <c>OutputPath</c>。
    /// 组命名发生在「新发现的内层包导入任务表」之前时，入口任务还不存在，但**生产者任务**一直知道
    /// 这一组落在哪个成品目录（真机 2026-11-10：tar 产出 5 片，命名时 .7z.001 还没导入 ⇒
    /// 以前判 Unknown、白等一轮）。</description></item>
    /// </list>
    ///
    /// <para>两档都拿不到 ⇒ null（由调用方把组落 Unknown）。</para>
    /// </summary>
    internal static class SecondStepEntryRootResolver
    {
        internal const string WorkFolderName = ".ArchiveFixer.work";

        internal static string? Resolve(
            PreparedVolumeGroup? group,
            IReadOnlyList<ArchiveTask>? tasks,
            FirstStepFileLedger? ledger,
            Action<string, string>? log = null)
        {
            if (group == null || tasks == null)
            {
                return null;
            }

            string entry = group.OriginalEntryPath;

            if (string.IsNullOrWhiteSpace(entry) && group.Pieces.Count > 0)
            {
                entry = group.Pieces[0].OriginalPath;
            }

            if (string.IsNullOrWhiteSpace(entry))
            {
                return null;
            }

            ArchiveTask? task = tasks.FirstOrDefault(candidate =>
                candidate != null
                && (PathsEqual(candidate.CurrentPath, entry)
                    || PathsEqual(candidate.OriginalPath, entry)
                    || candidate.VolumePaths.Any(path => PathsEqual(path, entry))));

            if (task != null && !string.IsNullOrWhiteSpace(task.OutputPath))
            {
                return Path.Combine(task.OutputPath!, WorkFolderName);
            }

            if (ledger == null
                || !ledger.TryGetByPath(entry, out FirstStepFileLedgerEntry ledgerEntry)
                || string.IsNullOrWhiteSpace(ledgerEntry.ProducedByTaskId))
            {
                return null;
            }

            ArchiveTask? producer = tasks.FirstOrDefault(candidate =>
                candidate != null
                && string.Equals(candidate.TaskId, ledgerEntry.ProducedByTaskId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(candidate.OutputPath));

            if (producer == null)
            {
                return null;
            }

            log?.Invoke(
                "INFO",
                $"[第二大步·入口根] {group.BaseName}：入口任务还没建，按生产者任务（{producer.LogName}）的落点建工作区。");

            return Path.Combine(producer.OutputPath!, WorkFolderName);
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
