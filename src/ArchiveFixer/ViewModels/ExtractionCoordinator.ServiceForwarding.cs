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

using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 搬进 Services 独立类之后留下的接线（UiThread / ProgressHeartbeat / Conflict / Workspace /
    /// PartialPublish / Placement）：这里只有一行转发与 host 接口实现，实现都在 <c>Services/*Step.cs</c>。
    /// ⛔ 判据/文案/终态不在这里。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IProgressHeartbeatHost, IConflictStepHost, IWorkspaceStepHost, IPartialPublishStepHost, IPlacementStepHost, IRestHandlingStepHost, IPasswordCandidatesHost
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


        // ===== 来自 ExtractionCoordinator.RestHandlingStep.cs（其余物处理的接线；实现见 Services/RestHandlingStep.cs）=====


        /// <summary>
        /// 链里的输出校验有没有缺口：**每一个**把这个目录当落点的任务都必须**可证完整**
        /// （L4 唯一出口 <see cref="ResultCompletenessClassifier"/>，2026-09-30 检验等级）。
        ///
        /// <para>
        /// 为什么要看**整条链**而不是只看根任务：用户那个形状里内容物是**续解子任务**产出的，
        /// 根任务那一轮的校验只覆盖了"过程物都搬进去了"。只看根任务，就会出现
        /// "第二层校验没过（比如声明的条目数与落盘不符）却照样把源包搬走"。
        /// </para>
        /// <para>
        /// 为什么是"每一个都必须过"而不是"有一个过就行"：同一个最终目录是链上所有任务共同产出的，
        /// 其中任何一个没通过校验，就没有"内容物已定稿并校验通过"这个事实 ——
        /// 这与"本轮直接搬"那条路径的口径一致（它也要求 L4 = 可证完整）。
        /// </para>
        /// <para>
        /// ⚠ 判据刻意**不是** <c>IsOutputVerified</c>：那个字段在"拿不到清单、只做了非空底线校验"时
        /// 也是 true（老口径下两者长得一模一样），而这一处的下游是**移动 / 删除用户的源包**。
        /// </para>
        /// </summary>
        /// <returns>
        /// null = 这条链在这个目录上校验齐了（且至少有一个任务确实落在这里）；
        /// 非 null = 说给用户听的原因（哪个任务没通过 / 压根没有任务落在这里）。
        /// </returns>
        internal static string? DescribeChainVerificationGap(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks) =>
            RestChainVerificationGate.Describe(destinationDirectory, rootTask, chainTasks);



        /// <summary>
        /// 抠出来的内嵌归档（双面文件尾部那一段）在定稿后是否搬进 <c>其余物</c>。
        ///
        /// 契约 §3.2 把"抠出来的中间 ZIP"列为其余物，本来是**该搬**的 —— 但实测下来不能搬，
        /// 因为它不是"解出来的产物"，而是**解压的输入**（用户那个源文件的一段副本）：
        ///
        /// · 双面文件 <c>user.mp4</c> 的 ZIP 里就是内层分卷 <c>inner.7z.001/.002</c>；
        ///   把它搬进 <c>其余物\user.zip</c> 之后，一键处理的续解扫描（它只看"本轮新出现的归档起点"）
        ///   会把这个 ZIP 也当成一个**新的内层包**，于是又解一遍、又产出 <c>inner.7z.001</c>、
        ///   再多续解一轮 —— 实测就是这个结果（回归测试"双面文件真实现场"从 2 轮变成 3 轮）。
        /// · 而且它往往很大（双面文件能到 760MB 量级），搬进用户目录等于凭空多一份大副本。
        ///
        /// 所以默认 **false**：它留在工作区（<c>&lt;work&gt;\&lt;taskId&gt;\</c>），
        /// 成功且校验通过后随工作区一起清掉，失败/取消时留着供排查。
        /// 如果将来判定表/ResultFinalizer 能把它标注成"输入而非产物"（续解扫描据此跳过），
        /// 把这里改成 true 即可，其余代码不用动。
        /// </summary>
        private const bool MoveCarvedArtifactIntoProcessDirectory = false;





        private readonly RestHandlingStep _restHandlingStep;

        /// <summary>本批的其余物处理档（PrepareRestHandlingForBatch 写，其余步骤读）。</summary>
        internal string RestHandlingThisBatch
        {
            get => _restHandlingStep.RestHandlingThisBatch;
            set => _restHandlingStep.RestHandlingThisBatch = value;
        }

        private Task CollectChainInnerPackagesIntoRestAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken,
            string? planAllowedRoot = null) =>
            _restHandlingStep.CollectChainInnerPackagesIntoRestAsync(
                rootTask, chainTasks, cancellationToken, planAllowedRoot);

        private void CollectChainInnerLayerInputsIntoRest(ArchiveTask rootTask, string restDirectory, string outputRoot) =>
            _restHandlingStep.CollectChainInnerLayerInputsIntoRest(rootTask, restDirectory, outputRoot);

        private void ReportLeftBehindInnerPackages(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks) =>
            _restHandlingStep.ReportLeftBehindInnerPackages(rootTask, chainTasks);

        internal static List<(string From, string To)> PlanChainInnerPackageMoves(
            ArchiveTask? rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            string? restDirectory,
            string? outputRoot,
            ContentKeepRules? keepRules,
            out List<string> warnings) =>
            RestHandlingStep.PlanChainInnerPackageMoves(rootTask, chainTasks, restDirectory, outputRoot, keepRules, out warnings);

        internal static string DescribeThroughput(long writtenBytes, TimeSpan elapsed) =>
            RestHandlingStep.DescribeThroughput(writtenBytes, elapsed);

        private string TryRepairBrokenVolumeChainName(ArchiveTask task, string engineArchivePath) =>
            _restHandlingStep.TryRepairBrokenVolumeChainName(task, engineArchivePath);

        private Task ApplyRestHandlingAfterChainAsync(
            ArchiveTask task,
            IReadOnlyList<ArchiveTask>? chainTasks,
            CancellationToken cancellationToken) =>
            _restHandlingStep.ApplyRestHandlingAfterChainAsync(task, chainTasks, cancellationToken);

        private void AppendCompletenessEvidence(
            ArchiveTask task,
            OutputVerificationResult verification,
            SourceHandlingMode sourceHandling) =>
            _restHandlingStep.AppendCompletenessEvidence(task, verification, sourceHandling);

        private Task<DeferredSourceMoveWork> RunDeferredSourceMoveWorkAsync(
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks,
            SourceHandlingMode sourceHandling,
            CancellationToken cancellationToken) =>
            _restHandlingStep.RunDeferredSourceMoveWorkAsync(rootTask, chainTasks, sourceHandling, cancellationToken);

        internal static IEnumerable<ArchiveTask> EnumerateChainTasks(ArchiveTask rootTask, IReadOnlyList<ArchiveTask>? chainTasks) =>
            RestHandlingStep.EnumerateChainTasks(rootTask, chainTasks);

        internal static string ResolveRestDirectoryAfterCollect(StageCommitResult commit, CollectResult? collected) =>
            RestHandlingStep.ResolveRestDirectoryAfterCollect(commit, collected);

        private void PurgePassThroughRestOfSettledProducer(ArchiveTask producer, ArchiveTask consumer, string groupBaseName) =>
            _restHandlingStep.PurgePassThroughRestOfSettledProducer(producer, consumer, groupBaseName);

        internal static string DescribeRestScope(SourceHandlingMode sourceHandling) =>
            RestHandlingStep.DescribeRestScope(sourceHandling);

        internal static bool CanSweepPassThroughRest(
            ArchiveTask? task,
            IReadOnlyDictionary<string, string>? consumedVolumeSources) =>
            RestHandlingStep.CanSweepPassThroughRest(task, consumedVolumeSources);

        private Task SweepPassThroughRestDirectoriesAsync(ArchiveTask rootTask, CancellationToken cancellationToken) =>
            _restHandlingStep.SweepPassThroughRestDirectoriesAsync(rootTask, cancellationToken);

        internal static bool IsRestInsideRootTree(string? rootDirectory, string? restDirectory) =>
            RestHandlingStep.IsRestInsideRootTree(rootDirectory, restDirectory);

        internal static bool TryMoveIntoRest(
            string from,
            string to,
            HashSet<string> movedTargets,
            out string target,
            out string? failure) =>
            RestHandlingStep.TryMoveIntoRest(from, to, movedTargets, out target, out failure);

        internal Task RunRestHandlingAsync(
            ArchiveTask task,
            ScheduledTaskRuntime runtime,
            bool oneClickRun,
            RestHandlingStep.RestHandlingTrigger trigger = RestHandlingStep.RestHandlingTrigger.TaskEnd) =>
            _restHandlingStep.RunRestHandlingAsync(task, runtime, oneClickRun, trigger);

        internal static string DescribeLayerReclaimBlocker(ArchiveTask task) =>
            RestHandlingStep.DescribeLayerReclaimBlocker(task);

        internal static string DescribeRestPurgedLine(RestPurgeOutcome outcome) =>
            RestHandlingStep.DescribeRestPurgedLine(outcome);

        private void PrepareRestHandlingForBatch() => _restHandlingStep.PrepareRestHandlingForBatch();

        private void LogRestHandlingForBatch() => _restHandlingStep.LogRestHandlingForBatch();

        private void PurgeRestItemsExceptChainRootSource(ArchiveTask task) =>
            _restHandlingStep.PurgeRestItemsExceptChainRootSource(task);

        PathService IRestHandlingStepHost.Paths => _pathService;

        bool IRestHandlingStepHost.SpaceTightThisBatch => _spaceTightPolicy.Enabled;

        bool IRestHandlingStepHost.SpaceTightKeepSourceThisBatch => _spaceTightPolicy.KeepSource;

        bool IRestHandlingStepHost.LayerReclaimThisBatch
        {
            get => _layerReclaimThisBatch;
            set => _layerReclaimThisBatch = value;
        }

        CancellationTokenSource? IRestHandlingStepHost.OperationCts => _operationCts;

        List<string> IRestHandlingStepHost.ChainInnerLayerInputs => _chainInnerLayerInputs;

        HashSet<ArchiveTask> IRestHandlingStepHost.PassThroughRestHandled => _passThroughRestHandled;

        Dictionary<string, List<(ArchiveTask Task, string PieceName)>> IRestHandlingStepHost.GroupPieceProducers => _groupPieceProducers;

        Dictionary<string, string> IRestHandlingStepHost.GroupConsumerByName => _groupConsumerByName;

        Dictionary<string, string> IRestHandlingStepHost.ConsumedVolumeSources => _consumedVolumeSources;

        List<(ArchiveTask Producer, string GroupBaseName, string SourcePath)> IRestHandlingStepHost.ByteBudgetLinkedSources =>
            _byteBudgetLinkedSources;

        AppSettings IRestHandlingStepHost.Settings => Settings;

        OneClickRunOptions? IRestHandlingStepHost.RunOptions => RunOptions;

        ObservableCollection<ArchiveTask> IRestHandlingStepHost.Tasks => Tasks;

        bool IRestHandlingStepHost.IsStopping => IsStopping;

        SourceHandlingMode IRestHandlingStepHost.EffectiveSourceHandling => EffectiveSourceHandling;

        IDeleteExecutor? IRestHandlingStepHost.RestDeleteExecutor => RestDeleteExecutor;

        void IRestHandlingStepHost.AppendLog(string level, string message) => AppendLog(level, message);

        List<string> IRestHandlingStepHost.SnapshotGroupPieceProducerBaseNames() => SnapshotGroupPieceProducerBaseNames();

        bool IRestHandlingStepHost.TrySnapshotGroupPieceProducers(
            string baseName,
            out List<(ArchiveTask Task, string PieceName)> producers) =>
            TrySnapshotGroupPieceProducers(baseName, out producers);
        void IRestHandlingStepHost.CollectConsumedSourcePiecesIntoRest(ArchiveTask consumer) =>
            CollectConsumedSourcePiecesIntoRest(consumer);

        ScheduledTaskRuntime IRestHandlingStepHost.GetOrCreateRuntime(ArchiveTask task) => GetOrCreateRuntime(task);

        string? IRestHandlingStepHost.PurgeLayerSourcePackage(
            ArchiveTask task,
            OutputVerificationResult? verification,
            StageCommitResult? commit,
            List<(string Level, string Message)> logEntries,
            LayerPurgeTrigger trigger,
            ResultCompletenessVerdict? reclaimVerdict,
            bool reclaimRootSource) =>
            PurgeLayerSourcePackage(task, verification, commit, logEntries, trigger, reclaimVerdict, reclaimRootSource);

        string IRestHandlingStepHost.ResolveContinuationOutputDirectory(ArchiveTask task) =>
            ResolveContinuationOutputDirectory(task);

        bool IRestHandlingStepHost.AllRestFilesBelongToGroup(
            ArchiveTask? task,
            string groupBaseName,
            IEnumerable<string>? fileNames) =>
            AllRestFilesBelongToGroup(task, groupBaseName, fileNames);

        int IRestHandlingStepHost.CountRestEntries(string? directory) => LogLineText.CountRestEntries(directory);

        string IRestHandlingStepHost.DescribeShortDestination(string? collectedPath, string? outputPath) =>
            LogLineText.DescribeShortDestination(collectedPath, outputPath);

        string IRestHandlingStepHost.DescribeShortTail(string? path, int segments) => LogLineText.DescribeShortTail(path, segments);

        void IRestHandlingStepHost.LogPassThroughRestCriterionInputs(
            ArchiveTask producer,
            ArchiveTask? consumer,
            string groupBaseName,
            string restDirectory,
            string why) =>
            LogPassThroughRestCriterionInputs(producer, consumer, groupBaseName, restDirectory, why);

        void IRestHandlingStepHost.NoteBatchPurge(long freedBytes, bool permanent) => NoteBatchPurge(freedBytes, permanent);

        ArchiveTask[] IRestHandlingStepHost.SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks) => SnapshotTaskTable(tasks);

        string? IRestHandlingStepHost.DescribeChainVerificationGap(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks) =>
            DescribeChainVerificationGap(destinationDirectory, rootTask, chainTasks);

        // ===== 来自 ExtractionCoordinator.PasswordCandidates.cs（候选/密码失败的接线；状态见 Services/PasswordFailureRegistry，候选实现见 Services/PasswordCandidatesStep）=====

        /// <summary>把"密码没通过"的任务登记到本批（只登记，不弹窗；状态与判据在 PasswordFailureRegistry）。</summary>
        internal void RecordPasswordFailure(ArchiveTask task) => _passwordFailureRegistry.Record(task);


        private void ClearPasswordFailures() => _passwordFailureRegistry.ClearFailures();


        /// <summary>
        /// 批开始时把手动密码清干净：**只对本次运行有效**（不变量 5）。
        ///
        /// 不是"记住上次输的密码"—— 那是持久化，红线。上一批输过什么，这一批重新问。
        /// </summary>
        private void ResetManualBatchPassword() => _passwordFailureRegistry.ResetManualBatchPassword();





        private readonly PasswordCandidatesStep _passwordCandidatesStep;

        internal IReadOnlyList<string> BuildRecursionPasswordCandidates(
            string archivePath,
            IDictionary<string, string>? sourceSink = null) =>
            _passwordCandidatesStep.BuildRecursionPasswordCandidates(archivePath, sourceSink);

        internal IReadOnlyList<string> BuildVolumeProbePasswordCandidates(string archivePath) =>
            _passwordCandidatesStep.BuildVolumeProbePasswordCandidates(archivePath);

        private static void InsertManualPasswordCandidates(
            List<PasswordItem> candidates,
            IReadOnlyList<string>? manualPasswords,
            string remark = "本次运行手动输入（不落盘）") =>
            PasswordCandidatesStep.InsertManualPasswordCandidates(candidates, manualPasswords, remark);

        internal PasswordFallbackTriggerFacts BuildPasswordFallbackFacts(
            bool extractSuccess,
            bool hasWrongPassword,
            bool sawAmbiguousPasswordFailure,
            bool passwordProvenDataFailed,
            bool passwordIrrelevant,
            bool stoppedByStopRequest,
            ArchiveOperationResult? lastResult,
            int attemptedCandidates,
            int maxPasswordAttempts,
            int candidateCount,
            bool archiveEncrypted,
            bool anyCandidateWroteBytes) =>
            _passwordCandidatesStep.BuildPasswordFallbackFacts(
                extractSuccess,
                hasWrongPassword,
                sawAmbiguousPasswordFailure,
                passwordProvenDataFailed,
                passwordIrrelevant,
                stoppedByStopRequest,
                lastResult,
                attemptedCandidates,
                maxPasswordAttempts,
                candidateCount,
                archiveEncrypted,
                anyCandidateWroteBytes);

        internal PasswordCandidatesStep.FallbackCandidateSlice BuildFallbackCandidateSlice(List<PasswordItem> candidates, int maxPasswordAttempts) =>
            _passwordCandidatesStep.BuildFallbackCandidateSlice(candidates, maxPasswordAttempts);

        internal Task<List<PasswordItem>> RankCandidatesByDecryptedPrefixAsync(
            ArchiveTask task,
            string archivePath,
            SevenZipEngine engine,
            string entryPath,
            List<PasswordItem> candidates,
            int maxPasswordAttempts,
            CancellationToken cancellationToken) =>
            _passwordCandidatesStep.RankCandidatesByDecryptedPrefixAsync(
                task,
                archivePath,
                engine,
                entryPath,
                candidates,
                maxPasswordAttempts,
                cancellationToken);

        internal Task<PasswordCandidatesStep.PasswordProbeOutcome> ProbePasswordAsync(
            string archivePath,
            ExtractOptions extractOptions,
            string stageDirectory,
            string entryPath,
            string password,
            ProgressHeartbeatStep.Sink progressSink,
            CancellationToken cancellationToken) =>
            _passwordCandidatesStep.ProbePasswordAsync(
                archivePath,
                extractOptions,
                stageDirectory,
                entryPath,
                password,
                progressSink,
                cancellationToken);

        private List<ArchiveTask> FindTasksWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks) =>
            _passwordCandidatesStep.FindTasksWithoutUsablePassword(tasks);

        private List<ArchiveTask> FindSuspectsWithoutUsablePassword(IReadOnlyList<ArchiveTask> tasks) =>
            _passwordCandidatesStep.FindSuspectsWithoutUsablePassword(tasks);

        private int MaxPasswordAttemptsPerLayer => _passwordCandidatesStep.MaxPasswordAttemptsPerLayer;

        AppSettings IPasswordCandidatesHost.Settings => Settings;

        string IPasswordCandidatesHost.GlobalPassword => GlobalPassword;

        PasswordService IPasswordCandidatesHost.Passwords => _passwordService;

        IArchiveEngine IPasswordCandidatesHost.Engine => _archiveEngine;

        List<string> IPasswordCandidatesHost.ManualBatchPasswords => _passwordFailureRegistry.ManualBatchPasswords;

        bool IPasswordCandidatesHost.IsStopping => IsStopping;

        ProgressHeartbeatStep.Sink IPasswordCandidatesHost.CreateProgressSink(ArchiveTask task) =>
            _progressHeartbeat.CreateSink(task);

        ArchiveRequest IPasswordCandidatesHost.BuildTrackedRequest(
            string archivePath,
            string password,
            string? outputPath,
            ProgressHeartbeatStep.Sink progressSink) =>
            _progressHeartbeat.BuildTrackedRequest(archivePath, password, outputPath, progressSink);

        void IPasswordCandidatesHost.AppendLog(string level, string message) => AppendLog(level, message);
    }
}

