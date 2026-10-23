using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;



namespace ArchiveFixer.ViewModels
{
    internal sealed partial class ExtractionCoordinator : IRestHandlingStepHost
    {


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
            IReadOnlyList<ArchiveTask>? chainTasks)
        {
            bool anyLanding = false;

            foreach (ArchiveTask? candidate in EnumerateChainTasks(rootTask, chainTasks))
            {
                /*
                 * 同一分卷组的后续卷**不算这一条链上的产物**（用户 2026-10-01）：它没有解压过、
                 * 没有产物、也没有自己的落点目录 —— 它的落点是用同一套路径算法**重算**出来的，
                 * 于是必然与首卷那一单重合，拿它去比"链上的输出校验"必然得出"链里有一层没过"，
                 * 整组的源包就一个都删不掉了（真机现象）。
                 */
                if (candidate.IsVolumeGroupFollower)
                {
                    continue;
                }

                if (!SafePathHelper.PathEquals(
                        ResolveContinuationOutputDirectory(candidate), destinationDirectory))
                {
                    continue;
                }

                anyLanding = true;

                /*
                 * ⚠ 2026-09-30（检验等级 L4）：判据从 <c>IsOutputVerified</c>（= "校验通过"，
                 * 含"拿不到清单只做了底线校验"那一档）**收紧**成"可证完整"——
                 * 唯一出口 <see cref="ResultCompletenessClassifier"/>。这一处是**链尾补搬/删源**的最后一道
                 * 前置判据，判不出 ⇒ 源包一个字节都不许动。
                 */
                ResultCompletenessVerdict completeness = ResultCompletenessClassifier.Classify(candidate);

                if (!completeness.AllowsSourceRemoval)
                {
                    return $"{candidate.LogName}：{completeness.Message}"
                           + $"（机器结论：{completeness.Evidence}）";
                }
            }

            return anyLanding
                ? null
                : $"链里没有一个任务把 {destinationDirectory} 当落点（没有产物被定稿到那里）";
        }



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
            CancellationToken cancellationToken) =>
            _restHandlingStep.CollectChainInnerPackagesIntoRestAsync(rootTask, chainTasks, cancellationToken);

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

        internal static void ApplySourceMoveResult(ArchiveTask task, SourcePackageMoveResult result) =>
            RestHandlingStep.ApplySourceMoveResult(task, result);

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

        private Task RunRestHandlingAsync(ArchiveTask task, ScheduledTaskRuntime runtime, bool oneClickRun, bool force = false) =>
            _restHandlingStep.RunRestHandlingAsync(task, runtime, oneClickRun, force);

        internal static string DescribeLayerReclaimBlocker(ArchiveTask task) =>
            RestHandlingStep.DescribeLayerReclaimBlocker(task);

        internal static string DescribeRestPurgedLine(RestPurgeOutcome outcome, DeleteMode deleteMode) =>
            RestHandlingStep.DescribeRestPurgedLine(outcome, deleteMode);

        private void PrepareRestHandlingForBatch() => _restHandlingStep.PrepareRestHandlingForBatch();

        private void LogRestHandlingForBatch() => _restHandlingStep.LogRestHandlingForBatch();

        private void PurgeRestItemsExceptChainRootSource(ArchiveTask task) =>
            _restHandlingStep.PurgeRestItemsExceptChainRootSource(task);

        PathService IRestHandlingStepHost.Paths => _pathService;

        bool IRestHandlingStepHost.SpaceTightThisBatch => _spaceTightThisBatch;

        bool IRestHandlingStepHost.SpaceTightKeepSourceThisBatch => _spaceTightKeepSourceThisBatch;

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

        void IRestHandlingStepHost.PurgeGroupLandedPieces(ArchiveTask consumer, string groupBaseName) =>
            PurgeGroupLandedPieces(consumer, groupBaseName);

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
    }
}
