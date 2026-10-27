using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System.Collections.Generic;
using System.Threading;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 落点/定稿这一步的接线（第 20 轮③：实现已搬进 <c>Services/PlacementStep.cs</c> 独立类；
    /// 协调器只留一行转发，静态入口保留给用例直接钉）。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IPlacementStepHost
    {
        private readonly PlacementStep _placementStep;

        internal FinalLayoutPlan PlanFinalLayoutForTask(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null,
            bool stageFilesAreProducedContent = false) =>
            _placementStep.PlanFinalLayoutForTask(
                task,
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                terminalLayout,
                specialExtraction,
                recursion,
                stageFilesAreProducedContent);

        internal static FinalLayoutPlan PlanFinalLayout(
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            string? archiveBaseName,
            TerminalLayoutMode terminalLayout = TerminalLayoutMode.KeepLastFolder,
            SpecialExtractionPlan? specialExtraction = null,
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
                specialExtraction,
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
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null,
            bool stageFilesAreProducedContent = false) =>
            _placementStep.PrecheckFinalLayoutConflicts(
                task,
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                terminalLayout,
                specialExtraction,
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
            SpecialExtractionPlan? specialExtraction = null,
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
                specialExtraction,
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