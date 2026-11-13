using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 普通模式（不先测试、不直读）：候选循环 → 失败结论收尾，以及所有路共用的方法尾收尾
    /// （恢复加密头结论 / 密码类补充句 / 登记本批密码失败 / 结束时间 / 进度与操作复位）。
    ///
    /// <para>只做编排；候选循环、结论、判据全在既有出口里。⛔ 文案/终态/清进度的顺序一字不改。</para>
    /// </summary>
    internal sealed class ExtractionNormalMode
    {
        internal async Task<string> RunAsync(
            ExtractionCoordinator host,
            ArchiveTask task,
            int maxPasswordAttempts,
            int attemptLimit,
            bool candidatesTruncated,
            List<PasswordItem> candidates,
            ArchiveListResult? preflightList,
            bool preflightSaidEncryptedHeaders,
            string? encryptedHeadersMessage,
            string engineArchivePath,
            string engineOutputPath,
            string outputRedirectNote,
            bool sharedOutputRoot,
            ArchiveOperationResult? lastResult,
            string selectedPassword,
            bool passwordConfirmed,
            ArchiveOperationResult? ambiguousTestResult,
            string passwordConclusionNote,
            string lastListErrorType,
            ExtractOptions extractOptions,
            ProgressHeartbeatStep.Sink progressSink,
            bool oneClickRun,
            CancellationToken cancellationToken)
        {
            /*
             * 普通模式：不先测试，直接解压。
             * 好处：①普通无密码压缩包会立刻产生解压产物；②不会让用户误以为卡住；③遇到密码错误再试下一个候选。
             */
            ExtractionRunState loopState = new()
            {
                MaxPasswordAttempts = maxPasswordAttempts,
                AttemptLimit = attemptLimit,
                CandidatesTruncated = candidatesTruncated,
                Candidates = candidates,
                PreflightList = preflightList,
                PreflightSaidEncryptedHeaders = preflightSaidEncryptedHeaders,
                EncryptedHeadersMessage = encryptedHeadersMessage,
                EngineArchivePath = engineArchivePath,
                EngineOutputPath = engineOutputPath,
                OutputRedirectNote = outputRedirectNote,
                SharedOutputRoot = sharedOutputRoot,
                LastResult = lastResult,
                SelectedPassword = selectedPassword,
                PasswordConfirmed = passwordConfirmed,
                AmbiguousTestResult = ambiguousTestResult,
                PasswordConclusionNote = passwordConclusionNote
            };

            await new ExtractionCandidateLoop()
                .RunAsync(host, loopState, task, cancellationToken, oneClickRun, extractOptions, progressSink, lastListErrorType)
                .ConfigureAwait(true);

            return (await new ExtractionTaskConclusionWriter()
                .WriteAsync(host, task, loopState, oneClickRun, cancellationToken)
                .ConfigureAwait(true)).PasswordConclusionNote;
        }

        internal async Task FinishAsync(
            ExtractionCoordinator host,
            ArchiveTask task,
            bool preflightSaidEncryptedHeaders,
            string? encryptedHeadersMessage,
            string passwordConclusionNote)
        {
            await Task.CompletedTask.ConfigureAwait(true);

            /*
             * 把预检的"文件名已加密"结论恢复回来：只在"这一层最后仍然只是密码没通过"时生效。
             */
            host.Auxiliary.RestoreEncryptedHeadersConclusion(task, preflightSaidEncryptedHeaders, encryptedHeadersMessage);

            /*
             * 密码类结论上要补的两句话（换引擎兜底 / 未试候选点名）——⛔ 必须排在加密头恢复之后。
             */
            if (!string.IsNullOrEmpty(passwordConclusionNote) && !string.IsNullOrEmpty(task.ErrorMessage))
            {
                task.ErrorMessage += passwordConclusionNote;
            }

            // 密码类失败登记到本批，批次结束后合并成一次提示。
            host.RecordPasswordFailure(task);

            task.EndTime = ArchiveFixer.Helpers.Clock.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            // 实时进度到此为止：清百分比与"长时间无响应"提示。
            task.ClearProgress();

            task.Operation = StatusText.OpWaiting;

            // 进度文案只在没有失败/取消结论时才写"完成"。
            if (task.ProgressText != StatusText.ProgressFailed &&
                task.ProgressText != StatusText.ProgressCancelled)
            {
                task.ProgressText = StatusText.ProgressCompleted;
            }

            task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;
        }
    }
}

