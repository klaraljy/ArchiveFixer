using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>用户源包就地保护/真名还原（改名与缺卷补救两处共用；第 8 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 批末一条**汇总**（第 44 条的补充）：任务数 / 成功 / 失败 / 跳过 / 用时 + 失败逐条。
        ///
        /// <para>为什么要它：瘦身之后每个任务只剩两行，但 136 个任务仍是 270 多行 ——
        /// 用户真正想知道的是"这批到底成不成、哪几个不成"，那应该是**一眼**能看到的结论，
        /// 而不是让他自己数行。失败逐条写"名字 + 状态"（最多 20 条，多的写个数）。
        /// ⛔ 判据全用机器终态（<see cref="TaskOutcome"/>），不比对中文。</para>
        /// </summary>
        /// <summary>
        /// **批末收掉产品树里"多余的片名"**（判据 = 这一份在盘上**还有别的名字**）。
        ///
        /// <para>运行期证据（2026-10-17 真机 `6666`，逐字）：残留 `pack111\pack111-L2.7z.003`
        /// 与 `…\pack111(3)\…\其余物\pack111-L2.7z(1)(1)(1)(1)(1).003` **是同一个 inode**
        /// （`fsutil file queryfileid` 同 File ID、`fsutil hardlink list` 只列出这两个名字）⇒
        /// 删掉产品树里这个**别名一个字节都不丢**，而判据 ③ 正是把它算成"盘上残留"。</para>
        ///
        /// <para>只扫**落点根这一层**（残留实测都在这儿）、只碰**名字像分卷片**的文件、
        /// 只碰**硬链接数 &gt; 1** 的（`FileIdentity.TryGetLinkCount`）⇒ 三重收窄；
        /// ⛔ 不递归、⛔ 不碰「其余物」、⛔ 不碰只剩一个名字的文件。整批复跑会从备份还原，删了也不影响复现。</para>
        /// </summary>
        internal void BeginKeepInPlaceSourceProtection(IReadOnlyList<ArchiveTask> tasks)
        {
            _keepSourceSnapshots.Clear();
            _keepSourceSnapshotDirectory = string.Empty;

            if (AppSettings.ParseSourceHandling(Settings.SourceHandling) != SourceHandlingMode.KeepInPlace)
            {
                return;
            }

            /*
             * 2026-10-25 真机（链路测试 sp1）：空间不足模式先按设计删了源包，批末
             * RestoreKeepInPlaceSourceNames 又拿保护快照把源包硬链接恢复回来 ——
             * 日志逐字「空间不足模式 —— 已立刻永久删除源包 1 个」紧跟
             * 「源包档=留下，但源名被搬走/改名 —— 已按原路径硬链接恢复」。
             * 空间不足模式的源包处置是运行期最高优先：要删的档不需要"改名胜出后恢复原名"
             * 的保护快照（没有快照，恢复那一步自然没有可恢复的东西）。⛔ 不碰「不删原包」安全档。
             */
            if (_vm.SpaceTightMode)
            {
                return;
            }

            var sources = (tasks ?? Array.Empty<ArchiveTask>())
                .Where(task => task != null)
                .SelectMany(task => new[] { task!.OriginalPath, task.CurrentPath }
                    .Concat(task.VolumePaths))
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path)
                    && !ProcessArtifactLayout.IsInsideDeletableProcessFolders(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(240)
                .ToList();

            if (sources.Count == 0)
            {
                return;
            }

            string? volumeRoot = Path.GetPathRoot(sources[0]);

            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                return;
            }

            string backupDirectory = Path.Combine(
                volumeRoot,
                ".ArchiveFixerSrcProtect",
                Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(backupDirectory);
                HideDirectory(Path.GetDirectoryName(backupDirectory));
                HideDirectory(backupDirectory);
                int index = 0;

                foreach (string source in sources)
                {
                    if (!HardLinkHelper.CanHardLink(source, backupDirectory))
                    {
                        continue;
                    }

                    string backup = Path.Combine(backupDirectory, $"{index++:D4}.bin");

                    if (HardLinkHelper.TryCreateHardLink(backup, source))
                    {
                        _keepSourceSnapshots.Add((source, backup));
                    }
                }

                _keepSourceSnapshotDirectory = backupDirectory;

                AppendLog(
                    "INFO",
                    $"KeepInPlace 源文件保护：已给 {_keepSourceSnapshots.Count} 份源文件建同卷硬链接快照");
            }
            catch
            {
                _keepSourceSnapshots.Clear();
                _keepSourceSnapshotDirectory = string.Empty;
            }
        }

        internal void RestoreKeepInPlaceSourceNames()
        {
            if (_vm.SpaceTightMode)
            {
                return;
            }

            foreach ((string original, string backup) in _keepSourceSnapshots.ToList())
            {
                try
                {
                    if (File.Exists(original) || !File.Exists(backup))
                    {
                        continue;
                    }

                    string? directory = Path.GetDirectoryName(original);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);

                    if (HardLinkHelper.TryCreateHardLink(original, backup))
                    {
                        AppendLog(
                            "WARN",
                            $"{Path.GetFileName(original)}：源包档=留下，但源名被搬走/改名 —— 已按原路径硬链接恢复"
                            + "（源字节不动）");
                    }
                }
                catch
                {
                    // 恢复不了就保持原样。
                }
            }

            try
            {
                if (_keepSourceSnapshotDirectory.Length > 0
                    && Directory.Exists(_keepSourceSnapshotDirectory))
                {
                    Directory.Delete(_keepSourceSnapshotDirectory, recursive: true);
                }
            }
            catch
            {
                // 零字节链接清不掉不影响结论。
            }

            _keepSourceSnapshots.Clear();
            _keepSourceSnapshotDirectory = string.Empty;
        }

        private (string Directory, List<(string Original, string Backup)> Items) ProtectKeepInPlaceSourcesBeforeRepair(
            string? entryPath)
        {
            if (AppSettings.ParseSourceHandling(Settings.SourceHandling) != SourceHandlingMode.KeepInPlace
                || string.IsNullOrWhiteSpace(entryPath))
            {
                return (string.Empty, new List<(string, string)>());
            }

            string backupDirectory = Path.Combine(
                Path.GetDirectoryName(entryPath) ?? string.Empty,
                ".ArchiveFixer.work",
                "keep-src-" + Guid.NewGuid().ToString("N"));

            var items = new List<(string Original, string Backup)>();

            try
            {
                Directory.CreateDirectory(backupDirectory);
                int index = 0;

                foreach (string source in CollectOriginalSourceFiles(Tasks))
                {
                    if (items.Count >= 120 || !File.Exists(source))
                    {
                        break;
                    }

                    string backup = Path.Combine(backupDirectory, $"{index++:D4}-{Path.GetFileName(source)}");

                    if (HardLinkHelper.CanHardLink(source, backupDirectory)
                        && HardLinkHelper.TryCreateHardLink(backup, source))
                    {
                        items.Add((source, backup));
                    }
                }
            }
            catch
            {
                return (backupDirectory, items);
            }

            return (backupDirectory, items);
        }

        private void RestoreKeepInPlaceSourcesAfterRepair(
            (string Directory, List<(string Original, string Backup)> Items) protection)
        {
            foreach ((string original, string backup) in protection.Items)
            {
                try
                {
                    if (!File.Exists(original)
                        && File.Exists(backup)
                        && HardLinkHelper.TryCreateHardLink(original, backup))
                    {
                        AppendLog(
                            "WARN",
                            $"{Path.GetFileName(original)}：源包档=留下，但规范化改名把源名改走了 —— "
                            + "已按原路径硬链接恢复（源字节不动）");
                    }
                }
                catch
                {
                    // 恢复不了就保持原样，不拿坏链接冒充恢复。
                }
            }
        }

        private void CleanupKeepInPlaceSourceProtection(
            (string Directory, List<(string Original, string Backup)> Items) protection)
        {
            if (string.IsNullOrWhiteSpace(protection.Directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(protection.Directory))
                {
                    Directory.Delete(protection.Directory, recursive: true);
                }

                string? parent = Path.GetDirectoryName(protection.Directory);

                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    if (!Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                    }
                    else
                    {
                        HideDirectory(parent);
                    }
                }
            }
            catch
            {
                // 零字节链接清不掉不影响结论。
            }
        }

        /// <summary>
        /// 源保护目录是程序自己的临时区，默认带 `.` 前缀在 Windows 资源管理器里**仍然可见**；
        /// 这里补上隐藏属性（用户 2026-10-25：工作区要完全隐藏，不能在 H:\ 根上露出来）。
        /// </summary>
        private static void HideDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                File.SetAttributes(path, attributes | FileAttributes.Hidden);
            }
            catch
            {
                // 属性设不上不影响保护逻辑；不因为隐藏失败中断流程。
            }
        }

    }
}