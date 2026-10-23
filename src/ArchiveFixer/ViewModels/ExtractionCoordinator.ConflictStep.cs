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
    /// <summary>同名冲突询问/决定（第 7 块，从 Volume 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>同名冲突处理档（唯一来源；空 / 非法回落 AutoRename，见 <see cref="ConflictActions"/>）。</summary>
        private string ConflictActionValue => ConflictActions.Normalize(Settings.ConflictAction);

        /// <summary>本批开始时把冲突记账清零（上一批的"全部 X"绝不许影响这一批）。</summary>
        private void ResetBatchConflictState()
        {
            lock (_conflictDecisionLock)
            {
                _batchConflictDecision = null;
                _taskConflictDecision = null;
                _conflictPromptUnavailable = false;
                _conflictPromptCount = 0;
            }
        }

        /// <summary>每个任务开始时清掉"这一个"的记账（批量档位不受影响）。</summary>
        private void ResetTaskConflictState()
        {
            lock (_conflictDecisionLock)
            {
                _taskConflictDecision = null;
            }
        }

        /// <summary>本批 / 本任务已经定下的答案（"全部 X"优先于"这一个"）；没有就是 null。</summary>
        private ConflictDecision? GetEffectiveConflictDecision()
        {
            lock (_conflictDecisionLock)
            {
                return _batchConflictDecision ?? _taskConflictDecision;
            }
        }

        /// <summary>
        /// 问一次同名冲突并记账（**本批最多真正弹一次**）。
        ///
        /// <para>
        /// 返回值 <c>null</c> = 没问到答案（无界面宿主 / 等待超时）：调用方按保守档处理，**绝不覆盖**。
        /// 选择「取消本批」时本方法直接抛出取消，由上层把任务落成「已取消」（不变量 6）。
        /// </para>
        /// <para>
        /// 只允许在 UI 线程的上下文里调用（管线的那条 <c>await</c> 续体就在 UI 上下文）：
        /// 弹框本身走 <see cref="DialogService.ShowConflictDecisionAsync"/>，它自己负责
        /// "无界面宿主不弹窗、不阻塞"以及"后台线程用 InvokeAsync 而不是同步 Invoke"。
        /// </para>
        /// </summary>
        private async Task<ConflictDecision?> AskConflictAsync(
            ArchiveTask task,
            string message,
            string detail,
            CancellationToken cancellationToken)
        {
            lock (_conflictDecisionLock)
            {
                _conflictPromptCount++;
            }

            ConflictDecision? decision = await _dialogService.ShowConflictDecisionAsync(
                new DialogService.ConflictPrompt
                {
                    Message = message,
                    Subtitle = "同名冲突在本批只会问你这一次；勾上「对后面所有同名冲突都照此办理」就不会再打断你。",
                    Detail = detail,
                    Destructive = true
                },
                cancellationToken);

            /*
             * 询问期间被取消（用户点了「取消当前」）：不覆盖、不落位，把取消照原样抛上去，
             * 由 ProcessExtractTaskAsync 落成「已取消」—— 询问期间点了取消却报成功是最不能接受的。
             */
            cancellationToken.ThrowIfCancellationRequested();

            if (decision == null)
            {
                /*
                 * 问不到答案：**绝不代用户拍板成「覆盖」**（不变量 3：默认不得覆盖）。
                 * 记下"问不了"，后面的冲突不再尝试弹窗（否则一次批量会刷满降级日志）。
                 */
                lock (_conflictDecisionLock)
                {
                    _conflictPromptUnavailable = true;
                }

                AppendLog(
                    "WARN",
                    $"{task.LogName}：同名冲突问不到答案（当前宿主没有界面或等待超时），已按保守档继续 —— " +
                    "同名条目自动重命名落位，已有文件一个字节都不动。");

                return null;
            }

            ConflictDecision answered = decision.Value;

            if (answered.IsCancel)
            {
                // 「取消本批」= 既有的两套取消语义一起用：停止后续（不再启动新任务）+ 取消当前（正在跑的这个）。
                AppendLog("WARN", $"{task.LogName}：你在同名冲突询问里选择了「取消本批」，停止后续并取消当前任务。");

                StopAfterCurrent();
                CancelCurrentTask();

                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException();
            }

            lock (_conflictDecisionLock)
            {
                if (answered.ApplyToAll)
                {
                    _batchConflictDecision = answered;
                }
                else
                {
                    _taskConflictDecision = answered;
                }
            }

            AppendLog("INFO", $"{task.LogName}：同名冲突 —— 你的选择：{answered.Describe()}");

            if (answered.ApplyToAll)
            {
                AppendLog("INFO", "本批后续的同名冲突一律照此办理，不再询问。");
            }

            return answered;
        }

        /// <summary>
        /// 解析一次同名冲突：按档位（或用户的答案）给出"怎么办 + 落到哪"。
        ///
        /// Ask 档在这里才真的去问 —— 第一次遇到"目标已存在"时聚合问一次；
        /// 用户选了"全部 X"之后本批不再问，选了"这一个"则本任务内不再问（一次询问管一个任务）。
        /// 非 Ask 档一个字都不问，直接由档位推出结论（默认 AutoRename：绝不覆盖）。
        /// 落点的算法只有一处实现（<see cref="PathService.ResolveConflict"/>）。
        /// </summary>
        private async Task<ConflictResolution> ResolveConflictAsync(
            ArchiveTask task,
            ConflictTargetKind kind,
            string targetPath,
            string message,
            string detail,
            CancellationToken cancellationToken)
        {
            string action = ConflictActionValue;
            ConflictDecision? decision = GetEffectiveConflictDecision();

            if (ConflictActions.IsAsk(action) && decision == null && !IsConflictPromptUnavailable)
            {
                decision = await AskConflictAsync(task, message, detail, cancellationToken);
            }

            if (ConflictActions.IsAsk(action))
            {
                // decision 仍可能为 null —— 那就是"问不到"，走保守档（自动重命名，绝不覆盖）。
                return _pathService.ResolveConflict(
                    targetPath,
                    kind,
                    action,
                    decision ?? ConflictDecision.Conservative);
            }

            return _pathService.ResolveConflict(targetPath, kind, action);
        }

        /// <summary>把预检结论拼成给用户看的冲突清单（等宽小字那一栏）。</summary>
        private static string BuildConflictDetail(ConflictPrecheck precheck)
        {
            if (precheck.Samples.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            foreach ((string path, ConflictTargetKind kind) in precheck.Samples)
            {
                builder.Append(kind == ConflictTargetKind.Directory ? "［目录］" : "［文件］");
                builder.AppendLine(path);
            }

            if (precheck.TotalCount > precheck.Samples.Count)
            {
                builder.Append($"… 另有 {precheck.TotalCount - precheck.Samples.Count} 项同名（完整清单见日志）");
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 续解任务的落点路径里**是不是已经带着"以本层包名命名的那一层"**。
        ///
        /// <para>它是"要不要再套一层"的判据，只读路径本身这一个事实（⛔ 不猜、不看文案）：
        /// 忠实档 / 分支退化档下父任务落点已经追加过本层的包基名 → true（不再套，否则 <c>666\666</c>）；
        /// 简洁档的单链下层落点还停在父任务那一层 → false（末层那一层就该由定稿套出来）。</para>
        ///
        /// <para>单独提出来是因为它是**唯一判据**、而且要被测试直接钉住 —— 埋在定稿流程里
        /// 只能靠整条管线跑真 7z 才验得到。</para>
        /// </summary>
        internal static bool IsContinuationLayerAlreadyInPath(string? parentDirectory, string? archiveBaseName)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(archiveBaseName))
            {
                return false;
            }

            string last = Path.GetFileName(
                parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.Equals(last, FileNameHelper.SanitizeFileName(archiveBaseName), StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>预检里最多列几条冲突给用户看（再多就让他去看日志，别弹一个占满屏幕的框）。</summary>
        private const int MaxConflictSamplesInDialog = 5;

        /// <summary>定稿前的冲突预检结论（Ask 档要在动最终目录之前先问完）。</summary>
        private sealed class ConflictPrecheck
        {
            /// <summary>计划里"落点已经被占着"的条目总数。</summary>
            public int TotalCount { get; set; }

            /// <summary>其中属于**内容物**的条数（其余物撞名不算用户的内容被顶掉）。</summary>
            public int ContentCount { get; set; }

            /// <summary>给用户看的样例（最多 <see cref="MaxConflictSamplesInDialog"/> 条）。</summary>
            public List<(string Path, ConflictTargetKind Kind)> Samples { get; } = new();
        }

        private bool IsConflictPromptUnavailable
        {
            get
            {
                lock (_conflictDecisionLock)
                {
                    return _conflictPromptUnavailable;
                }
            }
        }


        private readonly object _conflictDecisionLock = new();

        private ConflictDecision? _batchConflictDecision;

        private ConflictDecision? _taskConflictDecision;

        private bool _conflictPromptUnavailable;

        /// <summary>本批已经真的弹过几次询问（日志与排障用；正常情况下 ≤ 1）。</summary>
        private int _conflictPromptCount;

    }
}