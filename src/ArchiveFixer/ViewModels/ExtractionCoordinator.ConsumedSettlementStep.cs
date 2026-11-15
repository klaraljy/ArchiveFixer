using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using System.Collections.Generic;


namespace ArchiveFixer.ViewModels;

/// <summary>
/// 被别单接手的片/组的结算入口（第 10 块，从 Password 大杂烩搬出；同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="ConsumedSettlementHandler"/>
/// （<c>ViewModels/Extraction/Steps/ConsumedSettlementHandler.cs</c>）与两个结算协作类
/// <see cref="ExtractionConsumedPieceSettler"/> / <see cref="ExtractionConsumedGroupSettler"/>；
/// 本文件只保留同签名薄转发与一处最小 internal 访问器；⛔ 判据、日志顺序、账本写入点与所有调用点一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/ConsumedSettlementHandler.cs 与两个 Settler）=====
    private ConsumedSettlementHandler? _consumedSettlementHandler;

    private ConsumedSettlementHandler ConsumedSettlement =>
        _consumedSettlementHandler ??= new ConsumedSettlementHandler(this);

    private ExtractionConsumedPieceSettler? _consumedPieceSettler;
    private ExtractionConsumedPieceSettler ConsumedPieceSettler =>
        _consumedPieceSettler ??= new ExtractionConsumedPieceSettler(this);

    private ExtractionConsumedGroupSettler? _consumedGroupSettler;
    private ExtractionConsumedGroupSettler ConsumedGroupSettler =>
        _consumedGroupSettler ??= new ExtractionConsumedGroupSettler(this);

    /// <summary>薄转发：实现见 <see cref="ConsumedSettlementHandler.AnyTaskRepresentsGroup"/>。</summary>
    private bool AnyTaskRepresentsGroup(string piecePath) =>
        ConsumedSettlement.AnyTaskRepresentsGroup(piecePath);

    /// <summary>薄转发：实现见 <see cref="ConsumedSettlementHandler.IsByteIdentical"/>。</summary>
    private static bool IsByteIdentical(string left, string right) =>
        ConsumedSettlementHandler.IsByteIdentical(left, right);

    /// <summary>薄转发：实现见 <see cref="ConsumedSettlementHandler.LogPassThroughRestCriterionInputs"/>。</summary>
    private void LogPassThroughRestCriterionInputs(
        ArchiveTask producer,
        ArchiveTask? consumer,
        string groupBaseName,
        string restDirectory,
        string why) =>
        ConsumedSettlement.LogPassThroughRestCriterionInputs(
            producer, consumer, groupBaseName, restDirectory, why);

    /// <summary>薄转发：实现见 <see cref="ConsumedSettlementHandler.EnumerateOwnPaths"/>。</summary>
    internal static IEnumerable<string> EnumerateOwnPaths(ArchiveTask task) =>
        ConsumedSettlementHandler.EnumerateOwnPaths(task);

    /// <summary>薄转发：实现见 <see cref="ConsumedSettlementHandler.EnumerateTaskPaths"/>。</summary>
    internal static IEnumerable<string> EnumerateTaskPaths(ArchiveTask task) =>
        ConsumedSettlementHandler.EnumerateTaskPaths(task);

    /// <summary>薄转发：实现见 <see cref="ConsumedSettlementHandler.ResolveCompleteGroupFromDisk"/>。</summary>
    internal static VolumeGroup? ResolveCompleteGroupFromDisk(ArchiveTask task, string? preferDirectory = null) =>
        ConsumedSettlementHandler.ResolveCompleteGroupFromDisk(task, preferDirectory);

    /*
     * 协作类要用、但定义在**别的 partial** 里的 private 成员（不改那个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在 VolumeRepairStep）。
     */
    /// <summary>访问器：给 <see cref="ConsumedSettlementHandler"/> 重新归组（唯一实现 = VolumeRepairStep 的 RebuildVolumeGroup）。</summary>
    internal static VolumeGroup? RebuildVolumeGroupForConsumedSettlement(ArchiveTask task, IEnumerable<string> extraFiles) =>
        RebuildVolumeGroup(task, extraFiles);

    internal void DeleteHarvestTargets(ArchiveTask consumer, List<string> targets) =>
        ConsumedPieceSettler.DeleteHarvestTargets(consumer, targets);

    internal void SettleOneConsumedPieceTask(
        ArchiveTask piece,
        ArchiveTask consumer,
        string groupBaseName,
        string producedPieceName,
        bool moveSources,
        List<(string Level, string Message)> logEntries,
        string? producedPiecePath = null) =>
        ConsumedPieceSettler.SettleOneConsumedPieceTask(
            piece, consumer, groupBaseName, producedPieceName, moveSources, logEntries, producedPiecePath);

    internal void SettleConsumedGroupVolumeSources(List<(string Level, string Message)>? logEntries) =>
        ConsumedPieceSettler.SettleConsumedGroupVolumeSources(logEntries);

    internal static string ResolveOwnPiecePath(ArchiveTask task) =>
        ExtractionConsumedPieceSettler.ResolveOwnPiecePath(task);

    internal void SettleSucceededGroupPieceProducers() => ConsumedGroupSettler.SettleSucceededGroupPieceProducers();

    internal void SettleParentOfSucceededVolumeGroupForTests() =>
        ConsumedGroupSettler.SettleParentOfSucceededVolumeGroupForTests();

    internal void SettleParentOfSucceededVolumeGroup() => ConsumedGroupSettler.SettleParentOfSucceededVolumeGroup();

    internal void SettleVerifiedGroupFollowers() => ConsumedGroupSettler.SettleVerifiedGroupFollowers();

    internal bool TryGetGroupConsumerPending(ArchiveTask task, out string consumer) =>
        ConsumedGroupSettler.TryGetGroupConsumerPending(task, out consumer);

    internal bool TryResolveConsumedByAnotherTask(ArchiveTask task, out string consumerName) =>
        ConsumedGroupSettler.TryResolveConsumedByAnotherTask(task, out consumerName);
    internal bool IsBorrowedPieceTask(ArchiveTask task) => ConsumedGroupSettler.IsBorrowedPieceTask(task);
}
