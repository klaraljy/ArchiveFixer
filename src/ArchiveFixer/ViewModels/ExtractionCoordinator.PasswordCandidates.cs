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
    /// <summary>落点/层计算编排（第 4 块，2026-10-23 从主分片搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator : IPasswordCandidatesHost
    {

        /// <summary>把"密码没通过"的任务登记到本批（只登记，不弹窗）。</summary>
        private void RecordPasswordFailure(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            /*
             * 三档（2026-10-01）：`PasswordOrCorrupted`（RAR 那句"文件已损坏或密码错误"）也算**密码类失败**。
             *
             * 为什么必须算：那一档恰恰是"最可能缺密码"的一档 —— 用户真机那批 RAR `-p` 包走的全是它，
             * 而原来它既不被登记、批末那条提示（"下一步去密码页导入 / 在确认框里填"）也就**一次都不出现**，
             * 用户只看到一行红字，不知道下一步该干什么。
             *
             * ⛔ 但**不许**因此把它说成确认的密码问题：引擎在那条路上本来就没法区分"密码错"与"数据坏"
             *（§11.4 的 RAR 两义句），批末文案里必须带着"可能"。
             */
            if (task.Status != StatusText.WrongPassword &&
                task.Status != StatusText.PasswordAttemptLimitReached &&
                task.Status != StatusText.PasswordOrCorrupted)
            {
                return;
            }

            // 本批出现过"密码类失败"这个事实：批次结束后据此在合并提示里带上"手动输入密码"的出口。
            _batchHadPasswordFailures = true;

            string fileName = string.IsNullOrWhiteSpace(task.FileName)
                ? Path.GetFileName(task.CurrentPath)
                : task.FileName;

            lock (_passwordFailuresLock)
            {
                _passwordFailures.Add((fileName, task.Status));
            }
        }

        private void ClearPasswordFailures()
        {
            lock (_passwordFailuresLock)
            {
                _passwordFailures.Clear();
            }
        }

        /// <summary>
        /// 批开始时把手动密码清干净：**只对本次运行有效**（不变量 5）。
        ///
        /// 不是"记住上次输的密码"—— 那是持久化，红线。上一批输过什么，这一批重新问。
        /// </summary>
        private void ResetManualBatchPassword()
        {
            _manualBatchPasswords.Clear();
            _manualPasswordPrompted = false;
            _batchHadPasswordFailures = false;
        }



        internal readonly List<string> _manualBatchPasswords = new();

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

        private PasswordFallbackTriggerFacts BuildPasswordFallbackFacts(
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

        private PasswordCandidatesStep.FallbackCandidateSlice BuildFallbackCandidateSlice(List<PasswordItem> candidates, int maxPasswordAttempts) =>
            _passwordCandidatesStep.BuildFallbackCandidateSlice(candidates, maxPasswordAttempts);

        private Task<List<PasswordItem>> RankCandidatesByDecryptedPrefixAsync(
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

        private Task<PasswordCandidatesStep.PasswordProbeOutcome> ProbePasswordAsync(
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

        List<string> IPasswordCandidatesHost.ManualBatchPasswords => _manualBatchPasswords;

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