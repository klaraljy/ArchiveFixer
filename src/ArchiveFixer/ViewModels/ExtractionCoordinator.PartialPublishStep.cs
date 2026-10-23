using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 「部分完成」发布这一步的接线（第 21 轮③：实现已搬进 <c>Services/PartialPublishStep.cs</c> 独立类，
    /// 协调器只留一行转发；候选表通过属性暴露给收尾那一处读）。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IPartialPublishStepHost
    {
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

        private Task<bool> TryPublishPartialProductsAsync(ArchiveTask task, bool cancelled) =>
            _partialPublishStep.TryPublishPartialProductsAsync(task, cancelled);

        private bool PartialPublishActive => _partialPublishStep.PartialPublishActive;

        AppSettings? IPartialPublishStepHost.Settings => Settings;

        bool IPartialPublishStepHost.SpaceTightThisBatch => _spaceTightThisBatch;

        IArchiveEngine IPartialPublishStepHost.Engine => _archiveEngine;

        void IPartialPublishStepHost.AppendLog(string level, string message) => AppendLog(level, message);

        string IPartialPublishStepHost.RestHandlingThisBatch => _restHandlingThisBatch;

        bool IPartialPublishStepHost.IsInsideRecursionWorkspace(string? path) => IsInsideRecursionWorkspace(path);

        void IPartialPublishStepHost.PurgeRestItemsExceptChainRootSource(ArchiveTask task) =>
            PurgeRestItemsExceptChainRootSource(task);
    }
}