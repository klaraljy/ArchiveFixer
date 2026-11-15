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
/// 缺卷不在批首判死：跑完再补判一次 + 链尾收口（第 10 块，从 Password 大杂烩搬出；同一 partial 类）。
///
/// <para>实现已整块搬进 <see cref="DeferredVolumeDeficitHandler"/>
/// （<c>ViewModels/Extraction/Steps/DeferredVolumeDeficitHandler.cs</c>），本文件只保留同名薄转发、
/// 既有两处试开/补判转发，以及一处最小 internal 访问器；⛔ 判据、日志顺序、事实位读写点一个字不改。</para>
/// </summary>
internal sealed partial class ExtractionCoordinator
{
    // ===== 搬出后的入口薄转发（实现见 Extraction/Steps/DeferredVolumeDeficitHandler.cs）=====
    private DeferredVolumeDeficitHandler? _deferredVolumeDeficitHandler;

    private DeferredVolumeDeficitHandler DeferredVolumeDeficit =>
        _deferredVolumeDeficitHandler ??= new DeferredVolumeDeficitHandler(this);

    /// <summary>薄转发：实现见 <see cref="DeferredVolumeDeficitHandler.FinalizeDeferredVolumeDeficitsAsync"/>。</summary>
    internal Task<IReadOnlyList<ArchiveTask>> FinalizeDeferredVolumeDeficitsAsync(
        CancellationToken cancellationToken = default) =>
        DeferredVolumeDeficit.FinalizeDeferredVolumeDeficitsAsync(cancellationToken);

    /// <summary>薄转发：实现见 <see cref="DeferredVolumeDeficitHandler.IsDeferredVolumeDeficit"/>。</summary>
    internal bool IsDeferredVolumeDeficit(ArchiveTask? task) =>
        DeferredVolumeDeficit.IsDeferredVolumeDeficit(task);

    /// <summary>薄转发：实现见 <see cref="DeferredVolumeDeficitHandler.MarkStillMissingAtFinalPass"/>。</summary>
    internal void MarkStillMissingAtFinalPass(ArchiveTask task, string missing) =>
        DeferredVolumeDeficit.MarkStillMissingAtFinalPass(task, missing);

    /// <summary>薄转发：实现见 <see cref="DeferredVolumeDeficitHandler.TrySettleAsVolumeGroupFollower"/>。</summary>
    internal bool TrySettleAsVolumeGroupFollower(ArchiveTask task) =>
        DeferredVolumeDeficit.TrySettleAsVolumeGroupFollower(task);

    /// <summary>薄转发：实现见 <see cref="DeferredVolumeDeficitHandler.ResolveEntryLayerFallbacks"/>。</summary>
    internal string ResolveEntryLayerFallbacks(ArchiveTask task, string entryLayer) =>
        DeferredVolumeDeficit.ResolveEntryLayerFallbacks(task, entryLayer);

    // ===== 搬出去的那一块要用的私有成员：只在这里加最小 internal 访问器（⛔ 判据一个字不改）=====

    /// <summary>访问器：给 <see cref="DeferredVolumeDeficitHandler"/> 补一份"整批跑完之后"的行快照。</summary>
    internal void AppendTaskRowSnapshotForDeferredVolumeDeficit() => LogCapture.AppendTaskRowSnapshot();

    // ===== 试开那一段的结论（把 9 个被改写的局部量一次交回调用方）=====
    private ExtractionDeferredVolumeRechecker? _deferredVolumeRechecker;
    private ExtractionDeferredVolumeRechecker DeferredVolumeRechecker =>
        _deferredVolumeRechecker ??= new ExtractionDeferredVolumeRechecker(this);

    internal Task<List<ArchiveTask>> RecheckDeferredVolumeDeficitsAsync(bool finalPass, CancellationToken cancellationToken) =>
        DeferredVolumeRechecker.RecheckDeferredVolumeDeficitsAsync(finalPass, cancellationToken);

    private ExtractionOrderTrial? _orderTrial;
    private ExtractionOrderTrial OrderTrial => _orderTrial ??= new ExtractionOrderTrial(this);

    internal Task<OrderTrialOutcome> RunOrderTrialAsync(
        ArchiveTask task,
        string entryLayer,
        VolumeGatherPlan gatherPlan,
        List<string> gatherCandidates,
        string gatherReference,
        CardVolumeGroupReading cardReading,
        string groupResolutionLayer,
        string taskOutputPathBeforeRecheck,
        int gatherScannedInEntryLayer,
        CancellationToken cancellationToken) =>
        OrderTrial.RunOrderTrialAsync(
            task, entryLayer, gatherPlan, gatherCandidates, gatherReference, cardReading,
            groupResolutionLayer, taskOutputPathBeforeRecheck, gatherScannedInEntryLayer, cancellationToken);
}
