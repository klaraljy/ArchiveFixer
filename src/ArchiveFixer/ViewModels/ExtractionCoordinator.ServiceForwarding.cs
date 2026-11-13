using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 搬进 Services 独立类之后留下的接线（UiThread / ProgressHeartbeat / Conflict / Workspace /
    /// PartialPublish / Placement）：这里只有一行转发与 host 接口实现，实现都在 <c>Services/*Step.cs</c>。
    /// ⛔ 判据/文案/终态不在这里。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IProgressHeartbeatHost, IConflictStepHost, IWorkspaceStepHost, IPartialPublishStepHost, IPlacementStepHost
    {

        // ===== 来自 ExtractionCoordinator.UiThreadStep.cs（实现见 Services 对应 Step 类）=====
        private readonly UiThreadStep _uiThread;

        private Task<bool> ShowConfirmOnUiThreadAsync(string message) => _uiThread.ShowConfirmAsync(message);

        private Task ShowWarningOnUiThreadAsync(string message) => _uiThread.ShowWarningAsync(message);

        // ===== 来自 ExtractionCoordinator.ProgressHeartbeatStep.cs（实现见 Services 对应 Step 类）=====
        private readonly ProgressHeartbeatStep _progressHeartbeat;

        /// <summary>特定解压也复用同一个进度接收端：任务状态、10% 档位日志、30 秒心跳同源。</summary>
        internal IProgress<ArchiveProgress> CreateProgressSink(ArchiveTask task) =>
            _progressHeartbeat.CreateSink(task);

        /// <summary>心跳：绕开"成功就丢"缓冲直接写出去 —— 它要回答的正是"此刻还在动吗"。</summary>
        private void AppendHeartbeat(string message) => _vm.AppendLog("INFO", message);

        void IProgressHeartbeatHost.AppendLog(string level, string message) => AppendLog(level, message);

        void IProgressHeartbeatHost.AppendHeartbeat(string message) => AppendHeartbeat(message);

        // ===== 来自 ExtractionCoordinator.ConflictStep.cs（实现见 Services 对应 Step 类）=====
        private readonly ConflictStep _conflictStep;

        /// <summary>同名冲突处理档（唯一来源；空 / 非法回落 AutoRename）。</summary>
        private string ConflictActionValue => _conflictStep.ConflictActionValue;

        private void ResetBatchConflictState() => _conflictStep.ResetBatchConflictState();

        private void ResetTaskConflictState() => _conflictStep.ResetTaskConflictState();

        private ConflictDecision? GetEffectiveConflictDecision() => _conflictStep.GetEffectiveConflictDecision();

        private bool IsConflictPromptUnavailable => _conflictStep.IsConflictPromptUnavailable;

        private Task<ConflictDecision?> AskConflictAsync(
            ArchiveTask task,
            string message,
            string detail,
            CancellationToken cancellationToken) =>
            _conflictStep.AskConflictAsync(task, message, detail, cancellationToken);

        internal Task<ConflictResolution> ResolveConflictAsync(
            ArchiveTask task,
            ConflictTargetKind kind,
            string targetPath,
            string message,
            string detail,
            CancellationToken cancellationToken) =>
            _conflictStep.ResolveConflictAsync(task, kind, targetPath, message, detail, cancellationToken);

        private static string BuildConflictDetail(ConflictPrecheck precheck) =>
            ConflictStep.BuildConflictDetail(precheck);

        /// <summary>续解落点里是不是已经有"以本层包名命名的那一层"（唯一判据；用例直接钉住它）。</summary>
        internal static bool IsContinuationLayerAlreadyInPath(string? parentDirectory, string? archiveBaseName) =>
            ConflictStep.IsContinuationLayerAlreadyInPath(parentDirectory, archiveBaseName);

        string IConflictStepHost.ConflictAction => Settings.ConflictAction;

        DialogService IConflictStepHost.Dialogs => _dialogService;

        PathService IConflictStepHost.Paths => _pathService;

        void IConflictStepHost.AppendLog(string level, string message) => AppendLog(level, message);

        void IConflictStepHost.StopAfterCurrent() => StopAfterCurrent();

        void IConflictStepHost.CancelCurrentTask() => CancelCurrentTask();

        // ===== 来自 ExtractionCoordinator.WorkspaceStep.cs（实现见 Services 对应 Step 类）=====
        private readonly WorkspaceStep _workspaceStep;

        private WorkspaceRootResolution ApplyBatchWorkspaceRoot(IReadOnlyList<ArchiveTask> tasks) =>
            _workspaceStep.ApplyBatchWorkspaceRoot(tasks);

        private void RemoveEmptyWorkspaceShells() => _workspaceStep.RemoveEmptyWorkspaceShells();

        private void RemoveEmptyTargetShellIfWeCreatedIt() => _workspaceStep.RemoveEmptyTargetShellIfWeCreatedIt();

        /// <summary>新的一趟开始：清掉上一趟的工作区跟踪（一键处理一次 / 手动一批各算一趟）。</summary>
        internal void BeginWorkspaceRun() => _workspaceStep.ResetRunTracking();

        /// <summary>链尾：把这一趟用过的每一个工作区根都收一遍空壳（判据仍是"根下无文件才删"）。</summary>
        internal void CleanupRunWorkspaceShells() => _workspaceStep.CleanupRunWorkspaceShells();

        private void CleanupTaskWorkspaceDirectory(ArchiveTask task, string stageDirectory) =>
            _workspaceStep.CleanupTaskWorkspaceDirectory(task, stageDirectory);

        internal void CleanupFailedTaskWorkspace(ArchiveTask task, bool keepWorkspace = false) =>
            _workspaceStep.CleanupFailedTaskWorkspace(task, keepWorkspace);

        internal bool HasProductEntriesOutsideWorkspace(string directory) =>
            _workspaceStep.HasProductEntriesOutsideWorkspace(directory);

        /// <summary>单测注入：目标目录 → 假盘根（默认 = <see cref="System.IO.Path.GetPathRoot(string)"/>）。</summary>
        internal Func<string, string>? WorkspaceDriveOverride
        {
            get => _workspaceStep.WorkspaceDriveOverride;
            set => _workspaceStep.WorkspaceDriveOverride = value;
        }

        /// <summary>本次运行每个任务真正用的工作区目录（键 = 任务对象）。</summary>
        internal ConcurrentDictionary<ArchiveTask, string> TaskWorkspaceDirectories =>
            _workspaceStep.TaskWorkspaceDirectories;

        /// <summary>本批我们自己建出来的那个目标目录（空串 = 这一批没建过）。</summary>
        internal string BatchCreatedTargetDirectory
        {
            get => _workspaceStep.BatchCreatedTargetDirectory;
            set => _workspaceStep.BatchCreatedTargetDirectory = value;
        }

        PathService IWorkspaceStepHost.PathService => _pathService;

        AppSettings? IWorkspaceStepHost.Settings => Settings;

        OneClickRunOptions? IWorkspaceStepHost.RunOptions => RunOptions;

        ExtractOptions IWorkspaceStepHost.BuildExtractOptions(bool tryExtractUnknownFormat) =>
            BuildExtractOptions(tryExtractUnknownFormat);

        bool IWorkspaceStepHost.IsUnresolvedVolumeGroupMember(ArchiveTask task) =>
            IsUnresolvedVolumeGroupMember(task);

        Func<string, bool> IWorkspaceStepHost.IsOurWorkspaceSubdirectory => IsOurWorkspaceSubdirectory;

        void IWorkspaceStepHost.AppendLog(string level, string message) => AppendLog(level, message);

        RecursiveExtractor? IWorkspaceStepHost.TakeTaskRecursiveExtractor(ArchiveTask task) =>
            _taskRecursiveExtractors.TryRemove(task, out RecursiveExtractor? extractor) ? extractor : null;

        // ===== 来自 ExtractionCoordinator.PartialPublishStep.cs（实现见 Services 对应 Step 类）=====
        private readonly PartialPublishStep _partialPublishStep;

        /// <summary>本批已登记的"部分完成"候选（收尾时先读它再决定终态）。</summary>
        internal ConcurrentDictionary<ArchiveTask, PartialPublishStep.PartialPublishCandidate> PartialPublishCandidates =>
            _partialPublishStep.Candidates;

        private static bool IsDamagedArchive(string? detectedErrorType) =>
            PartialPublishStep.IsDamagedArchive(detectedErrorType);

        private void RecordPartialPublishCandidate(
            ArchiveTask task,
            string stagingRoot,
            string archivePath,
            string password,
            IReadOnlyList<(string Path, long Size)>? manifestEntries,
            IReadOnlyList<string>? engineFailedEntries,
            int engineReportedErrorCount,
            bool engineReportedFailure,
            string? detectedErrorType = null,
            bool chainProducedContent = false) =>
            _partialPublishStep.RecordPartialPublishCandidate(
                task,
                stagingRoot,
                archivePath,
                password,
                manifestEntries,
                engineFailedEntries,
                engineReportedErrorCount,
                engineReportedFailure,
                detectedErrorType,
                chainProducedContent);

        private void RecordRecursivePartialPublishCandidate(ArchiveTask task, RecursionResult? result) =>
            _partialPublishStep.RecordRecursivePartialPublishCandidate(task, result);

        private void PreserveStoppedLayerProcessArtifact(ArchiveTask task, RecursionResult? result) =>
            _partialPublishStep.PreserveStoppedLayerProcessArtifact(task, result);

        private bool PreserveCurrentProcessArtifact(ArchiveTask task, string? artifactPath) =>
            _partialPublishStep.PreserveCurrentProcessArtifact(task, artifactPath);

        private void PreserveCurrentProcessArtifacts(ArchiveTask task, IReadOnlyList<string>? artifactPaths, string? entryPath) =>
            _partialPublishStep.PreserveCurrentProcessArtifacts(task, artifactPaths, entryPath);

        internal Task<bool> TryPublishPartialProductsAsync(ArchiveTask task, bool cancelled) =>
            _partialPublishStep.TryPublishPartialProductsAsync(task, cancelled);

        internal bool PartialPublishActive => _partialPublishStep.PartialPublishActive;

        AppSettings? IPartialPublishStepHost.Settings => Settings;

        bool IPartialPublishStepHost.SpaceTightThisBatch => _spaceTightPolicy.Enabled;

        SpaceTightPolicy IPartialPublishStepHost.SpaceTightPolicy => _spaceTightPolicy;

        IArchiveEngine IPartialPublishStepHost.Engine => _archiveEngine;

        void IPartialPublishStepHost.AppendLog(string level, string message) => AppendLog(level, message);

        string IPartialPublishStepHost.RestHandlingThisBatch => RestHandlingThisBatch;

        bool IPartialPublishStepHost.IsInsideRecursionWorkspace(string? path) => IsInsideRecursionWorkspace(path);

        void IPartialPublishStepHost.PurgeRestItemsExceptChainRootSource(ArchiveTask task) =>
            PurgeRestItemsExceptChainRootSource(task);

        // ===== 来自 ExtractionCoordinator.PlacementStep.cs（实现见 Services 对应 Step 类）=====
        private readonly PlacementStep _placementStep;

        internal FinalizePlan PlanFinalLayoutForTask(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            RecursionResult? recursion = null,
            bool stageFilesAreProducedContent = false) =>
            _placementStep.PlanFinalLayoutForTask(
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
            IReadOnlyCollection<string>? assembledVolumeBaseNames = null) =>
            PlacementStep.PlanFinalLayout(
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                archiveBaseName,
                terminalLayout,
                suppressPackageFolderLayer,
                innermostPackageBaseName,
                engineOutput,
                contentKeepRules,
                assembledVolumeBaseNames);

        /// <summary>
        /// 续解任务的落点目录（判据唯一出口在 <see cref="OneClickCoordinator"/>，这里只转一行）。
        /// ⚠ 它不随 PlacementStep 搬走：那是个纯静态转发，留在协议层给其余物那一档调。
        /// </summary>
        private static string ResolveContinuationOutputDirectory(ArchiveTask task) =>
            OneClickCoordinator.ResolveContinuationOutputDirectory(task);

        private string ResolveContentLayerDirectory(string destinationDirectory, string processArtifactDirectory) =>
            _placementStep.ResolveContentLayerDirectory(destinationDirectory, processArtifactDirectory);

        private static string ResolveContentDirectoryAfterCollect(StageCommitResult commit, CollectResult? collected) =>
            PlacementStep.ResolveContentDirectoryAfterCollect(commit, collected);

        private ConflictPrecheck PrecheckFinalLayoutConflicts(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            RecursionResult? recursion = null,
            bool stageFilesAreProducedContent = false) =>
            _placementStep.PrecheckFinalLayoutConflicts(
                task,
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                terminalLayout,
                recursion,
                stageFilesAreProducedContent);

        private StageCommitResult ExecuteFinalLayout(
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
            _placementStep.ExecuteFinalLayout(
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

        AppSettings IPlacementStepHost.Settings => Settings;

        PathService IPlacementStepHost.Paths => _pathService;

        bool IPlacementStepHost.ExtractIntoSourceFolderThisRun => _extractIntoSourceFolderThisRun;

        bool IPlacementStepHost.ManualSingleLayerThisRun => _manualSingleLayerThisRun;

        bool IPlacementStepHost.MoveCarvedArtifactIntoProcessDirectory => MoveCarvedArtifactIntoProcessDirectory;

        bool IPlacementStepHost.TryResolveGroupLayer(string baseName, out string layer)
        {
            if (_groupLayerByBaseName.TryGetValue(baseName, out string? remembered))
            {
                layer = remembered;
                return true;
            }

            layer = string.Empty;
            return false;
        }

        IReadOnlyList<string> IPlacementStepHost.SnapshotGroupPieceProducerBaseNames() =>
            SnapshotGroupPieceProducerBaseNames();

        bool IPlacementStepHost.IsOwnLayerOccupiedByPackageFile(string directory, string? ownLayerName, string? innerPackagePath) =>
            OneClickCoordinator.IsOwnLayerOccupiedByPackageFile(directory, ownLayerName, innerPackagePath);

        string IPlacementStepHost.BuildEmbeddedArchivePath(ArchiveTask task) => BuildEmbeddedArchivePath(task);

    }
}

