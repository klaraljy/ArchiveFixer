using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace ArchiveFixer.Services
{
    /// <summary>其余物这一步要问宿主的事（跨步账本、设置档位、路径服务、运行期 CTS）。</summary>
    internal interface IRestHandlingStepHost
    {
        PathService Paths { get; }

        bool SpaceTightThisBatch { get; }

        bool SpaceTightKeepSourceThisBatch { get; }

        bool LayerReclaimThisBatch { get; set; }

        CancellationTokenSource? OperationCts { get; }

        List<string> ChainInnerLayerInputs { get; }

        HashSet<ArchiveTask> PassThroughRestHandled { get; }

        Dictionary<string, List<(ArchiveTask Task, string PieceName)>> GroupPieceProducers { get; }

        Dictionary<string, string> GroupConsumerByName { get; }

        Dictionary<string, string> ConsumedVolumeSources { get; }

        List<(ArchiveTask Producer, string GroupBaseName, string SourcePath)> ByteBudgetLinkedSources { get; }

        AppSettings Settings { get; }

        OneClickRunOptions? RunOptions { get; }

        ObservableCollection<ArchiveTask> Tasks { get; }

        bool IsStopping { get; }

        SourceHandlingMode EffectiveSourceHandling { get; }

        IDeleteExecutor? RestDeleteExecutor { get; }

        void AppendLog(string level, string message);

        List<string> SnapshotGroupPieceProducerBaseNames();

        bool TrySnapshotGroupPieceProducers(string baseName, out List<(ArchiveTask Task, string PieceName)> producers);

        void CollectConsumedSourcePiecesIntoRest(ArchiveTask consumer);

        ScheduledTaskRuntime GetOrCreateRuntime(ArchiveTask task);

        string? PurgeLayerSourcePackage(
            ArchiveTask task,
            OutputVerificationResult? verification,
            StageCommitResult? commit,
            List<(string Level, string Message)> logEntries,
            LayerPurgeTrigger trigger,
            ResultCompletenessVerdict? reclaimVerdict = null,
            bool reclaimRootSource = true);

        string ResolveContinuationOutputDirectory(ArchiveTask task);

        bool AllRestFilesBelongToGroup(ArchiveTask? task, string groupBaseName, IEnumerable<string>? fileNames);

        int CountRestEntries(string? directory);

        string DescribeShortDestination(string? collectedPath, string? outputPath);

        string DescribeShortTail(string? path, int segments);

        void LogPassThroughRestCriterionInputs(
            ArchiveTask producer,
            ArchiveTask? consumer,
            string groupBaseName,
            string restDirectory,
            string why);

        void NoteBatchPurge(long freedBytes, bool permanent);

        ArchiveTask[] SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks);

        /// <summary>链完整性缺口的说明（唯一实现留在协调器的静态转发里，用例也直接钉它）。</summary>
        string? DescribeChainVerificationGap(
            string destinationDirectory,
            ArchiveTask rootTask,
            IReadOnlyList<ArchiveTask>? chainTasks);
    }
}
