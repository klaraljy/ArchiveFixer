using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// **批末诊断清单的唯一出口**（用户 2026-09-30 第 1 条）。
    ///
    /// <para>他要的东西：批末那个汇总框除了"成功 x / 失败 y / 跳过 z"这几个数字，还要像编译器那样
    /// **点名说清错在哪** —— "空间不足、哪个压缩包密码不对、111.7z.001 的分卷找不到"。
    /// 判据与排版都在这里，⛔ GUI / XAML / 窗口里一个字都不判断（它们只把
    /// <see cref="BatchSummaryReport.Text"/> 摆出来）。</para>
    ///
    /// <para><b>分组口径（⛔ 不新造第二套分类）</b>：</para>
    /// <list type="number">
    /// <item><description><b>成功/失败这一层的裁决不在这里</b> —— 颜色仍由
    /// <see cref="BatchSummarySeverityRules.FromTasks"/> 回答，本类把它**照抄**进
    /// <see cref="BatchSummaryReport.Severity"/>（调一次 <see cref="Build"/> 就两样都有）。</description></item>
    /// <item><description><b>具体原因只读既有的状态常量</b>（<see cref="ArchiveTask.Status"/> 对
    /// <c>StatusText</c> 的那几个常量）—— 这正是 <c>TaskSummaryService.ClassifyOutcome</c> /
    /// <c>OneClickCoordinator.IsFailureStatus</c> 一直在用的那一套口径；本类只是把它们**再细分一层**，
    /// 没有引入新的状态、也没有改它们的含义。</description></item>
    /// <item><description><b>状态说不出原因时按机器终态兜底</b>（<see cref="ArchiveTask.Outcome"/>）——
    /// 保证"没做成的任务一个都不会从清单里消失"，落不到具体原因的进"其他失败"。</description></item>
    /// </list>
    ///
    /// <para><b>成功的那条不许出现在任何组里</b>：终态是 <see cref="TaskOutcome.Succeeded"/> 且校验没有判否
    /// 的任务直接跳过（判据与不变量 6 一致：部分成功 / 校验判否都不算成功）。</para>
    /// </summary>
    public static class BatchSummaryDiagnosticsRules
    {
        /// <summary>每组最多点几个名字（其余折成"还有 K 个"）—— 用户给的范围是 3~5，取小的那个。</summary>
        public const int MaxNamesPerGroup = 3;

        /// <summary>「下一步」那一行最多说几条动作（再多就折成一句"其余看…"）。</summary>
        public const int MaxActionPhrases = 4;

        /// <summary>单个文件名最长显示多少个字符（超了保留头尾，尾巴上常常是 <c>.7z.001</c>）。</summary>
        public const int MaxNameLength = 60;

        /// <summary>
        /// 按**同一份**任务终态算出这一批的颜色与诊断清单（本类的唯一入口）。
        /// </summary>
        /// <param name="tasks">这一批真正处理过的任务（一键处理传的是根任务 + 全部续解子任务）。</param>
        /// <param name="maxNamesPerGroup">每组最多点几个名字（默认 <see cref="MaxNamesPerGroup"/>）。</param>
        public static BatchSummaryReport Build(
            IEnumerable<ArchiveTask>? tasks,
            int maxNamesPerGroup = MaxNamesPerGroup)
        {
            List<ArchiveTask> list = tasks?.Where(task => task != null).ToList() ?? new List<ArchiveTask>();

            BatchReport report = BatchReport.Create(list);
            BatchSummarySeverity severity = report.Severity;

            int limit = maxNamesPerGroup <= 0 ? 1 : maxNamesPerGroup;

            // 一次遍历归组：每个任务只被判一次，⛔ 不为每组建一次列表。
            var buckets = new Dictionary<BatchProblemKind, List<ArchiveTask>>();

            foreach (ArchiveTask task in list)
            {
                BatchProblemKind? kind = Classify(task);

                if (kind == null)
                {
                    continue;
                }

                if (!buckets.TryGetValue(kind.Value, out List<ArchiveTask>? bucket))
                {
                    bucket = new List<ArchiveTask>();
                    buckets[kind.Value] = bucket;
                }

                bucket.Add(task);
            }

            /*
             * 「其余物为什么还在」逐链点名（用户 2026-10-04 真机）：那一批的日志写着
             * 「Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）」，
             * 可这个框里一个字都没有 —— 他在界面上看到的是"解压成功、其余物还在"。
             *
             * 事实位 = `ArchiveTask.RestKeptReason`（唯一写入点在协调器，读的是一句现成的话，⛔ 这里不重推）。
             * 它**独立成行**而不是挂进某个组：链尾那一条判据可能落在**成功**的那个任务上，
             * 而成功的任务按口径一个字都不进任何组（见 Classify 的第一条）。
             */
            List<string> restKeptLines = BuildRestKeptLines(list);
            List<string> leftBehindLines = BuildLeftBehindInnerPackageLines(list);

            if (buckets.Count == 0 && restKeptLines.Count == 0 && leftBehindLines.Count == 0)
            {
                // 全成功（或空批）：**一个字都不写**，连标题都不许出现。
                return new BatchSummaryReport { Severity = severity };
            }

            var groups = new List<BatchProblemGroup>();

            foreach (BatchProblemKind kind in Enum.GetValues<BatchProblemKind>())
            {
                if (!buckets.TryGetValue(kind, out List<ArchiveTask>? bucket) || bucket.Count == 0)
                {
                    continue;
                }

                groups.Add(new BatchProblemGroup
                {
                    Kind = kind,
                    Title = DescribeKind(kind),
                    Note = BuildGroupNote(kind, bucket),
                    TotalCount = bucket.Count,
                    CommitMoveFailedCount = bucket.Count(task => task.CommitMoveFailed),
                    Items = bucket.Take(limit).Select(BuildItem).ToList()
                });
            }

            var lines = new List<string>();

            foreach (BatchProblemGroup group in groups)
            {
                lines.Add(group.ToLine());
            }

            /*
             * 「其余物为什么还在」逐链点名（用户 2026-10-04 真机）：那一批的日志写着
             * 「Sociology.7z：链尾的其余物不处理（链上的「老王.apk」没有成功…）」，
             * 可这个框里一个字都没有 —— 他在界面上看到的是"解压成功、其余物还在"。
             *
             * 事实位 = `ArchiveTask.RestKeptReason`（唯一写入点在协调器，读的是一句现成的话，⛔ 这里不重推）。
             * 它**独立成行**而不是挂进某个组：链尾那一条判据可能落在**成功**的那个任务上，
             * 而成功的任务按口径一个字都不进任何组（见 Classify 的第一条）。
             */
            lines.AddRange(restKeptLines);
            lines.AddRange(leftBehindLines);

            /*
             * "内层包没解开"不是失败，但这一批**确实有东西没做成**（内容还在里面）——
             * 全成功时也不许显示成蓝色（用户 2026-11-10：「汇总好像没有」就是这一档）。
             */
            if (leftBehindLines.Count > 0 && severity == BatchSummarySeverity.Success)
            {
                severity = BatchSummarySeverity.Partial;
            }

            string nextStep = BuildNextStepLine(groups);

            if (!string.IsNullOrWhiteSpace(nextStep))
            {
                lines.Add(nextStep);
            }

            return new BatchSummaryReport
            {
                Severity = severity,
                Groups = groups,
                Lines = lines
            };
        }

        /// <summary>
        /// 「其余物为什么还在」逐条那一行（没有就返回空清单，⛔ 不写空话）：
        /// <c>· 任务名 —— 其余物没有处理：留在 …，原因：…；一个字节都没动。</c>
        ///
        /// <para>只读任务上那一句现成的话（<see cref="ArchiveTask.RestKeptReason"/>）——
        /// 判据与措辞的唯一出口在协调器（<c>DescribeRestKeptNote</c>），这里只负责摆出来。</para>
        /// </summary>
        /// <summary>
        /// 「成品目录里还留着内层包」逐条那一行（事实位 <see cref="ArchiveTask.LeftBehindInnerPackageNames"/>，
        /// 唯一写入点在链尾清点；⛔ 这里不重新判、不重算路径）。
        /// </summary>
        private static List<string> BuildLeftBehindInnerPackageLines(List<ArchiveTask> tasks)
        {
            var lines = new List<string>();

            foreach (ArchiveTask task in tasks)
            {
                if (task.LeftBehindInnerPackageNames.Count == 0)
                {
                    continue;
                }

                string names = string.Join("、", task.LeftBehindInnerPackageNames.Take(5));

                if (task.LeftBehindInnerPackageNames.Count > 5)
                {
                    names += string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.BatchDiagnosticsMoreFormat,
                        task.LeftBehindInnerPackageNames.Count - 5);
                }

                lines.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.BatchDiagnosticsLeftBehindInnerPackagesLineFormat,
                    Shorten(ResolveName(task)),
                    task.LeftBehindInnerPackageNames.Count,
                    names));
            }

            return lines;
        }
        private static List<string> BuildRestKeptLines(List<ArchiveTask> tasks)
        {
            var lines = new List<string>();

            foreach (ArchiveTask task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.RestKeptReason))
                {
                    continue;
                }

                lines.Add(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.BatchDiagnosticsRestKeptLineFormat,
                    Shorten(ResolveName(task)),
                    task.RestKeptReason.Trim()));
            }

            return lines;
        }

        /// <summary>
        /// 这一组的注脚（可能不止一句，按"先部分完成、后定稿搬运失败"的顺序用「；」连起来；
        /// 一句都没有时返回空串，⛔ 不写空话）。
        ///
        /// <para>密码那一组是固定的那一句（必须带"可能"，见 <see cref="StatusText.BatchDiagnosticsPasswordNote"/>）。</para>
        /// </summary>
        private static string BuildGroupNote(BatchProblemKind kind, List<ArchiveTask> bucket)
        {
            if (kind == BatchProblemKind.Password)
            {
                return StatusText.BatchDiagnosticsPasswordNote;
            }

            var notes = new List<string>();

            string partialPublished = DescribePartialPublishNote(bucket);

            if (!string.IsNullOrWhiteSpace(partialPublished))
            {
                notes.Add(partialPublished);
            }

            string commitMoveFailed = DescribeCommitMoveFailedNote(bucket);

            if (!string.IsNullOrWhiteSpace(commitMoveFailed))
            {
                notes.Add(commitMoveFailed);
            }

            return string.Join("；", notes);
        }

        /// <summary>
        /// 这一组里"定稿搬运失败"那几个的补充一句（没有就返回空串）。
        ///
        /// <para>为什么要它（用户 2026-10-04 真机）：那一批的「其他失败」里就是 `老王.apk` 这一单
        /// 「定稿搬运失败」，而组里/「下一步」原来的说法把人指去"看引擎原话" —— 这一类失败**根本没有
        /// 引擎原话**（引擎那一步早就成功、校验也通过了，是程序自己搬不动）。判据只读任务上的事实位
        /// <see cref="ArchiveTask.CommitMoveFailed"/>，⛔ 不比中文文案。</para>
        /// </summary>
        private static string DescribeCommitMoveFailedNote(List<ArchiveTask> bucket)
        {
            int count = bucket.Count(task => task.CommitMoveFailed);

            return count == 0
                ? string.Empty
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.BatchDiagnosticsCommitMoveFailedNoteFormat,
                    count);
        }

        /// <summary>
        /// 这一组里"部分完成发布救回来多少"的补充一句（没有就返回空串，⛔ 不写空话）。
        ///
        /// <para>为什么要有（用户 2026-10-02 那一档）：批末只数"失败 / 部分完成几个"，
        /// 用户看到"3 个没做成"会以为那些包一个字节都没救回来 —— 而实际上有的已经发布了
        /// 557 个文件、现在就能用。判据只读任务上那一刻写下的两个字段（
        /// <see cref="ArchiveTask.PartialPublishedCount"/> / <see cref="ArchiveTask.PartialPublishDirectoryPath"/>），
        /// ⛔ 不去重算、也不看中文状态。</para>
        /// </summary>
        private static string DescribePartialPublishNote(List<ArchiveTask> bucket)
        {
            int publishedTasks = 0;
            long publishedFiles = 0;

            foreach (ArchiveTask task in bucket)
            {
                if (task.PartialPublishedCount > 0)
                {
                    publishedTasks++;
                    publishedFiles += task.PartialPublishedCount;
                }
            }

            return publishedTasks == 0
                ? string.Empty
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.BatchDiagnosticsPartialPublishedNoteFormat,
                    publishedTasks,
                    publishedFiles);
        }

        /// <summary>
        /// 一个任务该进哪一组；<c>null</c> = 这一条**不进清单**（唯一的一档就是"成功"）。        ///
        /// <para>判定顺序是刻意的：先剔掉成功，再按状态找具体原因，最后按机器终态兜底 ——
        /// 这样"状态说不出原因"的失败不会消失，而"状态是失败、终态却写着成功"的怪帧
        /// （真机出现过，见 <c>OneClickCoordinator.IsSuccessStatus</c>）也不会被当成成功放过去。</para>
        /// </summary>
        public static BatchProblemKind? Classify(ArchiveTask? task)
        {
            if (task == null)
            {
                return null;
            }

            /*
             * ① 成功这一档：终态成功 **且** 校验没有判否。
             * 用户明确要求"成功的那条不许出现在任何失败组里" —— 所以这里是 return null，不是某个"成功组"。
             */
            if (task.Outcome == TaskOutcome.Succeeded && task.OutputVerification != OutputVerificationOutcome.Failed)
            {
                return null;
            }

            /*
             * ①b **跟班卷不算"问题"**（用户 2026-10-01 第三报：「这四个应该是要跳过的，我绝对没必要
             * （提醒），你这样会让用户觉得还有任务没弄完」）：同一分卷组的后续卷已经由首卷那一单
             * 整组负责了，它不是一件没做成的事 ⇒ 不进任何问题组、也不进批末"已跳过"那一行。
             * 判据只有一位事实（`ArchiveTask.CountsTowardBatchOutcome`），⛔ 不在这里另推。
             */
            if (!task.CountsTowardBatchOutcome)
            {
                return null;
            }

            /*
             * ①c **「缺卷待批末判」那一档不算"问题"**（用户 2026-10-08：「没有到最后一步都是先跳过」）。
             *
             * 批中间它既不是缺卷结论、也不该出现在「分卷缺失」那一组里（那一行会让用户以为已经判死了）；
             * 到批末定稿时协调器会把这个事实位清掉，届时按真结论分组（真缺卷 ⇒ 那一组 + 「下一步：补卷」）。
             * 判据只有一位事实（<see cref="ArchiveTask.IsVolumeDeficitDeferred"/>），⛔ 不在这里另推。
             */
            if (task.IsVolumeDeficitDeferred)
            {
                return BatchProblemKind.Skipped;
            }

            /*
             * ② 具体原因：只认既有状态常量（与 TaskSummaryService / OneClickCoordinator 同一批常量）。
             *
             * ⚠ 这一支刻意排在"机器终态兜底"**之前**：状态能说出具体原因时就必须说具体原因，
             * 兜底只在状态说不出原因时才允许用（用户 2026-09-27：⛔ 不许出现"下一步：其他"这种废话兜底）。
             */
            if (task.Reason == TaskReason.FinalGroupIncomplete)
            {
                return BatchProblemKind.MissingVolume;
            }

            if (task.Reason == TaskReason.FinalGroupCorrupted)
            {
                return BatchProblemKind.Corrupted;
            }

            if (task.Reason == TaskReason.VolumeMissing)
            {
                return BatchProblemKind.MissingVolume;
            }

            // 机器事实优先：终态/机器原因能判的，不再靠中文 StatusText 才能归类。
            if (task.Outcome == TaskOutcome.Cancelled)
            {
                return BatchProblemKind.Cancelled;
            }

            if (task.Outcome == TaskOutcome.Skipped)
            {
                return BatchProblemKind.Skipped;
            }

            if (task.Reason == TaskReason.PasswordExhausted)
            {
                return BatchProblemKind.Password;
            }

            if (task.Reason == TaskReason.SourceChanged)
            {
                return BatchProblemKind.SourceChanged;
            }

            switch (task.Status)
            {
                case StatusText.DiskSpaceInsufficient:
                    return BatchProblemKind.DiskSpace;

                case StatusText.WrongPassword:
                case StatusText.PasswordAttemptLimitReached:
                case StatusText.EncryptedHeaders:

                /*
                 * 两义那一档（密码可能不对、也可能数据坏）归"密码"这一组：这一组的注脚本来就写着
                 * "可能"（<see cref="StatusText.BatchDiagnosticsPasswordNote"/>），
                 * ⛔ 不许写成"就是没有密码"；而另一个可能是"数据坏"，那一条由任务自己的
                 * ErrorMessage（带引擎原话）说清 —— 归到"损坏"组反而会把两种可能压成一种。
                 */
                case StatusText.PasswordOrCorrupted:
                    return BatchProblemKind.Password;

                case StatusText.VolumeMissing:
                case StatusText.FinalGroupIncomplete:
                    return BatchProblemKind.MissingVolume;

                case StatusText.Corrupted:
                case StatusText.FinalGroupCorrupted:
                    return BatchProblemKind.Corrupted;

                case StatusText.AccessDenied:
                    return BatchProblemKind.AccessDenied;

                case StatusText.OutputConflict:
                    return BatchProblemKind.OutputConflict;

                case StatusText.SourceChanged:
                    return BatchProblemKind.SourceChanged;

                case StatusText.PartiallyCompleted:

                    /*
                     * 「部分完成」这一格**要看终态**（用户 2026-09-27 真机 `giu.7z.001`）：
                     *
                     * · 终态 = 部分完成 → 确实做了一半，归"部分完成"组；
                     * · 终态 = 失败 → 这一单**一个文件都没解出来**（失败才是事实），
                     *   它还顶着「部分完成」只是老口径的残留。这时必须去认更具体的原因，
                     *   否则它会掉进"其他失败"、日志里再打一句「下一步：其他」——
                     *   用户点名的就是这句废话。
                     */
                    if (task.Outcome == TaskOutcome.Failed)
                    {
                        break;
                    }

                    return BatchProblemKind.PartiallyCompleted;

                case StatusText.Skipped:
                    return BatchProblemKind.Skipped;

                case StatusText.Cancelled:
                    return BatchProblemKind.Cancelled;
            }

            /*
             * ③ 状态没给出具体原因 → 看**机器终态**兜底（⛔ 不猜原因）。
             * 走到这里的 Succeeded 只可能是"校验判否"那一帧：不变量 6 要求它按失败报，归"其他失败"。
             */
            return task.Outcome switch
            {
                TaskOutcome.Failed => BatchProblemKind.Other,
                TaskOutcome.Succeeded => BatchProblemKind.Other,
                TaskOutcome.PartiallyCompleted => BatchProblemKind.PartiallyCompleted,
                TaskOutcome.Skipped => BatchProblemKind.Skipped,
                TaskOutcome.Cancelled => BatchProblemKind.Cancelled,
                _ => BatchProblemKind.NotReached
            };
        }

        /// <summary>组名（能复用既有状态常量的就复用：同一件事在界面上只允许有一个说法）。</summary>
        public static string DescribeKind(BatchProblemKind kind) => kind switch
        {
            BatchProblemKind.DiskSpace => StatusText.DiskSpaceInsufficient,
            BatchProblemKind.Password => StatusText.BatchDiagnosticsPasswordTitle,
            BatchProblemKind.MissingVolume => StatusText.VolumeMissing,
            BatchProblemKind.Corrupted => StatusText.Corrupted,
            BatchProblemKind.AccessDenied => StatusText.AccessDenied,
            BatchProblemKind.OutputConflict => StatusText.OutputConflict,
            BatchProblemKind.SourceChanged => StatusText.SourceChanged,
            BatchProblemKind.Other => StatusText.BatchDiagnosticsOtherTitle,
            BatchProblemKind.PartiallyCompleted => StatusText.PartiallyCompleted,
            BatchProblemKind.Skipped => StatusText.Skipped,
            BatchProblemKind.Cancelled => StatusText.Cancelled,
            _ => StatusText.BatchDiagnosticsNotReachedTitle
        };

        /// <summary>
        /// 「下一步」那一行：只说这一批**真的出现过**的那几档动作（最多
        /// <see cref="MaxActionPhrases"/> 条，其余折成一句），一行看完。
        /// </summary>
        private static string BuildNextStepLine(IReadOnlyList<BatchProblemGroup> groups)
        {
            var phrases = new List<string>();
            bool more = false;

            foreach (BatchProblemGroup group in groups)
            {
                string? action = DescribeAction(group);

                if (string.IsNullOrWhiteSpace(action) || phrases.Contains(action))
                {
                    continue;
                }

                if (phrases.Count >= MaxActionPhrases)
                {
                    more = true;
                    break;
                }

                phrases.Add(action);
            }

            if (phrases.Count == 0)
            {
                return string.Empty;
            }

            if (more)
            {
                phrases.Add(StatusText.BatchDiagnosticsNextStepRest);
            }

            return StatusText.BatchDiagnosticsNextStepPrefix + string.Join("；", phrases) + "。";
        }

        /// <summary>
        /// 每一组对应的**动作**（与各档现有的处置建议同一口径：空间那几条引空间门/中途提示的说法，
        /// 密码那一条引"到「密码」页一键导入"）。返回 <c>null</c> = 这一档没有需要用户做的事。
        ///
        /// <para>⚠ 「其他失败」那一档**按事实分派**（用户 2026-10-04 真机）：组里全是**定稿搬运失败**时，
        /// 原来那句"对照…引擎原话逐条看"就是错的指路（这一类失败没有引擎原话）⇒ 换成
        /// <see cref="StatusText.BatchDiagnosticsActionCommitMoveFailed"/>；
        /// 混合档与引擎类失败**照旧用原句**（⛔ 一个字都没删，它对引擎类失败是对的）。</para>
        /// </summary>
        private static string? DescribeAction(BatchProblemGroup group) => group.Kind switch
        {
            BatchProblemKind.DiskSpace => StatusText.BatchDiagnosticsActionDiskSpace,
            BatchProblemKind.Password => StatusText.BatchDiagnosticsActionPassword,
            BatchProblemKind.MissingVolume => StatusText.BatchDiagnosticsActionMissingVolume,
            BatchProblemKind.Corrupted => StatusText.BatchDiagnosticsActionCorrupted,
            BatchProblemKind.AccessDenied => StatusText.BatchDiagnosticsActionAccessDenied,
            BatchProblemKind.OutputConflict => StatusText.BatchDiagnosticsActionOutputConflict,
            BatchProblemKind.SourceChanged => StatusText.BatchDiagnosticsActionSourceChanged,
            BatchProblemKind.Other => group.CommitMoveFailedCount > 0 && group.CommitMoveFailedCount == group.TotalCount
                ? StatusText.BatchDiagnosticsActionCommitMoveFailed
                : StatusText.BatchDiagnosticsActionOther,
            BatchProblemKind.PartiallyCompleted => StatusText.BatchDiagnosticsActionPartiallyCompleted,
            BatchProblemKind.Cancelled => StatusText.BatchDiagnosticsActionNotFinished,
            BatchProblemKind.NotReached => StatusText.BatchDiagnosticsActionNotFinished,

            // 跳过（不是压缩包 / 用户自己在同名冲突里选的跳过）不需要用户做任何事。
            _ => null
        };

        /// <summary>
        /// 把任务渲染成清单里的一项：**只写文件名**（隐私 §8），该带补充的档把既有数字带出来。
        ///
        /// <para>两个补充都取自**已经算好的**值，⛔ 这里一个数都不重算：
        /// 空间那三个数来自空间门（<see cref="ArchiveTask.SpaceBlocked"/>，源头是
        /// <c>SpaceGate</c> / <c>ScheduledExtractionItem</c>）；缺的卷名来自识别阶段写好的
        /// <see cref="ArchiveTask.MissingVolumeNames"/>。</para>
        /// </summary>
        private static BatchProblemItem BuildItem(ArchiveTask task) => new()
        {
            Name = Shorten(ResolveName(task)),
            Detail = DescribeDetail(task)
        };

        private static string DescribeDetail(ArchiveTask task)
        {
            if (task.SpaceBlocked != null)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.BatchDiagnosticsSpaceDetailFormat,
                    TaskSpaceEstimate.FormatSize(task.SpaceBlocked.RequiredBytes),
                    TaskSpaceEstimate.FormatSize(task.SpaceBlocked.ShortfallBytes));
            }

            if (task.MissingVolumeNames.Count > 0)
            {
                string volumes = string.Join("、", task.MissingVolumeNames.Take(MaxNamesPerGroup));

                if (task.MissingVolumeNames.Count > MaxNamesPerGroup)
                {
                    volumes += string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.BatchDiagnosticsMoreFormat,
                        task.MissingVolumeNames.Count - MaxNamesPerGroup);
                }

                return string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.BatchDiagnosticsMissingVolumesFormat,
                    volumes);
            }

            /*
             * 「部分完成」这一组必须说清**差在哪一步**（用户 2026-09-27：批末诊断只说
             * 「下一步：其他」，而这件事其实有一句现成的原因 —— 比如"内容物已好、源包未能移入其余物"）。
             * 原因已经在任务上（`ErrorMessage`，由收尾那一刻写好的那一句），这里**原样取来**，
             * ⛔ 不重算、不另造一套说法。
             *
             * 只对部分完成这一档取：其余各档的原因由上面的结构化补充或组名本身说得更准。
             */
            if (string.Equals(task.Status, StatusText.PartiallyCompleted, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(task.ErrorMessage))
            {
                return ShortenText(task.ErrorMessage.Trim());
            }

            return string.Empty;
        }

        /// <summary>补充那句话最多留多少个字（超出截断 —— 一行诊断不该变成一段散文）。</summary>
        private const int MaxDetailLength = 80;

        private static string ShortenText(string text) =>
            text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";


        /// <summary>文件名（没有就退回路径的文件名部分；两者都没有才写一个短横）。</summary>
        private static string ResolveName(ArchiveTask task)
        {
            if (!string.IsNullOrWhiteSpace(task.FileName))
            {
                return task.FileName.Trim();
            }

            string fromPath = Path.GetFileName(task.CurrentPath ?? string.Empty);

            return string.IsNullOrWhiteSpace(fromPath) ? "-" : fromPath;
        }

        /// <summary>
        /// 超长名字保留**头 + 尾**：尾巴上常常是 <c>.7z.001</c> 这种能定位是哪一卷的信息，
        /// 直接截尾会把它切掉（用户要靠它去目录里找那一个文件）。
        /// </summary>
        private static string Shorten(string name)
        {
            if (name.Length <= MaxNameLength)
            {
                return name;
            }

            int head = MaxNameLength / 2;
            int tail = MaxNameLength - head - 1;

            return name[..head] + "…" + name[^tail..];
        }
    }
}
