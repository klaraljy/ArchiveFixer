using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using System.Collections.Generic;
using System.Threading;

namespace ArchiveFixer.Services;

/// <summary>
/// 定稿这一步的公开入口（**薄转发，不含判据**）：
/// 落点判定在 <see cref="PlacementLayoutPlanner"/>、搬动在 <see cref="PlacementMoveExecutor"/>、
/// 落位结算在 <see cref="PlacementCommitSettlement"/>、摘要与收尾日志在 <see cref="PlacementCommitLog"/>。
///
/// <para>为什么保留这一层：调用方（<c>ExtractionCoordinator</c> 的协议层与测试）认的是这几个签名
/// —— 原样留在原处，拆类就只是"实现搬家"，一个调用点、一条判据都不用动。</para>
/// </summary>
internal sealed class PlacementStep
{
    private readonly PlacementLayoutPlanner _planner;
    private readonly PlacementCommitSettlement _settlement;
    private readonly PlacementMoveExecutor _executor;

    internal PlacementStep(IPlacementStepHost host)
    {
        _planner = new PlacementLayoutPlanner(host);
        _settlement = new PlacementCommitSettlement(host.Paths);
        _executor = new PlacementMoveExecutor(host, _planner, _settlement);
    }

    internal FinalizePlan PlanFinalLayoutForTask(
        ArchiveTask task,
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        RecursionResult? recursion = null,
        bool stageFilesAreProducedContent = false) =>
        _planner.PlanFinalLayoutForTask(
            task,
            stageDirectory,
            destinationDirectory,
            sharedOutputRoot,
            terminalLayout,
            recursion,
            stageFilesAreProducedContent);

    internal static FinalizePlan PlanFinalLayout(
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        string? archiveBaseName,
        TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
        bool suppressPackageFolderLayer = false,
        string? innermostPackageBaseName = null,
        RecursionResult? engineOutput = null,
        ContentKeepRules? contentKeepRules = null,
        IReadOnlyCollection<string>? assembledVolumeBaseNames = null,
        bool allStageFilesAreContent = false) =>
        PlacementLayoutPlanner.PlanFinalLayout(
            stageDirectory,
            destinationDirectory,
            sharedOutputRoot,
            archiveBaseName,
            terminalLayout,
            suppressPackageFolderLayer,
            innermostPackageBaseName,
            engineOutput,
            contentKeepRules,
            assembledVolumeBaseNames,
            allStageFilesAreContent);

    internal string ResolveContentLayerDirectory(string destinationDirectory, string processArtifactDirectory) =>
        _settlement.ResolveContentLayerDirectory(destinationDirectory, processArtifactDirectory);

    internal static string ResolveContentDirectoryAfterCollect(StageCommitResult commit, CollectResult? collected) =>
        PlacementCommitSettlement.ResolveContentDirectoryAfterCollect(commit, collected);

    internal ConflictPrecheck PrecheckFinalLayoutConflicts(
        ArchiveTask task,
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        RecursionResult? recursion = null,
        bool stageFilesAreProducedContent = false) =>
        _planner.PrecheckFinalLayoutConflicts(
            task,
            stageDirectory,
            destinationDirectory,
            sharedOutputRoot,
            terminalLayout,
            recursion,
            stageFilesAreProducedContent);

    internal StageCommitResult ExecuteFinalLayout(
        ArchiveTask task,
        string stageDirectory,
        string destinationDirectory,
        bool sharedOutputRoot,
        TerminalLayoutMode terminalLayout,
        string conflictAction,
        ConflictDecision? conflictDecision,
        CancellationToken cancellationToken,
        bool verificationPassed = true,
        RecursionResult? recursion = null,
        bool stageFilesAreProducedContent = false) =>
        _executor.ExecuteFinalLayout(
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
}
