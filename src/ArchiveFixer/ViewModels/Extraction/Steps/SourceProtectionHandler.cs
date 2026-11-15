using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 用户源包就地保护/真名还原（从 `ExtractionCoordinator.SourceProtectionStep.cs` 搬出，第 8 块）：
/// KeepInPlace 的批末硬链接快照与恢复、后缀清洗别名的真名还原、缺卷补救前后的一次性源包保护、
/// 快照目录清理与隐藏属性。
///
/// <para>⛔ 判据、日志文案与顺序、硬链接方向、删除范围一个字都没改；本类只持有宿主
/// <see cref="ExtractionCoordinator"/>，状态仍全部落在宿主那几个字段上（实现只此一份）。</para>
///
/// <para>M5 类型总行数治理：宿主那一分片 <c>ExtractionCoordinator.SourceProtectionStep.cs</c> 已删除，
/// 不再留薄转发/访问器 —— 入口是 <c>ExtractionCoordinator.SourceProtection</c>（懒建属性，住在
/// <c>ViewModels/ExtractionCoordinator.cs</c>），调用点直接调本类同名方法；读写的那两份账直接走宿主
/// <c>_volumeLedgerState.KeepSourceSnapshots</c> / <c>_volumeLedgerState.KeepSourceSnapshotDirectory</c>。
/// 那个属性只能住在协调器自己的 partial 里：把属性挪进本文件会让本文件出现第 2 个顶层类型，
/// 结构守卫（一类型一文件）当场变红。</para>
/// </summary>
internal sealed class SourceProtectionHandler
{
    private readonly ExtractionCoordinator _host;

    internal SourceProtectionHandler(ExtractionCoordinator host)
    {
        _host = host;
    }

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
        _host._volumeLedgerState.KeepSourceSnapshots.Clear();
        _host._volumeLedgerState.KeepSourceSnapshotDirectory = string.Empty;

        if (AppSettings.ParseSourceHandling(_host.Settings.SourceHandling) != SourceHandlingMode.KeepInPlace)
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
        if (_host._vm.SpaceTightMode)
        {
            return;
        }

        var sources = (tasks ?? Array.Empty<ArchiveTask>())
            .Where(task => task != null && !IsSingleCompleteArchive(task!))
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

