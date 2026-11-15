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
/// 缺失卷槽位补链接（打分分配/字节预算/槽名生成；第 9 块）。
///
/// <para>2026-11-15 起实现整块落在协作类 <see cref="VolumeSlotFillHandler"/>（它持有本类当宿主）；
/// 这一格只留**原方法签名的薄转发** + 两个最小 internal 访问器 —— 卷槽判据、补片顺序、
/// 日志文案与顺序一个字都没改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    private ExtractionSlotScoredAssignment? _slotScoredAssignment;
    private ExtractionSlotScoredAssignment SlotScoredAssignment =>
        _slotScoredAssignment ??= new ExtractionSlotScoredAssignment(this);

    private VolumeSlotFillHandler? _volumeSlotFill;
    private VolumeSlotFillHandler VolumeSlotFill =>
        _volumeSlotFill ??= new VolumeSlotFillHandler(this);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.TryNameSlotLikeExistingPiece"/>。</summary>
    internal static bool TryNameSlotLikeExistingPiece(string existingPiecePath, int slot, out string name) =>
        VolumeSlotFillHandler.TryNameSlotLikeExistingPiece(existingPiecePath, slot, out name);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.ResolveLayerNamingFamily"/>。</summary>
    internal static VolumeNamingFamily? ResolveLayerNamingFamily(string? directory) =>
        VolumeSlotFillHandler.ResolveLayerNamingFamily(directory);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.ResolveMissingSlotName"/>（默认参数照旧）。</summary>
    internal static string ResolveMissingSlotName(
        IReadOnlyDictionary<int, string> present,
        string entryName,
        int slot,
        VolumeNamingFamily? namingFamily = null) =>
        VolumeSlotFillHandler.ResolveMissingSlotName(present, entryName, slot, namingFamily);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.CollectCandidateRoots"/>。</summary>
    internal List<(string Directory, ArchiveTask? Owner, bool RequiresBaseNameRelation)> CollectCandidateRoots(
        IEnumerable<ArchiveTask?> tasks,
        string entryLayer) =>
        VolumeSlotFill.CollectCandidateRoots(tasks, entryLayer);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.DetachImpossibleSlots"/>。</summary>
    internal static List<string> DetachImpossibleSlots(SortedDictionary<int, string> present, int declaredCount) =>
        VolumeSlotFillHandler.DetachImpossibleSlots(present, declaredCount);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.TryLinkMissingPiecesByByteBudget"/>（默认参数照旧）。</summary>
    internal void TryLinkMissingPiecesByByteBudget(ArchiveTask task, string entryLayer, string? memberPath, string? cardEntryPath = null) =>
        VolumeSlotFill.TryLinkMissingPiecesByByteBudget(task, entryLayer, memberPath, cardEntryPath);

    /// <summary>薄转发：实现见 <see cref="VolumeSlotFillHandler.ResolveCanonicalGroupFromCardSlots"/>。</summary>
    internal static VolumeGroup? ResolveCanonicalGroupFromCardSlots(
        string entryLayer,
        ArchiveFixer.Detection.CardVolumeGroupReading reading) =>
        VolumeSlotFillHandler.ResolveCanonicalGroupFromCardSlots(entryLayer, reading);

    /// <summary>打分分配档（自述字节数给不出证据时的第二版尝试）：薄转发到 <see cref="ExtractionSlotScoredAssignment"/>。</summary>
    internal void TryLinkMissingPiecesByScoredAssignment(
        ArchiveTask task,
        string entryLayer,
        string firstVolume,
        FileNatureCard entryCard,
        SortedDictionary<int, string> present,
        long largest) =>
        SlotScoredAssignment.TryLinkMissingPiecesByScoredAssignment(
            task, entryLayer, firstVolume, entryCard, present, largest);

    /*
     * 协作类要用、但定义在**别的 partial** 里的两个 private 成员（不改那两个文件）：
     * 这里只加**最小 internal 访问器**，⛔ 不复制判据、⛔ 不用反射（唯一实现仍在原处）。
     */
    /// <summary>访问器：给 <see cref="VolumeSlotFillHandler"/> 取"这一单的观察片路径"（唯一实现 = 本类）。</summary>
    internal static string ResolveObservedPiecePathForSlotFill(ArchiveTask? task, string? memberPath) =>
        ResolveObservedPiecePath(task, memberPath);

    /// <summary>访问器：给 <see cref="VolumeSlotFillHandler"/> 记"谁吐过这一组的片"（唯一实现 = 本类）。</summary>
    internal void RememberPieceSupplyingTaskForSlotFill(string baseName, string piecePath) =>
        RememberPieceSupplyingTask(baseName, piecePath);

}
