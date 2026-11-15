using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 内嵌归档探针 / 尾部归档直读 / 双面文件补救（原 <c>RecursiveExtractor.Probe.Embedded.cs</c> 的全部实现）——
/// 实现已整块搬到协作类 <see cref="RecursiveEmbeddedProbeHandler"/>，这一分片只留同签名薄转发 + 最小 internal 访问器。
///
/// <para>⛔ 判据、日志顺序、调用顺序一个字都没改；调用点（<c>LayerExecutor</c>、
/// <c>CandidateFailureClassifier</c>、<c>RecursiveLayerExpansion</c>、<c>RecursiveResultBuilder</c>、
/// <c>ViewModels/ExtractionCandidateLoop</c>、同分片的 <c>RecursiveExtractor.Probe.Layer.cs</c> 等）一处未动。</para>
/// </summary>
public sealed partial class RecursiveExtractor
{
    /// <summary>搬出去的那一份实现（懒建一次；宿主就是本类，全类共用同一个实例）。</summary>
    private RecursiveEmbeddedProbeHandler? _embeddedProbeHandler;

    private RecursiveEmbeddedProbeHandler EmbeddedProbe =>
        _embeddedProbeHandler ??= new RecursiveEmbeddedProbeHandler(this);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.TryTakeSingleStoredEntryAsync"/>。</summary>
    internal Task<LayerOutcome?> TryTakeSingleStoredEntryAsync(
        WorkItem item,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken) =>
        EmbeddedProbe.TryTakeSingleStoredEntryAsync(item, progress, cancellationToken);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.DetectEmbeddedArchive"/>。</summary>
    private static EmbeddedArchiveInfo DetectEmbeddedArchive(string archivePath) =>
        RecursiveEmbeddedProbeHandler.DetectEmbeddedArchive(archivePath);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.IsDoubleFacedEmbeddedArchive"/>。</summary>
    internal static bool IsDoubleFacedEmbeddedArchive(string archivePath) =>
        RecursiveEmbeddedProbeHandler.IsDoubleFacedEmbeddedArchive(archivePath);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.RetryWithCarvedEmbeddedArchiveAsync"/>。</summary>
    internal Task<LayerOutcome?> RetryWithCarvedEmbeddedArchiveAsync(
        WorkItem item,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled) =>
        EmbeddedProbe.RetryWithCarvedEmbeddedArchiveAsync(item, cancellationToken, progress, stalled);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.EnumerateWorkspacePool"/>。</summary>
    internal static List<VolumeCandidate> EnumerateWorkspacePool(ExtractionWorkspace? workspace) =>
        RecursiveEmbeddedProbeHandler.EnumerateWorkspacePool(workspace);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.ResolveProbeEntrySize"/>。</summary>
    private static long ResolveProbeEntrySize(ArchiveListResult? listed, string probeEntry) =>
        RecursiveEmbeddedProbeHandler.ResolveProbeEntrySize(listed, probeEntry);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.FindLandingViolation"/>。</summary>
    internal static string? FindLandingViolation(string outputRoot) =>
        RecursiveEmbeddedProbeHandler.FindLandingViolation(outputRoot);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.MapEngineErrorToStopReason"/>。</summary>
    internal static RecursionStopReason MapEngineErrorToStopReason(ArchiveOperationResult result) =>
        RecursiveEmbeddedProbeHandler.MapEngineErrorToStopReason(result);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.ResolvePasswordStopReason"/>。</summary>
    internal static RecursionStopReason ResolvePasswordStopReason(
        int candidateCount,
        int attempts,
        ArchiveOperationResult? lastFailure,
        bool triedAny) =>
        RecursiveEmbeddedProbeHandler.ResolvePasswordStopReason(candidateCount, attempts, lastFailure, triedAny);

    /// <summary>薄转发：实现见 <see cref="RecursiveEmbeddedProbeHandler.IsArchiveByMagicWithoutVolumeName"/>。</summary>
    private static bool IsArchiveByMagicWithoutVolumeName(string path) =>
        RecursiveEmbeddedProbeHandler.IsArchiveByMagicWithoutVolumeName(path);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那几个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     *
     * 已经在别的 partial 里 internal 的那些直接复用：Limits / Log / DangerousEntriesReported /
     * ReportDangerousEntries / FinishSuccessfulLayerAsync / CarveDirectoryName。
     */

    /// <summary>访问器：给 <see cref="RecursiveEmbeddedProbeHandler"/> 取探针日志的层标签（唯一实现 = 探针层分片）。</summary>
    internal static string DescribeProbeLabelForEmbeddedProbe(WorkItem item) =>
        DescribeProbeLabel(item);

    /// <summary>访问器：给 <see cref="RecursiveEmbeddedProbeHandler"/> 抠出副本后重跑本层（唯一实现 = 主分片）。</summary>
    internal Task<LayerOutcome> ExtractLayerAsyncForEmbeddedProbe(
        WorkItem item,
        CancellationToken cancellationToken,
        IProgress<ArchiveProgress>? progress,
        Action<ArchiveStallNotice>? stalled,
        string? carvedSource) =>
        ExtractLayerAsync(item, cancellationToken, progress, stalled, carvedSource);

    /// <summary>访问器：给 <see cref="RecursiveEmbeddedProbeHandler"/> 判产物里的链接点（唯一实现 = 失败收尾分片）。</summary>
    internal static bool TryGetAttributesForEmbeddedProbe(string path, out FileAttributes attributes) =>
        TryGetAttributes(path, out attributes);
}
