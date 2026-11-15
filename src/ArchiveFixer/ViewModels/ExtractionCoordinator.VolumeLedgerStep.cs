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
/// 分卷账本记账（过路层其余物/组片消费/缺卷延期/测试钩子；第 9 块，同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="VolumeLedgerHandler"/>
/// （<c>ViewModels/Extraction/Steps/VolumeLedgerHandler.cs</c>），账本状态整块搬进
/// <see cref="VolumeLedgerState"/>（<c>ViewModels/VolumeLedgerState.cs</c>），本文件只保留
/// **同签名薄转发、同名同类型的字段薄转发与三处最小 internal 访问器**；
/// ⛔ 判据、日志顺序、账本写入点与所有调用点一个字不改。</para>
///
/// <para>⚠ 两个搬出去的类都只持有/存放事实：<see cref="VolumeLedgerHandler"/> 只有宿主、没有实例状态
/// （所以它的懒建照旧用 <c>??=</c>）；账本状态由 <see cref="VolumeLedgerState"/> 单一持有
/// （字段名、类型、初始化与比较器逐字沿用搬家前的声明）。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/VolumeLedgerHandler.cs）=====
    private VolumeLedgerHandler? _volumeLedgerHandler;

    private VolumeLedgerHandler VolumeLedger => _volumeLedgerHandler ??= new VolumeLedgerHandler(this);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberPendingPassThroughRest"/>。</summary>
    internal void RememberPendingPassThroughRest(ArchiveTask producer, ArchiveTask consumer, string groupBaseName) =>
        VolumeLedger.RememberPendingPassThroughRest(producer, consumer, groupBaseName);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RetryPendingPassThroughRestPurges"/>。</summary>
    private void RetryPendingPassThroughRestPurges() => VolumeLedger.RetryPendingPassThroughRestPurges();

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberGroupPiecesConsumedBy"/>。</summary>
    internal void RememberGroupPiecesConsumedBy(ArchiveTask consumer) =>
        VolumeLedger.RememberGroupPiecesConsumedBy(consumer);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberOneConsumedVolumeSource"/>。</summary>
    internal void RememberOneConsumedVolumeSource(string? piecePath, string consumerName) =>
        VolumeLedger.RememberOneConsumedVolumeSource(piecePath, consumerName);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberDeferredVolumeDeficit"/>。</summary>
    internal void RememberDeferredVolumeDeficit(ArchiveTask? task, string? stalledMemberPath = null) =>
        VolumeLedger.RememberDeferredVolumeDeficit(task, stalledMemberPath);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RecordDeferredVolumeDeficit"/>。</summary>
    private void RecordDeferredVolumeDeficit(ArchiveTask task, string missingNames) =>
        VolumeLedger.RecordDeferredVolumeDeficit(task, missingNames);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.ResetBatchLedgersForTests"/>。</summary>
    internal void ResetBatchLedgersForTests() => VolumeLedger.ResetBatchLedgersForTests();

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberConsumedVolumeSourceForTests"/>。</summary>
    internal void RememberConsumedVolumeSourceForTests(string piecePath, string consumerName) =>
        VolumeLedger.RememberConsumedVolumeSourceForTests(piecePath, consumerName);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberGroupConsumerForTests"/>。</summary>
    internal void RememberGroupConsumerForTests(string baseName, string consumerName) =>
        VolumeLedger.RememberGroupConsumerForTests(baseName, consumerName);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberGroupPieceProducerForTests"/>。</summary>
    internal void RememberGroupPieceProducerForTests(string baseName, ArchiveTask producer) =>
        VolumeLedger.RememberGroupPieceProducerForTests(baseName, producer);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RememberAdoptedLandedPieceForTests"/>。</summary>
    internal void RememberAdoptedLandedPieceForTests(
        ArchiveTask adoptionOwner,
        string groupBaseName,
        string piecePath) =>
        VolumeLedger.RememberAdoptedLandedPieceForTests(adoptionOwner, groupBaseName, piecePath);

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.RecordDeferredVolumeDeficitForTests"/>。</summary>
    internal void RecordDeferredVolumeDeficitForTests(ArchiveTask task) =>
        VolumeLedger.RecordDeferredVolumeDeficitForTests(task);

    // ================================================================
    // 账本状态：唯一存放点 = VolumeLedgerState（字段与说明整块搬过去）
    // ================================================================

    /*
     * 这些账本原来就是本分片的实例字段，而且同时被**别的 partial** 直接读写、被**协作类**通过宿主访问 ——
     * 所以搬走时字段名、类型、初始化（含 OrdinalIgnoreCase 比较器）**一个字不改**，这里只留同名薄转发：
     * 调用点里的 `_host._volumeDeficitDeferred` 这类写法因此零改动。
     *
     * ⚠ 立刻建、不懒建：状态对象没有构造依赖，而任务线程本来就会并发读写其中几本账
     * （见 `GroupPieceProducersGate` 的说明）；懒建一旦造出第二个实例，账就会分成两半。
     */
    private readonly VolumeLedgerState _volumeLedgerState = new();

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.VolumeRepairAsked"/>。</summary>
    private HashSet<ArchiveTask> _volumeRepairAsked => _volumeLedgerState.VolumeRepairAsked;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.VolumeDeficitDeferred"/>。</summary>
    internal List<ArchiveTask> _volumeDeficitDeferred => _volumeLedgerState.VolumeDeficitDeferred;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.ConfirmedOrderStagingDirectories"/>。</summary>
    internal List<string> _confirmedOrderStagingDirectories => _volumeLedgerState.ConfirmedOrderStagingDirectories;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.KeepSourceSnapshots"/>。</summary>
    private List<(string Original, string Backup)> _keepSourceSnapshots => _volumeLedgerState.KeepSourceSnapshots;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.KeepSourceSnapshotDirectory"/>。</summary>
    private string _keepSourceSnapshotDirectory
    {
        get => _volumeLedgerState.KeepSourceSnapshotDirectory;
        set => _volumeLedgerState.KeepSourceSnapshotDirectory = value;
    }

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.StagedGroupOutputPaths"/>。</summary>
    internal Dictionary<ArchiveTask, string> _stagedGroupOutputPaths => _volumeLedgerState.StagedGroupOutputPaths;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.ByteBudgetLinkedSources"/>。</summary>
    internal List<(ArchiveTask Producer, string GroupBaseName, string SourcePath)> _byteBudgetLinkedSources =>
        _volumeLedgerState.ByteBudgetLinkedSources;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.ScoredSlotAttempts"/>。</summary>
    internal Dictionary<string, int> _scoredSlotAttempts => _volumeLedgerState.ScoredSlotAttempts;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.ScoredSlotLinks"/>。</summary>
    internal Dictionary<string, List<string>> _scoredSlotLinks => _volumeLedgerState.ScoredSlotLinks;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.SuffixRenamedSources"/>。</summary>
    private List<(string Original, string Renamed)> _suffixRenamedSources => _volumeLedgerState.SuffixRenamedSources;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.DeferredVolumeGroupMember"/>。</summary>
    internal Dictionary<ArchiveTask, string> _deferredVolumeGroupMember => _volumeLedgerState.DeferredVolumeGroupMember;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.ConsumedVolumeSources"/>。</summary>
    internal Dictionary<string, string> _consumedVolumeSources => _volumeLedgerState.ConsumedVolumeSources;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.GroupConsumerByName"/>。</summary>
    internal Dictionary<string, string> _groupConsumerByName => _volumeLedgerState.GroupConsumerByName;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.VolumeGroupUnits"/>。</summary>
    internal Dictionary<string, VolumeGroupUnit> _volumeGroupUnits => _volumeLedgerState.VolumeGroupUnits;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.GroupPieceProducers"/>。</summary>
    internal Dictionary<string, List<(ArchiveTask Task, string PieceName)>> _groupPieceProducers =>
        _volumeLedgerState.GroupPieceProducers;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.GroupPieceProducersGate"/>。</summary>
    internal object _groupPieceProducersGate => _volumeLedgerState.GroupPieceProducersGate;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.ProducerSourcesCollected"/>。</summary>
    internal HashSet<ArchiveTask> _producerSourcesCollected => _volumeLedgerState.ProducerSourcesCollected;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.PassThroughRestHandled"/>。</summary>
    internal HashSet<ArchiveTask> _passThroughRestHandled => _volumeLedgerState.PassThroughRestHandled;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.PendingPassThroughRest"/>。</summary>
    private List<(ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName)> _pendingPassThroughRest =>
        _volumeLedgerState.PendingPassThroughRest;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.AdoptedLandedPieces"/>。</summary>
    internal List<(ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot)>
        _adoptedLandedPieces => _volumeLedgerState.AdoptedLandedPieces;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.GroupLayerByBaseName"/>。</summary>
    internal Dictionary<string, string> _groupLayerByBaseName => _volumeLedgerState.GroupLayerByBaseName;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.VolumeStepTasks"/>。</summary>
    private HashSet<ArchiveTask> _volumeStepTasks => _volumeLedgerState.VolumeStepTasks;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.VolumeGroupEntriesThisBatch"/>。</summary>
    internal List<(string Directory, string ReferenceName)> _volumeGroupEntriesThisBatch =>
        _volumeLedgerState.VolumeGroupEntriesThisBatch;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.VolumeDeficitFinalPass"/>。</summary>
    internal HashSet<ArchiveTask> _volumeDeficitFinalPass => _volumeLedgerState.VolumeDeficitFinalPass;

    /// <summary>薄转发：账本状态见 <see cref="VolumeLedgerState.UnrecognizedMaybePiece"/>。</summary>
    internal HashSet<ArchiveTask> _unrecognizedMaybePiece => _volumeLedgerState.UnrecognizedMaybePiece;

    // ===== 三处最小 internal 访问器（给 VolumeLedgerHandler / SourceProtectionHandler 用；唯一的账仍在状态对象）=====

    /// <summary>访问器：给 <see cref="VolumeLedgerHandler"/> 排/重试"批末再删一次"的那份名单。</summary>
    internal List<(ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName)> PendingPassThroughRestForVolumeLedger =>
        _pendingPassThroughRest;

    /// <summary>访问器：给 <see cref="VolumeLedgerHandler"/> 清空本批"第二大步任务位"（唯一写点仍在宿主）。</summary>
    internal HashSet<ArchiveTask> VolumeStepTasksForVolumeLedger => _volumeStepTasks;

    /// <summary>访问器：给 <see cref="VolumeLedgerHandler"/> 清空本批"后缀清洗账"。</summary>
    internal List<(string Original, string Renamed)> SuffixRenamedSourcesForVolumeLedger =>
        _suffixRenamedSources;

    /// <summary>薄转发：实现见 <see cref="VolumeLedgerHandler.GroupPieceProducerCount"/>。</summary>
    internal int GroupPieceProducerCount => VolumeLedger.GroupPieceProducerCount;
}
