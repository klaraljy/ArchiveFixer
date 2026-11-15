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

internal sealed partial class ExtractionCoordinator
{
    /// <summary>本批清理账本；只记事实，链尾规划读取。</summary>
    internal readonly CleanupLedger _cleanupLedger = new();
    /// <summary>
    /// 收尾重活本体（同签名薄转发）—— 唯一实现见 <see cref="SourceHandler.RunPostProcessWork"/>：
    /// 顺序、判据、日志与搬出前逐字一致，线程规则 / 取消规则 / 例外见那边的 XML 注释。
    /// </summary>
    private PostProcessWorkResult RunPostProcessWork(
        ArchiveTask task,
        string stageDirectory,
        ArchiveListResult expected,
        bool collectResults,
        string collectTargetDirectory,
        SourceHandlingMode sourceHandling,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        bool oneClickRun,
        string conflictAction,
        ConflictDecision? conflictDecision,
        CancellationToken cancellationToken,
        RecursionResult? recursion = null,
        string expectedRootPath = "") =>
        new SourceHandler(this).RunPostProcessWork(
            task,
            stageDirectory,
            expected,
            collectResults,
            collectTargetDirectory,
            sourceHandling,
            sharedOutputRoot,
            terminalLayout,
            oneClickRun,
            conflictAction,
            conflictDecision,
            cancellationToken,
            recursion,
            expectedRootPath);

    // ===== 供 SourceHandler 使用的访问器 =====

    /// <summary>
    /// 定稿（暂存区 → 最终目录）—— 只给 <see cref="SourceHandler"/> 用的一行访问器；
    /// 实现仍只有 <c>ServiceForwarding</c> 那一条出口（⛔ 不复制判据、不另开路径）。
    /// </summary>
    internal StageCommitResult ExecuteFinalLayoutForSourceHandler(
        ArchiveTask task,
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        string conflictAction,
        ConflictDecision? conflictDecision,
        CancellationToken cancellationToken,
        bool verificationPassed,
        RecursionResult? recursion,
        bool stageFilesAreProducedContent) =>
        ExecuteFinalLayout(
            task,
            stageDirectory,
            destinationDirectory,
            sharedOutputRoot,
            terminalLayout,
            conflictAction,
            conflictDecision,
            cancellationToken,
            verificationPassed,
            recursion,
            stageFilesAreProducedContent);

    /// <summary>
    /// 这一份是不是落在**递归工作区**里 —— 同签名薄转发（判据唯一实现与设计说明见
    /// <see cref="WorkspacePathRules.IsInsideRecursionWorkspace"/>）；调用方 =
    /// <see cref="ExtractionSourcePurge"/> 与 <see cref="IPartialPublishStepHost"/> 那一档。
    /// ⛔ 判不出 / 拿不到路径 ⇒ false ⇒ 上层什么都不做（删除不可恢复）。
    /// </summary>
    internal static bool IsInsideRecursionWorkspace(string? path) =>
        WorkspacePathRules.IsInsideRecursionWorkspace(path);

    /// <summary>
    /// 删源包的执行体（<see cref="SourceCleanupService"/> 的实例持有者）。
    ///
    /// <para>可注入只为一件事：单测要能断言"该不删时一次都没被调用"（记账式假文件系统）。
    /// 产品代码永远走真实的 <see cref="FileSystemSourceDeleteFileSystem"/>。</para>
    /// </summary>
    internal ISourceDeleteFileSystem SourcePackageDeleteFileSystem { get; set; } =
        FileSystemSourceDeleteFileSystem.Instance;

    /// <summary>
    /// 其余物删除的**执行体**（可注入，只为一件事：单测要能断言"回收站档到底调了几次、
    /// 有没有碰真回收站"）。与 <see cref="SourcePackageDeleteFileSystem"/> 同一条理由、同一个形态。
    ///
    /// <para>⛔ 产品代码永远走默认（<c>RestItemPurger</c> 自己 new 出来的 <c>ShellDeleteExecutor</c>）；
    /// 换掉它**只换执行体**，<c>RestItemPurger</c> 的六道门槛与本批档位一个字都不改。
    /// 为什么需要它：套件里绝不允许往**用户的系统回收站**里塞东西
    /// （见 <c>RecycleBinServiceTests</c> 顶上那三条自我约束）。</para>
    /// </summary>
    internal IDeleteExecutor? RestDeleteExecutor { get; set; }

