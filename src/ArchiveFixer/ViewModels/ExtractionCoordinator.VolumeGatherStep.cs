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
/// 收片/组内片编排（第 2 块，2026-10-23 从主分片搬出；同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="VolumeGatherHandler"/>，本文件只保留同名薄转发
/// 与五处最小 internal 访问器；⛔ 判据与日志顺序一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeGatherHandler.cs）=====
    private VolumeGatherHandler? _volumeGatherStepHandler;

    private VolumeGatherHandler VolumeGatherStepHandler =>
        _volumeGatherStepHandler ??= new VolumeGatherHandler(this);

    internal void SettleGroupPieceProducers(
        ArchiveTask consumer,
        bool moveSources,
        List<(string Level, string Message)> logEntries) =>
        VolumeGatherStepHandler.SettleGroupPieceProducers(consumer, moveSources, logEntries);

    internal void AdoptUnresolvedVolumePieces(
        RecursionResult? recursion,
        ArchiveTask? owner,
        IReadOnlyList<string>? publishedPieces = null) =>
        VolumeGatherStepHandler.AdoptUnresolvedVolumePieces(recursion, owner, publishedPieces);

    internal List<string> PublishUnresolvedVolumePieces(RecursionResult? recursion, ArchiveTask? owner) =>
        VolumeGatherStepHandler.PublishUnresolvedVolumePieces(recursion, owner);

    internal void PublishUnresolvedPieceLandingCopies(IReadOnlyList<string>? pieces, ArchiveTask? owner) =>
        VolumeGatherStepHandler.PublishUnresolvedPieceLandingCopies(pieces, owner);

    internal VolumeGroup? ResolveGatheredGroupFromEntryLayer(
        ArchiveTask task,
        string? entryLayer,
        string? memberPath = null) =>
        VolumeGatherStepHandler.ResolveGatheredGroupFromEntryLayer(task, entryLayer, memberPath);

    internal string ResolveDeferredGroupMemberPath(ArchiveTask task) =>
        VolumeGatherStepHandler.ResolveDeferredGroupMemberPath(task);

    private string FindLandedCopyBesideProducer(string piecePath, ArchiveTask? owner) =>
        VolumeGatherStepHandler.FindLandedCopyBesideProducer(piecePath, owner);

    private void CollectConsumedSourcePiecesIntoRest(ArchiveTask consumer) =>
        VolumeGatherStepHandler.CollectConsumedSourcePiecesIntoRest(consumer);

    // ===== 搬出去的那几处要用协调器的私有成员：最小 internal 访问器（⛔ 判据一个字不改）=====
    /// <summary>
    /// 「产出方落点里那一份落地副本」的唯一出口（`RememberAdoptedLandedPiece` 那一档要用）：
    /// 实现就是本 partial 的私有薄转发，这里只把它开给同程序集的 Handler（⛔ 判据一个字不改）。
    /// </summary>
    internal string FindLandedCopyBesideProducerForHandler(string piecePath, ArchiveTask? owner) =>
        FindLandedCopyBesideProducer(piecePath, owner);

    internal static string ResolveObservedPiecePathForHandler(ArchiveTask? task, string? memberPath) =>
        ResolveObservedPiecePath(task, memberPath);

    internal string ResolveOwnerChainDirectoryForHandler(ArchiveTask? owner) =>
        ResolveOwnerChainDirectory(owner);

    internal ArchiveTask? ResolveLandedPieceProducerForHandler(string piecePath, ArchiveTask? owner) =>
        ResolveLandedPieceProducer(piecePath, owner);

    internal bool TrySnapshotGroupPieceProducersForHandler(
        string baseName,
        out List<(ArchiveTask Task, string PieceName)> producers) =>
        TrySnapshotGroupPieceProducers(baseName, out producers);

    internal static bool IsByteIdenticalForHandler(string left, string right) =>
        IsByteIdentical(left, right);

    /// <summary>第二大步收卷那一档的措辞（候选 = 这一趟按组定下来的那几份卷）。</summary>
    internal const string HarvestCandidatePrefix = "第二大步收卷准备收的";

    /// <summary>第二大步收卷那一档的结论措辞（与「逐层回收」那一档同一件事、不同入口）。</summary>
    internal const string HarvestBlockerTail =
        "当场收掉这几份就等于把这一组拆开，所以这一趟一个字节都不收"
        + "（删除不可恢复；这一组留到确认完整之后再收）";

    private ExtractionVolumeHarvest? _volumeHarvest;
    private ExtractionVolumeHarvest VolumeHarvest =>
        _volumeHarvest ??= new ExtractionVolumeHarvest(this);

    internal void HarvestSolvedGroupVolumesForRound(string entryPath) =>
        VolumeHarvest.HarvestSolvedGroupVolumesForRound(entryPath);

    internal void HarvestRememberedGroupVolumesForBatch() => VolumeHarvest.HarvestRememberedGroupVolumesForBatch();

    internal void RememberGroupPieceProducer(string baseName, string pieceName, ArchiveTask? owner) =>
        VolumeHarvest.RememberGroupPieceProducer(baseName, pieceName, owner);
}
