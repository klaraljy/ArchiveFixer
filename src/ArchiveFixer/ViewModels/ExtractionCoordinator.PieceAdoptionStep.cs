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
/// 收片采纳与路径红线（接片判据/产出方链/是否可搬/任务表同步；第 9 块，同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="PieceAdoptionHandler"/>
/// （<c>ViewModels/Extraction/Steps/PieceAdoptionHandler.cs</c>），本文件只保留同名同签名的薄转发
/// 与三处最小 internal 访问器；⛔ 判据、日志顺序、删除/搬家路径与所有调用点一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/PieceAdoptionHandler.cs）=====
    private PieceAdoptionHandler? _pieceAdoptionHandler;

    private PieceAdoptionHandler PieceAdoption => _pieceAdoptionHandler ??= new PieceAdoptionHandler(this);

    /// <summary>
    /// 用户源文件路径集合：任务原始路径、当前路径、组的每一卷、链根路径。
    /// 别名清理**只许删这个集合之外的名字**。
    /// </summary>
    internal static HashSet<string> BuildUserSourcePathSet(IEnumerable<ArchiveTask>? tasks) =>
        PieceAdoptionHandler.BuildUserSourcePathSet(tasks);

    internal void RememberUserSourcePaths(IEnumerable<ArchiveTask>? tasks) =>
        PieceAdoption.RememberUserSourcePaths(tasks);

    internal int PurgeRedundantPieceAliases() => PieceAdoption.PurgeRedundantPieceAliases();

    private void CleanupConfirmedOrderStagingDirectories() =>
        PieceAdoption.CleanupConfirmedOrderStagingDirectories();

    private void RememberPieceSupplyingTask(string baseName, string piecePath) =>
        PieceAdoption.RememberPieceSupplyingTask(baseName, piecePath);

    private List<string> SnapshotGroupPieceProducerBaseNames() =>
        PieceAdoption.SnapshotGroupPieceProducerBaseNames();

    private bool TrySnapshotGroupPieceProducers(
        string baseName,
        out List<(ArchiveTask Task, string PieceName)> producers) =>
        PieceAdoption.TrySnapshotGroupPieceProducers(baseName, out producers);

    internal List<List<(ArchiveTask Task, string PieceName)>> SnapshotGroupPieceProducerLists() =>
        PieceAdoption.SnapshotGroupPieceProducerLists();

    internal void ClearGroupPieceProducers() => PieceAdoption.ClearGroupPieceProducers();

    internal void RememberAdoptedLandedPiece(
        string piecePath,
        ArchiveTask? owner,
        string groupBaseName,
        bool renamedInPlace) =>
        PieceAdoption.RememberAdoptedLandedPiece(piecePath, owner, groupBaseName, renamedInPlace);

    internal bool IsAnyTaskSourcePath(string path) => PieceAdoption.IsAnyTaskSourcePath(path);

    private bool IsAnyTaskOriginalPath(string path) => PieceAdoption.IsAnyTaskOriginalPath(path);

    private bool IsAnyTaskOriginalIdentity(string path) => PieceAdoption.IsAnyTaskOriginalIdentity(path);

    internal bool IsUserImportedSourcePath(string path) => PieceAdoption.IsUserImportedSourcePath(path);

    private ArchiveTask? ResolveLandedPieceProducer(string piecePath, ArchiveTask? owner) =>
        PieceAdoption.ResolveLandedPieceProducer(piecePath, owner);

    private static bool IsInsideOwnLandingTree(ArchiveTask? task, string piecePath) =>
        PieceAdoptionHandler.IsInsideOwnLandingTree(task, piecePath);

    private void SyncEveryTaskPathAfterMove(string from, string to) =>
        PieceAdoption.SyncEveryTaskPathAfterMove(from, to);

    private ExtractionPieceHandoverRules? _pieceHandoverRules;
    private ExtractionPieceHandoverRules PieceHandoverRules =>
        _pieceHandoverRules ??= new ExtractionPieceHandoverRules(this);

    internal static string FindUserOwnFileBesideItsDirectory(ArchiveTask task, string startPointPath) =>
        PieceAdoptionHandler.FindUserOwnFileBesideItsDirectory(task, startPointPath);

    internal static bool ShouldMovePieceIntoGroup(
        string piecePath,
        string ownerStageDirectory,
        IEnumerable<string>? liveChainWorkDirectories = null) =>
        PieceAdoptionHandler.ShouldMovePieceIntoGroup(piecePath, ownerStageDirectory, liveChainWorkDirectories);

    internal static bool IsTargetSlotAlreadyThisPiece(string piecePath, string target) =>
        PieceAdoptionHandler.IsTargetSlotAlreadyThisPiece(piecePath, target);

    private string ResolveOwnerChainDirectory(ArchiveTask? owner) => PieceAdoption.ResolveOwnerChainDirectory(owner);

    // ===== 搬出去的那份实现要用的私有成员（只加最小 internal 访问器；⛔ 字段语义一个字不改）=====

    /// <summary>用户源路径集合（只增不减的账；见 <see cref="RememberUserSourcePaths"/>）。</summary>
    internal HashSet<string> UserSourcePathsForHandler => _userSourcePaths;

    /// <summary>接片判据的唯一实例（缓存仍在宿主这一侧，生命周期与搬出前一致）。</summary>
    internal ExtractionPieceHandoverRules PieceHandoverRulesForHandler => PieceHandoverRules;

    // `FindLandedCopyBesideProducerForHandler` 由 VolumeGatherStep 分片提供（同一 private 转发的访问器，
    // ⛔ 这里不再声明第二个同名成员）；搬出去的实现直接走宿主那一个。
}