    internal SourceCleanupService SourcePackageCleanup => new(SourcePackageDeleteFileSystem);

    /// <summary>
    /// **链内每一层"被用掉的那几个输入文件"**（递归钩子每层报上来的 `GroupFiles`）——
    /// 只记账、不动盘（用户 2026-10-12 口径：搬不能挂在"逐层"那一刻，见
    /// <see cref="CollectChainInnerPackagesIntoRestAsync"/> 末尾那一段"链尾集中"）。
    /// 写入点唯一 = <c>OnRecursiveLayerReclaim</c>（<see cref="RecursiveLayerReclaimHandler"/>）；
    /// 清空点 = 批首 <c>PrepareRestHandlingForBatch</c>。
    /// </summary>
    private readonly List<string> _chainInnerLayerInputs = new();

    /// <summary>本批（或本单）生效的「源包怎么处理」——RunOptions 优先，否则读设置。</summary>
    internal SourceHandlingMode EffectiveSourceHandling
        => RunOptions?.SourceHandling ?? AppSettings.ParseSourceHandling(Settings.SourceHandling);

    // ===== 搬出后的入口薄转发（实现见独立类）=====
    private ExtractionSourcePurge? _sourcePurge;
    private ExtractionSourcePurge SourcePurge => _sourcePurge ??= new ExtractionSourcePurge(this);

    internal string? PurgeLayerSourcePackage(
        ArchiveTask task,
        OutputVerificationResult? verification,
        StageCommitResult? commit,
        List<(string Level, string Message)> logEntries,
        LayerPurgeTrigger trigger,
        ResultCompletenessVerdict? reclaimVerdict = null,
        bool reclaimRootSource = true,
        string? reclaimLabel = null,
        string? reclaimWorkspaceDirectory = null,
        SourcePackageDonePolicy donePolicy = SourcePackageDonePolicy.Respect) =>
        SourcePurge.PurgeLayerSourcePackage(
            task, verification, commit, logEntries, trigger, reclaimVerdict, reclaimRootSource,
            reclaimLabel, reclaimWorkspaceDirectory, donePolicy);

    private ExtractionSplitGateScanner? _splitGate;
    private ExtractionSplitGateScanner SplitGate => _splitGate ??= new ExtractionSplitGateScanner(this);

    internal IReadOnlyList<string> EnumerateSplitGateArtifactRoots(
        ArchiveTask task,
        IReadOnlyList<string> candidates) =>
        SplitGate.EnumerateSplitGateArtifactRoots(task, candidates);

    internal static string ResolveSplitGateScanBoundary(ArchiveTask task, AppSettings? settings) =>
        ExtractionSplitGateScanner.ResolveSplitGateScanBoundary(task, settings);

    private ExtractionRootSourceCompletion? _rootCompletion;
    private ExtractionRootSourceCompletion RootCompletion => _rootCompletion ??= new ExtractionRootSourceCompletion(this);

    internal async Task CompleteRootSourcePackagesAfterChainAsync(
        IReadOnlyList<ArchiveTask>? rootTasks,
        IReadOnlyList<ArchiveTask>? chainTasks,
        CancellationToken cancellationToken = default,
        OneClickRunOptions? runOptions = null) =>
        await RootCompletion
            .CompleteRootSourcePackagesAfterChainAsync(rootTasks, chainTasks, cancellationToken, runOptions)
            .ConfigureAwait(true);

    private ExtractionSourceSnapshot? _sourceSnapshot;
    private ExtractionSourceSnapshot SourceSnapshot => _sourceSnapshot ??= new ExtractionSourceSnapshot(this);

    public void EnsureSourceSnapshot(ArchiveTask task) => SourceSnapshot.EnsureSourceSnapshot(task);

    internal bool StopIfSourceChanged(ArchiveTask task) => SourceSnapshot.StopIfSourceChanged(task);

    private Task<string?> CheckRootSourceUnchangedAsync(ArchiveTask task) =>
        SourceSnapshot.CheckRootSourceUnchangedAsync(task);
}