        /*
         * ⛔ 只在**这一批已经解析过的工作区**里放保护快照：
         * 旧实现拿 sources[0] 的盘根拼 `<盘>\.ArchiveFixerSrcProtect`，会在 C/E/H 根目录
         * 留下隐藏目录（C 盘用户明确说只放重要资料）。工作区由批首
         * `ApplyBatchWorkspaceRoot` 定为 `<选中目标目录>\.ArchiveFixer.work`；
         * 源包不在同一卷时硬链接建不出来 ⇒ 这一份跳过，绝不回落盘根。
         */
        string workspaceRoot = _host._vm.CurrentWorkspaceRoot;

        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || !string.Equals(
                Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar)),
                ".ArchiveFixer.work",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string backupDirectory = Path.Combine(
            workspaceRoot,
            "source-protect",
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
                    _host._volumeLedgerState.KeepSourceSnapshots.Add((source, backup));
                }
            }

            _host._volumeLedgerState.KeepSourceSnapshotDirectory = backupDirectory;

            _host.AppendLog(
                "INFO",
                $"KeepInPlace 源文件保护：已给 {_host._volumeLedgerState.KeepSourceSnapshots.Count} 份源文件建同卷硬链接快照");
        }
        catch
        {
            _host._volumeLedgerState.KeepSourceSnapshots.Clear();
            _host._volumeLedgerState.KeepSourceSnapshotDirectory = string.Empty;
        }
    }
    private static bool IsSingleCompleteArchive(ArchiveTask task)
    {
        try
        {
            string path = string.IsNullOrWhiteSpace(task.CurrentPath) ? task.OriginalPath : task.CurrentPath;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            FileNatureCard card = FileNature.Inspect(path);

            if (card.BodyKind == ArchiveBodyKind.SingleComplete)
            {
                return true;
            }

            // 与 ProcessExtractTaskAsync 的闸门后改名条件同一份口径：
            // 内容卡判不出、但任务已被识别成归档且后缀需要修，也会在第一大步改名。
            return card.BodyKind == ArchiveBodyKind.Unknown
                && task.IsArchive
                && (task.ExtensionStatus == StatusText.ExtensionMismatch
                    || task.ExtensionStatus == StatusText.ExtensionMissing
                    || task.ExtensionStatus == StatusText.ExtensionMultiFake);
        }
        catch
        {
            return false;
        }
    }

    internal void RestoreKeepInPlaceSourceNames()
    {
        if (_host._vm.SpaceTightMode)
        {
            return;
        }

        ReconcileSuffixRenamedSources();

        foreach ((string original, string backup) in _host._volumeLedgerState.KeepSourceSnapshots.ToList())
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
                    _host.AppendLog(
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
            DeleteSourceSnapshotDirectory(_host._volumeLedgerState.KeepSourceSnapshotDirectory);
        }
        catch
        {
            // 零字节链接清不掉不影响结论。
        }

        _host._volumeLedgerState.KeepSourceSnapshots.Clear();
        _host._volumeLedgerState.KeepSourceSnapshotDirectory = string.Empty;
    }

    /// <summary>
    /// 删掉保护快照目录；**归集把整棵工作区搬走**时按目录名到当前工作区里再找一次。
    ///
    /// <para>真机 125 批 926000055（归集 + 同基名形状，运行期事实）：快照建在
    /// `原包\aaa\.ArchiveFixer.work\source-protect\<guid>`，归集把产物树搬到
    /// `归集\aaa` 之后原路径已不在盘上 ⇒ 老写法只判 `Directory.Exists(旧路径)`，
    /// 整个快照目录（含 4.1 MB 源包硬链接）留在归集目录里没清。</para>
    ///
    /// <para>判据只认"同一个快照目录名 + 当前工作区"这条路径事实；⛔ 不猜别的目录。</para>
    /// </summary>
    private void DeleteSourceSnapshotDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
            return;
        }

        string name = Path.GetFileName(directory);
        string workspace = _host._vm.CurrentWorkspaceRoot;

        if (name.Length == 0 || string.IsNullOrWhiteSpace(workspace))
        {
            return;
        }

        string relocated = Path.Combine(workspace, "source-protect", name);

        if (Directory.Exists(relocated))
        {
            Directory.Delete(relocated, recursive: true);
            _host.AppendLog(
                "INFO",
                $"KeepInPlace 源文件保护：快照目录被归集搬走，已按当前工作区清理（{name}）");
        }
    }
    /// <summary>
    /// 后缀清洗把用户源名 <c>.001删</c> 改成标准名 <c>.001</c> 后，源包档=留下时收尾必须
    /// 把标准名**还回原名**，不能“恢复原名再留一个标准名硬链接”。
    /// 判据读文件身份（卷序列号 + 文件索引），不按路径/名字猜。
    /// </summary>
    private void ReconcileSuffixRenamedSources()
    {
        if (_host._volumeLedgerState.SuffixRenamedSources.Count == 0)
        {
            return;
        }

        IReadOnlyList<(string Original, string Renamed)> changed = SuffixAliasReconciler.Reconcile(
            _host._volumeLedgerState.SuffixRenamedSources,
            original => _host._volumeLedgerState.KeepSourceSnapshots
                .Where(item => SafePathHelper.PathEquals(item.Original, original))
                .Select(item => item.Backup)
                .FirstOrDefault(),
            message => _host.AppendLog("INFO", message));

        foreach ((string original, string renamed) in changed)
        {
            UpdateRenamedPathReferences(renamed, original);
        }

        _host._volumeLedgerState.SuffixRenamedSources.Clear();
    }

    private void UpdateRenamedPathReferences(string oldPath, string newPath)
    {
        foreach (ArchiveTask task in _host.Tasks)
        {
            if (SafePathHelper.PathEquals(task.CurrentPath, oldPath))
            {
                task.CurrentPath = newPath;
            }

            for (int index = 0; index < task.VolumePaths.Count; index++)
            {
                if (SafePathHelper.PathEquals(task.VolumePaths[index], oldPath))
                {
                    task.VolumePaths[index] = newPath;
                }
            }
        }

        foreach (ArchiveFixer.Detection.VolumeHandoffEntry entry in _host.VolumeHandoff.Entries)
        {
            if (SafePathHelper.PathEquals(entry.Path, oldPath))
            {
                entry.Path = newPath;
            }
        }
    }

    private (string Directory, List<(string Original, string Backup)> Items) ProtectKeepInPlaceSourcesBeforeRepair(
        string? entryPath)
    {
        if (AppSettings.ParseSourceHandling(_host.Settings.SourceHandling) != SourceHandlingMode.KeepInPlace
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

            foreach (string source in VolumeRepairHandler.CollectOriginalSourceFiles(_host.Tasks))
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
                    _host.AppendLog(
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
