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
using System.Collections.Concurrent;
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
/// 分卷交接登记与"这一份是不是本批某组的一片"（第 13 块，从主文件搬出；同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="VolumeHandoffHandler"/>，本文件只保留交接账本/管线这些**状态字段**、
/// 同名薄转发与一处最小 internal 访问器；⛔ 判据、日志顺序、落点推导一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    /// <summary>第一大步的交接事实表（第二大步的组分析从这里取候选）。</summary>
    internal readonly List<ArchiveFixer.Detection.VolumeHandoffEntry> _volumeHandoffEntries = new();

    /// <summary>
    /// **第一大步文件账本**（共用骨架）：闸门通过的每一份文件都登记，
    /// 包括内容 Unknown 的中间片；族 resolver 从这里取候选。
    /// </summary>
    internal readonly FirstStepFileLedger _firstStepFileLedger = new();

    /// <summary>本批的源根 → 产出物 → 内容组账本骨架（第一阶段只登记源根）。</summary>
    internal readonly ExtractionContentGraph _extractionContentGraph = new();

    /// <summary>
    /// **边界解析管线**（共用骨架）：族 resolver 从这里注册；骨架阶段未注册任何族，
    /// 因此 `ResolveAllAsync` 返回空，既有行为一个字不变。
    /// </summary>
    private readonly VolumeBoundaryPipeline _volumeBoundaryPipeline = CreateVolumeBoundaryPipeline();

    private static VolumeBoundaryPipeline CreateVolumeBoundaryPipeline()
    {
        var pipeline = new VolumeBoundaryPipeline();

        // 7z / RAR 先接；ZIP 支线再接，⛔ 不在共享层写族特判。
        pipeline.Register(new SevenZipBoundaryResolver());
        pipeline.Register(new RarBoundaryResolver());
        pipeline.Register(new ZipSpannedBoundaryResolver());
        pipeline.Register(new ZipNumberedBoundaryResolver());

        return pipeline;
    }

    /// <summary>本批的交接事实（只读给第二大步/日志用）。</summary>
    internal IReadOnlyList<ArchiveFixer.Detection.VolumeHandoffEntry> VolumeHandoffEntries => _volumeHandoffEntries;

    /// <summary>边界解析管线（第二大步/测试用）。</summary>
    internal VolumeBoundaryPipeline VolumeBoundaryPipeline => _volumeBoundaryPipeline;

    /// <summary>本批的第一大步文件账本（只读给第二大步/日志用）。</summary>
    internal FirstStepFileLedger FirstStepLedger => _firstStepFileLedger;

    /// <summary>本批源根图（只读给测试/后续阶段用）。</summary>
    internal ExtractionContentGraph ContentGraph => _extractionContentGraph;

    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeHandoffHandler.cs）=====
    private VolumeHandoffHandler? _volumeHandoffHandler;

    private VolumeHandoffHandler VolumeHandoff =>
        _volumeHandoffHandler ??= new VolumeHandoffHandler(this);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.RegisterSourceRoots"/>。</summary>
    internal int RegisterSourceRoots(IEnumerable<ArchiveTask>? tasks, OneClickRunOptions? runOptions) =>
        VolumeHandoff.RegisterSourceRoots(tasks, runOptions);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.BuildSourcePlacementKey"/>。</summary>
    private static string BuildSourcePlacementKey(
        ArchiveTask task,
        OutputPlacementMode placementMode,
        string? customRoot) =>
        VolumeHandoffHandler.BuildSourcePlacementKey(task, placementMode, customRoot);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.RegisterContentGroups"/>。</summary>
    internal int RegisterContentGroups(IEnumerable<PreparedVolumeGroup>? groups) =>
        VolumeHandoff.RegisterContentGroups(groups);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.AttachContentGroupOwner"/>。</summary>
    internal void AttachContentGroupOwner(
        PreparedVolumeGroup? prepared,
        string? ownerTaskFileName,
        string? ownerTaskPath = null) =>
        VolumeHandoff.AttachContentGroupOwner(prepared, ownerTaskFileName, ownerTaskPath);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.NormalizePlacementPath"/>。</summary>
    private static string NormalizePlacementPath(string? path) =>
        VolumeHandoffHandler.NormalizePlacementPath(path);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.ShouldStopAtCandidateVolumePiece"/>。</summary>
    private bool ShouldStopAtCandidateVolumePiece(string path, ArchiveTask owner) =>
        VolumeHandoff.ShouldStopAtCandidateVolumePiece(path, owner);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.ResolveZipVariant"/>。</summary>
    internal static ZipVariant ResolveZipVariant(ArchiveFixer.Detection.FileNatureCard card) =>
        VolumeHandoffHandler.ResolveZipVariant(card);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.CleanupSuffixJunkAtSecondStep"/>。</summary>
    internal int CleanupSuffixJunkAtSecondStep(IReadOnlyCollection<ArchiveTask>? tasks) =>
        VolumeHandoff.CleanupSuffixJunkAtSecondStep(tasks);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     */
    /// <summary>访问器：给 <see cref="VolumeHandoffHandler"/> 往本批「后缀清洗」账上记一笔。</summary>
    internal List<(string Original, string Renamed)> SuffixRenamedSourcesForVolumeHandoff =>
        _suffixRenamedSources;

    internal readonly List<(string OriginalPath, string CurrentPath)> _gatheredUserSourcePieces = new();

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.RememberGatheredUserSourcePieces"/>。</summary>
    private void RememberGatheredUserSourcePieces(IReadOnlyList<VolumeRepairItem> items) =>
        VolumeHandoff.RememberGatheredUserSourcePieces(items);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.RestoreGatheredUserSourcePieces"/>。</summary>
    internal int RestoreGatheredUserSourcePieces(List<(string Level, string Message)>? logEntries) =>
        VolumeHandoff.RestoreGatheredUserSourcePieces(logEntries);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.BuildVolumeGroupAttemptSignature"/>。</summary>
    private static string BuildVolumeGroupAttemptSignature(VolumeGroup group) =>
        VolumeHandoffHandler.BuildVolumeGroupAttemptSignature(group);

    /// <summary>薄转发：实现见 <see cref="VolumeHandoffHandler.LogVolumeGroupFirstVolumeLanding"/>。</summary>
    private void LogVolumeGroupFirstVolumeLanding(ArchiveTask task, string outputPath) =>
        VolumeHandoff.LogVolumeGroupFirstVolumeLanding(task, outputPath);

    // ===== 搬出后的入口薄转发（实现见独立类）=====
    private ExtractionVolumeHandoffLedger? _volumeHandoffLedger;
    private ExtractionVolumeHandoffLedger VolumeHandoffLedger =>
        _volumeHandoffLedger ??= new ExtractionVolumeHandoffLedger(this);

    internal ArchiveFixer.Detection.VolumeHandoffEntry RegisterVolumeHandoff(
        ArchiveTask task,
        ArchiveFixer.Detection.FileNatureCard card) =>
        VolumeHandoffLedger.RegisterVolumeHandoff(task, card);

    internal void ClearVolumeHandoffLedger() => VolumeHandoffLedger.ClearVolumeHandoffLedger();

    internal void MarkVolumeHandoffConsumed(IEnumerable<string> paths, string by) =>
        VolumeHandoffLedger.MarkVolumeHandoffConsumed(paths, by);

    internal void AppendVolumeHandoffBatchSummary() => VolumeHandoffLedger.AppendVolumeHandoffBatchSummary();

    internal void MarkVolumeHandoffVerdict(IEnumerable<string>? pieces, string verdict) =>
        VolumeHandoffLedger.MarkVolumeHandoffVerdict(pieces, verdict);

    private void RegisterUnrecognizedSuspect(ArchiveTask task) =>
        VolumeHandoffLedger.RegisterUnrecognizedSuspect(task);

    private ExtractionProducedVolumeGate? _producedVolumeGate;
    private ExtractionProducedVolumeGate ProducedVolumeGate =>
        _producedVolumeGate ??= new ExtractionProducedVolumeGate(this);

    internal static (long Lookups, long Hits, long Misses, long FileInfoCalls, long Remembered) ProducedCardDiagnostics =>
        ExtractionProducedVolumeGate.ProducedCardDiagnostics;

    internal static void ResetProducedCardDiagnostics() => ExtractionProducedVolumeGate.ResetProducedCardDiagnostics();

    internal bool TryGetProducedCard(string path, out FileNatureCard card) =>
        ProducedVolumeGate.TryGetProducedCard(path, out card);

    internal Task RegisterProducedOutputsToLedgerAsync(ArchiveTask task) =>
        ProducedVolumeGate.RegisterProducedOutputsToLedgerAsync(task);

    internal FirstStepFileLedgerEntry RegisterFirstStepLedgerEntry(
        ArchiveTask task,
        ArchiveFixer.Detection.FileNatureCard card) =>
        ProducedVolumeGate.RegisterFirstStepLedgerEntry(task, card);

    internal bool HasContentProvenVolumeFirstSibling(ArchiveTask task, string entryName) =>
        ProducedVolumeGate.HasContentProvenVolumeFirstSibling(task, entryName);
    /// <summary>已经试过的分卷组签名（跨 partial 使用：批内去重）。</summary>
    internal readonly HashSet<string> _triedVolumeGroupSignatures =
        new(StringComparer.OrdinalIgnoreCase);

    internal void ClearProducedCardCache() => ProducedVolumeGate.ClearProducedCardCache();
}
