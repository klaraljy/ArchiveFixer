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
/// 缺卷补救与批前收卷（第 8 块）。实现已整块搬进 <see cref="VolumeRepairHandler"/>
/// （<c>ViewModels/Extraction/Steps/VolumeRepairHandler.cs</c>），本文件只保留同名薄转发与三处最小访问器；
/// ⛔ 判据与日志顺序一个字不改。
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeRepairHandler.cs）=====
    private VolumeRepairHandler? _volumeRepairStepHandler;

    private VolumeRepairHandler VolumeRepairStepHandler =>
        _volumeRepairStepHandler ??= new VolumeRepairHandler(this);

    private Task<bool> TryRepairMissingVolumesAsync(ArchiveTask task, CancellationToken cancellationToken) =>
        VolumeRepairStepHandler.TryRepairMissingVolumesAsync(task, cancellationToken);

    internal static List<string> CollectVolumeFilesInDirectory(string directory) =>
        VolumeRepairHandler.CollectVolumeFilesInDirectory(directory);

    private static VolumeGroup? RebuildVolumeGroup(ArchiveTask task, IEnumerable<string> extraFiles) =>
        VolumeRepairHandler.RebuildVolumeGroup(task, extraFiles);

    private static IEnumerable<string> CollectOriginalSourceFiles(IEnumerable<ArchiveTask> tasks) =>
        VolumeRepairHandler.CollectOriginalSourceFiles(tasks);

    private int CountFirstVolumeTasksInTable() =>
        VolumeRepairStepHandler.CountFirstVolumeTasksInTable();

    /// <summary>兼容出口（测试/轮次层读它）：实现已搬进 <see cref="ArchiveFixer.Extraction.VolumeNameOrchestration"/>。</summary>
    internal static bool TryConfirmVolumeGroupComplete(string filePath, out string note) =>
        VolumeRepairHandler.TryConfirmVolumeGroupComplete(filePath, out note);

    // ===== 搬出去的那几处要用协调器的私有成员：最小 internal 访问器（⛔ 判据一个字不改）=====
    /// <summary>
    /// 「这一批已经问过这一单的缺卷补救」的登记（字段实现在 VolumeLedgerStep 分片）：
    /// 锁与登记都留在宿主这一侧，Handler 只取"这次是不是第一次问"。
    /// </summary>
    internal bool TryMarkVolumeRepairAskedForHandler(ArchiveTask task)
    {
        lock (_volumeRepairAsked)
        {
            return _volumeRepairAsked.Add(task);
        }
    }

    /// <summary>访问器：确认框的唯一出口（实现链见 BatchReminderStep 分片）；Handler 用。</summary>
    internal Task<bool> DispatcherSafeConfirmForHandlerAsync(string message) =>
        DispatcherSafeConfirmAsync(message);

    /// <summary>访问器：UI 线程目录选择框的唯一出口（实现链见 BatchReminderStep 分片）；Handler 用。</summary>
    internal Task<string> ShowFolderBrowserOnUiThreadForHandlerAsync() =>
        ShowFolderBrowserOnUiThreadAsync();
}
