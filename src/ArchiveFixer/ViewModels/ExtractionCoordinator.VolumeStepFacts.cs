using ArchiveFixer.Extraction;
using ArchiveFixer.Models;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 批首分卷入口点名 + 入口包/产出链解析 + 第二大步事实位（第 12 块）的**调用点兼容层** ——
/// 真实现已搬到独立协作类 <see cref="ExtractionVolumeStepFacts"/>（持宿主 `_host`），
/// 这里只保留必要转发，签名/可见性与搬出前逐字一致。
/// </summary>
internal sealed partial class ExtractionCoordinator
{

    // ===== 搬出后的入口薄转发（实现见 ExtractionVolumeStepFacts）=====
    private ExtractionVolumeStepFacts? _volumeStepFacts;

    private ExtractionVolumeStepFacts VolumeStepFacts =>
        _volumeStepFacts ??= new ExtractionVolumeStepFacts(this);

    /// <summary>薄转发：实现见 <see cref="ExtractionVolumeStepFacts.ResolveProducerEntryArchivePathCore"/>。</summary>
    internal string ResolveProducerEntryArchivePathCore(
        ArchiveTask task,
        ExtractOptions options,
        string archivePath,
        int depth) =>
        VolumeStepFacts.ResolveProducerEntryArchivePathCore(task, options, archivePath, depth);

    /// <summary>
    /// 薄转发：实现见 <see cref="ExtractionVolumeStepFacts.FindProducerTaskForPackage"/> ——
    /// 另一个 partial（`FindProducerTaskForPackageForHandler`）要用，方法名不另起。
    /// </summary>
    private ArchiveTask? FindProducerTaskForPackage(ArchiveTask task, string packagePath, string entryName) =>
        VolumeStepFacts.FindProducerTaskForPackage(task, packagePath, entryName);

    /// <summary>
    /// 薄转发：实现见 <see cref="ExtractionVolumeStepFacts.IsUnresolvedVolumeGroupMember"/> ——
    /// `IWorkspaceStepHost` 的显式实现（另一个 partial）要用，方法名不另起。
    /// </summary>
    private bool IsUnresolvedVolumeGroupMember(ArchiveTask task) =>
        VolumeStepFacts.IsUnresolvedVolumeGroupMember(task);

    /// <summary>薄转发：实现见 <see cref="ExtractionVolumeStepFacts.RememberVolumeStepTask"/>。</summary>
    internal void RememberVolumeStepTask(ArchiveTask task) => VolumeStepFacts.RememberVolumeStepTask(task);

    /// <summary>薄转发：实现见 <see cref="ExtractionVolumeStepFacts.RememberVolumeStepTaskForRound"/>。</summary>
    internal void RememberVolumeStepTaskForRound(ArchiveTask task) =>
        VolumeStepFacts.RememberVolumeStepTaskForRound(task);

    /// <summary>薄转发：实现见 <see cref="ExtractionVolumeStepFacts.IsSecondStepTask"/>。</summary>
    internal bool IsSecondStepTask(ArchiveTask? task) => VolumeStepFacts.IsSecondStepTask(task);

}
