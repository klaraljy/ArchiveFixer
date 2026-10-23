using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 同名冲突这一步的接线（第 17 轮③：实现已搬进 <c>Services/ConflictStep.cs</c> 独立类，
    /// 协调器只留一行转发；预检结论 <see cref="ConflictPrecheck"/> 也住在那个类旁边，供定稿侧共用）。
    /// </summary>
    internal sealed partial class ExtractionCoordinator : IConflictStepHost
    {
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

        private Task<ConflictResolution> ResolveConflictAsync(
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
    }
}
