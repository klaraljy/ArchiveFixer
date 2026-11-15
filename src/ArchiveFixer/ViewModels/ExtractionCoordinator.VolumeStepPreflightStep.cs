using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 第二大步开工前检查与组收片账（同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="VolumeStepPreflightHandler"/>
/// （<c>ViewModels/Extraction/Steps/VolumeStepPreflightHandler.cs</c>），本文件只保留
/// 别的 partial 直接读写的那两本账与嵌套记录、仍有调用方的同签名薄转发、以及搬出去的实现要用的
/// 最小 internal 访问器；⛔ 判据、文案、日志顺序、删除路径一个字不改。</para>
///
/// <para>⚠ 开工前检查 `TryStopVolumeStepBeforeStartAsync` 的宿主调用点已按 2026-10-24 口径停用
/// （第一大步不再判分卷）；实现随本块原样搬进 handler，⛔ 这里不给它留空转发（ADR 0002/0003）。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeStepPreflightHandler.cs）=====
    private VolumeStepPreflightHandler? _volumeStepPreflightHandler;

    private VolumeStepPreflightHandler VolumeStepPreflight =>
        _volumeStepPreflightHandler ??= new VolumeStepPreflightHandler(this);

    /// <summary>薄转发：实现见 <see cref="VolumeStepPreflightHandler.RememberVolumeStepGroupWork"/>。</summary>
    internal void RememberVolumeStepGroupWork(
        ArchiveTask task,
        string baseName,
        string entryLayer,
        IReadOnlyList<string> pieces) =>
        VolumeStepPreflight.RememberVolumeStepGroupWork(task, baseName, entryLayer, pieces);

    /// <summary>薄转发：实现见 <see cref="VolumeStepPreflightHandler.ResetVolumeStepGroupWork"/>。</summary>
    internal void ResetVolumeStepGroupWork() => VolumeStepPreflight.ResetVolumeStepGroupWork();

    /// <summary>薄转发：实现见 <see cref="VolumeStepPreflightHandler.CleanupVolumeStepGroupEntry"/>。</summary>
    internal void CleanupVolumeStepGroupEntry(ArchiveTask task) =>
        VolumeStepPreflight.CleanupVolumeStepGroupEntry(task);

    /// <summary>薄转发：实现见 <see cref="VolumeStepPreflightHandler.CleanupAllVolumeStepEntryDirectories"/>。</summary>
    internal void CleanupAllVolumeStepEntryDirectories() =>
        VolumeStepPreflight.CleanupAllVolumeStepEntryDirectories();

    /// <summary>薄转发：实现见 <see cref="VolumeStepPreflightHandler.CleanupGroupEntryShellsFor"/>。</summary>
    internal void CleanupGroupEntryShellsFor(IEnumerable<ArchiveTask>? tasks) =>
        VolumeStepPreflight.CleanupGroupEntryShellsFor(tasks);

    /// <summary>薄转发：实现见 <see cref="VolumeStepPreflightHandler.TryPurgeSolvedVolumeStepPieces"/>。</summary>
    internal void TryPurgeSolvedVolumeStepPieces(ArchiveTask task) =>
        VolumeStepPreflight.TryPurgeSolvedVolumeStepPieces(task);

    // ===== 账本状态：别的 partial / 别的类直接读写，原样留在这里（⛔ 不搬第二份）=====
    /// <summary>第二大步入口任务 ⇒ 它这一组的基名/入口层/片清单（成功后收片与回写用；批首清空）。</summary>
    internal readonly Dictionary<ArchiveTask, VolumeStepGroupWork> _volumeStepGroupWork = new();

    /// <summary>第二大步每组一句话结论（批末回写读它；⛔ 不从日志反推）。</summary>
    internal readonly Dictionary<string, string> _volumeStepGroupVerdicts = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record VolumeStepGroupWork(string BaseName, string EntryLayer, IReadOnlyList<string> Pieces);

    // ===== 搬出去的实现要用的 private 成员：定义在别的 partial 里（不改那些文件），
    // 这里只加最小 internal 访问器，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）=====

    /// <summary>访问器：缺卷补救（实现 = VolumeRepairStep 分片的 private 方法）。</summary>
    internal Task<bool> TryRepairMissingVolumesForVolumeStepPreflightAsync(
        ArchiveTask task,
        CancellationToken cancellationToken) =>
        TryRepairMissingVolumesAsync(task, cancellationToken);

    /// <summary>访问器：解前分卷判定（实现 = Recursion 分片的 private 转发）。</summary>
    internal Task<VolumeGroupResolution> ResolveVolumeGroupForVolumeStepPreflightAsync(
        ArchiveTask task,
        CancellationToken cancellationToken) =>
        ResolveVolumeGroupAsync(task, cancellationToken);

    /// <summary>访问器：缺卷诊断文案（实现 = Recursion 分片的 private static）。</summary>
    internal static string BuildVolumeVerdictMessageForVolumeStepPreflight(
        ArchiveTask task,
        VolumeGroupResolution resolution) =>
        BuildVolumeVerdictMessage(task, resolution);

    /// <summary>访问器：分卷证据行（实现 = Recursion 分片的 private 转发）。</summary>
    internal void LogVolumeGroupEvidenceForVolumeStepPreflight(
        ArchiveTask task,
        VolumeGroupResolution resolution) =>
        LogVolumeGroupEvidence(task, resolution);

    /// <summary>访问器：缺卷延期账（实现 = VolumeLedgerStep 分片的 private 转发）。</summary>
    internal void RecordDeferredVolumeDeficitForVolumeStepPreflight(ArchiveTask task, string missingNames) =>
        RecordDeferredVolumeDeficit(task, missingNames);
}
