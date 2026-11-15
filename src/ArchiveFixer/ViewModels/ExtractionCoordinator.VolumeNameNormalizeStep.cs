using ArchiveFixer.BindingModels;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 批首改名归一 + 改后任务表同步（原 <c>ExtractionCoordinator.VolumeNameNormalizeStep.cs</c> 的全部实现）——
/// 实现已整块搬到协作类 <see cref="VolumeNameNormalizeHandler"/>，这一分片只留同签名薄转发。
///
/// <para>⛔ 判据、文案、任务表同步范围与原来逐字一致；调用点
/// （<c>TaskActionCoordinator</c> / <c>ExtractionCandidateLoop</c> / 其它 partial / 多个 Handler /
/// 用例 <c>InnerPackageRestGroupMoveTests</c>）一处未动 —— 转发签名、参数名与可见性一字不改，
/// 跨类静态调用（<c>ExtractionCoordinator.SnapshotTaskTable</c> 等）照旧可用。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private VolumeNameNormalizeHandler? _volumeNameNormalizeHandler;

    private VolumeNameNormalizeHandler VolumeNameNormalize =>
        _volumeNameNormalizeHandler ??= new VolumeNameNormalizeHandler(this);

    /// <summary>薄转发：实现见 <see cref="VolumeNameNormalizeHandler.SyncTasksAfterVolumeRename"/>。</summary>
    internal IReadOnlyList<ArchiveTask> SyncTasksAfterVolumeRename(
        IEnumerable<VolumeRepairItem>? items,
        ArchiveTask? primary = null) =>
        VolumeNameNormalize.SyncTasksAfterVolumeRename(items, primary);

    /// <summary>薄转发：实现见 <see cref="VolumeNameNormalizeHandler.TryMeasureProducedContent"/>。</summary>
    internal static bool TryMeasureProducedContent(
        string? directory,
        ArchiveOperationResult engineResult,
        out int files,
        out long bytes) =>
        VolumeNameNormalizeHandler.TryMeasureProducedContent(directory, engineResult, out files, out bytes);

    /// <summary>薄转发：实现见 <see cref="VolumeNameNormalizeHandler.SnapshotTaskTable"/>。</summary>
    internal static ArchiveTask[] SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks) =>
        VolumeNameNormalizeHandler.SnapshotTaskTable(tasks);

    /// <summary>薄转发：实现见 <see cref="VolumeNameNormalizeHandler.HasSiblingVolumeBeside"/>。</summary>
    internal static bool HasSiblingVolumeBeside(string? path) =>
        VolumeNameNormalizeHandler.HasSiblingVolumeBeside(path);
}
