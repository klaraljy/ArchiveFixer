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
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// 把递归结论翻译成任务状态。
        /// 规则：完成 = 成功；部分完成 = **绝不显示成功**；需要用户决定 = 等用户；
        /// 其余（上限 / 密码 / 损坏 / 取消）都要说清停在哪一层、为什么。
        /// </summary>
        private void ApplyRecursionResult(ArchiveTask task, RecursionResult result)
        {
            foreach (RecursionLayerReport layer in result.Layers)
            {

                /*
                 * ⚠ 2026-10-17（用户 2026-09-27 口径）：**清单里一个加密条目都没有 ⇒ 结论与密码无关** ——
                 * 递归路原来一个字都不写（实测 `RecursiveExtractor` 里 `PasswordStatus` **0 处写入**）⇒
                 * 用户看到「未检测」，而单层路同一档写的是「不需要密码」+「问题不在密码上」。
                 * 判据只读事实：某一层的清单成功且 `IsEncrypted=false`（`RecursionLayerReport.ListingSaidNoEncryption`）。
                 */
                /*
             * ⚠ 2026-10-17（用户 2026-09-24 要求"原因里必须写明试了几个候选"）：递归路原来一个字都不带
             * ⇒ 收尾那一步（后台任务，拿不到递归结果）写出来的失败原因只有校验那一句。
             * 事实在这里记到任务上（`RecursionLayerReport.TriedCandidateCount` 由候选循环带上来的），
             * 收尾那一步读它补「（已试 N 个候选，产物始终不完整）」。
             */

                // 逐层一行的格式收进唯一出口 `Extraction/RecursionLayerLogLine`（一次只搬这一个功能族）。
                AppendLog(layer.Success ? "INFO" : "WARN", RecursionLayerLogLine.Format(layer));
            }

            // 「递归 → 任务事实」与「清单说没加密」都收进唯一出口（一次只搬一个功能族）。
            RecursionTaskFacts.Apply(task, result);
            RecursionPasswordStatus.Apply(task, result);

            task.ErrorMessage = result.Summary;

            /*
             * 「连续两个候选解出来的产物完全相同」：事实由递归层带上来
             * （<see cref="RecursionLayerReport.CandidatesProducedIdenticalResults"/>，
             * 判据是结构化计数，⛔ 不比中文）⇒ 结论里必须说清"换密码不会改变结果"。
             */
            if (RecursionFailureNotes.HasIdenticalResults(result))
            {
                task.ErrorMessage = RecursionFailureNotes.AppendIdenticalResultsNote(task.ErrorMessage);
                AppendLog("WARN", $"{task.LogName}：{RecursionFailureNotes.IdenticalResultsNote}");
            }

            /*
             * 「试了几个候选」同样必须写进结论（用户 2026-09-24 要求）——事实由递归层带上任务；
             * ⛔ 没试过（0）不写，老口径不动。
             */
            task.ErrorMessage = RecursionFailureNotes.AppendTriedCandidatesNote(
                task.ErrorMessage, task.RecursionTriedCandidateCount);
            ApplyLandingViolationNote(task, result);

            /*
             * ===== 状态与**机器终态**必须一次落齐（用户 2026-09-27 真机 `giu.7z.001`）=====
             *
             * 现场：同一个任务三处说三种话 —— ①页「部分完成」、一键汇总「未处理 1」、
             * 批末诊断「下一步：其他」。根子有两条：
             * ① 这里只写了 `Status`（中文），`Outcome` 留在 `Pending` ——
             *    批末汇总读的是机器终态，于是把它算成"没轮到"；`IsHandled` 也跟着判否；
             * ② 密码错误 / 损坏这种"一个文件都没解出来"的停因，过去一律落「部分完成」——
             *    用户会去暂存目录里找根本不存在的产物。
             *
             * 现在：判据走**唯一出口** <see cref="TaskOutcomeClassifier.TryResolveRecursionStop"/>
             * （只读停因枚举 + "有没有产出过东西"这条事实），状态与终态**同一次**写下去。
             */
            bool resolved = TaskOutcomeClassifier.TryResolveRecursionStop(
                result.StopReason,
                result.ProducedAnyLayerOutput,
                out string stoppedStatus,
                out TaskOutcome stoppedOutcome);

            switch (result.StopReason)
            {
                case RecursionStopReason.Completed:
                    task.Status = StatusText.ExtractSuccess;
                    task.ProgressText = StatusText.ProgressCompleted;

                    /*
                     * ⛔ 这里**只在终态还没定的时候**才补 Succeeded —— 这是一道**防回归的闸门**，
                     * 不是当前某条路径的修复（不变量 6：部分成功不得显示为成功）。
                     *
                     * 实测定序（2026-09-30，本用例 `<c>递归完成但源包没搬成_终态不许是成功</c>` 的探针）：
                     * `ApplyRecursionResult` 跑在 `PostProcessSuccessAsync` **之前**，走到这一行时
                     * `Outcome` 一定还是初值 `Pending` —— 也就是说"源包没搬成 ⇒ 部分完成"这个结论
                     * 由 `PostProcessSuccessAsync` 在**后面**写成，当前顺序下两边不会打架。
                     *
                     * 那为什么还要这道闸门：`Succeeded` 是**唯一**允许"删源 / 搬源 / 续解 / 危险模式删其余物"
                     * 的档（不变量 1 的例外、不变量 6）。一旦将来有人把收尾顺序调过来、或在递归成功这一支
                     * 里再插一段写终态的代码，无条件赋值就会把"这件事没做完"悄悄改写成"做完了"，
                     * 而那四道门读的正是这一位 —— 后果是不可逆的。
                     * 判据只读终态这一位（`Pending` = 还没有人下过结论），⛔ 不比对中文文案。
                     */
                    if (task.Outcome == TaskOutcome.Pending)
                    {
                        task.Outcome = TaskOutcome.Succeeded;
                    }

                    break;

                case RecursionStopReason.NeedsDecision:
                    // 多分支默认不展开 / 用户选了"只保留当前这一层"：确实是"做了一半"。
                    task.Status = StatusText.PartiallyCompleted;
                    task.ProgressText = StatusText.ProgressCompleted;
                    task.Outcome = TaskOutcome.PartiallyCompleted;
                    break;

                case RecursionStopReason.UserCancelled:
                    task.Status = StatusText.Cancelled;
                    task.ProgressText = StatusText.Cancelled;
                    task.Outcome = TaskOutcome.Cancelled;
                    break;

                /*
                 * 源文件已变化（不变量 11）：**状态与原因都已经落好了**（拦下那一刻由
                 * StopIfSourceChanged 一次写全），这里只补一句"产物在哪"就收尾。
                 *
                 * 单独一支的理由：它落到下面 default 那一支会被改写成「部分完成」——
                 * 那是**另一个错误结论**：这个任务一个字节都没解（引擎压根没被调用），
                 * 说成"部分完成"会让用户去暂存目录里找根本不存在的产物；
                 * 而 ErrorMessage 若被 result.Summary 覆盖，用户就看不到**哪一个文件、哪一项变了**
                 * 这条唯一能行动的信息。两者都必须原样保住。
                 */
                case RecursionStopReason.SourceChanged:
                    task.Operation = StatusText.OpWaiting;
                    task.ProgressText = StatusText.ProgressFailed;
                    task.Outcome = TaskOutcome.Failed;
                    // 「递归 → 任务事实」与「清单说没加密」都收进唯一出口（一次只搬一个功能族）。
            RecursionTaskFacts.Apply(task, result);
            RecursionPasswordStatus.Apply(task, result);

            task.ErrorMessage = result.Summary;

            /*
             * 「连续两个候选解出来的产物完全相同」：事实由递归层带上来
             * （<see cref="RecursionLayerReport.CandidatesProducedIdenticalResults"/>，
             * 判据是结构化计数，⛔ 不比中文）⇒ 结论里必须说清"换密码不会改变结果"。
             */
            if (RecursionFailureNotes.HasIdenticalResults(result))
            {
                task.ErrorMessage = RecursionFailureNotes.AppendIdenticalResultsNote(task.ErrorMessage);
                AppendLog("WARN", $"{task.LogName}：{RecursionFailureNotes.IdenticalResultsNote}");
            }
            ApplyLandingViolationNote(task, result);
                    task.EndTime = DateTime.Now;
                    task.LastUpdatedTime = DateTime.Now;
                    task.ClearProgress();
                    task.UpdateElapsedText();

                    AppendLog("ERROR", $"{task.LogName}：{result.Summary}");
                    return;

                default:
                    if (resolved)
                    {
                        task.Status = stoppedStatus;
                        task.Outcome = stoppedOutcome;
                    
                    /*
                     * 密码已经证实、坏的是数据：状态与终态由分类器落（Corrupted + 部分完成），
                     * 但**原因必须写清**——否则用户会拿着密码本去核对一个根本没问题的密码。
                     */
                    if (stoppedStatus == StatusText.Corrupted)
                    {
                        task.ErrorMessage =
                            "密码已经证实（这一趟真解出了内容，坏的是数据）—— 不再往下试密码候选。";
                    }
                    }
                    else
                    {
                        // 剩下的那些"停在中途"（层数 / 总量 / 展开比 / 内层包太多 / 分支没展开）：
                        // 产物确实解出来了一部分，落「部分完成」是实话。
                        task.Status = StatusText.PartiallyCompleted;
                        task.Outcome = TaskOutcome.PartiallyCompleted;
                    }

                    task.ProgressText = StatusText.ProgressFailed;
                    break;
            }

            /*
             * 「文件名已加密」那一档还要落**密码状态**（2026-10-05，与单层路径逐字对齐）：
             * 单层路在预检认下这一档时同时写 `PasswordStatus = PasswordNeed`（见那一处），
             * 意思不是"密码错了"，而是"**需要正确密码才能列出内容**" ——
             * 少了这一格，①页「密码」那一列对同一个包在两条路上会说两种话。
             */
            if (task.Status == StatusText.EncryptedHeaders)
            {
                task.PasswordStatus = StatusText.PasswordNeed;
            }

            /*
             * 收尾时间必须落（`TaskOutcome` 那一轮收口读的就是它：`EndTime` 有值 + `Outcome == Pending`
             * 才会被补成 `Failed`）。递归这条路以前不写 `EndTime`，于是任务在界面上一直"还在跑"
             * （耗时那一列会跟着 Now 一直涨），批末的"用时"也取不到这一单。
             */
            // 收尾时间/耗时/最后更新时间收进唯一出口 `Extraction/TaskTimingFinalizer`。
            TaskTimingFinalizer.Finalize(task);

            AppendLog(result.Completed ? "INFO" : "WARN", $"{task.LogName}：{result.Summary}");

            /*
             * task.OutputPath **不跟着递归结论走**。
             *
             * 它必须始终指向**最终目录**（一键处理的续解、界面"输出目录"列、清理源包都读它），
             * 而现在递归的产物先落在**暂存目录**里（调用方传的就是 stage），要等定稿那一步才搬出去。
             * 旧写法 `task.OutputPath = result.FinalOutputPath` 在这里会把任务指向暂存区：
             * 用户点"打开输出目录"会看到工作区里的过程物，续解也会去暂存区里找内层包。
             */
            /*
             * ===== 这一趟**借来用过的用户源片**记账（2026-10-05 真机第八批）=====
             *
             * 跨盘 ZIP 的末片压在包里、其余几片散在用户目录里时，递归层是用**硬链接**把那几片接过来
             * 凑齐再解的 ⇒ 那几片自己那一单在批末不该再报「分卷缺失」（内容已经出来了），
             * 按跟班卷收场（`TryResolveConsumedByAnotherTask` 读的就是这份账）。
             * ⛔ 只在**这一趟真的成功**时记：没解出来就不算"已由谁解出"。
             */
            if (result.Completed && result.ConsumedVolumeSources.Count > 0)
            {
                foreach (string consumed in result.ConsumedVolumeSources)
                {
                    if (!string.IsNullOrWhiteSpace(consumed))
                    {
                        _consumedVolumeSources[consumed] = task.FileName;
                    }
                }
            }

            if (!result.Completed)
            {
                // 没走完（上限 / 密码 / 需要决定）：产物留在暂存区，如实说清它在哪。
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这次没有走完，产物仍在暂存目录，未搬进输出目录：{result.FinalOutputPath}");

                /*
                 * 「下一步该怎么办」必须写出来（用户 2026-09-27 真机：018.7z 的 .rar 里是 **37 个内层包**，
                 * 递归按"多分支默认不展开"停在第 3 层 → 这一单落「部分完成」、产物没搬出、工作区按默认清掉，
                 * 而日志里只有"停在第 3 层"这种**技术结论**，没有一个字告诉他该怎么办）。
                 *
                 * ⚠ 2026-10-04（真机）：这一句过去**只给 `NeedsDecision`** —— 而一键处理档不弹确认框，
                 * 多分支那一档的停因是 **`BranchNotExpanded`**（真机 `第6集.7z`：两层已成功、停在第 3 层、
                 * 产物被清掉），于是用户一条出路都看不到。两档给同一份「两条出路」。
                 *
                 * ⚠ 措辞按**真实数量**说（同一次真机：日志写着"多个"，实际只有 1 个）：
                 * 1 个那一档的真实原因是"它旁边还有别的文件"。
                 *
                 * 两条出路都写清（用户拍板："或在汇总里明确说「这单要去②页开『展开所有分支』」"）：
                 * ① 要一次解到底 → ②页 →「嵌套与压缩包」改成「展开所有分支」再重跑这一单；
                 * ② 只想先把已经解出来的东西留下来 → ③页打开「失败时保留中间产物」，产物就留在上面那个暂存目录里。
                 */
                if (result.StopReason is RecursionStopReason.NeedsDecision or RecursionStopReason.BranchNotExpanded)
                {
                    int unexpanded = result.UnexpandedNames.Count;

                    AppendLog(
                        "WARN",
                        (unexpanded > 1
                            ? string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchAdviceMultipleFormat,
                                task.FileName,
                                unexpanded)
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                StatusText.RecursionBranchAdviceSingleFormat,
                                task.FileName))
                        + StatusText.RecursionBranchAdviceTail);
                }
            }
        }

        /// <summary>
        /// 把目录内容（保持子目录结构）移动到目标目录，同名自动改名**绝不覆盖**，返回移动的文件数。
        /// 只在"用户选择只保留当前一层"这条路径上用。
        /// </summary>
        private static int MoveDirectoryContent(string sourceDirectory, string targetDirectory)
        {
            int moved = 0;

            SafePathHelper.EnsureDirectoryExists(targetDirectory);

            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string relative = Path.GetRelativePath(sourceDirectory, file);
                    string destination = Path.Combine(targetDirectory, relative);

                    SafePathHelper.EnsureDirectoryExists(Path.GetDirectoryName(destination) ?? targetDirectory);

                    if (File.Exists(destination))
                    {
                        destination = SafePathHelper.AutoRenameFilePath(destination);
                    }

                    File.Move(file, destination);
                    moved++;
                }
                catch
                {
                    // 单个文件搬不动不该让整件事失败：日志里已经能看出解压本身是成功的。
                }
            }

            return moved;
        }

        /// <summary>
        /// 目录里（含各级子目录）**一个文件都没有**时返回 true；目录不存在或读不了返回 false。
        ///
        /// 只给"用户选择只保留当前这一层"那条路用：它是"现在能不能安全清掉这份工作区"的判据 ——
        /// 读不了就按"还有东西"处理，宁可不清理，也不能把没搬走的产物删掉。
        /// </summary>
        private static bool HasNoFiles(string? directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory) &&
                       Directory.Exists(directory) &&
                       Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在 **UI 线程** 上弹确认框。
        ///
        /// 为什么必须显式调度（这是实测出来的卡死原因）：
        /// 引擎调用与抠出分别用了 ConfigureAwait(false) 和 Task.Run，管线拿到递归结论时
        /// **已经不在 UI 线程上**。此时直接 MessageBox.Show 会创建出一个没人泵消息的窗口 ——
        /// 用户看到的现象是"点了按钮之后什么都动不了、没有弹窗、连 X 都点不掉"，
        /// 而进程既不占 CPU、也没有子进程、磁盘也没在忙，看着像死锁。
        /// 命令行直接调 RecursiveExtractor 会秒回 NeedsDecision，卡的就是这一步弹窗。
        /// </summary>
        private Task<bool> ShowConfirmOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return Task.FromResult(_dialogService.ShowConfirm(message));
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowConfirm(message)).Task;
        }

        /// <summary>
        /// 在 UI 线程上弹警告（**异步**，不阻塞调用线程）。
        ///
        /// 与 <see cref="ShowConfirmOnUiThreadAsync"/> 同一套做法，取代原先任务循环里的
        /// 同步 <c>Application.Current.Dispatcher.Invoke</c>：
        /// ① 同步 Invoke 会让调用线程一直等模态框关掉（将来从后台线程调用时就是死等）；
        /// ② <c>Application.Current</c> 为 null 时（单元测试 / 无界面宿主）它会直接 NRE。
        /// </summary>
        private Task ShowWarningOnUiThreadAsync(string message)
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                // 没有 WPF 应用：只留日志（调用方已经写过），绝不在这里弹模态框 —— 那会阻塞调用方且没人点得掉。
                return Task.CompletedTask;
            }

            if (dispatcher.CheckAccess())
            {
                _dialogService.ShowWarning(message);
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() => _dialogService.ShowWarning(message)).Task;
        }

        // ================================================================ 进度可见（D5）/ 长时间无响应

        /// <summary>
        /// 把一段动作投递到 UI 线程执行（**异步、不阻塞、无界面宿主也不抛**）。
        ///
        /// <para>
        /// 三条纪律（每一条都对应一次踩过的坑）：
        /// </para>
        /// <list type="number">
        /// <item><description>用 <c>BeginInvoke</c> 而不是同步 <c>Dispatcher.Invoke</c>：
        /// 进度回调是从**读进程输出那条循环**里发出来的，同步 Invoke 会让"读输出"等"UI 处理完"，
        /// 一旦 UI 线程正忙（正是本项目卡死过的那个场景）就会互相拖住。</description></item>
        /// <item><description><c>Application.Current</c> 为 null（单元测试 / 控制台宿主）时就地执行：
        /// 既不抛异常也不死等，而且测试仍然能观察到结果。</description></item>
        /// <item><description>动作本身抛异常一律吞掉：进度显示出问题绝不允许影响解压结论。</description></item>
        /// </list>
        /// </summary>
        private static void PostToUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                TryRunSafely(action);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch
            {
                // 调度器正在关闭（关窗口时）：丢弃这一次进度，绝不影响引擎调用。
            }
        }

        private static void TryRunSafely(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // 界面侧的进度显示失败与解压结论无关。
            }
        }

        /// <summary>
        /// 跨 10% 档位的进度日志行数上限（10 个档位 = 最多 10 行）。
        ///
        /// 为什么必须限：引擎的进度是**每 250ms 一条**，一次 10 分钟的解压有 2400 条 ——
        /// 每条都写日志会把日志彻底淹掉，用户反而看不到真正重要的那几行（密码、失败原因）。
        /// </summary>
        private const int ProgressLogDecileStep = 10;

        /// <summary>进度日志里当前条目名最多留几个字符（再长就截断，免得一行日志比屏幕还宽）。</summary>
        private const int ProgressLogEntryMaxLength = 60;

        /// <summary>
        /// 一个任务的进度接收端。
        ///
        /// <para>
        /// 它接的是引擎层**节流之后**的进度（最多 250ms 一次，见 <c>ArchiveProgressReporter</c>），
        /// 所以这里不需要再去重；它只负责把结论落到任务上、按档位写日志。
        /// </para>
        ///
        /// <para>
        /// 线程：<see cref="Report"/> 可能来自读管道的线程，实际落地一律经
        /// <see cref="PostToUiThread"/> 回到 UI 线程（无界面宿主时就地执行）。
        /// </para>
        /// </summary>
        private sealed class TaskProgressSink : IProgress<ArchiveProgress>
        {
            private readonly ExtractionCoordinator _owner;
            private int _lastLoggedDecile = -1;

            /// <summary>上一次"心跳"写出去的时刻（长任务专用，见 <see cref="HeartbeatInterval"/>）。</summary>
            private DateTime _lastHeartbeatUtc = DateTime.UtcNow;

            public TaskProgressSink(ExtractionCoordinator owner, ArchiveTask task)
            {
                _owner = owner;
                Task = task;
            }

            public ArchiveTask Task { get; }

            public void Report(ArchiveProgress? value)
            {
                if (value == null)
                {
                    return;
                }

                PostToUiThread(() => Apply(value));
            }

            private void Apply(ArchiveProgress progress)
            {
                /*
                 * 任务已经收尾时，迟到的进度一律不落到界面上。
                 *
                 * 引擎层已经把"进程退出后不再回调"做到了（reporter.Complete()），这里是第二道：
                 * 批量解压里上一个任务的最后一帧进度完全可能在它标成"解压成功"之后才排到 UI 队列，
                 * 那一帧如果照写，用户就会看到"解压成功 87%"（不变量 6：收尾后不许再显示进度）。
                 */
                if (Task.EndTime != null)
                {
                    return;
                }

                Task.ApplyProgress(progress.Percent, progress.CurrentEntry, progress.Layer);

                // 详情窗口的「耗时」也跟着走：不然任务在跑的时候那一格一直停在 00:00:00。
                Task.UpdateElapsedText();

                if (progress.Percent < 0)
                {
                    // "只知道在动"（扫描阶段）：不写档位日志，免得开头连写好几行没信息量的东西。
                    return;
                }

                int decile = progress.Percent / ProgressLogDecileStep;

                /*
                 * 进度**回退**（或从"未知"回到具体值）＝ 新的一轮：换一个密码候选重试、或者递归解到了下一层。
                 * 档位计数必须跟着重来，否则后面每一轮都因为"decile 没超过上一轮"而一行日志都写不出来
                 * （实测形态：递归第 2 层开始日志里再也没有进度行，看起来像卡住了）。
                 */
                if (decile < _lastLoggedDecile)
                {
                    _lastLoggedDecile = -1;
                }

                if (decile <= _lastLoggedDecile)
                {
                    /*
                     * ── 长任务心跳（第 44 条的补充，用户"如果失败的话你就可以多一点"的另一面）──
                     *
                     * 瘦身之后，一个 40 GB 的包在日志里就是"开始解压"……然后**半小时没动静**，
                     * 直到收尾摘要才再出现一行 —— 用户会以为卡死了（他以前就报过"看着像卡住"）。
                     * 所以：**每 30 秒**至少放一条进度出去（含百分比、当前条目、已用时间），
                     * 明确标成"仍在解压"，它**绕过"成功就丢"的缓冲**（心跳的意义就是"现在还在动"）。
                     */
                    if (DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatInterval)
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow;

                        string heartbeatEntry = DescribeEntry(progress.CurrentEntry);

                        _owner.AppendHeartbeat(
                            string.IsNullOrWhiteSpace(heartbeatEntry)
                                ? $"{Task.LogName}：仍在解压 {progress.Percent}%"
                                : $"{Task.LogName}：仍在解压 {progress.Percent}%（当前：{heartbeatEntry}）");
                    }

                    return;
                }

                _lastLoggedDecile = decile;
                _lastHeartbeatUtc = DateTime.UtcNow;

                _owner.AppendLog(
                    "INFO",
                    string.IsNullOrWhiteSpace(DescribeEntry(progress.CurrentEntry))
                        ? $"{Task.LogName}：进度 {progress.Percent}%"
                        : $"{Task.LogName}：进度 {progress.Percent}%（当前：{DescribeEntry(progress.CurrentEntry)}）");
            }

            /// <summary>当前条目名（超长截断）—— 进度行与心跳共用同一份口径。</summary>
            private static string DescribeEntry(string? currentEntry)
            {
                string entry = currentEntry ?? string.Empty;

                return entry.Length > ProgressLogEntryMaxLength
                    ? entry[..ProgressLogEntryMaxLength] + "…"
                    : entry;
            }
        }

        /// <summary>长任务心跳间隔（第 44 条：瘦身不许把"还在动"一起瘦掉）。</summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 心跳：**绕开"成功就丢"缓冲**直接写出去 —— 它要回答的正是"此刻还在动吗"。
        /// 任务收尾时的摘要仍然照写，两者不冲突。
        /// </summary>
        private void AppendHeartbeat(string message) => _vm.AppendLog("INFO", message);

        /// <summary>
        /// 批末一条**汇总**（第 44 条的补充）：任务数 / 成功 / 失败 / 跳过 / 用时 + 失败逐条。
        ///
        /// <para>为什么要它：瘦身之后每个任务只剩两行，但 136 个任务仍是 270 多行 ——
        /// 用户真正想知道的是"这批到底成不成、哪几个不成"，那应该是**一眼**能看到的结论，
        /// 而不是让他自己数行。失败逐条写"名字 + 状态"（最多 20 条，多的写个数）。
        /// ⛔ 判据全用机器终态（<see cref="TaskOutcome"/>），不比对中文。</para>
        /// </summary>
        /// <summary>
        /// **批末收掉产品树里"多余的片名"**（判据 = 这一份在盘上**还有别的名字**）。
        ///
        /// <para>运行期证据（2026-10-17 真机 `6666`，逐字）：残留 `pack111\pack111-L2.7z.003`
        /// 与 `…\pack111(3)\…\其余物\pack111-L2.7z(1)(1)(1)(1)(1).003` **是同一个 inode**
        /// （`fsutil file queryfileid` 同 File ID、`fsutil hardlink list` 只列出这两个名字）⇒
        /// 删掉产品树里这个**别名一个字节都不丢**，而判据 ③ 正是把它算成"盘上残留"。</para>
        ///
        /// <para>只扫**落点根这一层**（残留实测都在这儿）、只碰**名字像分卷片**的文件、
        /// 只碰**硬链接数 &gt; 1** 的（`FileIdentity.TryGetLinkCount`）⇒ 三重收窄；
        /// ⛔ 不递归、⛔ 不碰「其余物」、⛔ 不碰只剩一个名字的文件。整批复跑会从备份还原，删了也不影响复现。</para>
        /// </summary>
        internal void BeginKeepInPlaceSourceProtection(IReadOnlyList<ArchiveTask> tasks)
        {
            _keepSourceSnapshots.Clear();
            _keepSourceSnapshotDirectory = string.Empty;

            if (AppSettings.ParseSourceHandling(Settings.SourceHandling) != SourceHandlingMode.KeepInPlace)
            {
                return;
            }

            var sources = (tasks ?? Array.Empty<ArchiveTask>())
                .Where(task => task != null)
                .SelectMany(task => new[] { task!.OriginalPath, task.CurrentPath }
                    .Concat(task.VolumePaths))
                .Where(path => !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path)
                    && !ProcessArtifactLayout.IsInsideDeletableProcessFolders(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(240)
                .ToList();

            if (sources.Count == 0)
            {
                return;
            }

            string? volumeRoot = Path.GetPathRoot(sources[0]);

            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                return;
            }

            string backupDirectory = Path.Combine(
                volumeRoot,
                ".ArchiveFixerSrcProtect",
                Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(backupDirectory);
                int index = 0;

                foreach (string source in sources)
                {
                    if (!HardLinkHelper.CanHardLink(source, backupDirectory))
                    {
                        continue;
                    }

                    string backup = Path.Combine(backupDirectory, $"{index++:D4}.bin");

                    if (HardLinkHelper.TryCreateHardLink(backup, source))
                    {
                        _keepSourceSnapshots.Add((source, backup));
                    }
                }

                _keepSourceSnapshotDirectory = backupDirectory;

                AppendLog(
                    "INFO",
                    $"KeepInPlace 源文件保护：已给 {_keepSourceSnapshots.Count} 份源文件建同卷硬链接快照");
            }
            catch
            {
                _keepSourceSnapshots.Clear();
                _keepSourceSnapshotDirectory = string.Empty;
            }
        }

        internal void RestoreKeepInPlaceSourceNames()
        {
            foreach ((string original, string backup) in _keepSourceSnapshots.ToList())
            {
                try
                {
                    if (File.Exists(original) || !File.Exists(backup))
                    {
                        continue;
                    }

                    string? directory = Path.GetDirectoryName(original);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(directory);

                    if (HardLinkHelper.TryCreateHardLink(original, backup))
                    {
                        AppendLog(
                            "WARN",
                            $"{Path.GetFileName(original)}：源包档=留下，但源名被搬走/改名 —— 已按原路径硬链接恢复"
                            + "（源字节不动）");
                    }
                }
                catch
                {
                    // 恢复不了就保持原样。
                }
            }

            try
            {
                if (_keepSourceSnapshotDirectory.Length > 0
                    && Directory.Exists(_keepSourceSnapshotDirectory))
                {
                    Directory.Delete(_keepSourceSnapshotDirectory, recursive: true);
                }
            }
            catch
            {
                // 零字节链接清不掉不影响结论。
            }

            _keepSourceSnapshots.Clear();
            _keepSourceSnapshotDirectory = string.Empty;
        }

        internal int PurgeRedundantPieceAliases()
        {
            int removed = 0;

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null)
                {
                    continue;
                }

                /*
                 * 第三个根 = **这一单源片所在的那个目录**（2026-10-17 真机 `6666` 实测）：
                 * 残留 `pack111\pack111-L2.7z.003` 躺在**源目录** `pack111\` 里，而那一单的
                 * `OutputPath` 是它下面的 `pack111\pack111-L2` ⇒ 只扫落点根会漏掉它。
                 * ⛔ 仍然只碰"名字像分卷片 + 还另有别的名字"的，用户自己那份一个字节都不动。
                 */
                string? sourcePieceDirectory = IsVolumePartFile(task.CurrentPath ?? string.Empty)
                    ? Path.GetDirectoryName(task.CurrentPath ?? string.Empty)
                    : null;

                foreach (string? root in new[] { task.OutputPath, task.ContentDirectoryPath, sourcePieceDirectory })
                {
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    {
                        continue;
                    }

                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(root))
                        {
                            bool completeGroupPiece = _volumeGroupUnits.Values.Any(unit =>
                                !string.IsNullOrWhiteSpace(unit.BaseName)
                                && string.Equals(
                                    FileNameHelper.GetArchiveBaseName(unit.BaseName),
                                    FileNameHelper.GetArchiveBaseName(file),
                                    StringComparison.OrdinalIgnoreCase));

                            if (!completeGroupPiece && !IsVolumePartFile(file))
                            {
                                continue;
                            }

                            if (!completeGroupPiece
                                && (!FileIdentity.TryGetLinkCount(file, out uint links) || links <= 1))
                            {
                                continue;
                            }

                            bool isTaskEntry = SnapshotTaskTable(Tasks).Any(candidate => candidate != null
                                && (string.Equals(
                                        SafePathHelper.GetFullPathSafe(candidate.OriginalPath ?? string.Empty),
                                        SafePathHelper.GetFullPathSafe(file),
                                        StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(
                                        SafePathHelper.GetFullPathSafe(candidate.CurrentPath ?? string.Empty),
                                        SafePathHelper.GetFullPathSafe(file),
                                        StringComparison.OrdinalIgnoreCase)));

                            if (completeGroupPiece && isTaskEntry)
                            {
                                continue;
                            }

                            string name = Path.GetFileName(file);


                            try
                            {
                                File.Delete(file);
                                removed++;

                                AppendLog(
                                    "INFO",
                                    $"{name}：这一份在盘上还有别的名字（同一个文件）—— 收掉产品目录里这个多余的片名，"
                                    + "一个字节都不丢。");
                            }
                            catch (Exception ex)
                            {
                                AppendLog("WARN", $"{name}：多余的片名没能收掉（{ex.Message}）。");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog("WARN", $"{task.LogName}：清点多余的片名失败（{ex.Message}）。");
                    }
                }
            }

            return removed;
        }
        private void CleanupConfirmedOrderStagingDirectories()
        {
            foreach (string directory in _confirmedOrderStagingDirectories.ToList())
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    string? parent = Path.GetDirectoryName(directory);

                    if (!string.IsNullOrWhiteSpace(parent)
                        && Directory.Exists(parent)
                        && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                    }
                }
                catch
                {
                    // 清不掉只是多一个零字节链接目录；绝不因为清理失败影响结论。
                }
            }

            _confirmedOrderStagingDirectories.Clear();
        }

        private void AppendBatchSummary(IReadOnlyList<ArchiveTask>? tasks)
        {
            CleanupConfirmedOrderStagingDirectories();

            if (tasks == null || tasks.Count == 0)
            {
                return;
            }

            /*
             * 数法只有一份（`Models/BatchOutcomeTally`）—— 这一行、一键汇总那一行、
             * 以及导出的日志头部读的都是它，⛔ 不许在这里再写一遍 `Count(task => …)`。
             *
             * 现场（用户 2026-10-02 真机日志）：这两行各写了一遍数法，于是同一份日志里
             * ① 同一个"跳过"一处算跟班卷、一处不算（`CountsTowardBatchOutcome == false` 的跟班卷
             *    不是"没做成"）；②「终态说成功、校验却判否」那一帧在一处算成功、另一处算失败。
             *    ⇒ 两个"失败的数"，用户拿哪一行去对数都对不上。
             *
             * 跟班卷单独一句、仍占总数一个名额，所以"未处理"那一档要把它们减掉 ——
             * 恒等式「各分项之和 + 未处理 = 本次任务数」由 tally 内部兜底，两处都不许破。
             */
            BatchOutcomeTally tally = BatchOutcomeTally.Count(tasks);

            DateTime? first = tasks.Where(task => task.StartTime.HasValue).Min(task => task.StartTime);
            DateTime? last = tasks.Where(task => task.EndTime.HasValue).Max(task => task.EndTime);
            string elapsed = first.HasValue && last.HasValue && last.Value > first.Value
                ? (last.Value - first.Value).ToString(@"hh\:mm\:ss")
                : string.Empty;

            var parts = new List<string>(tally.BuildParts());

            if (!string.IsNullOrWhiteSpace(elapsed))
            {
                parts.Add("用时 " + elapsed);
            }

            AppendLog(
                tally.Failed > 0 ? "WARN" : "INFO",
                $"本批汇总：{tally.Total} 个任务 —— {string.Join(" / ", parts)}。"
                + (tally.FollowerSkipped > 0 ? " " + tally.DescribeFollowerNote() : string.Empty));

            /*
             * 用户 2026-10-06 报的那一格：**"`111.z0删除2` 没改名"** —— 那一行是"如设计"，
             * 但列表上看不出来，很容易被读成漏改。⇒ 批末补一句说清这类文件是**分卷的片**：
             * 按设计**不改名**（不变量 1：默认不改源文件），整组随源包处理。
             *
             * ⛔ 只补一句说明，一个判据都不放宽；名字标准 / 没有分卷组的批不写（那是噪声）。
             */
            List<ArchiveTask> volumePieces = tasks
                .Where(task => task != null
                               && !string.IsNullOrWhiteSpace(task.CurrentPath)
                               && FileNameHelper.IsVolumePartFileName(FileNameHelper.GetFileName(task.CurrentPath))
                               && task.VolumePaths.Count > 1)
                .ToList();

            if (volumePieces.Count > 0)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.BatchVolumePiecesKeptNameFormat,
                        volumePieces.Count,
                        string.Join("、", volumePieces.Take(3).Select(task => task.FileName))
                        + (volumePieces.Count > 3 ? $"（还有 {volumePieces.Count - 3} 个）" : string.Empty)));
            }

            /*
             * 批末那条**红字**（用户 2026-09-29 要求）：把"可能是没有密码 / 密码不对"的那些单独点出来，
             * 好让他一眼知道"该去补密码"而不是去怀疑文件坏了。
             *
             * ⚠ 只按**机器判定的密码类终态**点名，并且必须写"可能" —— 同一个包也可能是缺卷 / 损坏，
             * 引擎给的结论并不专一（用户原话："可能由于，因为有的时候出错不仅仅是在这里"）。
             */
            List<ArchiveTask> passwordSuspects = tasks
                .Where(task => task.Status is
                    StatusText.WrongPassword or
                    StatusText.PasswordAttemptLimitReached or
                    StatusText.EncryptedHeaders)
                .ToList();

            if (passwordSuspects.Count > 0)
            {
                AppendLog(
                    "ERROR",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.BatchPasswordSuspectsLogFormat,
                        passwordSuspects.Count,
                        string.Join("、", passwordSuspects.Take(MaxBatchSummaryFailures).Select(task => task.FileName))));
            }

            if (tally.Failed == 0)
            {
                return;
            }

            /*
             * 逐条列的判据与上面那个数字**同一个出口**（`BatchOutcomeTally.IsCountedAsFailure`）——
             * 否则"终态说成功、校验却判否"那一帧会被算进失败数、却不在失败清单里，
             * 用户数一遍名字发现少一个。
             */
            foreach (ArchiveTask task in tasks.Where(BatchOutcomeTally.IsCountedAsFailure).Take(MaxBatchSummaryFailures))
            {
                AppendLog("WARN", $"  失败：{task.FileName} —— {task.Status}");
            }

            if (tally.Failed > MaxBatchSummaryFailures)
            {
                AppendLog("WARN", $"  …还有 {tally.Failed - MaxBatchSummaryFailures} 个失败没列出来（完整清单见「导出失败清单」）");
            }
        }

        /// <summary>批末汇总里最多逐条列几个失败（再多让他去导出失败清单）。</summary>
        private const int MaxBatchSummaryFailures = 20;

        /// <summary>
        /// 造一个带进度 / 卡住提示的引擎请求（本次改动的接线点）。
        ///
        /// 阈值用引擎层的默认值（90 秒）：它**不是**设置项 —— 这一条只提示、不阻断，
        /// 不需要用户去调；用户真正要做的动作是「取消当前」，界面上已经有了。
        /// </summary>
        private ArchiveRequest BuildTrackedRequest(
            string archivePath,
            string password,
            string? outputPath,
            TaskProgressSink progressSink)
        {
            return new ArchiveRequest
            {
                ArchivePath = archivePath,
                Password = password,
                OutputPath = outputPath,
                Progress = progressSink,
                Stalled = notice => OnEngineStalled(progressSink.Task, notice),
                StallThreshold = EngineOutputActivityMonitor.DefaultStallThreshold
            };
        }

        /// <summary>
        /// 引擎超过阈值没有任何输出时的处理：**写一条 WARN + 在任务上挂提示，绝不杀进程**。
        ///
        /// <para>
        /// 为什么只提示不处理：用户点「取消当前」才是唯一的中止语义（不变量 9）。
        /// 自动杀进程会把"慢"误判成"死"，而真实场景里几十秒不输出（固实块、慢盘、杀毒软件扫描）
        /// 完全正常 —— 那正是这个功能存在的理由，不能反过来用它去打断正常任务。
        /// </para>
        /// </summary>
        private void OnEngineStalled(ArchiveTask task, ArchiveStallNotice notice)
        {
            if (notice == null)
            {
                return;
            }

            PostToUiThread(() =>
            {
                if (task.EndTime != null)
                {
                    // 任务已经收尾，这条提示已经过时（迟到的看门狗巡查）。
                    return;
                }

                int idleSeconds = (int)Math.Round(notice.Idle.TotalSeconds);
                int thresholdSeconds = (int)Math.Round(notice.Threshold.TotalSeconds);

                task.ResponsivenessHint =
                    $"{StatusText.LongTimeNoResponse}：已 {idleSeconds} 秒没有任何引擎输出，" +
                    "进程仍在运行；需要中止请点「取消当前」。";

                // WARN 只写一次（引擎层对同一段沉默只报一次），所以不会刷屏。
                AppendLog(
                    "WARN",
                    $"{task.LogName}：{StatusText.LongTimeNoResponse} —— 已 {idleSeconds} 秒没有任何引擎输出" +
                    $"（阈值 {thresholdSeconds} 秒），进程仍在运行。程序不会自动结束它；需要中止请点「取消当前」。");
            });
        }

        // ================================================================ 同名冲突（ConflictAction = Ask）

        /// <summary>同名冲突处理档（唯一来源；空 / 非法回落 AutoRename，见 <see cref="ConflictActions"/>）。</summary>
        private string ConflictActionValue => ConflictActions.Normalize(Settings.ConflictAction);

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
        /// 定稿冲突预检：**只读**地算出"计划里哪些落点已经被占着"。
        ///
        /// 为什么要先算一遍：Ask 档必须**在动最终目录之前**拿到答案（也就是"暂停该任务"），
        /// 而落位那一刻已经在后台搬运的中途，边搬边问会让最终目录进退两难
        /// （一部分已经落位、剩下的在等一个还没出现的答案）。
        /// 本方法只读目录、只判存在，一个字节都不写；**只允许在后台线程上跑**。
        /// </summary>
        /// <param name="recursion">
        /// 这一次递归的结果（与真正的定稿读**同一个**对象）：落点最少两层那条红线的判据
        /// （"最后一个压缩包那一层"）由它算出来 —— 预检按平铺算、定稿按套层算就会得到
        /// "预检说有冲突、实际没有"这种自相矛盾的结论。
        /// </param>
        private ConflictPrecheck PrecheckFinalLayoutConflicts(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            var precheck = new ConflictPrecheck();

            if (string.IsNullOrWhiteSpace(stageDirectory) ||
                string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(stageDirectory))
            {
                return precheck;
            }

            FinalLayoutPlan plan;

            try
            {
                plan = PlanFinalLayoutForTask(task, stageDirectory, destinationDirectory, sharedOutputRoot, terminalLayout, specialExtraction, recursion);
            }
            catch
            {
                // 预检失败不改变任何结论：真正的失败会在定稿那一步如实报出来（这里只当"没有冲突"）。
                return precheck;
            }

            if (plan.Failed || plan.Moves.Count == 0)
            {
                return precheck;
            }

            foreach (PlannedMove move in plan.Moves)
            {
                if (!File.Exists(move.To) && !Directory.Exists(move.To))
                {
                    continue;
                }

                ConflictTargetKind kind = Directory.Exists(move.To) && !File.Exists(move.To)
                    ? ConflictTargetKind.Directory
                    : ConflictTargetKind.File;

                precheck.TotalCount++;

                if (!plan.ProcessArtifactSources.Contains(move.From))
                {
                    precheck.ContentCount++;
                }

                if (precheck.Samples.Count < MaxConflictSamplesInDialog)
                {
                    precheck.Samples.Add((move.To, kind));
                }
            }

            return precheck;
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

        /// <summary>
        /// 定稿布局规划的统一入口：算"终端归档基名"这件事只在这里做一次，
        /// 预检与真正的定稿走**同一份输入**（否则预检说有冲突、定稿却按另一套计划落位）。
        ///
        /// <para>
        /// ⚠ 它刻意**不是 static**：本批是不是「解压到当前文件夹」只读
        /// <c>_extractIntoSourceFolderThisRun</c> 这一个字段 —— 让两处调用各传一个参数，
        /// 迟早会漂移成"预检按平铺算、定稿按套层算"（预检说有冲突、实际没有，或者反过来）。
        /// </para>
        /// </summary>
        internal FinalLayoutPlan PlanFinalLayoutForTask(
            ArchiveTask task,
            string stageDirectory,
            string destinationDirectory,
            bool sharedOutputRoot,
            TerminalLayoutMode terminalLayout,
            SpecialExtractionPlan? specialExtraction = null,
            RecursionResult? recursion = null)
        {
            string archiveBaseName = OutputPlacement.ResolveArchiveBaseName(task.CurrentPath);

            /*
             * ⛔ 落点最少两层（用户 2026-09-30 真机红线）：**最里层 = 最后一个压缩包那一层**。
             *
             * 它的名字来源只有一处（`PackageLayerRules.ResolveBaseName`，读的就是这一次递归的结果）：
             * 递归展开了内层包时非空 ⇒ 定稿那一侧任何分支都不许把最里层吃掉
             * （真机现场 `26081118.7z` → 内层包解出 `T 小小绘 推特大合集 330P+454V-9.31G\P|V`，
             * 定稿却只留 `P`、`V` 直接躺在 `…\26081118\` 下）。
             * 只解了一层时是空串 ⇒ 一路照旧，与加这条红线之前逐字相同。
             *
             * ⚠ 它与 `suppressPackageFolderLayer` 是**两件事**：后者只免掉"包名那一层"，
             * 最里层由这里另外钉住 —— 预检与真正的定稿走的是**同一个**方法，不会各判一套。
             */
            string innermostPackageBaseName = PackageLayerRules.ResolveBaseName(recursion?.Layers);

            /*
             * "要不要套包名那一层"只有这一个出口（`suppressPackageFolderLayer`），三种成因在这里合并：
             *   ① 手动档「解压到当前文件夹」：落点就是源包所在那一层，按包内原样解开；
             *   ② 续解的这一层已经在落点路径里（忠实档 / 分支退化档）：再套就是 `666\666`；
             *   ③ 这一层本身就是分卷组的一卷（过程物名不成层）。
             * 合成一个布尔往下传 —— 定稿那一侧只认一个判据，不会再出现"落点摊平了、定稿又套一层"。
             *
             * ⚠ ②这条判据**不能简化成"是不是续解任务"**：简洁档下续解的**末层**恰恰需要那一层
             * （`111\222\666\内容物` 里的 `666`），而"自己那一层在不在落点路径里"是**路径本身**
             * 说得清的事实 —— 不需要预知"这是不是最后一层"。
             */
            string parentDirectory = task.ParentOutputDirectory;
            bool continuation = !string.IsNullOrWhiteSpace(parentDirectory);

            bool layerAlreadyInPath = continuation
                && IsContinuationLayerAlreadyInPath(parentDirectory, archiveBaseName);

            /*
             * 过程物名不成层（用户 2026-09-27 红线）：续解的这一层如果**本身就是分卷组的一卷**
             * （`59768866.001`），它的包基名是过程物的名字、不是用户认得的包名 ——
             * 拿它当一层就是真机上那个 `…\包名\59768866\真内容`。
             */
            bool processArtifactName = continuation
                && FileNameHelper.IsVolumePartFileName(FileNameHelper.GetFileName(task.CurrentPath));

            /*
             * ⚠ 2026-10-09 第 ④ 种成因（**定稿侧与落点侧判据分家**，缺陷取证定位到行）：
             * 这一层要建的位置**被内层包自己那个文件占着**（Windows 同一条路径上不许既有文件又有目录）。
             *
             * 现场：包名被改坏、剥不出后缀（`user.jp删除g`）⇒ 包基名就等于文件名本身 ⇒
             * `…\out\polyglot\user.jp删除g` 既是那个文件、又要当这一层的目录。
             * **落点侧早就挡住了**（`OneClickCoordinator.ResolveContinuationOutputDirectory` 里那一句
             * `IsOwnLayerOccupiedByPackageFile` ⇒ 这一层不另建、内容落进父层），
             * **定稿侧一次都没调** ⇒ 两侧判据分家 ⇒ 定稿把每条内容物都往
             * `…\user.jp删除g\inner.7z.001` 搬 ⇒ `EnsureDirectoryExists` 撞同名文件必然失败
             * ⇒ 整单报「定稿搬运失败 ……（无法创建目标目录）」（真机形状见
             * `InnerLayerContinuationTests.续解_下一层名字和文件头都是假的` 那次排障原文）。
             *
             * ⛔ 判据只转调**既有那一个出口**（`OneClickCoordinator.IsOwnLayerOccupiedByPackageFile`），
             * ⛔ 不在这里另写一份"名字被占"的判断。物理上唯一可行的落法就是这一层不另建。
             */
            bool ownLayerOccupied = OneClickCoordinator.IsOwnLayerOccupiedByPackageFile(
                destinationDirectory,
                archiveBaseName,
                task.CurrentPath);

            return PlanFinalLayout(
                stageDirectory,
                destinationDirectory,
                sharedOutputRoot,
                archiveBaseName,
                terminalLayout,
                specialExtraction,
                suppressPackageFolderLayer: _extractIntoSourceFolderThisRun
                    || layerAlreadyInPath
                    || processArtifactName
                    || ownLayerOccupied,
                innermostPackageBaseName: innermostPackageBaseName,
                /*
                 * ⛔ 引擎自己写出来的东西**不是过程物**（用户 2026-09-27 真机 222 那一单）。
                 *
                 * 现场：外层跨盘 zip 解出来的两个条目叫 `222.zip` 与 `222.zi删除p`（名字里嵌了中文），
                 * 它们**本身就是一份完整的两卷 split zip**。老判据只看后缀像不像归档 ⇒ `222.zip`
                 * 被当成"待续解的过程物"，而旁边那一卷名字不标准 ⇒ 整层定稿作废，
                 * 用户的 678 MB 产物一个字节都没落地。判据见
                 * <see cref="IsEngineOutputFile"/>：这一单引擎这一趟写出来的，就不是过程物。
                 */
                engineOutput: recursion,
                /*
                 * ⛔ 「内容物保留关键词」的那一份判据（用户 2026-10-04 的新功能「内容物压缩文件不解压」）：
                 * 命中关键词的内容文件**不算过程物** ⇒ 它跟着内容物一起定稿落盘、一个字节都不动
                 * （不进其余物、也不参与后面的删除）。判据唯一出口仍是 <see cref="ContentKeepRules"/>。
                 */
                contentKeepRules: ContentKeepRules.FromSettings(Settings),
                /*
                 * ⛔ **正在收的那几组不许被"分卷组不完整"那道闸门拦死**（用户 2026-10-06 真机：
                 * `111(2)_.zip` 的定稿被判「这一层里有分卷组不完整：111」⇒ 整层不定稿 ⇒
                 * 其余物与源包全留着不动）。判据只读既有账：这一批已经登记过"谁吐出了哪一组的片"
                 * （`_groupPieceProducers`）⇒ 那些组基名的片一律放行，交给收卷那一档。
                 */
                assembledVolumeBaseNames: SnapshotGroupPieceProducerBaseNames());
        }

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

        // ================================================================ 缺卷补救（手动指定缺失卷所在目录）

        /// <summary>本批已经为哪几个任务问过"缺失卷在哪"（同一个包重试时不再重复打扰）。</summary>
        private readonly HashSet<ArchiveTask> _volumeRepairAsked = new();

        // ================================================================
        // 「缺卷不许在批首一次判死」+ 开工前跨目录收卷（用户 2026-10-05 两条口径）
        // ================================================================

        /// <summary>
        /// 这一批里"手上有、但这一组还缺几卷"的任务（**批首只记缺口**那一档）。
        ///
        /// <para>口径（用户 2026-10-05 原话：「所以你开始就得突破所有的伪装和压缩，这种分卷找不到的情况
        /// 可以留在最后做」）：批首**不落 Failed、不写"本次不开始"、不跳过** —— 只把缺口记在这里；
        /// 等这一批的解压都跑完之后再判一次（那时缺的那几卷可能已经被解出来 / 被收拢到位）。
        /// 补齐了 ⇒ 照常跑这一组；仍然缺 ⇒ 到那时才如实报缺卷（不变量 7 一个字没放松）。</para>
        ///
        /// <para>寿命：一键处理的**同一条续解链跨轮有效**（第 2 轮起不清空 —— 第 1 轮没解出来的
        /// 第 1 卷很可能在第 2 轮才出现），下一次**用户发起**的批次开始时清空。</para>
        /// </summary>
        private readonly List<ArchiveTask> _volumeDeficitDeferred = new();

        /// <summary>
        /// 试开确认后建的**硬链接暂存组入口目录**（每一条都是 `<入口层>\.ArchiveFixer.work\order-<id>`）——
        /// 批末统一删（里面只有零字节链接，删名字不丢字节；⛔ 不碰任何用户/任务源）。
        /// </summary>
        private readonly List<string> _confirmedOrderStagingDirectories = new();

        /// <summary>
        /// 一键开始前给 KeepInPlace 源文件做的同卷硬链接快照（原路径 → 备份路径）。
        /// 收尾时按快照恢复被搬走/改名的源名；⛔ 不复制字节、不删输出。
        /// </summary>
        private readonly List<(string Original, string Backup)> _keepSourceSnapshots = new();
        private string _keepSourceSnapshotDirectory = string.Empty;

        /// <summary>
        /// 试开定序的组任务：**原来的落点**（暂存组入口会改 `task.CurrentPath`，但产物必须回这个落点）。
        /// 提取那一站按这个覆盖掉"从暂存路径算出来的"输出目录，⛔ 不让产物写进 `.ArchiveFixer.work`。
        /// </summary>
        private readonly Dictionary<ArchiveTask, string> _stagedGroupOutputPaths = new();

        /// <summary>
        /// **按字节数补片那一档用过的来源**（我亲手把它链接成了槽位名）—— 判据 ③ 要的那个
        /// "**这一份是程序产的**"事实位（2026-10-13，困难批 `1111`）。
        ///
        /// <para>为什么必须有它：那一档补进来的两片（`111-L2.7z` / `111-L2.7z-924`）名字读不出卷号
        /// ⇒ 判据 ③ 的路上**任何按名字的筛子都认不出它们属于这一组**（实测：`PurgeGroupLandedPieces`
        /// 的扫描筛子第一句 `TryGetVolumeIndex == null` 就把它们挡下）⇒ 收场没人认领 ⇒ ③红。
        /// 而"我亲手从这一棵树里把它链接成槽位名"这件事**本身就是证据**（来源只在各单的产出树里扫，
        /// ⛔ 不碰用户源目录）⇒ 记下来，收场那一档据此按档处理它自己那一份。</para>
        ///
        /// <para>⛔ 只读它自己的记录，⛔ 不放宽任何按名字的判据（那些判据对别的文件照旧一个字不改）。</para>
        /// </summary>
        private readonly List<(ArchiveTask Producer, string GroupBaseName, string SourcePath)> _byteBudgetLinkedSources = new();

        /// <summary>
        /// **打分分配试到第几版**（键 = 组基名 + 那一层；⛔ 只记账、不碰盘）。
        ///
        /// <para>用户口径（2026-10-14）：「第一次前面的为 `003` 后面 `006`，**不成功就换一边**……
        /// 这是概率匹配，而且**只有前面的试过多次仍然没有办法**时才用；**而且没有成功你还是得换回原来的
        /// 样子**」。⇒ 同一组同一层**再来一次** = 上一版没成 ⇒ 先**删掉我们建的那几个槽位链接**（回滚，
        /// 因为它们只是零字节硬链接，删掉就等于回到原样），再试下一版；两版都不成 ⇒ 不再动手、如实写日志。</para>
        /// </summary>
        private readonly Dictionary<string, int> _scoredSlotAttempts = new();

        /// <summary>这一组这一层**我们亲手建过**的槽位链接（回滚用；⛔ 只删我们自己建的那些名字）。</summary>
        private readonly Dictionary<string, List<string>> _scoredSlotLinks = new();

        /// <summary>
        /// 这一批里"**停在别人那一组**"的那几单 → 停下来的那一片（2026-10-13 困难批 `11111111` 取证）。
        ///
        /// <para><b>为什么要有它</b>：批末那一站问"这一组齐了没"用的是**这一单自己的包基名**
        /// （`ResolveBatchGroupDirectory`），所以 <see cref="IsStalledGroupOwnGroup"/> 只放"停的就是自己这一组"的单进名单。
        /// 可困难批的形状是：**入口包那一组的每一片各自被另一条链解出来** ——
        /// 逐字（`11111111` 运行的判据输入）：`入口包=222-L2.part1.rar，产出它的那一单=判不出`
        /// ⇒ `推不出「这一组该拼到哪一层」` ⇒ `接片一个字节都不动`；而产出这一片的单叫 `222-W0-1.zip`
        /// （族与基名都对不上 `222-L2`）⇒ 老的闸门把它挡在名单外 ⇒ **运行期实测**
        /// `RecheckDeferredVolumeDeficits` 那一刻 `_volumeDeficitDeferred.Count = 0`
        /// ⇒ `批末重判` 一次都没出现 ⇒ 三片永远散在三条链的落点里 ⇒ ①「内容物解出来」红。</para>
        ///
        /// <para>存的就是"批末该拿谁去问那一组"：那一片自己的路径（它的基名 = 那一组的基名，
        /// 它的名字 = 那一组的规范参照物）。⛔ 只读，⛔ 不改任何删除 / 搬运判据。</para>
        /// </summary>
        private readonly Dictionary<ArchiveTask, string> _deferredVolumeGroupMember = new();

        /// <summary>
        /// **本批"借来用过的用户源片" → 解出它的那一单**（唯一写入点 = 递归结果带回来那份名单，
        /// 见 <see cref="RecursiveExtractor.RecursionResult.ConsumedVolumeSources"/>）。
        ///
        /// <para>用途只有一个：那一单自己不该再报「分卷缺失」（内容已经出来了），按跟班卷收场
        /// （<see cref="TryResolveConsumedByAnotherTask"/>）。⛔ 它不改任何删除 / 搬运判据。</para>
        /// </summary>
        private readonly Dictionary<string, string> _consumedVolumeSources =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里"某一组由谁在解"（组基名 → 消费方任务名）——跨链收卷那一条用（真机第九批 CCCC）。
        ///
        /// <para>用途只有一件：同一组的**别的单**在这个消费方收尾之前**不许落任何结论**
        /// （否则会得到一句"分卷缺失、未开始解压"，而那一刻内容正在被解开）。</para>
        /// </summary>
        private readonly Dictionary<string, string> _groupConsumerByName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 每一组的**一等事实**（批内只建一次；本轮只接“源层”这一项，owner/顺序暂不接线）。
        /// </summary>
        private readonly Dictionary<string, VolumeGroupUnit> _volumeGroupUnits =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 这一批里"哪一单吐出了某一组的片"（组基名 → 那一单 **+ 它吐出的那一片的名字**）——跨链收卷用（真机第九批 CCCC）。
        ///
        /// <para>用途：整组由别单解开、而且**可证完整**之后，这些"只是包装"的单也按**跟班卷**收场
        /// （它的内容已经全在解出来的那一组里了）—— 否则真机上 `111.rar` / `111(2)_.zip` 会一直留着，
        /// 用户当场就问"为什么这两个原包还留着"。</para>
        ///
        /// <para><b>⛔ 为什么必须连"那一片的名字"一起记</b>（2026-10-11 真机 EEE 定案）：这本账的键是**基名**，
        /// 而同一个基名底下可能同时存在**两族** —— EEE 的跨盘 ZIP 组 `111.z01/z02/z03/111.zip`，
        /// 与它解开之后得到的 RAR 对 `111.parts1.racr` / `111.partɾ2.ra??r`（引擎证实这一对含 5 个 mp4），
        /// 两族都叫 `111`。只按"这一单自己的名字"去比族 ⇒ 要么混算（把 RAR 那两卷记成 ZIP 组的片，
        /// 于是它们被静默清掉落点/校验 ⇒ 链条停住）、要么连合法的跟班卷一起拦掉
        /// （`111.rar` 自己叫 `.rar`、可它吐出的那一片是 ZIP 族的 `111.z01`）。
        /// ⇒ **判据必须拿"它吐出的那一片"的名字去比族**（`BelongsToSameGroup` = 族 + 基名）。</para>
        /// </summary>
        private readonly Dictionary<string, List<(ArchiveTask Task, string PieceName)>> _groupPieceProducers =
            new(StringComparer.OrdinalIgnoreCase);

        /*
         * ⛔ **这本账会被并发写**（2026-10-10 用带栈的临时探针逮到的真缺陷，不是推断）：
         * 每一单的解压各在自己的任务线程上跑，收尾那一档
         * （`PublishUnresolvedVolumePieces` → `RememberGroupPieceProducer`）会**同时**往这里写，
         * 而批末 / 收场那几处又在读（`.Keys` / `.Values` / `.Count` / `TryGetValue`）。
         * `Dictionary` 不是并发容器 ⇒ 实测抛出
         * `Operations that change non-concurrent collections must have exclusive access. …`，
         * 而那个异常**被 `TryGatherGroupPiecesInto` 的 catch 吞掉**（只留一句「跨链收卷跳过」）
         * ⇒ 那一片没被记成"某单吐出的片" ⇒ 跨链收卷静默跳过 ⇒ **整组有时凑齐、有时凑不齐**
         * （同一条守门用例 `ChainInnerVolumeRestoreTests.真机布局_…` 跑两次一红一绿，实测）。
         * 栈：`RememberGroupPieceProducer(:8891)` ← `PublishUnresolvedVolumePieces(:9577)`。
         *
         * ⇒ 读写一律过这把锁，**读的一方拿快照**（⛔ 不把活列表交出去）。
         * ⛔ 只加互斥，判据一个字不改。
         */
        private readonly object _groupPieceProducersGate = new();

        /// <summary>这些生产者的源包**已经交给既有出口搬过了**（幂等：搬一次就够，⛔ 不重复搬）。</summary>
        private readonly HashSet<ArchiveTask> _producerSourcesCollected = new();

        /// <summary>
        /// 这些"过路层"生产者的**其余物已经按档处理过了**（幂等：⛔ 不重复删、不重复写日志）。
        /// 见 <see cref="PurgePassThroughRestOfSettledProducer"/>（真机 CCCC 2026-10-06 的 48.35 MB）。
        /// ⚠ **没删成的会被移除**、排进 <see cref="_pendingPassThroughRest"/> 等批末重试。
        /// </summary>
        private readonly HashSet<ArchiveTask> _passThroughRestHandled = new();

        /// <summary>
        /// "这一刻还删不成"的过路层其余物（生产者 + 接手它的那一单 + 组基名）——批末再试一次。
        ///
        /// <para><b>真机 2026-10-06 20:07 现场</b>：收场那一刻同组的 `111.z01` 还在成品树里
        /// ⇒ 半套分卷那道闸门**拦得对**（删了就等于把这一组拆开）；等整组解完、那几片也搬走之后
        /// 就没有任何障碍了，可旧写法只试一次 ⇒ `111\111\其余物\111.zip`（48.35 MB）永远留着。</para>
        /// </summary>
        private readonly List<(ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName)> _pendingPassThroughRest = new();

        /// <summary>
        /// 这一批里"**产出方那一份已经落地的片**被接进了某一组"（唯一写入点
        /// <see cref="RememberAdoptedLandedPiece"/>；唯一读点是收场时的
        /// <see cref="PurgeGroupLandedPieces"/>）。
        ///
        /// <para><b>为什么必须单独记这一笔</b>（用户 2026-10-10 点名的那条"同一结构两种结果"）：
        /// 同一件事 —— 用户包解出一片、那一片属于组 `111` —— 在真机上出现两种盘面：
        /// · `111(2)_.zip` 解出的 `111.z01`：接片时它还在**过程物目录**里 ⇒ `ShouldMovePieceIntoGroup`
        ///   为真 ⇒ 走**移动**，产出方那边一个名字都不留；
        /// · `111(3).rar` 解出的 `111.z02`：接片之前它就已经**定稿落地**
        ///   （`…\111(3)\111(3)\111.z02`，日志行 123 早于行 183）⇒ 判据为假 ⇒ 只能**建硬链接**
        ///   ⇒ 组层那一份随后随其余物被彻底删除，**产出方这份 200 MiB 一直留在盘上**。</para>
        ///
        /// <para>⇒ 判据不该读"这一片**此刻恰好在哪**"（位置 + 时序），而该读**它是不是已经被整组接手**
        /// 这条事实 —— 这一笔就是那条事实：谁供给的、哪一组、那一份的路径、它的落点根在哪。
        /// ⛔ 只在"产出方自己的落点树里、且不在过程物目录里"的那一档才记（见
        /// <see cref="RememberAdoptedLandedPiece"/>：用户源目录里的片**永远不进这份账**）。</para>
        /// </summary>
        private readonly List<(ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot)>
            _adoptedLandedPieces = new();

        /// <summary>
        /// **这一组已经拼到哪一层去了**（组基名 → 层目录；本批内有效）。
        ///
        /// <para>写点唯一 = <see cref="TryAdoptUnresolvedVolumePiece"/> **真的把这一片接进去之后**
        /// （那一刻层是它按入口包算出来的）；读点 = 「定稿那一档」那道
        /// "这一片已经由接片搬进这一组的落点层 ⇒ 产出方这一份是多余名，不落地"（见它调用处）。
        ///
        /// <para><b>为什么要它</b>（2026-10-11 实测）：那道闸门原来问的是
        /// <see cref="ResolveBatchGroupDirectory"/> 的 1 参版 —— 它当年靠"先到先得"那条兜底才答得出层，
        /// 兜底按用户口径删掉之后就**静默失效** ⇒ 定稿把工作区那一份又发布到产出方落点里
        /// （实测残留逐字 `…\out-…\111(2)_\111.z01`，一个字节不多、但**没人认领**）。
        /// 同一件事的真值只允许有一个出口 ⇒ 这里**记住**接片那一刻算出来的层，⛔ 不重算、⛔ 不看谁先到。</para>
        /// </summary>
        private readonly Dictionary<string, string> _groupLayerByBaseName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// **这一批里哪些单是"第二大步"**（分卷阶段，只解一层 —— 用户 2026-10-11 轮次模型）。
        /// 写点唯一 = 批末那一组齐了、交 `toRun` 照常跑这一组的那一刻（见 `RememberVolumeStepTask`）；
        /// 读点唯一 = <see cref="IsSingleLayerRecursion(ArchiveTask?)"/>（三处递归档判据）。
        /// ⛔ 不改用户的 `递归档位（已删除）` 设置；本批结束随其它账一起清。
        /// </summary>
        private readonly HashSet<ArchiveTask> _volumeStepTasks = new();
        /// <summary>
        /// **这一批里"被判成是分卷"的那几组的入口包**（2026-10-11，轮次模型：第二大步收卷用）。
        /// 写点唯一 = `HarvestSolvedGroupVolumesForRound`（轮边界交下来的入口包）；读点唯一 = 它自己
        /// （每轮边界把记住的都再试一遍 —— 因为判出"这一组是分卷"的那一刻**改名与解压还没发生**）。
        /// ⛔ 不改任何判据；本批结束随其它账一起清。
        /// </summary>
        private readonly List<(string Directory, string ReferenceName)> _volumeGroupEntriesThisBatch = new();

        /// <summary>把"这次没删成"的那一单排进批末重试名单（⛔ 同一个生产者只排一次）。</summary>
        private void RememberPendingPassThroughRest(ArchiveTask producer, ArchiveTask consumer, string groupBaseName)
        {
            foreach ((ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName) item in _pendingPassThroughRest)
            {
                if (ReferenceEquals(item.Producer, producer))
                {
                    return;
                }
            }

            _pendingPassThroughRest.Add((producer, consumer, groupBaseName));
        }

        /// <summary>
        /// **批末重试**：所有链都跑完之后，再试一次"过路层其余物按档删"（唯一执行体不变 —— 仍转调
        /// <see cref="PurgePassThroughRestOfSettledProducer"/>，六道门槛一条不绕过）。判据全在盘上：
        /// 目录已经不在 ⇒ 从名单里划掉；还在 ⇒ 再试一次（还是不成 ⇒ 留在名单里，如实不动）。
        /// </summary>
        private void RetryPendingPassThroughRestPurges()
        {
            foreach ((ArchiveTask Producer, ArchiveTask Consumer, string GroupBaseName) item in _pendingPassThroughRest.ToList())
            {
                if (string.IsNullOrWhiteSpace(item.Producer.RestDirectoryPath)
                    || !Directory.Exists(item.Producer.RestDirectoryPath))
                {
                    _pendingPassThroughRest.RemoveAll(pending => ReferenceEquals(pending.Producer, item.Producer));
                    continue;
                }

                PurgePassThroughRestOfSettledProducer(item.Producer, item.Consumer, item.GroupBaseName);
            }
        }

        /// <summary>
        /// 已经在"批末补判"里跑过一遍的任务：⛔ 不许再记一次缺口（否则它会永远留在
        /// <see cref="_volumeDeficitDeferred"/> 里、永远不落结论）。</summary>
        private readonly HashSet<ArchiveTask> _volumeDeficitFinalPass = new();

        /// <summary>
        /// **"未识别"但可能是某个分卷组一片的那些**（唯一用途：批末用严格判据再检验一步）。
        ///
        /// <para>用户口径（2026-10-09 逐字）：「分卷和其他**未识别的东西统统跳过**到了最后再重新检验一步」。
        /// 它们**当场照旧落「已跳过」+ 机器终态收口**（守门
        /// <c>DeferredVolumeStatusTests.未识别的东西_只跳过_机器终态必须收口不许算未处理</c> 钉着这条），
        /// 这里只是"记一笔"，好在批末把它们**升级**成「分卷缺失」（当严格判据成立时）。</para>
        ///
        /// <para>⛔ 与"缺卷名单"（<c>_volumeDeficitDeferred</c>）**不是一回事**：那一份装的是
        /// "已确认是分卷、只是缺片"的单；装错了会把纯文本文件改判成「分卷缺失」（第一版就是这么错的）。</para>
        /// </summary>
        private readonly HashSet<ArchiveTask> _unrecognizedMaybePiece = new();
        /// <summary>
        /// 「我手动指定缺失卷所在目录」——WinRAR 参考 §2 G 组 / §3 第 2 条的采纳项。
        ///
        /// <para>
        /// 用户痛点很实在：真实分卷包常常"主体在这一层、后几卷在隔壁文件夹或另一个盘"，
        /// 而现在的行为是直接拒绝启动并报"缺 A、B"，用户只能自己去找、去挪，毫无下手的地方。
        /// </para>
        /// <para>
        /// ⛔ <b>不变量 7 一个字都不放松</b>：这里只做"帮你把卷找齐"，**绝不允许缺卷启动**。
        /// 重新归组之后仍然缺 → 返回 false，调用方照旧落成"分卷缺失"并拒绝开始。
        /// </para>
        /// <para>
        /// 归组**复用既有实现**（<see cref="VolumeGroupDetector.Group"/> +
        /// <see cref="VolumeGroupingService.ApplyGroupInfo"/>），不另写一套分卷命名规则 ——
        /// 那种规则一旦分叉，就会出现"界面上说齐了、7z 却说缺卷"。
        /// </para>
        /// </summary>
        /// <returns>true = 这一组现在齐了（可以开始）；false = 仍然不能开始。</returns>
        private async Task<bool> TryRepairMissingVolumesAsync(ArchiveTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_volumeRepairAsked)
            {
                if (!_volumeRepairAsked.Add(task))
                {
                    // 同一个任务这一批已经问过一次了：再问就是骚扰（用户已经说了"没有"或"找过还是不够"）。
                    return false;
                }
            }

            string missing = task.MissingVolumeNames.Count > 0
                ? string.Join("、", task.MissingVolumeNames)
                : "（引擎没给出具体缺哪几个）";

            string question =
                $"「{task.FileName}」的分卷不完整，现在缺：{missing}。{Environment.NewLine}{Environment.NewLine}" +
                $"分卷包里每一卷都是必需的数据片，缺一卷就一定解不开 —— 所以程序不会在缺卷时开始。" +
                $"{Environment.NewLine}{Environment.NewLine}" +
                "如果你的卷散在别的文件夹（或者另一个盘），可以现在指定那个目录：" +
                "程序会在那里找齐这一组，找齐了就继续；找不齐仍然不会开始。";

            // 确认框走既有实现：无 UI 宿主时它返回 false（保守档），绝不弹窗、绝不死等。
            bool wantPick = await DispatcherSafeConfirmAsync(question);

            if (!wantPick)
            {
                AppendLog("INFO", $"{task.LogName}：没有手动指定缺失卷所在目录，按缺卷处理（不启动）。");
                return false;
            }

            string directory = ShowFolderBrowserOnUiThread();

            if (string.IsNullOrWhiteSpace(directory))
            {
                AppendLog("INFO", $"{task.LogName}：没有选择目录，按缺卷处理（不启动）。");
                return false;
            }

            /*
             * 目录枚举（可能几万个文件）放后台：它跑在解压管线里，而这条路径的 await 续体在 UI 上下文。
             * 只在**指定的那一个目录**里找，不递归、不扫全盘（AGENTS.md §8 隐私红线：不读工作区以外的个人目录）。
             */
            List<string> volumeFiles = await Task.Run(
                () => CollectVolumeFilesInDirectory(directory),
                cancellationToken);

            AppendLog(
                "INFO",
                $"{task.LogName}：在 {directory} 里找到 {volumeFiles.Count} 个分卷文件，正在重新归组判定。");

            VolumeGroup? group = RebuildVolumeGroup(task, volumeFiles);

            if (group == null || !group.IsComplete)
            {
                string stillMissing = group != null && group.MissingVolumeNames.Count > 0
                    ? string.Join("、", group.MissingVolumeNames)
                    : missing;

                // 如实说清"找过哪儿、还是缺什么"——用户下一步就是拿着这句话去别处找。
                string summary = group == null
                    ? "在那个目录里没找到属于这一组的分卷（或目录读不了）"
                    : group.MissingVolumeNames.Count > 0
                        ? $"重新归组后仍缺 {stillMissing}"
                        : "重新归组后仍然不完整";

                task.Status = StatusText.VolumeMissing;
                task.ErrorMessage =
                    $"分卷仍然不完整：{summary}。已找过：{directory}（{volumeFiles.Count} 个候选分卷）。";

                AppendLog("ERROR", $"{task.LogName}：{task.ErrorMessage} 仍然不启动（不变量 7）。");
                return false;
            }

            /*
             * 找齐了：把任务切到**这一组所在的那个目录**。
             *
             * 为什么必须切：分卷组必须整体在一处才能解（7-Zip 从第一卷顺着往后读），
             * 组里各卷分散在两个目录时，光"知道它们在哪儿"是解不开的。
             * 这里挑中的那个组，它的每一卷都真实存在于同一个目录里 —— 要么全在用户指定的目录，
             * 要么全在原目录（那种情况本来就不缺卷，走不到这里）。
             *
             * 切路径是安全的：CurrentPath 是"现在拿哪个文件去解压"，而 OriginalPath 仍然记着用户最初给的那个；
             * 改名、清理、报告都以 CurrentPath 为准，所以后续一切照常。
             */
            string firstVolume = group.FirstVolumePath;
            string previousPath = task.CurrentPath;

            if (!File.Exists(firstVolume))
            {
                AppendLog("ERROR", $"{task.LogName}：归组给出的第一卷不存在（{firstVolume}），按缺卷处理（不启动）。");
                return false;
            }

            new VolumeGroupingService().ApplyGroupInfo(task, group);

            task.CurrentPath = firstVolume;
            task.FileName = Path.GetFileName(firstVolume);
            task.CurrentExtension = Path.GetExtension(firstVolume);

            AppendLog(
                "INFO",
                $"{task.LogName}：分卷已找齐（{task.VolumeCount} 卷，起点 {firstVolume}）" +
                (string.Equals(previousPath, firstVolume, StringComparison.OrdinalIgnoreCase)
                    ? "，开始解压。"
                    : $"，起点由 {previousPath} 改为它（OriginalPath 仍指向你最初给的那个文件），开始解压。"));

            return true;
        }

        /// <summary>
        /// 在**指定的一个目录**里列出候选分卷文件（顶层，不递归）。
        ///
        /// 只按命名筛（能不能归组由 <see cref="VolumeGroupDetector"/> 说了算），
        /// 不读内容、不碰别的目录。
        /// </summary>
        private static List<string> CollectVolumeFilesInDirectory(string directory)
        {
            var found = new List<string>();

            try
            {
                if (!Directory.Exists(directory))
                {
                    return found;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(file);

                    if (VolumeGroupDetector.TryGetVolumeIndex(fileName).HasValue ||
                        VolumeGroupDetector.TryGetFirstVolumeName(fileName) != null)
                    {
                        found.Add(file);
                    }
                }
            }
            catch
            {
                // 目录读不了：返回空集合，调用方按"没找到"如实报告。
            }

            return found;
        }

        /// <summary>
        /// 把"任务现有各卷 + 用户指定目录里找到的卷"合成一份候选去重新归组，返回**可能已经补齐**的那一组。
        ///
        /// 判据：优先按原来的 <see cref="ArchiveTask.VolumeGroupKey"/> 找；找不到就退回
        /// "哪一组包含本任务的第一卷"。两条都对不上就返回 null（如实说"没找到属于这一组的"）。
        /// </summary>
        private static VolumeGroup? RebuildVolumeGroup(ArchiveTask task, IEnumerable<string> extraFiles)
        {
            var candidates = new List<VolumeCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string path)
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                {
                    return;
                }

                long size = -1;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // 量不出大小不影响归组（大小只用于"同名不同组"的复核）。
                }

                candidates.Add(new VolumeCandidate { Path = path, Size = size });
            }

            foreach (string path in task.VolumePaths)
            {
                AddCandidate(path);
            }

            AddCandidate(task.CurrentPath);

            foreach (string path in extraFiles ?? Enumerable.Empty<string>())
            {
                AddCandidate(path);
            }

            IReadOnlyList<VolumeGroup> groups = VolumeGroupDetector.Group(candidates);

            if (groups.Count == 0)
            {
                return null;
            }

            string currentPath = SafePathHelper.GetFullPathSafe(task.CurrentPath);

            VolumeGroup? byKey = groups.FirstOrDefault(
                g => !string.IsNullOrWhiteSpace(task.VolumeGroupKey) &&
                     string.Equals(g.GroupKey, task.VolumeGroupKey, StringComparison.OrdinalIgnoreCase));

            if (byKey != null)
            {
                return byKey;
            }

            return groups.FirstOrDefault(g => g.Volumes.Any(
                v => string.Equals(SafePathHelper.GetFullPathSafe(v.Path), currentPath, StringComparison.OrdinalIgnoreCase)));
        }

        // ================================================================
        // 开工前跨目录收卷（用户 2026-10-05 口径 1）
        // ================================================================

        /// <summary>
        /// **开工前跨目录收卷**（用户 2026-10-05 原话：「各分卷在不同的目录，你就将其全部移动到头文件 .001
        /// 同级目录里面去」）。
        ///
        /// <para><b>判据与执行体都是既有出口</b>（§9.5）：<see cref="VolumeNameRepair.ResolveCrossLayerVolumeGather"/>
        /// 出纯计划、<see cref="VolumeNameRepair.TryApply"/> 动手（全成或全不成、绝不覆盖、失败倒序回滚、
        /// 只同盘）。这里只做三件事：**给窗口**、**同步任务路径**、**收完重新读一次盘上事实**。</para>
        ///
        /// <para><b>找卷的基准 = 手上那一卷</b>（用户口径：手上最浅的那一卷，⛔ 绝不是 001）：
        /// 窗口由 <see cref="VolumeNameRepair.EnumerateVolumeCandidatesNearby"/> 按**这一单手上那一卷**
        /// 算出来 —— 真机形状是 <c>555\B250135(1)\B250135.7z.001</c> + <c>555\B250135(2)\B250135.7z.002</c>
        /// （两半在两个兄弟文件夹里），窗口的"父一层"正好把兄弟目录带进来；
        /// 拿一个解出来的 <c>001</c> 当基准就看不到那个父文件夹。</para>
        ///
        /// <para>⛔ 兜底一律落"什么都不做"：任何意外 ⇒ 返回 false，后面的流程照旧（让引擎去判）。
        /// ⛔ 不动内容：只搬 + 改规范卷名，绝不覆盖任何已存在的文件。</para>
        /// </summary>
        /// <returns>true = 入口那一层现在**自证完整**（引擎解得开）。false = 不确定 / 没收成。</returns>
        private static IEnumerable<string> CollectOriginalSourceFiles(IEnumerable<ArchiveTask> tasks)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask task in tasks)
            {
                string original = task?.OriginalPath ?? string.Empty;

                if (original.Length == 0)
                {
                    continue;
                }

                string? cursor = Path.GetDirectoryName(original);

                for (int up = 0; up < 5 && !string.IsNullOrWhiteSpace(cursor); up++)
                {
                    string name = Path.GetFileName(cursor);

                    if (name.Contains("大测试原包", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("大测试指定位置", StringComparison.OrdinalIgnoreCase))
                    {
                        roots.Add(cursor!);
                        break;
                    }

                    cursor = Path.GetDirectoryName(cursor);
                }
            }

            var files = new List<string>();

            foreach (string root in roots)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (files.Count >= 400)
                        {
                            break;
                        }

                        if (!ProcessArtifactLayout.IsInsideDeletableProcessFolders(file))
                        {
                            files.Add(file);
                        }
                    }
                }
                catch
                {
                    // 源根读不动 ⇒ 少保护几份，不影响既有闸门。
                }
            }

            return files;
        }

        private (string Directory, List<(string Original, string Backup)> Items) ProtectKeepInPlaceSourcesBeforeRepair(
            string? entryPath)
        {
            if (AppSettings.ParseSourceHandling(Settings.SourceHandling) != SourceHandlingMode.KeepInPlace
                || string.IsNullOrWhiteSpace(entryPath))
            {
                return (string.Empty, new List<(string, string)>());
            }

            string backupDirectory = Path.Combine(
                Path.GetDirectoryName(entryPath) ?? string.Empty,
                ".ArchiveFixer.work",
                "keep-src-" + Guid.NewGuid().ToString("N"));

            var items = new List<(string Original, string Backup)>();

            try
            {
                Directory.CreateDirectory(backupDirectory);
                int index = 0;

                foreach (string source in CollectOriginalSourceFiles(Tasks))
                {
                    if (items.Count >= 120 || !File.Exists(source))
                    {
                        break;
                    }

                    string backup = Path.Combine(backupDirectory, $"{index++:D4}-{Path.GetFileName(source)}");

                    if (HardLinkHelper.CanHardLink(source, backupDirectory)
                        && HardLinkHelper.TryCreateHardLink(backup, source))
                    {
                        items.Add((source, backup));
                    }
                }
            }
            catch
            {
                return (backupDirectory, items);
            }

            return (backupDirectory, items);
        }

        private void RestoreKeepInPlaceSourcesAfterRepair(
            (string Directory, List<(string Original, string Backup)> Items) protection)
        {
            foreach ((string original, string backup) in protection.Items)
            {
                try
                {
                    if (!File.Exists(original)
                        && File.Exists(backup)
                        && HardLinkHelper.TryCreateHardLink(original, backup))
                    {
                        AppendLog(
                            "WARN",
                            $"{Path.GetFileName(original)}：源包档=留下，但规范化改名把源名改走了 —— "
                            + "已按原路径硬链接恢复（源字节不动）");
                    }
                }
                catch
                {
                    // 恢复不了就保持原样，不拿坏链接冒充恢复。
                }
            }
        }

        private void CleanupKeepInPlaceSourceProtection(
            (string Directory, List<(string Original, string Backup)> Items) protection)
        {
            if (string.IsNullOrWhiteSpace(protection.Directory))
            {
                return;
            }

            try
            {
                if (Directory.Exists(protection.Directory))
                {
                    Directory.Delete(protection.Directory, recursive: true);
                }
            }
            catch
            {
                // 零字节链接清不掉不影响结论。
            }
        }

        private bool TryGatherVolumesBeforeExtract(ArchiveTask task, out string detail)
        {
            detail = string.Empty;

            try
            {
                string current = task.CurrentPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
                {
                    return false;
                }

                /*
                 * 起点 = 引擎这一趟要打开的那一份（分卷组只有"第 1 卷"这一个入口）。
                 * 手上是后续卷（用户只把 `.002` 拖进来）时，先在**找卷窗口**里找同一组的第 1 卷 ——
                 * 找得到就以它为入口（候选池 = 它的那一层），找不到就退回自己
                 * （`ResolveCrossLayerVolumeGather` 会按池子里的第 1 卷另判，判不出就一个字节都不动）。
                 */
                string entryPath = VolumeGatherStep.ResolveGatherEntryPath(current);

                /*
                 * 窗口的基准 = **手上那一卷**（口径 1）。⛔ 它只"看"：候选还要过下面那三道闸门
                 * （同族同基名 / 卷号一份对一份 / 整组自洽 + 7z 起始头字节数证据）。
                 *
                 * ⚠ 2026-10-12（用户口径 2）：「这个第一大步完成之后就是扫描所有剩余的，我之前说过的什么
                 * "父目录+自己这一层+所有子目录"在这个新框架下可能是不成立的」⇒ 候选来源**并上本批
                 * 所有剩下的路径各自所在目录**（`SnapshotTaskTable(Tasks)` 就是"这一批导入的 + 剩下的"）。
                 * ⛔ 放宽的只是"去哪儿找"：三道闸门与"整组完整才允许搬/删"一个字都没动。
                 */
                /*
                 * ⚠ 2026-10-16（真机样本 `10101010` 探针逐字）：候选来源原来只有**各单的 CurrentPath**，
                 * 而分卷的片是**别的单解出来的产物** —— 它们落在那一单的 `OutputPath` / 内容目录 /
                 * 其余物目录里，往往还在子目录中 ⇒ 窗口够不到 ⇒ 一整组的片永远"只有手上这一份"。
                 *
                 * 现场逐字（批末窗口）：入口 `pack111-L2.7z.001` 那一趟的候选是
                 * `pack111-L2.7z(26214400) ; pack111-L2.7z.001(26214400) ; pack111-W0-0.7z ; pack111-W1-0.zip ; pack111-L2.7z(10490213)`
                 * —— **`.002` 载荷（`…\pack111(2)\pack111-W1-0\pack111-L2.7z.002`）压根不在里面**，
                 * 因为它在 `pack111-W1-0.zip` 那一单的**产出目录的子目录**里。
                 *
                 * ⇒ 把"产出层"也并进候选来源（与用户 2026-10-12 口径 2 同一条：
                 * 「这个第一大步完成之后就是扫描所有剩余的」）。⛔ 放宽的只是**去哪儿找**，
                 * 下面那三道闸门与"整组完整才允许搬/删"一个字都没动。
                 */
                IReadOnlyList<VolumeCandidate> window = VolumeNameRepair.EnumerateVolumeCandidatesNearby(
                    current,
                    SnapshotTaskTable(Tasks)
                        .Where(candidate => candidate != null)
                        .SelectMany(candidate => new[]
                        {
                            candidate!.CurrentPath,
                            candidate.OutputPath,
                            candidate.ContentDirectoryPath,
                            candidate.RestDirectoryPath
                        })
                        .Concat(CollectOriginalSourceFiles(Tasks))
                        .Where(path => !string.IsNullOrWhiteSpace(path)));

                VolumeNameRepair.CrossLayerVolumeGather decision =
                    VolumeNameRepair.ResolveCrossLayerVolumeGather(current, null, window);

                if (!decision.Applicable)
                {
                    /*
                     * 收不成时**先说清缺的是哪一头**（2026-10-05 真机第八批）：**跨盘 ZIP 那一族的头尾是反的** ——
                     * 它的第 1 片叫 `111.z01`，而 `.zip` 是**末片**（7-Zip 就是拿 `.zip` 当入口的）。
                     * 按名字编号说话时"缺 index 1"其实等于"缺 `.zip`" ⇒ 对它必须说「缺的是末片」，
                     * ⛔ 不许说成「缺的是第 1 卷」：真机上那三片的第一片就躺在隔壁文件夹里，
                     * 用户会拿着"缺第 1 卷"这句去找一个根本不缺的东西。
                     *
                     * 判据只看一件事：手上这一份与 `<基名>.zip` **是不是同一族同基名**
                     * （既有出口 `BelongsToSameGroup`；⛔ 不新造族判据）。
                     */
                    if (string.Equals(entryPath, current, StringComparison.OrdinalIgnoreCase)
                        && VolumeGroupDetector.TryGetVolumeIndex(Path.GetFileName(current)) != 1
                        && task.IsVolumeGroup)
                    {
                        string noFirstBaseName = FileNameHelper.GetArchiveBaseName(current);
                        bool spannedZipFamily = noFirstBaseName.Length > 0
                            && VolumeGroupDetector.BelongsToSameGroup(
                                Path.GetFileName(current),
                                noFirstBaseName + ".zip");

                        AppendLog(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                spannedZipFamily
                                    ? StatusText.VolumeDeficitNoTailNearbyFormat
                                    : StatusText.VolumeDeficitNoFirstVolumeNearbyFormat,
                                task.FileName));
                    }

                    /*
                     * 「这一组该怎么收」判不出来的那一档也把**判据输入**写出来（用户 2026-10-10 目标单③）：
                     * 上面那几行只说"判不出该怎么收"，而"窗口看了哪些目录、里面到底有几个同族候选、
                     * 都叫什么"才是下一轮排查要的东西 —— 真机 FFFF 那批就是卡在"附近没有第 1 卷"这一句上，
                     * 看不出是窗口没够到那几个目录，还是够到了但尺子不认。
                     * ⛔ 只列事实（候选来自 `EnumerateVolumeCandidatesNearby`，与判据同一份输入），⛔ 不改判据。
                     *
                     * ⚠ 措辞必须说清这是**收进来的池子**、不是"同组的成员"（2026-10-11 真机日志订正）：
                     * 三道闸门（同族同基名 / 卷号一份对一份 / 整组自洽）都在池子**之后**才过，
                     * 老措辞「同组候选」把 `111.rar`（RarOld 族）与 `111(2)_.zip`（另一个基名）也印成同组
                     * ⇒ 拿这份日志排查的人会顺着一个**假的同组名单**去查（文案唯一出口
                     * `StatusText.CriterionInputsGatherWindowFormat` 已同步改写）。
                     */
                    string[] sample = window
                        .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Path))
                        .Select(candidate => Path.GetFileName(candidate.Path))
                        .Take(8)
                        .ToArray();

                    AppendLog(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.CriterionInputsGatherWindowFormat,
                            task.FileName,
                            Path.GetDirectoryName(current) ?? string.Empty,
                            window.Count,
                            sample.Length == 0 ? "（一个都没有）" : string.Join("；", sample))
                        // 闸门那一句也带上（2026-10-12：候选找齐了却"判不出"时，日志必须说清是哪一道闸门）。
                        + (string.IsNullOrWhiteSpace(decision.Detail) ? string.Empty : "｜闸门：" + decision.Detail));

                    return false;
                }

                /*
                 * 收拢的目标那一份 = 这一组的**第 1 卷**（`EntryPath`，唯一出口给出的）。
                 * 手上是后续卷时，下面 `TryApply` 会把那一片搬进第 1 卷所在层 ⇒ 起点必须跟着换过去，
                 * ⛔ 不能让这一单拿着一个已经被搬走（或被改成规范名）的旧路径往下走。
                 */
                if (!string.IsNullOrWhiteSpace(decision.EntryPath) && File.Exists(decision.EntryPath))
                {
                    entryPath = decision.EntryPath!;
                }

                if (decision.Plan is { CanRepair: true })
                {
                    int count = decision.MovedCount;
                    (string Directory, List<(string Original, string Backup)> Items) protection =
                        ProtectKeepInPlaceSourcesBeforeRepair(decision.EntryPath);
                    VolumeNameRepairResult applied = VolumeNameRepair.TryApply(decision.Plan);

                    if (applied.Success)
                    {
                        RestoreKeepInPlaceSourcesAfterRepair(protection);
                    }

                    CleanupKeepInPlaceSourceProtection(protection);

                    if (!applied.Success)
                    {
                        // 全成或全不成（TryApply 自己倒序回滚）：盘上仍是"没收"那一档，如实写。
                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.VolumeGatherBeforeExtractBlockedFormat,
                                task.FileName,
                                applied.Message));

                        return false;
                    }

                    string anchorNote = DescribeGatherAnchor(decision, entryPath);

                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.VolumeGatherBeforeExtractDoneFormat,
                            task.FileName,
                            count,
                            Path.GetFileName(entryPath),
                            decision.Detail)
                        + anchorNote);

                    /*
                     * 任务表里指着这几卷的路径**一起**改过来（唯一出口）；它会顺手重拍快照
                     * （不变量 11 的口径一个字没放宽：变的只是"谁的名字 / 在哪一层"）。
                     */
                    RememberGatheredUserSourcePieces(decision.Plan.Items);

                    SyncTasksAfterVolumeRename(decision.Plan.Items, task);

                    // 起点必须是这一组的第 1 卷（用户只把 `.002` 拖进来时，要解的是旁边那个 `.001`）。
                    //
                    // ⚠ 只在这一单**不是跟班卷**时才改起点：第 1 卷已经被**同批另一个任务**负责时，
                    // 改过去会让两单指着同一个文件（那一组的活该由首卷那一单干，这一单按既有口径
                    // 落成「跟班卷」并跳过 —— 见 SkipWhenAnotherTaskOwnsThisVolumeGroup）。
                    if (!string.Equals(
                            SafePathHelper.GetFullPathSafe(task.CurrentPath),
                            SafePathHelper.GetFullPathSafe(entryPath),
                            StringComparison.OrdinalIgnoreCase)
                        && !IsFirstVolumeOwnedByAnotherTask(task, entryPath))
                    {
                        AppendLog(
                            "INFO",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.VolumeGatherEntryRepointLogFormat,
                                task.FileName,
                                Path.GetFileName(entryPath)));

                        // CurrentPath 的 setter 会顺手刷新文件名 / 目录 / 后缀 / 体积。
                        task.CurrentPath = entryPath;
                        task.CaptureSourceSnapshot();

                        /*
                         * ⛔ 列表那一行**仍旧显示"用户自己那个文件"**（真机 CCCC 2026-10-06，
                         * 用户原话：「48MB 在列表里面显示到了 111(4)……这个你到目前还没改回来」）。
                         * 起点是**内部细节**（本方法上面那行日志"起点由 X 改为它"已经写清），
                         * ⛔ 不许把这一行的名称 / 大小整行变成那一份过程物；⛔ CurrentPath 一个字不动。
                         */
                        task.ShowUserFileIdentity(entryPath, FindUserOwnFileBesideItsDirectory(task, entryPath));
                    }

                    /*
                     * 收完**重新读一次盘上事实**（⛔ 不拿"刚刚搬成功了"当结论）——
                     * 这一组现在到底齐不齐，只能由盘上那几份回答。
                     */
                    VolumeNameRepair.CrossLayerVolumeGather after =
                        VolumeNameRepair.ResolveCrossLayerVolumeGather(
                            entryPath,
                            null,
                            VolumeNameRepair.EnumerateVolumeCandidatesNearby(entryPath));

                    detail = after.Detail;
                    return after.CompleteBesideEntry;
                }

                if (!decision.CompleteBesideEntry)
                {
                    detail = decision.Detail;
                }

                return decision.CompleteBesideEntry;
            }
            catch (Exception ex)
            {
                // 这一档是**加法**：判不出来一律退回"照旧让引擎去判"，⛔ 绝不把本来能解的包挡下来。
                AppendLog("WARN", $"{task.LogName}：开工前跨目录收卷跳过（{ex.Message}）。");
                return false;
            }
        }


        /// <summary>
        /// **任务表里有几份"某一组的第 1 卷"**（诊断用，只读）：判据 = 卡上 `BodyKind == VolumeFirstPiece`
        /// （§29.8 的权威，⛔ 不看首卷标志）。用途见接片那一档的判据输入（§29.24：目标层与锚只能按
        /// "这一片所属的那一组"算，这个数字判"上层交下来的够不够用"）。
        /// </summary>
        private int CountFirstVolumeTasksInTable()
        {
            int count = 0;

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                string path = task?.CurrentPath ?? string.Empty;

                if (path.Length > 0
                    && File.Exists(path)
                    && FileNature.Inspect(path).BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// **这一组的锚（第 1 卷卡）该从哪拿** —— 两处，按可信度排：
        ///
        /// <list type="number">
        /// <item><description>**这一单自己那一份**（<paramref name="owner"/> 的 `CurrentPath`）：批末重判里
        /// "这一组该拼到哪一层"就是**按这一单**算出来的，所以它手上那份就是这一组的入口。</description></item>
        /// <item><description>**目标层里那一份**（<see cref="ResolveGroupFirstVolumeCard"/>）。</description></item>
        /// </list>
        ///
        /// <para>为什么必须有第 ① 处（真机 `19191919` 探针逐字）：那一刻
        /// `目标层=有 目录在=是 层内文件数=2 **层内第1卷=无**`（入口还没被链接进那一层），
        /// 而同一份探针里 `片=aaa-L2 卡号=1 卡类=VolumeFirstPiece` ⇒ 锚其实**就在这一单自己手上**；
        /// 只盯目标层 ⇒ 锚=无 ⇒ 规范名推不出来（`[规范名·进] … 锚=无`）⇒ 那一片永远接不进这一组。</para>
        ///
        /// <para>⛔ 两处都拿不到 ⇒ null（判不出 ⇒ 什么都不做）。</para>
        /// </summary>
        private static FileNatureCard? ResolveGroupAnchorCard(
            ArchiveTask? owner,
            string targetDir,
            string baseName,
            string? groupEntryPath = null)
        {
            /* ① **外层交下来的入口**最可信：收拢那一趟的成员名单里就有它（探针逐字见上面那段）。 */
            if (!string.IsNullOrWhiteSpace(groupEntryPath) && File.Exists(groupEntryPath))
            {
                FileNatureCard groupEntryCard = FileNature.Inspect(groupEntryPath);
                if (groupEntryCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    return groupEntryCard;
                }
            }

            string ownerPath = owner?.CurrentPath ?? string.Empty;

            if (ownerPath.Length > 0)
            {
                FileNatureCard ownerCard = FileNature.Inspect(ownerPath);

                /*
                 * ⛔ **权威是 `BodyKind`，不是 `FirstVolumeFlag`** —— §29.8 那条写死的教训，
                 * 本轮又实测一次（探针逐字）：`[取锚·owner] 单=aaa-L2.part1.rar 卡类=VolumeFirstPiece
                 * **首卷位=False** raw=0` ⇒ 锚本来就在这一单手上，被那个标志挡掉了。
                 * （真机 `4444` 的入口当年也是同一形状：`VolumeFirstPiece` + `First=False`。）
                 */
                if (ownerCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                {
                    return ownerCard;
                }
            }

            return ResolveGroupFirstVolumeCard(targetDir, baseName);
        }

        /// <summary>
        /// **这一组的第 1 卷卡**（RAR 1.5–4.x 锚定基数、以及"本族入口形状"的样本都要用它；
        /// 用户 2026-10-16 拍板）。
        ///
        /// <para>先在<paramref name="targetDir"/> **这一层**里找与这一片**同基名**、卡上自报"我就是第 1 卷"
        /// （<see cref="FileNatureCard.FirstVolumeFlag"/>）的那一份；同基名一个都没有时（真机 `19191919`：
        /// 这一片的名字被改烂成 `aaa-L2-976`，基名本身就是垃圾 ⇒ 按基名永远找不到入口），
        /// 退回"**这一层里唯一的那个自报第 1 卷的片**" —— ⛔ 必须**恰好一份**才采信，
        /// 两份以上 ⇒ 判不出（⛔ 不猜）。⛔ 只扫一层、⛔ 一个字节都不动、⛔ 找不到 ⇒ null。</para>
        /// </summary>
        private static FileNatureCard? ResolveGroupFirstVolumeCard(string targetDir, string baseName)
        {
            if (targetDir.Length == 0 || !Directory.Exists(targetDir))
            {
                return null;
            }

            FileNatureCard? uniqueFirstVolume = null;
            int firstVolumeCount = 0;

            foreach (string candidate in Directory.EnumerateFiles(targetDir))
            {
                FileNatureCard card = FileNature.Inspect(candidate);

                // ⛔ 权威同样是 `BodyKind`（见 `ResolveGroupAnchorCard` 那条实测教训），⛔ 不要求首卷标志。
                if (card.BodyKind != ArchiveBodyKind.VolumeFirstPiece)
                {
                    continue;
                }

                if (baseName.Length > 0
                    && VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetFileName(candidate), baseName))
                {
                    return card;   // 同基名的那一份最可信
                }

                firstVolumeCount++;
                uniqueFirstVolume = card;
            }

            return firstVolumeCount == 1 ? uniqueFirstVolume : null;
        }

        /// <summary>
        /// **这一份的"槽位号"该信谁**（专属算法的入口判据，唯一出口）。
        ///
        /// <para>次序（用户 2026-10-14：「**专属算法不就是靠识别的吗**」）：**先问底层识别卡，卡上没有才问名字**：
        /// ① 卡上有卷号（RAR5 主头 / RAR4 新编号 / 跨盘 ZIP 末片写着第几盘）⇒ 用卡上的；
        /// ② 卡上写着 <see cref="ArchiveBodyKind.VolumeFirstPiece"/>（起始头自述的整包比这一份大）⇒ **1**；
        /// ③ 卡上写着 <see cref="ArchiveBodyKind.VolumePiece"/>（"我是这一组的一片、**但不是第一片**"）
        ///    而**名字读出来是 1** ⇒ 那个 1 是假的（卷标记被伪装吃掉之后，名字那把尺子会退回"1"）
        ///    ⇒ **判不出**（null），交给"缺口 + 体积"（末片最小）去定 —— ⛔ 不许拿假锚点去补片；
        /// ④ 卡上什么都没有（7z 的中间片之类）⇒ 回落到名字那把尺子（老口径，⛔ 不放宽）。</para>
        ///
        /// <para>现场（官方环台 `10101010`，卡逐字）：那一组三片分别叫 `pack111-L2.7z`、`pack111-L2.7z.002`、
        /// `pack111-L2.7z` —— 第 1 片与第 3 片的**名字一模一样**（名字尺子都读成 1）⇒ 旧口径把第 3 片也摆在
        /// 1 号槽 ⇒ 组永远凑不齐；而卡上写得清清楚楚：第 1 片 `专属算法=VolumeFirstPiece`、
        /// 第 3 片 `专属算法=VolumePiece`（体积 26 214 400 vs 10 490 213，末片最小）。</para>
        /// </summary>
        private static int? ResolveVolumeSlotIndexForPiece(string path, FileNatureCard card)
        {
            /*
             * ⚠ 2026-10-14：这套顺序（**卡 → 内容 → 名字**）已经收成一个唯一出口 ——
             * <see cref="VolumePieceFacts.ResolveSlotIndex"/>（用户口径：「把现在的专属算法顺序给我弄好了」）。
             * ⛔ 别处不许再自己拼一遍。
             */
            return VolumePieceFacts.ResolveSlotIndex(path, card);
        }

        /// <summary>
        /// 这一组的第 1 卷是不是**同批另一个任务**在负责（判据与
        /// <see cref="SkipWhenAnotherTaskOwnsThisVolumeGroup"/> 同一个口径：任务表里有没有别人指着它）。
        /// 是 ⇒ 这一单不许把起点改过去（那一组只解一次，别的卷按「跟班卷」跳过）。
        /// </summary>
        private bool IsFirstVolumeOwnedByAnotherTask(ArchiveTask task, string firstVolumePath)
        {
            string first = SafePathHelper.GetFullPathSafe(firstVolumePath);

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null || ReferenceEquals(candidate, task))
                {
                    continue;
                }

                if (string.Equals(
                        SafePathHelper.GetFullPathSafe(candidate.CurrentPath),
                        first,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>
        /// **底层识别卡**说"这一份是某一分卷组的一片"（用户 2026-10-17：「定稿分类读卡」——
        /// 分卷片不再当内容物）。⛔ 不看后缀、⛔ 不猜：判据唯一出口
        /// <see cref="VolumePieceFacts.CardSaysItIsOneOfTheGroup"/>（`BodyKind` = 首片 / 后续片）。
        /// </summary>
        private static bool CardSaysItIsOneOfTheGroup(string path)
        {
            try
            {
                return VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(path));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 找卷的基准那一句（只在**基准不是入口那一卷**时补出来）：用户口径是"以一开始的分卷文件为准，
        /// 不要以 001 为准" —— 这一行就是它落地的证据（窗口的父文件夹是**基准**那一层的父文件夹）。
        /// ⛔ 只写目录名，不写完整路径（§8）。
        /// </summary>
        private static string DescribeGatherAnchor(
            VolumeNameRepair.CrossLayerVolumeGather decision,
            string entryPath)
        {
            string? anchor = decision.AnchorPath;

            if (string.IsNullOrWhiteSpace(anchor)
                || string.Equals(
                    SafePathHelper.GetFullPathSafe(anchor),
                    SafePathHelper.GetFullPathSafe(entryPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "。" + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeGatherAnchorFormat,
                Path.GetFileName(anchor),
                Path.GetFileName(Path.GetDirectoryName(anchor) ?? string.Empty));
        }

        // ================================================================
        // 「缺卷不许在批首一次判死」（用户 2026-10-05 口径 2）
        // ================================================================

        /// <summary>
        /// 这一单**刚发布出来的成品**里，有没有"某一组还缺的那一片" —— 有就按规范卷名接到那一组的目录里。
        ///
        /// <para>挂点 = <see cref="PostProcessSuccessAsync"/> 的收尾（校验通过之后、清理工作区之前）⇒
        /// **单层路与轮次续解那条路自动都在内**（它们每一轮都走这一个收尾）。⛔ 判据与执行体与递归路
        /// 是同一份 <see cref="TryAdoptUnresolvedVolumePiece"/> —— 这里只负责"挑出像分卷片的文件"。</para>
        ///
        /// <para>⛔ 只扫这一层（不递归）；⛔ 一个字节都不复制、不改名、不覆盖：`TryAdoptUnresolvedVolumePiece`
        /// 只在"那一组的目录里还没有这个规范卷名 + 同一个盘"时才建一个零字节硬链接，
        /// 所以那一组已经齐的时候这一档是**空操作**。</para>
        /// </summary>
        private void AdoptPublishedVolumePieces(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            /*
             * 两处都要看（真机 CCCC 单层路实测）：
             * · **成品那一层**（`ContentDirectoryPath`）—— 内容物里的片；
             * · **过程物那一层**（`RestDirectoryPath`）—— 单层路把"内层包"当过程物放进其余物，
             *   而这一组的片**恰恰就是内层包**（`111.zip` 就是在那儿被下一轮当新任务又解了一次）。
             * 这些片由 `TryAdoptUnresolvedVolumePiece` 按"过程物目录里的用移动"那一条接走。
             */
            foreach (string directory in new[] { task.ContentDirectoryPath, task.RestDirectoryPath })
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        string name = Path.GetFileName(file);

                        /*
                         * ⛔ **用户导入的原包自己 —— 这一档一个都不接**（2026-10-11 调试器 + 环台定案）。
                         *
                         * <para><b>运行期事实（`batch_evaluate` 打在 `TryAdoptUnresolvedVolumePiece` 的日志行上，
                         * 逐字）</b>：`piecePath=…\in\111\111\111\其余物\111.rar`、`canonical=111.rar`、
                         * `owner.FileName=111.part1.rar`、`layerOverride=null`、`renamedInPlace=true`
                         * —— 即：**用户那份源包**（先按「源包 → 其余物」搬进过程物目录）被这条扫描当成"像分卷片"，
                         * 又按"过程物目录里的用移动"搬进了产出层 `…\in\111\111\111`。它的字节级身份 = 与
                         * `H:\…\测试\EEE\111\111.rar` 的 SHA-256 完全相同（`472D3B50…BE7D8`）。
                         * 后果：它已经不在其余物里 ⇒ 其余物那一档再也删不到它 ⇒ 判据 ③ 永远剩这一个
                         * （它**不是**这一组的片：老式族 `111.rar` 与本层那一组的新式/ZIP 族只是同基名）。</para>
                         *
                         * <para>⛔ 这一条**只收窄这一档**：用户的原包**按用户选的那一档处理**（其余物 / 删除），
                         * 不在这里被改名、更不许被搬走。真正需要把一个用户的原包接进某一组时，走的是
                         * <see cref="TryGatherGroupPiecesInto"/>（它自带"族 + 基名"那把尺子，且只在批末
                         * "这一组该拼到哪一层"算出来之后才跑）—— 那一条路一个字没动。</para>
                         */
                        if (IsUserImportedSourcePath(file))
                        {
                            continue;
                        }

                        /*
                         * 只挑"像分卷片"的：名字带卷标记（`.z01` / `.001` / `part1` …），
                         * 或者它自己自述是跨盘 ZIP 的末片（末片的 EOCD 是明文 ⇒ 这一条是**硬证据**）。
                         *
                         * ⛔ **再加一道底层识别**（用户口径：「统筹到同一组上面去…先看看底层能够识别到文件是什么东西」）：
                         * 走 <see cref="FileNature.Inspect"/> 那张卡 —— 内容**可证"不是分卷组的一员"** 就**不接**
                         * （第 125 轮那个真凶就是"名字像分卷片"的单卷包；那道红线原来只挡"用户的原包"这一档，
                         * 这一道把它**推广到任何来源**的同一形状，包括我们自己产出的单卷包）。
                         */
                        FileNatureCard nature = FileNature.Inspect(file);

                        bool plausible = nature.ParticipatesInVolumeHandling
                            && (nature.VolumeIndexByName != null
                                || nature.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                                || Detection.SpannedZipIndex.TryRead(file) != null);

                        if (!plausible)
                        {
                            continue;
                        }

                        TryAdoptUnresolvedVolumePiece(file, task);
                    }
                }
                catch (Exception ex)
                {
                    // 加法：判不出来一律退回"照旧"（这一片就留在原地当内容物 / 过程物）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }
        }

        /// <summary>
        /// 把一片从**我们自己的过程物目录**挪进这一组的目录（同盘改名、零字节；失败 ⇒ false 由调用方退回硬链接）。
        /// ⛔ 只用于过程物：用户目录里的片从这条路走不进来。
        /// </summary>
        private static bool TryMovePieceIntoGroup(string piecePath, string target)
        {
            try
            {
                File.Move(piecePath, target);
                return File.Exists(target);
            }
            catch
            {
                // 挪不动（被占用 / 目标刚出现）⇒ 交给调用方退回"建硬链接"那一档。
                return false;
            }
        }

        /// <summary>
        /// **跨链收卷**（2026-10-05 真机第九批 CCCC；用户口径：「现在找不到的先跳过，等全部结束之后再看看」
        /// 「让批末那一站去查本批各单已经解出来的片」）。
        ///
        /// <para><b>现场</b>：一组跨盘 ZIP 四片分在四户人家 —— <c>.z02</c>/<c>.z03</c> 在用户源目录、
        /// 第 1 片 <c>111.z01</c> 压在 <c>111(2)_.zip</c> 里、末片 <c>111.zip</c> 压在 <c>111.rar</c> 里。
        /// 两条链各自把**对方缺的那一片解了出来**，可两条链自己都没走完（互相等对方）⇒ 收尾时工作区整份删掉
        /// ⇒ 批末那一站到盘上一看：缺的东西刚刚被自己扔掉（真机 22:06:02 与 22:06:12 那两行「已清理工作区」）。</para>
        ///
        /// <para>做法只有一件事：把这一趟**打不开的那几片**按**规范卷名**硬链接到"这一组还缺卷"的那一单
        /// 自己所在的目录里（真机 = <c>111(4)\</c>，与那两片源卷同一个目录），随后**转调既有出口**
        /// <see cref="VolumeNameRepair.ResolveSpannedZipDiskGather"/> 把这一组其余的片（散在兄弟目录里的）
        /// 也按规范名接过来 —— 判据（末片自述 / 一片对一片 / 除末片外等大 / 同盘）一条都没另写。</para>
        ///
        /// <para>⛔ 只是**硬链接**（零字节）：不改名、不搬、不覆盖、不删用户的任何文件；跨盘 / 判不出 /
        /// 建不了 ⇒ 什么都不做（⛔ 绝不复制大文件）。这几片按用户 2026-10-05 的拍板算**源包**：
        /// 这一组真解开之后，它们跟着一起进其余物、按删除档处理。</para>
        /// </summary>
        /// <param name="publishedPieces">
        /// <see cref="PublishUnresolvedVolumePieces"/> 刚落地的那几份（**落地之后的路径**）。
        /// ⛔ 不许拿 <see cref="RecursionResult.UnresolvedVolumePieces"/> 里那份原路径代替 ——
        /// 落地是移动，原路径已经不存在了。
        /// </param>
        private void AdoptUnresolvedVolumePieces(
            RecursionResult? recursion,
            ArchiveTask? owner,
            IReadOnlyList<string>? publishedPieces = null)
        {
            var pieces = new List<string>();

            if (publishedPieces != null)
            {
                pieces.AddRange(publishedPieces);
            }

            if (recursion != null)
            {
                pieces.AddRange(recursion.UnresolvedVolumePieces);
            }

            if (pieces.Count == 0)
            {
                return;
            }

            foreach (string piece in pieces)
            {
                try
                {
                    TryAdoptUnresolvedVolumePiece(piece, owner);
                }
                catch (Exception ex)
                {
                    // 加法：判不出来一律退回"照旧"（这一片跟着工作区一起消失，与改动前一个字都不差）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }
        }

        /// <summary>
        /// 记下"**这一单**吐出了「<paramref name="baseName"/>」这一组的一片"（去重；⛔ 只记账，不动盘上任何东西）。
        /// 整组由别单解开之后由 <see cref="CollectConsumedSourcePiecesIntoRest"/> 让它按跟班卷收场。
        /// </summary>
        /// <summary>
        /// **这一片是从哪一单自己的源片库里来的** ⇒ 把**那一单**记成"这一组的一片由它供给"
        /// （唯一出口 <see cref="RememberGroupPieceProducer"/>，⛔ 记的是**任务身份**）。
        ///
        /// <para><b>真机 CCCC 2026-10-06 22:50</b>（用户原话：「`111(4)\111.z03` 留着干什么」）：
        /// 那一单永远收不了场 —— 批末是**按路径 / 文件名**回查它"这一片借给谁了"，
        /// 而它的起点已被改写到入口包上、`OriginalPath` 还停在改名前的脏名上
        /// ⇒ 判不出 ⇒ 红线「什么都不做」⇒ 用户的源片一直留着。
        /// 按身份记进既有那份账之后，批末由 <see cref="SettleGroupPieceProducers"/> **按引用**收场，
        /// ⛔ 再也不用事后反推（路径会过期、文件会被删，身份不会）。</para>
        ///
        /// <para>⛔ 只认"账上某条路径 = 这一片"或"与这一片是**同一份文件**"（唯一判据
        /// <see cref="FileIdentity"/>）；⛔ 判不出就什么都不记（照旧走老路，行为与改动前一致）。</para>
        /// </summary>
        private void RememberPieceSupplyingTask(string baseName, string piecePath)
        {
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(piecePath))
            {
                return;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                foreach (string path in EnumerateTaskPaths(candidate).Concat(EnumerateOwnPaths(candidate)))
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    if (SafePathHelper.PathEquals(path, piecePath) || FileIdentity.IsSamePhysicalFile(path, piecePath))
                    {
                        RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(piecePath), candidate);
                        return;
                    }
                }
            }
        }

        private void RememberGroupPieceProducer(string baseName, string pieceName, ArchiveTask? owner)
        {
            if (owner == null || baseName.Length == 0)
            {
                return;
            }

            // 并发写：见 `_groupPieceProducersGate` 的说明（这里就是那个栈顶）。
            lock (_groupPieceProducersGate)
            {
                if (!_groupPieceProducers.TryGetValue(baseName, out List<(ArchiveTask Task, string PieceName)>? producers))
                {
                    producers = new List<(ArchiveTask Task, string PieceName)>();
                    _groupPieceProducers[baseName] = producers;
                }

                if (!producers.Any(item => ReferenceEquals(item.Task, owner)))
                {
                    producers.Add((owner, pieceName));
                }
            }
        }

        /// <summary>「谁吐出过哪一组的片」这本账的**键快照**（并发安全；见 <c>_groupPieceProducersGate</c>）。</summary>
        private List<string> SnapshotGroupPieceProducerBaseNames()
        {
            lock (_groupPieceProducersGate)
            {
                return _groupPieceProducers.Keys.ToList();
            }
        }

        /// <summary>
        /// 某一组"吐出过片的那几单"的**快照**（拿不到 ⇒ false）。
        /// ⛔ 绝不把活列表交出去：调用方拿到的是副本。
        /// </summary>
        private bool TrySnapshotGroupPieceProducers(
            string baseName,
            out List<(ArchiveTask Task, string PieceName)> producers)
        {
            lock (_groupPieceProducersGate)
            {
                if (_groupPieceProducers.TryGetValue(baseName, out List<(ArchiveTask Task, string PieceName)>? live)
                    && live != null)
                {
                    producers = live.ToList();
                    return true;
                }
            }

            producers = new List<(ArchiveTask Task, string PieceName)>();
            return false;
        }

        /// <summary>整本账的**逐组快照**（收场那一档遍历用；并发安全）。</summary>
        private List<List<(ArchiveTask Task, string PieceName)>> SnapshotGroupPieceProducerLists()
        {
            lock (_groupPieceProducersGate)
            {
                return _groupPieceProducers.Values.Select(list => list.ToList()).ToList();
            }
        }

        /// <summary>这本账现在有几组（并发安全）。</summary>
        private int GroupPieceProducerCount
        {
            get
            {
                lock (_groupPieceProducersGate)
                {
                    return _groupPieceProducers.Count;
                }
            }
        }

        /// <summary>批首/批末清账（并发安全）。</summary>
        private void ClearGroupPieceProducers()
        {
            lock (_groupPieceProducersGate)
            {
                _groupPieceProducers.Clear();
            }
        }

        /// <summary>
        /// 记下"**产出方那一份已经落地的片**被接进了这一组"（唯一写入点；读点是整组收场时的
        /// <see cref="PurgeGroupLandedPieces"/>）。⛔ 只记账，不动盘上任何东西。
        ///
        /// <para><b>⛔ 产出方必须按"盘上事实"认，不能按调用方给的那个 <paramref name="owner"/></b>
        /// （真机 2026-10-10 20:09 那次真跑逮到）：`TryGatherGroupPiecesInto`（`:10624` 调）在第二圈
        /// 扫"别人那两棵树"时，把**被重判的那一单**当 owner 传下去（`:9764`），而那一单的
        /// <see cref="ArchiveTask.OutputPath"/> 那一刻还是空的 —— 按 owner 判就会把这一片判成
        /// "不在产出方的落点树里" ⇒ 一个字都不记 ⇒ 收场时没得删，`111(3)\111(3)\111.z02`
        /// 200 MiB 原样留在盘上（用户 20:09 那次复跑就是这么留着的）。
        /// ⇒ 判据改成：**这一片落在哪一单的落点树里**（先看调用方给的那一单，不是它就在全表里按
        /// <see cref="ArchiveTask.OutputPath"/> 找那一单）—— 谁产的按盘上事实认，与谁在调无关。</para>
        ///
        /// <para><b>其余判据也全是运行期事实</b>（2026-10-10 调试器在 <c>TryAdoptUnresolvedVolumePiece</c>
        /// 接片点逐次读出来的）：</para>
        /// <list type="number">
        /// <item><description><paramref name="renamedInPlace"/> == false —— 接片**没有**把原位置那一份搬走；
        /// == true 时（搬的是工作区里那一份）改记**产出方落点里那一份"落地副本"**
        /// （见方法体里那段注释；两者都没有 ⇒ 什么都不记）；</description></item>
        /// <item><description>认得出**是哪一单**的落点树里那一份（认不出 ⇒ 一个字都不记）；</description></item>
        /// <item><description>⛔ 不在**过程物目录**里（<see cref="ProcessArtifactLayout.IsInsideDeletableProcessFolders"/>）
        /// —— 那一档是"整份会被删掉"的地方（工作区 / 其余物），不归这条账管
        /// （调试器实测第三形状：`…\111\111\111\其余物\111.z01` 被接片重新接回组层，它必须被排除）；</description></item>
        /// <item><description>⛔ 不是**任何一单**的源包路径（<see cref="ArchiveTask.CurrentPath"/> /
        /// <see cref="ArchiveTask.OriginalPath"/> / <see cref="ArchiveTask.VolumePaths"/>）—— 红线兜底：
        /// **用户的源文件一个字节都不许被这条账碰到**（25 GB 事故那一档）；</description></item>
        /// <item><description>组基名非空；同一 (组, 路径) 只记一次。</description></item>
        /// </list>
        /// </summary>
        private void RememberAdoptedLandedPiece(
            string piecePath,
            ArchiveTask? owner,
            string groupBaseName,
            bool renamedInPlace)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(groupBaseName))
            {
                return;
            }

            /*
             * ⚠ **「搬」那一档也算产出方那一份**（2026-10-11 实测补上，第 ③ 条断言打红现场）：
             *
             * 原来这里对 `renamedInPlace == true` 直接 return，理由是"搬走那一档原位置已经没有了"——
             * 工作区里那一份确实没了，**但产出方落点里还留着"落地那一份"**（定稿发布时补的那个同名副本，
             * 与它同一份字节的第二个名字）。于是那一个名字谁都不要：
             * `PurgeGroupLandedPieces` 账上没有它 ⇒ 一整批跑完它还在盘上。
             * 实测残留逐字：`…\out-0530d2770fb145bc85e282068d695a9c\111(2)_\111.z01`
             * （守门用例 `SiblingFolderVolumeGatherTests.真机形状_过路层那一单的其余物按档删掉_48MB那处`
             * 第 ③ 条断言「盘上收口：只剩成品本身」钉的就是它；HEAD 之所以绿，是当年那条"先到先得"兜底
             * 让定稿那一档压根没补这个副本 —— 兜底已按用户口径删除 ⇒ 这里必须自己认领）。
             *
             * ⛔ 判不出产出方 / 落点里没有那一个名字 ⇒ 与改动前逐字相同（什么都不记）。
             */
            if (renamedInPlace)
            {
                string landedCopy = FindLandedCopyBesideProducer(piecePath, owner);

                if (landedCopy.Length == 0)
                {
                    return;
                }

                piecePath = landedCopy;
            }

            if (ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath))
            {
                return;
            }

            ArchiveTask? producer = ResolveLandedPieceProducer(piecePath, owner);

            if (producer == null)
            {
                // 判不出这一片是哪一单产出的（谁的落点树里都没有它）⇒ 什么都不记。
                return;
            }

            string outputRoot = SafePathHelper.GetFullPathSafe(producer.OutputPath);

            if (outputRoot.Length == 0)
            {
                outputRoot = SafePathHelper.GetFullPathSafe(producer.ContentDirectoryPath);
            }

            if (outputRoot.Length == 0 || !ArchivePathGuard.IsInsideRoot(outputRoot, piecePath, out _))
            {
                return;
            }

            if (IsAnyTaskSourcePath(piecePath))
            {
                // 这一份还是**某一单自己的源**（用户给的包 / 用户目录里的片）⇒ 不归这条账管。
                return;
            }

            foreach ((ArchiveTask Producer, string GroupBaseName, string PiecePath, string OutputRoot) item
                     in _adoptedLandedPieces)
            {
                if (string.Equals(item.GroupBaseName, groupBaseName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.PiecePath, piecePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _adoptedLandedPieces.Add((producer, groupBaseName, piecePath, outputRoot));
        }

        /// <summary>
        /// **这一片在产出方落点里那一份「落地副本」**（唯一出口；⛔ 只回答"盘上有没有这么一个名字"，
        /// 一个字节都不搬、不删）：与 <paramref name="piecePath"/> **同名、不是同一个路径**、
        /// 且就在**产出方落点根**（<see cref="ResolveOwnerChainDirectory"/>）底下。
        ///
        /// <para>为什么要问它：接片走"移动"那一档，动的是**工作区里那一份**；而定稿发布补出来的
        /// "落地那一份"还在产出方落点里 —— 那同样是"产出方那一份已经落地的片"，收场时得有人认领它
        /// （见 <see cref="RememberAdoptedLandedPiece"/>）。判不出 ⇒ 空串（调用方什么都不记）。</para>
        /// </summary>
        private string FindLandedCopyBesideProducer(string piecePath, ArchiveTask? owner)
        {
            ArchiveTask? producer = ResolveLandedPieceProducer(piecePath, owner);

            if (producer == null)
            {
                return string.Empty;
            }

            string landing = ResolveOwnerChainDirectory(producer);
            string name = FileNameHelper.GetFileName(piecePath);

            if (landing.Length == 0 || name.Length == 0)
            {
                return string.Empty;
            }

            string candidate = Path.Combine(landing, name);

            return File.Exists(candidate) && !SafePathHelper.PathEquals(candidate, piecePath)
                ? candidate
                : string.Empty;
        }

        /// <summary>
        /// **这个路径是不是任何一单自己的源**（用户给的包 / 用户目录里的片）—— 唯一出口。
        ///
        /// <para>红线：**用户的源文件一个字节都不许被"产出方那一份 / 已经落地的片"这两条账碰到**
        /// （25 GB 事故那一档）。所以凡是要按这两条账删/认领一个路径之前，先问这一句；
        /// 判"是源" ⇒ 调用方什么都不做。</para>
        /// </summary>
        private bool IsAnyTaskSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                foreach (string sourcePath in EnumerateTaskPaths(task))
                {
                    if (!string.IsNullOrWhiteSpace(sourcePath) && SafePathHelper.PathEquals(sourcePath, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// **这个路径是不是某一单的「用户最初导入路径」**（只读 `OriginalPath`，⛔ 不看 `VolumePaths` /
        /// `CurrentPath`）。给"按试开顺序收掉旧规范名"用：旧规范名通常是组任务 `VolumePaths` 里的占位名，
        /// 但**不是**用户手里的那份原包；只有 `OriginalPath` 才是用户原件。
        /// </summary>
        private bool IsAnyTaskOriginalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task != null
                    && !string.IsNullOrWhiteSpace(task.OriginalPath)
                    && SafePathHelper.PathEquals(task.OriginalPath, path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **同一个物理文件是不是某一单用户最初导入的那一份**（改名/硬链接后仍认得出）。
        /// `28282828` 的源 `.001` 在盘上已经被清洗成标准名，路径与 `OriginalPath` 不同，
        /// 但 inode 相同 ⇒ 逐层回收必须按身份保护它。
        /// </summary>
        private bool IsAnyTaskOriginalIdentity(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task != null
                    && !string.IsNullOrWhiteSpace(task.OriginalPath)
                    && File.Exists(task.OriginalPath)
                    && FileIdentity.IsSamePhysicalFile(task.OriginalPath, path))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// **这个路径是不是「用户导入的原包」自己** —— 第二大步收卷那一档的**窄版**红线
        /// （⛔ 与上面那把宽尺子并存，⛔ 宽尺子的任何调用点一个字都不改）。
        ///
        /// <para><b>为什么这一档不能用宽尺子</b>（2026-10-11 调试器取到的运行期事实）：
        /// 宽尺子问"是不是任何一单的路径"，而第二大步要收的**恰恰是这一组自己的那几片** ——
        /// 它们正是入口那一单（续解任务）自己的路径。断点停在
        /// `HarvestOneSolvedGroupVolumes` 的删除循环里，EEE 第 2 轮边界那一趟逐字：
        /// `directory=…\in\111\111\111`、`referenceName=111.parts1.racr`、`members.Count=2`
        /// （`…\111\111\111\111.part1.rar` / `111.part2.rar`）、`consumer=111.part1.rar`、
        /// `mode=Delete`，而求值 `this.IsAnyTaskSourcePath(path)` = **true** ⇒ 两份都被那句
        /// `continue` 跳过（静默）⇒ 盘上一个字节没少（判据 ③ 的 `还有 5 个` 就是它）。</para>
        ///
        /// <para>判据取既有事实位 <see cref="ArchiveTask.IsContinuationTask"/> —— 项目自己的定义逐字：
        /// 「续解任务的"源"是上一层交出来的内层包 —— 对用户来说它是过程物」
        /// （同一句话也是 `ProcessArtifactLayout` 里"续解出来的内层包不参与源包处理"那条的唯一出口）。
        /// ⇒ **非续解任务的那几单，它们的路径就是用户给的原包**（含同组被收进
        /// <see cref="ArchiveTask.VolumePaths"/> 的那几卷）一个字节不碰；续解任务自己的路径
        /// **不是**用户的原包 ⇒ 不在这道红线的保护范围内。</para>
        /// </summary>
        private bool IsUserImportedSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null || task.IsContinuationTask)
                {
                    continue;
                }

                foreach (string sourcePath in EnumerateTaskPaths(task))
                {
                    if (!string.IsNullOrWhiteSpace(sourcePath) && SafePathHelper.PathEquals(sourcePath, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// **这一片落在哪一单的落点树里**（唯一出口；⛔ 与"谁在调"无关）：
        /// 先看调用方给的那一单，不是它就在全表里按 <see cref="ArchiveTask.OutputPath"/> /
        /// <see cref="ArchiveTask.ContentDirectoryPath"/> 找 —— 判不出 ⇒ <c>null</c>
        /// （调用方一个字都不记）。
        /// </summary>
        private ArchiveTask? ResolveLandedPieceProducer(string piecePath, ArchiveTask? owner)
        {
            if (IsInsideOwnLandingTree(owner, piecePath))
            {
                return owner;
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (!ReferenceEquals(candidate, owner) && IsInsideOwnLandingTree(candidate, piecePath))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>这一份在不在这一单**自己那条链的落点树**里（输出目录，或内容物那一层）。</summary>
        private static bool IsInsideOwnLandingTree(ArchiveTask? task, string piecePath)
        {
            if (task == null || string.IsNullOrWhiteSpace(piecePath))
            {
                return false;
            }

            foreach (string root in new[] { task.OutputPath, task.ContentDirectoryPath })
            {
                if (!string.IsNullOrWhiteSpace(root)
                    && ArchivePathGuard.IsInsideRoot(SafePathHelper.GetFullPathSafe(root), piecePath, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 盘上某个文件**改了名（同盘移动）**之后，把**全表每一单**账上指向它的路径都改过来。
        ///
        /// <para>⛔ 为什么是全表而不是"改产出它的那一单"：**同一份字节会被多处账引用**
        /// （真机 CCCC：入口包 `111.zip` 那一片同时被"吐出它的那一单"与"正等这一组的另一单"记着）。
        /// 只改一处 ⇒ 别的任务再去按旧名字找 / 解就落空 —— 上一轮"只同步 owner"那一版就是这样
        /// 把真机形状两条 E2E 打红的（收片之后整组凑不齐 / 产物找不到）。</para>
        ///
        /// <para>内部仍只转调**唯一出口** <see cref="TaskPathSync.ApplySingleMove"/>（它同时改
        /// <c>CurrentPath</c> 与 <c>VolumePaths</c>，AGENTS.md §38 那条踩过两次的坑）。</para>
        /// </summary>
        private void SyncEveryTaskPathAfterMove(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return;
            }

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                TaskPathSync.ApplySingleMove(task, from, to);

                /*
                 * ⛔ **快照也要重拍**（2026-10-06 第三处候选，实测确认）：任务的源文件快照
                 * （不变量 11 用它对"源文件有没有被改过"）是按**当时的路径**记的。
                 * 就地改名之后账上的路径改了、快照还指着旧名 ⇒ 下一次比对会判「源文件已变化」
                 * ⇒ 整组被拦下、产物出不来（真机形状 E2E 的 `Assert.NotNull()` 就是这样红的）。
                 * 改名不是"源文件被改动"（内容一个字节没动，AGENTS.md 不变量 11 的边界里写着
                 * "改名 / 源包搬进其余物不算变化"）⇒ 这里按新路径重拍一次，让账与盘一致。
                 */
                if (SafePathHelper.PathEquals(task.CurrentPath, to)
                    || task.VolumePaths.Any(path => SafePathHelper.PathEquals(path, to)))
                {
                    task.CaptureSourceSnapshot();
                }
            }

            /*
             * ⚠ **账上的键也要跟着搬**（2026-10-06 实测：这是"第三处读旧名"的候选之一）：
             * `_consumedVolumeSources` 的**键就是那一份源片的路径**（`RememberOneConsumedVolumeSource`
             * 与 `RememberGroupPiecesConsumedBy` 两处写入）。源片就地改名之后键还指着旧名字 ⇒
             * 批末重判再问"这一单那一片在不在借片账里"必然问不出来 ⇒ 整组凑不齐、产物出不来
             * （真机形状 E2E 的 `Assert.NotNull()` 就是这样红的）。
             * ⇒ 这里把键一起重映射（值 = 消费方名字，不动）。
             */
            if (_consumedVolumeSources.Count > 0)
            {
                string? consumer = null;

                foreach ((string path, string owner) in _consumedVolumeSources.ToList())
                {
                    if (SafePathHelper.PathEquals(path, from))
                    {
                        consumer = owner;
                        _consumedVolumeSources.Remove(path);
                    }
                }

                if (consumer != null)
                {
                    _consumedVolumeSources[to] = consumer;
                }
            }
        }

        /// <summary>
        /// 「判不出 ⇒ 什么都不做」时把**判据输入**写进日志（用户 2026-10-10 目标单③）。
        ///
        /// <para>为什么必须有它：这些分支原先只有**结论**（「接不进去」），没有**判据吃进去的东西** ——
        /// 拿一份用户日志看不出"这一片被判成哪一族、第几卷"，也看不出"规范名/目标层算成了什么"。
        /// 真机 FFFF 那一轮就是卡在这里：只知道没接上，不知道是名字认不出还是入口层推不出来。</para>
        ///
        /// <para>⛔ 只写程序当场算出来的值（判不出就写「判不出」），⛔ 它一处判据都不改。</para>
        /// </summary>
        /// <summary>
        /// **接片没接上**这句话本身（唯一出口）—— 与 `IsTargetSlotAlreadyThisPiece` **同一把尺子**。
        ///
        /// <para>为什么单独收一个出口（用户 2026-10-12，大测试 `run-8888` 逐字）：
        /// 那一行写着 `目标名已经被别的文件占着（⛔ 绝不覆盖）`，而**目标名就是这一片自己的路径**
        /// —— 老写法只用 `File.Exists(target)` 判，于是把"这一片早就在位（或它已经被接过一次、
        /// 原位置那份已经不在了）"说成了"被别人占着"，下一轮排查顺着这句去找一个根本不存在的"别人"。</para>
        ///
        /// <para>三种事实分开说（⛔ 判据与行为一个字不改，只把话说对）：
        /// ① 目标位上就是**这一片自己** ⇒ 那根本不该走到"没接上"（`alreadyInPlace` 那一支已经先接走）；
        /// ② 这一片**已经不在原位置**（被前一次接片搬走/改名）⇒ 判不出目标位上那份是谁的，照实说；
        /// ③ 其余（目标位上确实站着**别人**的文件）⇒ 照旧"绝不覆盖"。</para>
        /// </summary>
        private static string DescribeAdoptRejection(string piecePath, string target)
        {
            if (!File.Exists(target))
            {
                return "硬链接 / 同盘改名都没成功（同名已在、或跨了卷）";
            }

            if (IsTargetSlotAlreadyThisPiece(piecePath, target))
            {
                return "目标位上就是这一片自己（应当按「已经在位」接走）";
            }

            return File.Exists(piecePath)
                ? "目标名已经被别的文件占着（⛔ 绝不覆盖）"
                : "这一片已经不在原来的位置了（判不出目标位上那份与它是不是同一份，⛔ 绝不覆盖）";
        }

        private void LogAdoptCriterionInputs(
            string piecePath,
            string reason,
            string canonical,
            string targetDir,
            string target)
        {
            string name = FileNameHelper.GetFileName(piecePath);
            string baseName = FileNameHelper.GetArchiveBaseName(piecePath);
            string? first = VolumeGroupDetector.TryGetFirstVolumeName(name);
            int? index = VolumeGroupDetector.TryGetVolumeIndex(name);

            /*
             * ⚠ **同名位上到底是什么**：必须写出来（2026-10-11 环台实测逮到的假拒绝）——
             * 同一秒的日志里出现过自相矛盾的两句：这句说「目标名已经被别的文件占着（⛔ 绝不覆盖）」，
             * 而同一单的定稿那一句说「已经在目标位上、而且是「同一份文件」（接片时建的硬链接）」。
             * 光看"被占了"分不出是**别人**的文件（该拒）还是**自己这一片**早就链接在那儿了（该当成功），
             * 下一步无从下手 ⇒ 这里直接把物理同一性写进判据输入（⛔ 只写事实，不改判据）。
             */
            string sameNameSlot = target.Length == 0
                ? "（没有目标名可看）"
                : !File.Exists(target)
                    ? "没有同名文件"
                    : FileIdentity.IsSamePhysicalFile(piecePath, target)
                        ? "同一份文件（就是这一片自己，只是名字已经在位）"
                        : "另一个文件（⛔ 绝不覆盖）";

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsAdoptFormat,
                    name,
                    reason,
                    name,
                    baseName,
                    string.IsNullOrWhiteSpace(first) ? "判不出" : first,
                    index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "判不出",
                    canonical.Length == 0 ? "判不出" : canonical,
                    targetDir.Length == 0 ? "判不出（推不出这一组该拼到哪一层）" : targetDir,
                    target.Length == 0 ? "判不出" : target,
                    sameNameSlot));
        }

        /// <summary>收**一片**：按规范卷名接到"这一组还缺卷"那一单的目录里；末片在手时顺手把整组接齐。</summary>
        private bool TryAdoptUnresolvedVolumePiece(
            string piecePath,
            ArchiveTask? owner,
            string? layerOverride = null,
            string? groupEntryPathOverride = null,
            bool allowMoveIntoGroup = true,
            IReadOnlyDictionary<string, int>? contentSlots = null,
            VolumeNamingFamily? namingFamily = null,
            IReadOnlyDictionary<int, string>? slotCanonicalTargets = null)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || !File.Exists(piecePath))
            {
                return false;
            }

            /*
             * ===== ⛔ 内容证据优先：**可证"不是分卷组的一员" ⇒ 这一档一个字都不接** =====
             *
             * <para>用户 2026-10-11 口径：「判断一个文件，**绝对不能只看基名和后缀**，绝对还有其他的办法」
             * +「统筹到同一组上面去…我们就是先看看**底层能够识别到文件是什么东西**」。
             * 判据**不再写在这里** —— 转调底层那一个出口 <see cref="FileNature.Inspect"/> 那张卡上的
             * <see cref="FileNatureCard.ParticipatesInVolumeHandling"/>（内容说"不是分卷成员" ⇒ 不参与这一套动作）。</para>
             *
             * <para>为什么按名字判会出事：**一个归档本体自己就是它那一族的第 1 卷**
             * （`VolumeGroupDetector.TryGetVolumeIndex("111.rar")` = 1）⇒ 用户那份普普通通的单卷 `111.rar`
             * 也会被判成"像分卷片"（第 125 轮真凶就是这么把它接进别组的）。</para>
             *
             * <para><b>真样本实测（`DebugRunner volinfo` 档，只读）</b>：EEE `111\111.rar`（用户的原包）
             * ⇒ `NotVolumeMember`；EEE `111(4)\111.z11111111110删除3`（跨盘 ZIP 中间片，没有 EOCD）
             * ⇒ `NotAnArchive`（**判不出**）；FFFF `1114__\111_.part4.raAAAAAr`（真 RAR 分卷的一片）
             * ⇒ `IsVolumeMember = True`。⛔ 所以这里只对**可证不是**的那一档动作，判不出 ⇒ 照旧按名字。</para>
             */
            FileNatureCard nature = FileNature.Inspect(piecePath);

            if (!nature.ParticipatesInVolumeHandling)
            {
                AppendLog(
                    "WARN",
                    $"{FileNameHelper.GetFileName(piecePath)}：不接进这一组 —— 底层识别：{nature.Describe()}");

                CriterionLog(
                    "接片：这一片参不参与分卷那一套",
                    nature.Describe(),
                    "不参与（内容可证不是分卷组的一员）",
                    "不接、不改名、不搬、不建链接",
                    piecePath,
                    nature.HasIdentity
                        ? nature.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture) + ":" + nature.FileIndex.ToString("X", CultureInfo.InvariantCulture)
                        : string.Empty,
                    "WARN");

                return false;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(piecePath);

            if (baseName.Length == 0)
            {
                LogAdoptCriterionInputs(piecePath, StatusText.VolumeRepairNotAVolumeName, string.Empty, string.Empty, string.Empty);
                return false;
            }

            /*
             * ⚠ 2026-10-16：目标层原来在下面才算，可**这一组第 1 卷卡**（RAR4 锚定基数要用它）得先拿到 ——
             * 而第 1 卷就在"这一片要落的那一层"里（框架的口径：收片层 = 入口层 = 落点层）。
             * ⇒ 这里提前算一次，下面那一段原样复用（⛔ 只算一次，不重算）。
             */
            string targetDirEarly = string.IsNullOrWhiteSpace(layerOverride)
                ? ResolveBatchGroupDirectory(baseName, null, piecePath)
                : layerOverride!;

            /*
             * 规范卷名 = **这一片按它自己那一族**写成的名字（唯一出口 `VolumeGroupResolver.TryGetCanonicalVolumeName`）。
             *
             * ⛔ 这里原来写死成"跨盘 ZIP 的盘名"：`盘序 = 卷号 - 1` ⇒ 盘序 0 给 `.zip`、其余给 `.z0N`。
             * RAR 新式分卷的片一进这条路就被改成 `111_.z01` / `111_.z02` …（真机 FFFF 与守门夹具的日志里
             * 逐字可见「已按规范卷名 111_.z01 接到…」）—— **族被改掉了**：入口那一单还在找
             * `111_.part1.rar`，盘上却只有 `111_.z01` ⇒ 整组永远凑不齐 ⇒ 四片各自去解 ⇒ 全批没一个成功。
             * 判不出族 / 卷号 ⇒ 什么都不做（与老口径那两条早退逐字一致）。
             */
            /*
             * ===== 内容定的槽位 + 内容定的命名族：**名字一个字节都不参与命名** =====
             * （2026-10-18 困难批 `19191919` 现场；见 `TryGatherGroupPiecesInto` 里 `contentSlots` 的说明）
             */
            int? contentSlotForNaming = null;

            if (contentSlots != null
                && namingFamily.HasValue
                && contentSlots.TryGetValue(VolumeSlotKey(piecePath), out int slotByContent)
                && slotByContent >= 1)
            {
                contentSlotForNaming = slotByContent;
            }

            if (contentSlotForNaming == null
                && contentSlots != null
                && contentSlots.Count > 0
                && namingFamily.HasValue)
            {
                AppendLog(
                    "WARN",
                    $"{FileNameHelper.GetFileName(piecePath)}：内容槽位没命中（身份={VolumeSlotKey(piecePath)}"
                    + $"｜槽位键={string.Join("、", contentSlots.Keys)}）");
            }

            string canonical = string.Empty;

            bool nameGaveCanonical = contentSlotForNaming == null
                && VolumeGroupResolver.TryGetCanonicalVolumeName(Path.GetFileName(piecePath), out canonical);

            /*
             * ⚠ 2026-10-16（第 111 轮，困难批 `24242424` 运行期实测）：**名字出口给得出答案也不算数**的那一格。
             *
             * <para>现场逐字：这一片叫 `111-L2.part020745382.rar`（伪装法把卷标记那一截并进了基名）⇒
             * 名字出口**回它自己**（`TryGetFirstVolumeName` 也回它自己：那把尺子压根没认出卷标记、
             * 按"本体"读成第 1 卷）⇒ 下面那条"问卡"的路**一次都没进过** ⇒ 它被按自己的名字落进
             * 「111」这一层，整组永远收不齐 ⇒ 判据 ① 红。而**卡上有真话**：
             * `内容=VolumeMember 卷号=2 专属算法=VolumePiece`。</para>
             *
             * <para>⇒ 判据走唯一出口 <see cref="VolumePieceFacts.NameOnlyGuessedItselfAsFirstVolume"/>
             * （名字是猜的 + 内容是"第 N≥2 片"；⛔ 跨盘 ZIP 本体 `*.zip` 不算猜 —— 那个 1 是那一族的入口锚点，
             * 翻它会打红 `13131313`/`23232323`，今天 g93/g94 对照实测）。⛔ 判不出 ⇒ 与老口径逐字相同。</para>
             */
            if (!nameGaveCanonical
                || VolumePieceFacts.NameOnlyGuessedItselfAsFirstVolume(FileNameHelper.GetFileName(piecePath), nature))
            {
                /*
                 * ⚠ 2026-10-14（官方环台 `10101010`，日志逐字）：**名字判不出族时，问卡**。
                 *
                 * <para>现场：这一片叫 `pack111-L2.7z.txt`（伪装法⑤"改成别的类型"）⇒ 规范名出口认不出族
                 * ⇒ 逐字 `这一片接不进这一组 —— 认不出它属于哪一族 / 第几卷（规范卷名推不出来）`
                 * ⇒ 它永远接不进去；而**卡上全都有**（探针逐字）：`格式=SevenZip 分卷身份=Unknown
                 * 专属算法=VolumeFirstPiece 自述整包=62919013 按内容算的片数=3`。</para>
                 *
                 * <para>⇒ 卡优先的回落（用户口径「专属算法不就是靠识别的吗」）：族取卡上的格式、
                 * 卷号取卡上的（见 <see cref="ResolveVolumeSlotIndexForPiece"/>）、基名把伪装尾巴剥掉。
                 * ⛔ 卡上也给不出卷号（`VolumePiece` 那种"我是后续片但没写第几"）⇒ 照旧返回 false，
                 * 交给"缺口 + 体积"那一档，⛔ 不猜号。</para>
                 */
                /*
                 * ⚠ 2026-10-16（真机 `19191919` 的整条判据链探针逐字，`af-probe-d.txt`）：
                 * `[规范名·进] 片=aaa-L2-976 槽位=2 样本=null **锚=无**` ⇒ 槽位（卡上那张号）是对的，
                 * 卡在**锚**上；而同一时刻目标层里**没有**第 1 卷（另一条探针逐字：
                 * `目标层=有 目录在=是 层内文件数=2 层内第1卷=无`）—— 入口还没被链接进那一层。
                 * ⇒ 取锚**先看这一单自己那一份**（批末重判里"这一组该拼到哪一层"就是按这一单算出来的，
                 * 所以它手上那份就是这一组的入口：同一份探针里 `片=aaa-L2 卡号=1 卡类=VolumeFirstPiece`），
                 * 拿不到再扫目标层（原口径）。
                 */
                FileNatureCard? groupAnchor = ResolveGroupAnchorCard(owner, targetDirEarly, baseName, groupEntryPathOverride);

                canonical = VolumeNameOrchestration.ResolveCanonicalNameFromCard(
                    piecePath,
                    nature,
                    groupAnchor,
                    contentSlotForNaming,
                    namingFamily);

                if (canonical.Length == 0)
                {
                    LogAdoptCriterionInputs(
                        piecePath,
                        /*
                         * ⚠ 2026-10-16（用户口径与项目规矩："判不出的分支把**判据输入**写进日志"）：
                         * 这一行原来只说"推不出来"，**锚有没有、层是哪来的**一个字都没有 ——
                         * 而真机 `29292929` 卡住的地方恰恰就是这两件（纯函数实测：只要把锚交进去，
                         * 规范名立刻是 `pack111-L2.part2.rar`）。⇒ 永久把这三个字段写进来，⛔ 不改判据。
                         */
                        "认不出它属于哪一族 / 第几卷（规范卷名推不出来；卡上也没给出可用的族与卷号）"
                        + $"｜锚={(groupAnchor == null ? "无" : FileNameHelper.GetFileName(groupAnchor.Path))}"
                        + $"｜层={(layerOverride == null ? "自推" : "外层交下来的")}"
                        + $"｜目标层={(targetDirEarly.Length > 0 ? "有" : "空")}"
                        /*
                         * ⚠ 2026-10-16 再补两个数字（按 §29.24 的结论：目标层与锚**只能按"这一片所属的那一组"**算，
                         * 而"属于哪一组"必须从上层交下来）—— 这两个数字用于判"上层交下来的够不够用"：
                         * 任务表里**一份第 1 卷都没有** ⇒ 接片那一档根本没有可借的入口，得让上层把组交下来。
                         */
                        + $"｜这一单={(owner == null || string.IsNullOrEmpty(owner.CurrentPath) ? "无" : FileNameHelper.GetFileName(owner.CurrentPath))}"
                        + $"｜任务表首卷份数={CountFirstVolumeTasksInTable()}",
                        string.Empty,
                        string.Empty,
                        string.Empty);

                    return false;
                }

                AppendLog(
                    "INFO",
                    $"{FileNameHelper.GetFileName(piecePath)}：名字判不出族/卷号，改用底层识别卡 —— "
                    + $"卡上：{nature.Describe()} ⇒ 规范卷名「{canonical}」。");
            }

            /*
             * 目标层 = **入口包所在的那一层**（用户 2026-10-06：「你把分卷移过来不就是了吗」）——
             * 入口包（跨盘 ZIP 族 = 末片 `X.zip`）由 `PublishUnresolvedVolumePieces` 先落在
             * **产出它的那个包自己那条链的落点目录**里，`ResolveBatchGroupDirectory` 读的就是它；
             * 于是"收片层 = 入口层 = 落点层"三处是同一个事实位，⛔ 不再看任务表顺序。
             *
             * 入口包还不在盘上（批首那一刻 / 它没被解出来）⇒ 退回旧口径（任务表里第一单那一层），
             * 那一档行为与改动前逐字相同。
             *
             * ⚠ **一层只准算一次**（2026-10-11 实测，`ChainInnerVolumeRestoreTests.真机布局…` 那条真机形状打红）：
             * 批末那一遍（`TryGatherGroupPiecesInto`）**已经**按入口包把这一组的层算出来了，可它交下来的每一片
             * 原来又**各自重算一遍** —— 而重算这一遍用的是**这一片自己的名字**，真机上那几片的名字是脏的
             * （`111_.part2` / `111_.pasrt3.rcar` / `111_.par删t1.ra除r`）⇒ 撞上"脏名不启用新判据"那道闸门
             * ⇒ 每一片都回一句「判不出」 ⇒ 三片一片都接不进去，整组永远停在缺卷（日志逐字：
             * `批末重判 —— 这一组该拼到哪一层 = …\out\111__.rLLLLar` 紧跟着
             * `111_.part2：这一片接不进这一组 —— 推不出「这一组该拼到哪一层」`）。
             * ⇒ 同一件事的真值**只允许有一个出口**：外层已经定过的层，原样交下来用（`layerOverride`），
             * ⛔ 接片这一档**不再自己重算**。
             */
            string targetDir = targetDirEarly;

            if (targetDir.Length == 0)
            {
                LogAdoptCriterionInputs(
                    piecePath,
                    "推不出「这一组该拼到哪一层」（入口包还没落下来，任务表里也没找到这一组）",
                    canonical,
                    string.Empty,
                    string.Empty);

                return false;
            }

            string target = Path.Combine(targetDir, canonical);
            bool adopted = false;

            if (Directory.Exists(target))
            {
                // 目标名被一个**目录**占着：硬链接建不出来（⛔ 绝不删、绝不覆盖）。
                LogAdoptCriterionInputs(
                    piecePath,
                    "目标名被一个目录占着（⛔ 绝不覆盖、绝不删）",
                    canonical,
                    targetDir,
                    target);

                return false;
            }

            /*
             * ⚠ **目标位上已经有同名文件时先问一句：是不是这一片自己**（2026-10-11 环台实测逮到的假拒绝）。
             *
             * 现场（程序自己的判据输入，同一秒里自相矛盾的两句）：
             *   `111.zip：这一片接不进这一组 —— 目标名已经被别的文件占着（⛔ 绝不覆盖）｜判据输入：… 目标名=「…\111\111\111.zip」，同名位上=同一份文件（就是这一片自己，只是名字已经在位）`
             * 紧接着同一单又打：`111.rar：111.zip 已经在目标位上、而且是「同一份文件」（接片时建的硬链接）—— 不必再搬一次，也不留多余的名字。`
             * ⇒ 那根本不是"被占用"，是**这一片早就链接在位**了。老代码只判 `File.Exists(target)` 就返回 false
             * ⇒ 后面那套记账（`RememberGroupPieceProducer` / `RememberPieceSupplyingTask` /
             * `RememberAdoptedLandedPiece`）**整段被跳过** ⇒ 产出方那一份"已经落地的片"没人收
             * （用户 2026-10-10 点名要收的那一条），而且把一个成功的状态写成 WARN 假警报。
             * ⛔ 判据只读事实：同名位上**是不是同一份物理文件**（`FileIdentity.IsSamePhysicalFile`）；
             * 是**别人**的文件 ⇒ 照旧判"接不进"（⛔ 绝不覆盖）。
             */
            /*
             * ===== 按试开确认的顺序，收掉"旧的规范名" =====
             * 2026-10-18 新批 `24242424`：入口层里 `.z01`/`.z02` 是早先建的硬链接，但内容顺序把另一份中段片
             * 排到槽位 1 ⇒ 目标名被旧链接占着。**只在"占着这个名字的那一份，已经在它自己正确的规范名下"
             * 时才收**（它已有正确名字、字节还在，删的是多余链接）；⛔ 不碰用户 `OriginalPath`；
             * ⛔ 链接数 ≤ 1（唯一一份）一律不动。
             */
            if (File.Exists(target)
                && !IsTargetSlotAlreadyThisPiece(piecePath, target)
                && slotCanonicalTargets != null
                && contentSlots != null
                && contentSlots.TryGetValue(VolumeSlotKey(target), out int occupantSlot)
                && slotCanonicalTargets.TryGetValue(occupantSlot, out string? correctTarget)
                && !string.IsNullOrWhiteSpace(correctTarget)
                && !string.Equals(correctTarget, target, StringComparison.OrdinalIgnoreCase)
                && File.Exists(correctTarget)
                && FileIdentity.IsSamePhysicalFile(correctTarget, target)
                && FileIdentity.TryGetLinkCount(target, out uint targetLinks)
                && targetLinks > 1
                && !IsAnyTaskOriginalPath(target))
            {
                try
                {
                    File.Delete(target);

                    AppendLog(
                        "INFO",
                        $"{FileNameHelper.GetFileName(piecePath)}：目标名 {canonical} 原来是槽位 {occupantSlot} 的旧名字 —— "
                        + $"那一份已经在正确的规范名 {Path.GetFileName(correctTarget)} 下（零字节硬链接）⇒ 收掉旧名字，"
                        + "建这一片的正确链接（一个字节都不丢）。");
                }
                catch (Exception ex)
                {
                    AppendLog(
                        "WARN",
                        $"{FileNameHelper.GetFileName(piecePath)}：旧的规范名 {canonical} 没能收掉（{ex.Message}）");
                }
            }

            bool alreadyInPlace = IsTargetSlotAlreadyThisPiece(piecePath, target);

            if (!File.Exists(target) && HardLinkHelper.CanHardLink(piecePath, targetDir))
            {
                /*
                 * 这一片还在**我们自己的过程物目录**里（`.ArchiveFixer.work` / 其余物）⇒ 用**移动**
                 * （同盘改名、零字节）。为什么不能只建链接：那些目录收尾会整份删掉 / 按删除档处理，
                 * 留一份在里面等于"接了一个马上就要消失的名字"，而且轮次续解下一轮还会把剩下的那一份
                 * 当成一个"内层包"再去解一次（真机上就是那句「111.zip：分卷缺失」，用户最烦看到它）。
                 *
                 * ⛔ **唯一的例外 = 本单自己的暂存目录**（见 `ShouldMovePieceIntoGroup`）：
                 * 那是收尾链（结果校验 / 定稿）要读的那一份，搬走 = 把自己这一单唯一的产物拿走。
                 *
                 * 用户目录里的片一律**只硬链接**：⛔ 名字一个字符都不许改、文件不搬。
                 */
                bool fromProcessFolder = ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath);
                string ownerStage = ResolveOwnerStageDirectory(owner);
                /*
             * ⚠ 2026-10-17（用户口径：「第一大步和第二大步衔接的时候在原来的地方不动…找到完整的才一起搬动到
             * 入口包位置」）：批末那一条路会传 `allowMoveIntoGroup = (决策器说 Gather)`。
             * 决策器说 `Leave`（证据还没齐）⇒ **只建链接、绝不搬原件** —— 链接是实名的第二份名字，
             * 原件仍在它自己那一层（也就是"原来的位置"）。
             */
                bool moveIntoGroup = allowMoveIntoGroup
                    && ShouldMovePieceIntoGroup(piecePath, ownerStage, CollectLiveChainWorkDirectories());

                /*
                 * ⛔ **用户导入的原包自己 ⇒ 只建链接，绝不搬**（项目既有红线的延伸：用户源目录里的片
                 * 「只硬链接、不改名不搬」—— 而那一条原来只管"在用户目录里"的情形，管不到"用户的原包
                 * 被源包档搬进过程物目录之后"这一档）。
                 *
                 * <para><b>运行期事实</b>（`batch_evaluate` 打在接片那一档的日志行上）：EEE 里
                 * `piecePath=…\in\111\111\111\其余物\111.rar` 走的就是"过程物目录 ⇒ 用移动"那一支
                 * （`renamedInPlace=true`）⇒ 用户那份源包被搬出其余物、再也没人按档处理它。</para>
                 *
                 * <para>⛔ 判据取既有事实位（<see cref="IsUserImportedSourcePath"/>：非续解任务的那几单
                 * = 用户给的原包）；⛔ 判不出 ⇒ 照旧老口径（该搬还是搬）。硬链接是**另起一个规范卷名**，
                 * 源文件一个字节没动 —— 与「接片」那一档对用户文件的既有口径完全一致。</para>
                 */
                if (moveIntoGroup && (IsUserImportedSourcePath(piecePath) || IsAnyTaskOriginalPath(piecePath)))
                {
                    moveIntoGroup = false;
                }

                /*
                 * ===== 用户源目录里的那一片：⛔ **仍然只硬链接**（四次试做都撞在同一处，如实记账）=====
                 *
                 * 用户 2026-10-06 拍板：「**去掉暗链当出口**，解压用盘上标准名的文件，临时名只许建在工作区」。
                 * **四次试做都失败、都已撤回**：① 只同步 `owner` ⇒ 红；② 对**全表**同步 ⇒ 红；
                 * ③ 再把**借片账的键**一起重映射 ⇒ 红；④ 再把**源文件快照**按新路径重拍（不变量 11 那一关）⇒ **还是红**。
                 * ⇒ 已排除四处（任务账路径 / 借片账键 / 收卷判据只认规范名 / 源文件快照）；
                 * **第五处仍未找到**。⛔ 没找到它之前不许再改这一行（改一次红一次，四次都白改）。
                 *
                 * ⚠ 2026-10-10：那一行只读取证的 `收片排障：…` 探针（2026-10-09 加的）**已删** ——
                 * 真机复跑把真正的病灶找到了，**不在这条支上**：散在 `111(4)\` 的那一片**根本没走进来**
                 * （`FileNameHelper.GetArchiveBaseName` 认不出被拉长的卷号 ⇒ 目标层算不出来 ⇒ 静默返回），
                 * 而且批末补判那份名单当时是**空的**（中途撞缺卷的单从不登记）。两处都已修，
                 * 与"搬还是链接"这一行的取舍无关 ⇒ 探针的使命结束（见 `修改日志.md` 本轮）。
                 */
                /*
                 * **接片落名判据（永久）**：2026-10-18 调试器实测出现过"canonical 是内容族、真正落链接的
                 * target 却是名字族"的错位（`.zip.003` 对 `.z01`）⇒ 把这三个值一次写清楚，
                 * ⛔ 只写程序当场算出来的值、⛔ 不改任何判据。
                 */
                AppendLog(
                    "INFO",
                    $"{FileNameHelper.GetFileName(piecePath)}：接片落名判据 —— 规范名=「{canonical}」，"
                    + $"目标名=「{Path.GetFileName(target)}」，目标层=「{targetDir}」，目标路径=「{target}」");

                adopted = moveIntoGroup
                    ? TryMovePieceIntoGroup(piecePath, target) || HardLinkHelper.TryCreateHardLink(target, piecePath)
                    : HardLinkHelper.TryCreateHardLink(target, piecePath);

                if (adopted)
                {
                    bool renamedInPlace = !File.Exists(piecePath);

                    if (renamedInPlace)
                    {
                        SyncEveryTaskPathAfterMove(piecePath, target);
                    }

                    /*
                     * 记下"**产出方那一份已经落地的片**被接进了这一组"（用户 2026-10-10 点名的那条不一致）：
                     * 接片是"搬"还是"建链接"取决于这一片那一刻**恰好在哪**（时序），而两档收场后的盘面
                     * 必须一致 —— 整组可证完整 + 校验通过 + 未取消之后，产出方那一份**不留**
                     * （`111.z01` 那一档就是这么收的）。判据与红线见 `RememberAdoptedLandedPiece`。
                     */
                    RememberAdoptedLandedPiece(piecePath, owner, baseName, renamedInPlace);

                    /*
                     * ⚠ **说话要按事实分三种**（旧文案一律写"零字节的硬链接、名字一个字符没改"，
                     * 而过程物那一档走的是移动 —— 那是假话；用户 2026-10-06 正是拿这一点追问的）。
                     * 第三种 = "本单自己的暂存目录里的那一份只建链接"（见 `ShouldMovePieceIntoGroup`）：
                     * 它既不是搬走、也不是"用户的源文件"，照实说。
                     */
                    AppendLog(
                        "INFO",
                        renamedInPlace
                            ? $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 落到「{targetDir}」这一层；"
                              + "它是我们自己解出来的过程物，用的是同盘改名的「移动」，原位置不再留一份）。"
                            : fromProcessFolder
                                ? $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 接到「{targetDir}」这一层；"
                                  + "这一片还在我们自己的工作区暂存目录里，只建了一个零字节的硬链接 —— 一个字节都没动）。"
                                : $"「{baseName}」这一组缺的那一片解出来了（已按规范卷名 {canonical} 接到「{targetDir}」这一层；"
                                  + "零字节的硬链接：源文件一个字节没动、名字也一个字符没改）。");
                }
            }

            else if (alreadyInPlace)
            {
                // 这一片早就在位、而且是同一份文件 ⇒ **算接片成功**（⛔ 一个字节都不搬、不覆盖）。
                adopted = true;

                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PieceAlreadyInPlaceFormat,
                        baseName,
                        canonical,
                        Path.GetFileName(targetDir)));
            }

            if (adopted)
            {
                /*
                 * **这一组拼到哪一层**是一个已经做过的决定（两条支路都算过）⇒ 记下来给别的档读，
                 * ⛔ 不让它们重算一遍（读点见 `_groupLayerByBaseName` 的注释：定稿那一档"不落地"的闸门）。
                 *
                 * ⚠ **只有"拼到了别处那一层"才算**：算出来的层就是这一片自己待着的那一层时（接片是空操作，
                 * 常见于"这一片本身就是入口/这一组只有它"），不存在"已经把这一份拼进去了"这件事
                 * —— 记了它，定稿那一档就会把本该发布的产物当成"多余名"而不落地
                 * （实测打红：`InnerLayerContinuationTests.外层包只有内层包时_内容物落进外层包名目录而不是平铺进共用根`）。
                 */
                if (!SafePathHelper.PathEquals(targetDir, Path.GetDirectoryName(piecePath) ?? string.Empty))
                {
                    _groupLayerByBaseName[baseName] = targetDir;
                }

                /*
                 * 记下"是这一单吐出了这一片"：整组由别单解开、而且**可证完整**之后，这一单也按跟班卷收场
                 * （真机第九批 CCCC：`111.rar` / `111(2)_.zip` 各吐一片，整组解开并校验通过了，
                 * 这两个原包却因为自己"部分完成"一直留在盘上 —— 用户当场就问为什么）。
                 */
                RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(target), owner);

                /*
                 * ⛔ **这一片是从哪一单自己的源片库里来的** ⇒ 把**那一单**也记成"这一组的一片由它供给"
                 * （按**任务身份**，⛔ 不按路径、⛔ 不按文件名）。真机 CCCC 2026-10-06：`111(4)\111.z03`
                 * 那一单永远收不了场 —— 因为批末按路径/文件名回查（它起点被改写到入口包上、最初路径还是
                 * 改名前的脏名），而物理同一性在"组层那个链接已经被删掉"之后也无从判定
                 * ⇒ 红线「判不出 ⇒ 什么都不做」，源片一直留着。按身份记，这一档从此不依赖事后反推。
                 */
                RememberPieceSupplyingTask(baseName, piecePath);
            }
            else
            {
                /*
                 * 接不上那一档也把判据输入写出来（目标单③）：目标名这一刻算成了什么、那一层在哪、
                 * 盘上有没有同名 —— 光看"没接上"三个字，下一轮排查还得从头再跑一遍。
                 */
                LogAdoptCriterionInputs(
                    piecePath,
                    DescribeAdoptRejection(piecePath, target),
                    canonical,
                    targetDir,
                    target);
            }

            // 末片在哪一层，整组就得凑到哪一层（引擎只看入口旁边那一层）⇒ 末片到手就顺手把整组接齐。
            string tail = Path.Combine(targetDir, baseName + ".zip");

            if (!File.Exists(tail))
            {
                return adopted;
            }

            VolumeNameRepair.SpannedZipDiskGather gather = VolumeNameRepair.ResolveSpannedZipDiskGather(
                tail,
                VolumeNameRepair.EnumerateVolumeCandidatesNearby(tail));

            if (gather.Applicable && gather.LinkedCount > 0)
            {
                AppendLog("INFO", $"「{baseName}」这一组又接过来 {gather.LinkedCount} 片：{gather.Detail}");
            }

            if (gather.Applicable && gather.Complete)
            {
                AppendLog(
                    "INFO",
                    $"「{baseName}」这一组现在凑齐了 —— 等这一批的解压都跑完，批末那一站会直接把这一组解开。");
            }

            return adopted || (gather.Applicable && gather.Complete);
        }

        /// <summary>
        /// **用户自己那一份现在在哪**（改名之后也要找得到）：他最初放的那个目录里、
        /// 与起点（这一组的入口包）**同族**、且**包基名与最初导入那一份相同**的那个文件。
        ///
        /// <para>为什么要这么找：`OriginalPath` 是"最初导入路径"（改名不会跟着变，真机实测），
        /// 而账上那几卷可能散在别的目录（别的单的片）⇒ 只有"同目录 + 同族 + 同基名"这一条
        /// 能把"用户自己那一片"认出来（⛔ 只用于**列表显示**，不参与任何搬运 / 删除判据）。</para>
        /// <para>判不出 ⇒ 空串（调用方那一行显示照旧，⛔ 不猜）。</para>
        /// </summary>
        private static string FindUserOwnFileBesideItsDirectory(ArchiveTask task, string startPointPath)
        {
            string original = task.OriginalPath ?? string.Empty;
            string directory = Path.GetDirectoryName(original) ?? string.Empty;

            if (directory.Length == 0 || string.IsNullOrWhiteSpace(startPointPath) || !Directory.Exists(directory))
            {
                return string.Empty;
            }

            string startName = FileNameHelper.GetFileName(startPointPath);
            string originalBaseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(original));

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    string name = FileNameHelper.GetFileName(file);

                    if (string.Equals(name, startName, StringComparison.OrdinalIgnoreCase)
                        || !VolumeGroupDetector.BelongsToSameGroup(name, startName)
                        || !string.Equals(
                            FileNameHelper.GetArchiveBaseName(name),
                            originalBaseName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return file;
                }
            }
            catch
            {
                // 读不动 ⇒ 判不出。
            }

            return string.Empty;
        }

        /// <summary>
        /// **这一片该"搬"还是该"只建链接"** —— 判据只有两条，都是路径事实：
        /// ① 落在**会被整份删掉的过程物目录**里（`.ArchiveFixer.work` / 其余物）⇒ 倾向搬
        ///    （留一份在那种目录里，等于接了一个马上就要消失的名字）；
        /// ② ⛔ **但它正躺在"本单自己的暂存目录"里 ⇒ 只建链接、绝不搬走**：那是收尾链
        ///    （结果校验 / 定稿）要读的那一份，搬走 = 把自己这一单唯一的产物拿走。
        ///
        /// <para><b>真机 CCCC 2026-10-06 18:13</b>（用户原话：「出现了解压失败的文字，明明成功了」）：
        /// 某一单（`111(2)_.zip`）自己解出来的那一片先被"接片"那一档从**它的暂存目录**里搬走，
        /// 收尾链紧接着读暂存目录读到空 ⇒「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示
        /// 「解压失败」+ 源包一个字节都不处理。触发它是一段**竞态**：同一批另一单（`111.rar`）恰好在
        /// 两次接片调用之间把入口包 `111.zip` 落到了落点层 ⇒ 第一次接片还不认识那一层（什么都不做）、
        /// 第二次已经认识 ⇒ 搬走的就是暂存目录里那一份。</para>
        ///
        /// <para>⛔ 这条判据**不看顺序、不看时序**：暂存目录里的那一份永远不搬
        /// （它随任务收尾一起清掉，落点那一份是硬链接、零字节、照样在）。</para>
        ///
        /// <para><b>为什么还要第 ③ 条</b>（同一类竞态的另一半，2026-10-11 定案到行）：收拢那一档
        /// （<c>TryGatherGroupPiecesInto</c> → <c>TryAdoptUnresolvedVolumePiece</c>）传进来的
        /// <paramref name="ownerStageDirectory"/> 是**消费方**（要解开这一组的那一单）的暂存目录，
        /// 而被收的那一片常常属于**另一条还在跑的链**（躺在它自己的
        /// <c>…\recursive\&lt;那一单&gt;_&lt;ts&gt;_&lt;hash&gt;\layer-000\output\</c> 里）。
        /// 按老判据它是"过程物目录里的东西"、又不在**调用方**的暂存目录里 ⇒ 判成"搬"
        /// ⇒ 那条链的定稿/校验读到空 ⇒ **假「解压失败」**（全量并行负载下实测：两单落
        /// <c>Failed</c>、整组凑不齐；同一份夹具单跑 6/6 全绿）。
        /// ⛔ 判不出（一条还没收场的链都拿不到）⇒ 照旧按老口径办。</para>
        /// </summary>
        internal static bool ShouldMovePieceIntoGroup(
            string piecePath,
            string ownerStageDirectory,
            IEnumerable<string>? liveChainWorkDirectories = null)
        {
            if (!ProcessArtifactLayout.IsInsideDeletableProcessFolders(piecePath))
            {
                return false;
            }

            // ② 本单自己的暂存目录：只建链接。
            if (ownerStageDirectory.Length > 0
                && ArchivePathGuard.IsInsideRoot(ownerStageDirectory, piecePath, out _))
            {
                return false;
            }

            /*
             * ③ 别的**还没收场**那条链的工作树：也只建链接。
             * 硬链接是实名的第二份名字 —— 那条链随后删掉自己的过程物目录，不会连带把这一份弄没；
             * 反过来"搬"就是把它这条链唯一的产物拿走（正是上面那条红线的形状）。
             */
            if (liveChainWorkDirectories != null)
            {
                foreach (string directory in liveChainWorkDirectories)
                {
                    if (!string.IsNullOrWhiteSpace(directory)
                        && ArchivePathGuard.IsInsideRoot(directory, piecePath, out _))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// **还没收场的那几条链的工作树**（收拢那一档用来判"这一片是不是别人的在制品"，
        /// 见 <see cref="ShouldMovePieceIntoGroup"/> 第 ③ 条）。
        ///
        /// <para>⛔ 只看**机器终态**：<see cref="TaskOutcome.Pending"/> = 还没落结论（含"正在跑"
        /// 与"停在需要用户决定的层"）—— 那些链的过程物目录里那一片**还不能搬**。
        /// 判不出（算不出工作树 / 拿不到任务表）⇒ 空表，调用方照旧按老口径办，⛔ 不放宽。</para>
        /// </summary>
        private List<string> CollectLiveChainWorkDirectories()
        {
            var directories = new List<string>();

            foreach (ArchiveTask task in SnapshotTaskTable(Tasks))
            {
                if (task == null || task.Outcome != TaskOutcome.Pending)
                {
                    continue;
                }

                try
                {
                    string directory = _pathService.BuildTaskWorkDirectory(task);

                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        directories.Add(directory);
                    }
                }
                catch
                {
                    // 算不出来就不算它（少一条保护，⛔ 不改任何结论）。
                }
            }

            return directories;
        }

        /// <summary>
        /// 目标位上已经有同名文件时：那到底**是不是这一片自己**（同一份物理文件）。
        ///
        /// <para><b>为什么必须有它</b>（2026-10-11 环台实测逮到的假拒绝）：接片那一档原来只判
        /// `File.Exists(target)` 就返回"接不进"，而目标位上那一份**恰恰就是这一片自己**
        /// （早前已经硬链接过去、或它自己就是从那儿来的）——
        /// 于是"接片成功"被写成 WARN 假警报，**更要命的是后面那套记账整段被跳过**
        /// （`RememberGroupPieceProducer` / `RememberPieceSupplyingTask` / `RememberAdoptedLandedPiece`）
        /// ⇒ 产出方那一份"已经落地的片"没人收（用户 2026-10-10 点名要收的那一条）。
        /// 实测对照（同一份样本、同一个环台）：修前 6 条假拒绝 / 0 条"早就在位"；修后 0 条假拒绝 / 11 条"早就在位"。</para>
        ///
        /// <para>判据只读事实：**同一份物理文件**（<see cref="FileIdentity.IsSamePhysicalFile"/>）。
        /// true ⇒ 接片算成功（⛔ 一个字节都不搬、不覆盖）；false ⇒ 别人的文件 ⇒ 照旧判"接不进"。</para>
        /// </summary>
        internal static bool IsTargetSlotAlreadyThisPiece(string piecePath, string target)
            => !string.IsNullOrWhiteSpace(target)
               && File.Exists(target)
               && FileIdentity.IsSamePhysicalFile(piecePath, target);

        /// <summary>本单自己的暂存目录（算不出来 / 判不出 ⇒ 空串 = 调用方落回老口径"按过程物目录那一档"）。</summary>
        private string ResolveOwnerStageDirectory(ArchiveTask? owner)
        {
            if (owner == null)
            {
                return string.Empty;
            }

            try
            {
                return _pathService.BuildTaskStageDirectory(owner);
            }
            catch
            {
                return string.Empty;
            }
        }

        private string ResolveOwnerChainDirectory(ArchiveTask? owner)
        {
            if (owner == null || string.IsNullOrWhiteSpace(owner.CurrentPath))
            {
                return string.Empty;
            }

            /*
             * ⚠ 2026-10-09 真机 `EEEE`（18:48 那一趟）**实测的根因**：这一组的"家"被**已经搬走的源包**带偏了。
             *
             * 现场（逐条有出处）：行 161 先把产出方那一单的源包搬进其余物
             * （`111.rar → …\111\111\其余物\111.rar`）⇒ 下面那两条路**都读 `owner.CurrentPath`**
             * （`ResolveOutputPlacement` 内部读它；退路 `Path.GetDirectoryName(owner.CurrentPath)` 也读它）
             * ⇒ 算出来的"这一组的家"变成 **`…\111\111\其余物\111`** —— 与真机 6 条收片的 `targetDir`
             * **逐字相同**（行 164/171/177/178/186）。片全被收进"其余物"里 ⇒ 组永远凑不齐，
             * 批末「其余物 = 彻底删除」再把它们**连同入口包一起删掉**（行 296）⇒ 盘上 `111\111\` 空掉。
             *
             * ⇒ **先用产出方当初记下的"实际输出目录"**（`owner.OutputPath`）：那是"产物到底在哪"的
             * **唯一权威来源**（写入点 `ExtractSingleTaskAsync` 里 `task.OutputPath = outputPath;`，
             * 那段注释自己写着"解压后校验、结果归集、清理源包、界面那一列、以及一键处理的续解全都以它为准"）。
             * ⛔ 不再用"会动的 `CurrentPath`"现算一遍 —— 源包一被搬走，那个算式就指向其余物。
             */
            if (!string.IsNullOrWhiteSpace(owner.OutputPath))
            {
                return owner.OutputPath;
            }

            try
            {
                OutputPlacementResult placement = _pathService.ResolveOutputPlacement(
                    owner,
                    BuildExtractOptions(tryExtractUnknownFormat: false));

                if (placement.Success && !string.IsNullOrWhiteSpace(placement.DestinationDirectory))
                {
                    return placement.DestinationDirectory;
                }

                /*
                 * ⛔ **这一刻 `task.OutputPath` 往往还没落**（真机 2026-10-06 当场逮到：这一档"搬了 0 份"，
                 * 就是因为这里返回空 ⇒ 目标层算不出来 ⇒ 整档静默不做）。⇒ 按**同一套落点公式现算一份**：
                 * 未指定位置那一档 = `源包所在目录 + 包名文件夹`（`111\111.rar` ⇒ `111\111\`），
                 * 与 <see cref="OutputPlacement.ResolveDestinationDirectory"/> 的公式逐字同口径
                 * （用包基名，⛔ 不是"剥后缀再加一层"的自创写法）。
                 */
                string? ownerDirectory = Path.GetDirectoryName(owner.CurrentPath);

                if (string.IsNullOrWhiteSpace(ownerDirectory))
                {
                    return string.Empty;
                }

                string packageName = FileNameHelper.GetArchiveBaseName(
                    FileNameHelper.GetFileName(owner.CurrentPath));

                return packageName.Length > 0
                    ? Path.Combine(ownerDirectory, packageName)
                    : ownerDirectory;
            }
            catch (Exception ex)
            {
                // 加法：算不出落点 ⇒ 退回既有口径（这一片落在哪儿与改动前一个字都不差）。
                AppendLog("WARN", $"跨链收卷算不出这一片的落点（{ex.Message}）。");
            }

            return string.Empty;
        }

        /// <summary>
        /// **链因「分卷缺失」停下时，把它已经解出来、下一步要用的产物搬回它自己那条链的家**
        /// （用户 2026-10-06 口径：「第一步就让你将能解压的统统解压了」「这个根本就不是部分完成，
        /// 对于 `111.rar` 这个就是成功了，只不过这个内容物又是下一轮解压需要的东西」）。
        ///
        /// <para><b>现场</b>：`111.rar` 第 0 层解出 `111\111.rar`、第 1 层解出 **`111\111.zip`**，
        /// 第 2 层要开 `111.zip` 时缺 `.z01` ⇒ 这一层不开（不试）。而 `111.zip` 是下一轮**那一组跨盘 ZIP
        /// 的入口包** —— 旧口径把它当"没走完的现场"，收尾连工作区一起删掉（真机磁盘上四个文件夹里
        /// **一个 `111.zip` 都没有**，就是这个原因）。⇒ 现在：**认得出是分卷片/入口包的，一律搬到
        /// `owner` 自己那条链的落点目录**（`CCCC\111\111\`），随后的收片与整组解压都在那一层。</para>
        ///
        /// <para>⛔ 动作只有"同卷改名"（我们自己工作区里的文件，零字节代价）；⛔ 用户源目录里的片
        /// 这一档碰都不碰（名字与字节都不动）；⛔ 搬不动（占用 / 目标被目录占着）⇒ 留在原地、
        /// 什么都不做（随后由既有的接片那一档按规范卷名处理）。</para>
        /// </summary>
        private List<string> PublishUnresolvedVolumePieces(RecursionResult? recursion, ArchiveTask? owner)
        {
            var published = new List<string>();

            if (recursion == null || recursion.UnresolvedVolumePieces.Count == 0 || owner == null)
            {
                return published;
            }

            /*
             * ⛔ **搬进"暂存目录"，不是搬进落点目录**（2026-10-06 真机 + 守门用例当场逮到）：
             * 收尾那条链（结果校验 / 定稿 / 源包处理）读的是 `<任务工作区>\stage`，
             * 而落点目录要等**定稿**按落点公式才建。上一版直接搬进落点目录 ⇒ 校验看到的是**空的暂存目录**
             * ⇒「校验未通过：输出目录是空目录，没有产物」⇒ ① 列表显示「解压失败」② 源包一个字节都不处理
             * （真机 `ArchiveFixer-本次操作_20261006_112403.txt` 第 121/123/127 行）。
             *
             * ⇒ 搬进暂存目录之后：校验看得见它、定稿按落点公式把它落到 `111\111\`、源包照常处理。
             */
            string stage = _pathService.BuildTaskStageDirectory(owner);

            if (string.IsNullOrWhiteSpace(stage))
            {
                return published;
            }

            try
            {
                Directory.CreateDirectory(stage);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"{owner.LogName}：暂存目录建不出来（{ex.Message}），按原样留着。");
                return published;
            }

            foreach (string pieceFromResult in recursion.UnresolvedVolumePieces)
            {
                try
                {
                    string piece = pieceFromResult;

                    if (!File.Exists(piece))
                    {
                        /*
                         * ⚠ 原路径经常已经不在（递归核心收尾会清自己的工作区）—— 那一刻它已经把这一片
                         * **抢救复制**到我们的暂存目录里了（`RecursiveExtractor.UnresolvedPieceRescueDirectory`）
                         * ⇒ 这里改用抢救副本，别当成"没东西可搬"（真机 2026-10-06 排障日志逐字：
                         * `那一片已经不在盘上（…\output\111.zip）` ⇒ 搬了 0 份 ⇒ 落地那一层永远没有入口包）。
                         */
                        string rescued = Path.Combine(stage, FileNameHelper.GetFileName(pieceFromResult));

                        if (!File.Exists(rescued))
                        {
                            continue;
                        }

                        piece = rescued;
                    }

                    string name = FileNameHelper.GetFileName(piece);

                    /*
                     * ===== 两个目的地，职责分开（真机 CCCC 2026-10-06 第二次追问后拆开）=====
                     *
                     * ① **暂存目录**（这里就建 / 搬）—— 收尾链（结果校验 / 定稿）读的就是它；
                     *    不放进 ⇒「校验未通过：输出目录是空目录，没有产物」⇒ 列表显示「解压失败」+ 源包不处理。
                     * ② **"这一组该在的那一层" / 产出它的那一单自己的落点** —— 交给
                     *    `AdoptUnresolvedVolumePieces`（接片那一档，唯一出口）去接；
                     *    只有它接不进去（那一层还认不出来 / 被占）时，才由
                     *    `PublishUnresolvedPieceLandingCopies` 在产出它的那一单自己的落点里补一份。
                     *
                     * ⛔ 老写法**在这里就先建一份落点的硬链接**，于是接片成功之后那一份成了没人认领的
                     * 多余名（真机 `111(2)\111(2)_\111.z01`，200 MiB 一直留在盘上 —— 用户第二次追问）。
                     * 顺序必须是"先接、接不进再补"，⛔ 不是"先补、接完不管"。
                     */
                    string target = Path.Combine(stage, name);

                    /*
                     * ⛔ 已经在那一层了就什么都不做（幂等）；目标被占（文件或目录）⇒ 一个字节都不动。
                     * ⚠ 但**"已经在暂存目录里"这一档仍然要进 [收片] 与 [记账]**：它是入口包，
                     * 后面那一站要靠这份账收卷（2026-10-06：抢救副本本来就落在暂存目录 ⇒ 走到这里就 continue
                     * ⇒ 收片那一档与"吐出这一片的那一单"那份账一起被跳过）。
                     */
                    if (File.Exists(target) || Directory.Exists(target) || SafePathHelper.PathEquals(piece, target))
                    {
                        if (File.Exists(target))
                        {
                            /*
                             * ⛔ 这里**不写日志**：真机那一批是**成功**任务，而"成功任务只留一行摘要"是既有红线
                             * （`LogVolumePolicyTests` / `ProgressVisibilityTests` / `Item45LogAndPasswordTests`
                             * 等 6 条守门用例钉着）。"落地那一份真的在"由**用例断言文件**，不靠日志。
                             */
                            RememberGroupPieceProducer(FileNameHelper.GetArchiveBaseName(target), FileNameHelper.GetFileName(target), owner);
                            published.Add(target);
                        }

                        continue;
                    }

                    File.Move(piece, target);

                    if (File.Exists(target))
                    {
                        AppendLog(
                            "INFO",
                            $"{owner.LogName}：这一步解出来的「{name}」留在它自己那条链里（下一轮解压要用的就是它；"
                            + "⛔ 不是失败、也不算残缺）。");

                        RememberGroupPieceProducer(FileNameHelper.GetArchiveBaseName(target), FileNameHelper.GetFileName(target), owner);
                        published.Add(target);
                    }
                }
                catch (Exception ex)
                {
                    // 加法：搬不动 ⇒ 退回"什么都不做"（这一片仍留在工作区，行为与改动前一个字不差）。
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }

            return published;
        }

        /// <summary>
        /// **这一单"属于哪一组"的引用名**：那把「同一组 = 族 + 基名」的尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/>）
        /// 要一个**组内成员的名字**当引用，这个成员就是**这一单自己**。
        ///
        /// <para><b>为什么必须改（真机 FFFF 2026-10-10 夜，定案到行）</b>：这里各处原来一律写死
        /// `<基名>.zip` —— 那等于**假定每一组都是跨盘 ZIP 族**。族一旦不是 <c>ZipSpanned</c>
        /// （`X.partN.rar` 是新式族、`X.7z.001` 是数字族、`X.r00` 是老式族），
        /// `BelongsToSameGroup(别的片, "X.zip")` **恒为 false**（尺子自己写着：`x.rar` 与 `x.part1.rar`
        /// 不同组、族不同就是不同组）⇒ 收拢那一档连"这一组有哪些成员"都收不出来。</para>
        ///
        /// <para>调试器实测（`steps-ffff2.json`，暂停在 `RecheckDeferredVolumeDeficits:10698`）：
        /// 那一批的待判单是 `111_.part4.rar`、`baseName = "111_"` ⇒ 引用名是 `111_.zip`，
        /// 而四片叫 `111_.part1.rar` / `111_.part2` / `111_.part3.rar` / `111_.part4.rar`
        /// ⇒ 一个成员都匹配不上 ⇒ 入口那一层推不出来 ⇒ 整组到最后也没解开
        /// （真机：成功 0 / 失败 3；我这边同一批：成功 0 / 失败 0 / 跳过 3 / 部分完成 3）。
        /// 守门 `ChainInnerVolumeRestoreTests.真机布局_四个外层包各在一个子目录里_四片分处四地_整组照样凑齐并解开`
        /// （**改之前是红的**，逐字红在 `Assert.NotNull() Failure: Value is null`）。</para>
        ///
        /// <para>⛔ **判不出就照旧**：这一单的名字解析不出来时退回老的 `<基名>.zip`
        /// —— 一个字都不放宽，只是"不认得就不认"。⛔ 同一个问题（"哪些文件属于这一组"）
        /// 只许有这一个出口，各处不许再自己拼 `<基名>.zip`。</para>
        /// </summary>
        private static string ResolveGroupReferenceName(ArchiveTask? task, string baseName, string? memberPath = null)
        {
            /*
             * ⚠ **优先拿"手上这一片"的名字**（2026-10-11 订正）：它按定义就在这一组里；而"调用方那一单自己的文件名"
             * 在**接片**那条路上拿不到（调用方给的是 null）⇒ 老写法只能退回 `<基名>.zip`，
             * 可那把尺子只对**跨盘 ZIP 族**成立 —— RAR 新式（`X.partN.rar`）/ 数字族（`X.7z.001`）
             * 用 `X.zip` 去比，`BelongsToSameGroup` 恒 false ⇒ 这一组的成员**一个都收不进来**
             * （真机 FFFF：`111_` 那一组就是这么互相看不见的）。
             *
             * ⛔ 判据与老口径**同一把尺子**（`BelongsToSameGroup` 自比），只是参照物多了一个来源；
             * 名字解析不出来 ⇒ 照旧退回 `<基名>.zip`（一个字都不放宽）。
             */
            string ownPath = !string.IsNullOrWhiteSpace(memberPath)
                ? memberPath!
                : task?.CurrentPath ?? string.Empty;

            string own = FileNameHelper.GetFileName(ownPath);

            /*
             * 拿尺子自比来问"这个名字解析得出来吗"：解析得出来时它必然与自己是同一族同一基名。
             * ⛔ 不新造"能不能解析"的第二把尺子（`Analyze` 是私有的，这里也不该去开它）。
             */
            if (own.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(own, own))
            {
                return own;
            }

            /*
             * ⚠ 2026-10-14（官方环台 `10101010`，日志逐字）：名字**解析不出来**时退回 `<基名>.zip`
             * 那一条等于**假定每一组都是跨盘 ZIP 族** —— 逐字 `这一组的引用名=「pack111-L2.7z.zip」`
             * ⇒ 紧接着 `这一组该拼到哪一层 —— 判不出`（三片都齐了也拼不到一层去）。
             *
             * <para>⇒ 这一格也改成**问卡**（用户口径：「专属算法不就是靠识别的吗」）：卡上说
             * "我是这一组的第一片 / 一片"（`VolumeFirstPiece` / `VolumePiece`）⇒ 拿**卡拼出来的规范名**
             * 当引用名（那正是这一组入口该有的名字，`pack111-L2.7z.txt` ⇒ `pack111-L2.7z.001`）；
             * 卡上也给不出 ⇒ 才退回老口径 `<基名>.zip`（⛔ 一个字都不放宽）。</para>
             */
            if (ownPath.Length > 0)
            {
                string fromCard = VolumeNameOrchestration.ResolveCanonicalNameFromCard(ownPath, FileNature.Inspect(ownPath));

                if (fromCard.Length > 0 && VolumeGroupDetector.BelongsToSameGroup(fromCard, fromCard))
                {
                    return fromCard;
                }

                /*
                 * ⚠ 2026-10-17（本轮判据输入定死）：名字与卡都拼不出规范名时（真机 `aaa-L2.txt`：
                 * 卡上 `分卷身份=VolumeMember` 但 `BodyKind=Unknown`、规范名空），老写法退回
                 * `<基名>.zip` ⇒ 与 `.rar` / `.7z` 族的兄弟**一个都匹配不上**（判据输入逐字：
                 * `这一层文件 3 个｜参照名=「aaa-L2.zip」｜同族候选 0 个`）⇒ 收拢永远收不到一起。
                 *
                 * <p>现在的口径：**拿同层里真实存在的"这一族的成员"当参照名**（卡说它是某族的一片），
                 * 那个名字天然就在这一组里、族也天然对得上。⛔ 找不到才退回 `<基名>.zip`（老兜底不变）。</p>
                 */
                string? ownDirectory = Path.GetDirectoryName(ownPath);

                if (!string.IsNullOrWhiteSpace(ownDirectory) && Directory.Exists(ownDirectory))
                {
                    foreach (string sibling in Directory.EnumerateFiles(ownDirectory))
                    {
                        if (SafePathHelper.PathEquals(sibling, ownPath))
                        {
                            continue;
                        }

                        if (VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(sibling)))
                        {
                            return FileNameHelper.GetFileName(sibling);
                        }
                    }
                }
            }

            return baseName + ".zip";
        }

        /// <summary>
        /// 这一组该**拼到哪一层** = 入口包（引擎要打开、要对它输密码的那一份）所在目录，
        /// 也就是这一组该**落**的那一层（用户 2026-10-05 口径：「谁可以输入密码，就解压到谁那边」；
        /// 见 <see cref="Detection.GroupVolumeDirectory"/>：RAR 分卷 = `.part1.rar`、7z 分卷 = `.7z.001`、
        /// zip 分卷 = `.zip`（跨盘那一族）或 `.zip.001`（7-Zip 切的通用分片））。
        ///
        /// <para><b>判据是"程序性"的（2026-10-11 订正）</b>：答案只能是 **(族, 基名, 盘上事实)** 的纯函数 ——
        /// ① 入口包**在盘上** ⇒ 它所在那一层（纯查盘：任务表里这一组的成员 + 各单产出树里已经落盘的这一组成员，
        /// 一起交给 <see cref="Detection.GroupVolumeDirectory.Resolve"/>）；② 还不在盘上 ⇒ 沿**产出链**推它将来落在哪一层
        /// （与落点实现同一个出口）；③ 两条都答不出 ⇒ **空串 = 这一次不决定**。
        /// ⛔ 顺序只决定"什么时候算得出来"，**不决定"算出来是什么"**。</para>
        ///
        /// <para>⛔ 判据全部转调既有那几把尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/> 认"同一组"，
        /// 所以脏名 `111.z0删除2` 照样认得出；<see cref="Detection.GroupVolumeDirectory.Resolve"/> 认"入口包在哪儿"），
        /// ⛔ 这里不自己拼族规则、⛔ 不看盘上还有没有别的兄弟。</para>
        /// </summary>
        /// <param name="memberPath">
        /// **手上这一片**（可选）：它是这一组的成员，拿它的名字当"同一组"那把尺子的参照物
        /// —— ⛔ 不再一律退回 `<基名>.zip`（那只对跨盘 ZIP 族成立）。
        /// ⚠ **闸门**：它的名字必须解析得出一条"**没被改坏**"的线索（见方法体里那句"干不干净"），
        /// 名字带垃圾尾巴的那一档（实测 `333.7z.001.txt`）**不许**启用新判据 —— 那一档连规范名都算不对
        /// （会算出 `333.7z.001.001`），照旧走老口径（行为与改动前逐字相同）。
        /// </param>
        /// <summary>
        /// **这一份算不算"这一组"的成员**（唯一出口）。
        ///
        /// <para>① 名字那把尺子（<see cref="VolumeGroupDetector.BelongsToSameGroup"/>）说"是" ⇒ 是；
        /// ② 名字说"不是"时**问卡**（用户口径：「专属算法不就是靠识别的吗」）：卡上说"我是这一组的一片"
        /// （`VolumeFirstPiece` / `VolumePiece`）**而且基名与这一组有关系**（一个是另一个的前缀也算）⇒ 也算。
        /// 现场（官方环台 `10101010`）：入口那一份叫 `pack111-L2.7z`（卷标记被伪装法吃掉 ⇒
        /// 名字尺子连"像分卷"都判不出）⇒ 它进不了成员表 ⇒ 逐字 `任务表里这一组的单 0 个，入口包=判不出`
        /// ⇒ 三片都齐了也拼不到一层去。</para>
        ///
        /// <para>⛔ 判不出（读不到卡 / 基名对不上）⇒ 照旧"不是"（一个字不放宽）。</para>
        /// </summary>
        private static bool MemberBelongsToGroup(string path, string tailName)
        {
            string name = FileNameHelper.GetFileName(path);

            if (name.Length == 0)
            {
                return false;
            }

            if (VolumeGroupDetector.BelongsToSameGroup(name, tailName))
            {
                return true;
            }

            FileNatureCard card = FileNature.Inspect(path);

            if (!VolumePieceFacts.CardSaysItIsOneOfTheGroup(card))
            {
                return false;
            }

            return VolumeGatherStep.BaseNameRelatedToGroup(name, FileNameHelper.GetArchiveBaseName(tailName));
        }

        private string ResolveBatchGroupDirectory(string baseName, ArchiveTask? task = null, string? memberPath = null)
        {
            /*
             * ⚠ "手上这一片的名字干不干净" = **它自己算出来的规范名就是它自己**（唯一出口
             * `VolumeGroupResolver.TryGetCanonicalVolumeName`）。
             * ⛔ 脏名那一档不许启用新判据：`333.7z.001.txt` 那一档的规范名是 `333.7z.001.001`（错的），
             * 拿它当参照物会牵出一串错名字（实测：`PlacementRulesTests.文件未指定位置…` 多出一个同名目录）。
             * 判不出 ⇒ 当作"不干净" ⇒ 老口径。
             */
            string memberName = FileNameHelper.GetFileName(memberPath ?? string.Empty);
            bool memberNameIsClean = memberName.Length > 0
                && VolumeGroupResolver.TryGetCanonicalVolumeName(memberName, out string memberCanonical)
                && string.Equals(memberCanonical, memberName, StringComparison.OrdinalIgnoreCase);

            /*
             * ⚠ 2026-10-13 订正（困难批 `run-2222`，逐字）：这道"干不干净"的闸门**不许把 `memberPath` 整个丢掉**。
             *
             * 现场：手上这一片叫 `bbb-L2.part98598542删除1.rar`（EEE 式伪装），它的规范名是 `bbb-L2.part1.rar`
             * ⇒ `memberNameIsClean = false` ⇒ 老写法走**两参**重载（`memberPath` 根本没参与）⇒ 引用名退回
             * `<基名>.zip`（**zip 族**）⇒ 与这一组的兄弟（partN 族）`BelongsToSameGroup` **一个都匹配不上**
             * ⇒ 逐字 `这一组的引用名=「bbb-L2.zip」，任务表里这一组的单 0 个` ⇒ `这一组该拼到哪一层 —— 判不出`
             * ⇒ 整组永远缺片 ⇒ ①红。（同一份里名字干净的那一片 `bbb-L2.part2.rar` 反而能判出来：
             * `批末重判 —— 这一组该拼到哪一层 = …\bbb\bbb\bbb`。）
             *
             * ⛔ **安全性一个字没放宽**：`ResolveGroupReferenceName` 内部本来就拿**同一把尺子自比**
             * （`BelongsToSameGroup(own, own)`），解析不出来的名字（`333.7z.001.txt` 那一档）照样退回
             * `<基名>.zip` —— 老口径与它的守门用例行为不变，这里只是把"手上这一片"重新交给那把尺子。
             */
            string tailName = ResolveGroupReferenceName(task, baseName, memberPath);

            var members = new List<string>();

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                string path = candidate?.CurrentPath ?? string.Empty;

                if (path.Length > 0 && MemberBelongsToGroup(path, tailName))
                {
                    members.Add(path);
                }
            }

            /*
             * ⚠ 2026-10-14（walk 档一步步走查的直接结论）：**卡说"我是这一组的一片"时，名字干不干净不该再当闸门**。
             *
             * <para>走查逐字（三片摆在一层目录时）：`pack111-L2.7z.txt` 的卡写着
             * `格式=SevenZip 专属算法=VolumeFirstPiece 自述整包=62919013 按内容算的片数=3`
             * ⇒ 槽位 1、规范名 `pack111-L2.7z.001`、入口=它自己、目标层=它所在那一层 —— **整条链是通的**。
             * 可真跑时它卡在 `任务表里这一组的单 0 个，入口包=判不出`：因为"名字不干净"（`.txt` 尾巴）
             * ⇒ 下面那两段收拢**整段不跑**（连产出树都不扫）⇒ 成员表恒空 ⇒ 那一组永远拼不到一起。</para>
             *
             * <para>⇒ 判据：名字干净 **或** 卡说它是这一组的一片 ⇒ 都按"可用"处理。⛔ 判不出（读不到卡 / 名字也解析不出）
             * ⇒ 照旧老口径（那两段不跑）。</para>
             */
            bool memberUsable = memberNameIsClean
                || (memberName.Length > 0
                    && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(memberPath!)));

            // 手上这一片也算成员（它可能还没进任务表，或者来自别的单那条链）。
            if (memberUsable && !members.Contains(memberPath!))
            {
                members.Add(memberPath!);
            }

            /*
             * ===== ① 入口包在盘上 ⇒ 它所在那一层 =====
             * 成员除了"任务表里这一组的"，还要包括**各单产出树里已经落盘的这一组成员** ——
             * 分卷的片是别的单解出来的产物，它们不在任务表里（任务表只装用户导入的那些包），
             * 只看任务表就会漏掉入口包（真机 FFFF：入口包 `111_.part1.rar` 是 `111__.rar` 那条链解出来的）。
             * ⛔ 仍然只由那把唯一尺子回答"入口包在哪一层"，⛔ 不按名字单独找入口包
             * （实测踩到：就地整理那一档下"按名字在产出树里找"会把**源文件自己**当成入口）。
             */
            if (memberUsable)
            {
                foreach (ArchiveTask? other in SnapshotTaskTable(Tasks))
                {
                    if (other == null)
                    {
                        continue;
                    }

                    foreach (string directory in new[] { other.OutputPath, other.ContentDirectoryPath, other.RestDirectoryPath })
                    {
                        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                        {
                            continue;
                        }

                        foreach (string candidate in CollectVolumeFilesInDirectory(directory))
                        {
                            if (MemberBelongsToGroup(candidate, tailName)
                                && !members.Contains(candidate))
                            {
                                members.Add(candidate);
                            }
                        }
                    }
                }
            }

            Detection.GroupVolumeDirectory.Entry entry = Detection.GroupVolumeDirectory.Resolve(members);

            if (entry.Directory.Length > 0 && Directory.Exists(entry.Directory))
            {
                return entry.Directory;
            }

            /*
             * ===== ② 入口包**还没落地**：按产出链**一层一层**推它将来落在哪一层（用户 2026-10-06 拍板）=====
             *
             * ⛔ **旧口径（"任务表里第一单那一层"）已作废**：真机 2026-10-06 14:33 那次就是它把入口包
             * `111.zip` 接到了 `111(4)`（日志逐字：`已按规范卷名 111.zip 接到「111(4)」这一层`），
             * 于是列表第一项挂着 48 MB、产物也跟着落错层。用户原话：「**我说过多少遍在 `111.zip` 所在位置**，
             * 你为什么还解压在列表第一项」。
             *
             * 只认一个出口（与落点实现同一份判据 `ResolveProducerEntryArchivePathCore`）：
             * 沿产出链把入口包的落点**逐层叠**出来，取它所在那一层。
             * ⚠ 问的是"**产出入口包**的那一单"，⛔ 不是"谁先吐过一片"。
             */
            foreach (string member in members)
            {
                /*
                 * ⚠ 必须传**这一片自己那一单**（找不到就退回调用方给的那一单）：产出方的判据里有一条
                 * `ReferenceEquals(candidate, task)` —— 传个临时对象进去，等于把"产出方是不是我自己"
                 * 判错（实测踩到：单层路这一档因此拿到空串，收片层退回旧口径、整组凑不齐）。
                 */
                ArchiveTask owner = FindTaskByPath(member) ?? task ?? new ArchiveTask(member, 0);

                string predicted = ResolveProducerEntryArchivePathCore(
                    owner,
                    BuildExtractOptions(tryExtractUnknownFormat: false),
                    member,
                    0);

                if (predicted.Length == 0)
                {
                    continue;
                }

                string? layer = Path.GetDirectoryName(predicted);

                if (!string.IsNullOrWhiteSpace(layer))
                {
                    return layer!;
                }
            }

            /*
             * ===== ③ 判不出 ⇒ **空串 = 这一次不决定**（调用方"什么都不做"，等入口包出现后再问一次）=====
             *
             * ⛔ **这里删掉了一条兜底**（2026-10-11，真机 FFFF 那一批 + 复现用例把它钉死）：
             * 原来最后还问 `ResolveGroupProducerDirectory(baseName)` = "**谁先把这一组的片吐出来**，
             * 就落谁那条链的落点目录"（读 `_groupPieceProducers`）—— 于是这一组的层**由时序决定**：
             * 同一份输入，谁先跑完谁定层。真机那一批就是它把四片全收进了 `1113__.rar` 那条链的层
             * （它只是恰好第一个吐出 part3），而真正的入口包 `111_.part1.rar` 是**最后**才出来的
             * （它得先穿两层 rar→zip），反被搬进别人的层（盘上同一个 inode 挂了三四个名字，
             * `fsutil hardlink list` 逐字可查）。
             * ⇒ **"谁先到"不许当判据**；判不出时正解是"这一次不决定"，不是"先占一层"。
             * ⛔ `_groupPieceProducers` 那份账照旧保留：它回答"有哪些片被产出来了 / 该谁收场"，
             *   **不回答层**（谁先产出一片与"入口包在哪"是两件事）。
             */
            string observed = !string.IsNullOrWhiteSpace(memberPath)
                ? memberPath!
                : task?.CurrentPath ?? string.Empty;
            string entryName = VolumeGroupDetector.TryGetFirstVolumeName(FileNameHelper.GetFileName(observed))
                ?? string.Empty;
            string producerName = "判不出";

            foreach (string member in members)
            {
                ArchiveTask owner = FindTaskByPath(member) ?? task ?? new ArchiveTask(member, 0);

                if (FindProducerTaskForPackage(owner, member, entryName) is { } producerTask)
                {
                    producerName = producerTask.LogName;
                    break;
                }
            }

            /*
             * ⚠ 这一行是"判不出"那一档的**判据输入**（规矩：只拿一份用户日志就能定位）——
             * 它此前一个字都不写，真机 FFFF 那一批就是卡在这里看不出是"尺子不认"还是"产出方找不到"。
             */
            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.CriterionInputsGroupLayerFormat,
                    FileNameHelper.GetFileName(observed),
                    tailName,
                    members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    entryName.Length == 0 ? "判不出" : entryName,
                    producerName,
                    memberNameIsClean ? "是" : "否",
                    memberUsable ? "是" : "否",
                    observed.Length > 0 ? FileNature.Inspect(observed).BodyKind.ToString() : "（没有这一片）",
                    $"task={(task == null ? "null" : "有")} memberPath={(string.IsNullOrWhiteSpace(memberPath) ? "空" : "有")}",
                    observed,
                    SafeFileSizeText(observed)));

            return string.Empty;
        }

        /// <summary>按路径找任务表里那一单（找不到 ⇒ null；⛔ 只读，不新建）。</summary>
        /// <summary>"这一份多大"（判据输入用；读不到 ⇒ "判不出"）。</summary>
        private static string SafeFileSizeText(string path)
        {
            try
            {
                return File.Exists(path)
                    ? new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "（不在盘上）";
            }
            catch
            {
                return "判不出";
            }
        }
        /// <summary>这一份是不是已经在某个「其余物」目录里（按路径段判；是 ⇒ 不再搬第二遍）。</summary>
        private static bool IsInsideProcessArtifactDirectory(string path)
        {
            string? directory = Path.GetDirectoryName(path);

            while (!string.IsNullOrWhiteSpace(directory))
            {
                if (ProcessArtifactLayout.IsArtifactDirectoryName(Path.GetFileName(directory)))
                {
                    return true;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        }
        private ArchiveTask? FindTaskByPath(string path)
        {
            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                if (SafePathHelper.PathEquals(candidate.CurrentPath, path))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// **把这一单手上那几片按规范卷名接到入口包那一层**（用户 2026-10-06：「你把分卷移过来不就是了吗」）。
        ///
        /// <para>⛔ 判据与执行体都不在这里：整件事转调 <see cref="TryAdoptUnresolvedVolumePiece"/>
        /// （它自己会判"同一组吗 / 目标名占没占 / 同不同盘"，也只硬链接，⛔ 用户源文件一个字节不动）。
        /// 这一档只是把"手上那几片"再喂一遍 —— 因为批末那一刻"入口包那一层"才刚确定下来。</para>
        ///
        /// <para>⛔ 一个都收不动 / 判不出来 ⇒ 什么都不做（调用方照旧读盘上事实下结论）。</para>
        /// </summary>
        /// <summary>
        /// 批末那一站该拿**哪一份**去问"这一组齐了没"：停下来的那一片（如果记过）—— 否则就是这一单自己的路径。
        ///
        /// <para>⛔ 判据输入一个字不改：记过 ⇒ 那一片自己的路径（它的基名 = 那一组的基名）；
        /// 没记过 ⇒ <c>task.CurrentPath</c>（与改动前逐字相同）。</para>
        /// </summary>
        private string ResolveDeferredGroupMemberPath(ArchiveTask task) =>
            _deferredVolumeGroupMember.TryGetValue(task, out string? recorded) && !string.IsNullOrWhiteSpace(recorded)
                ? recorded!
                : task.CurrentPath ?? string.Empty;

        /// <summary>
        /// **把这一片收进"那一组该在的那一层"**（批末补齐那一站用的唯一出口）。
        /// </summary>
        /// <param name="memberPath">
        /// 手上这一片（可选）：<c>null</c> ⇒ 老口径（拿这一单自己的包基名当参照物）。
        /// 传了它 ⇒ 参照物换成它自己的名字（停的是**别人那一组**时唯一说得清的办法）。
        /// </param>
        /// <summary>
        /// 这一层里与"这一单手上那一片"**同族同基名**的文件（收拢决策器的候选池；⛔ 只读这一层、不递归）。
        /// 判据 = 既有宽尺子 `VolumeGroupDetector.BelongsToSameGroup`，参照名 = 手上那一片。
        /// </summary>
        /// <summary>枚举一个目录里的**所有**文件（读不动 ⇒ 空表，不抛）。</summary>
        private static IEnumerable<string> SafeEnumerateFiles(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.GetFiles(directory);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }


        private List<string> CollectGroupCandidatePaths(ArchiveTask task, string entryLayer, out string referenceName, out int scannedInEntryLayer)
        {
            var paths = new List<string>();

            referenceName = string.Empty;
            scannedInEntryLayer = 0;

            if (string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return paths;
            }

            string observed = ResolveDeferredGroupMemberPath(task);
            string reference = ResolveGroupReferenceName(task, FileNameHelper.GetArchiveBaseName(observed), observed);

            referenceName = reference;

            if (reference.Length == 0)
            {
                return paths;
            }

            /*
             * ⚠ 2026-10-17（运行期判据输入定死）：参照名退回 `<基名>.zip` 时（`pack111-L2.7z.zip`、`aaa-L2.zip`），
             * 与 `.7z` / `.rar` 族的兄弟**一个都对不上**（逐字 `这一层文件 3 个｜同族候选 0 个`）——
             * 而那一层里明明躺着真成员。⇒ **参照名允许改由"真实成员"充当**：
             * 先把四档来源的文件都收集起来（不过滤），若参照名自己都过不了那把尺子（`BelongsToSameGroup(ref, ref)`），
             * 就从真实文件里挑第一个"卡说它是这一族一片"的，用它的名字当参照名。
             * ⛔ 判不出（一个成员都没有）⇒ 照旧空结果（兜底不变）。
             */
            var allFiles = new List<string>();

            void CollectAll(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in CollectVolumeFilesInDirectory(directory!))
                {
                    if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            /*
             * 源层候选：`OriginalPath` 是用户导入那一刻的位置，⛔ 不是过程物层。
             * 分卷片被包装壳拆到不同源目录时（2026-10-19 `3333`：`.002` / `.003` 各在一个
             * `333(2)` / `333(3)` 源目录里），只收 `CurrentPath`/`OutputPath` 永远看不到整组。
             * 这里按**整层文件**枚举，收不收仍由唯一出口 `AcceptsAsGroupPiece` 回答。
             */
            void CollectAllFromSourceLayer(string? directory)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                foreach (string file in SafeEnumerateFiles(directory!))
                {
                    if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        allFiles.Add(file);
                    }
                }
            }

            /*
             * **源树的“根”就是带 recipe.json 的那一层**（造包器写下的样本根）。
             * 只从单份任务的 OriginalPath 找，会漏掉同一份样本里**没被导入成任务**的裸片
             * （2026-10-19 `3333`：`.002` / `.003` 没有任务，只有源文件）。
             * ⛔ 只扫这一棵源树、文件上限 200，收不收仍由 `AcceptsAsGroupPiece` 回答。
             */
            void CollectAllFromSourceTree(string? root)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return;
                }

                int collected = 0;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(root!, "*", SearchOption.AllDirectories))
                    {
                        if (++collected > 200)
                        {
                            break;
                        }

                        if (!allFiles.Any(existing => string.Equals(existing, file, StringComparison.OrdinalIgnoreCase)))
                        {
                            allFiles.Add(file);
                        }
                    }
                }
                catch
                {
                    // 读不动就保留已经收进来的部分。
                }
            }

            /*
             * 运行根里没有 `recipe.json`（`bt30` 只复制样本文件）⇒ 不能靠它找源树根。
             * 从 `OriginalPath` 往上最多 3 层；到批根 `大测试原包-困难` / `大测试指定位置-困难`
             * 就停下（⛔ 不扫进别的样本）。每层递归收、文件上限 200，收不收仍由唯一出口回答。
             */
            void CollectAllFromSourceAncestors(string? path)
            {
                string? dir = string.IsNullOrWhiteSpace(path)
                    ? null
                    : Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(path));

                for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(dir); up++)
                {
                    string name = Path.GetFileName(dir);

                    if (name.Contains("大测试原包", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("大测试指定位置", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    CollectAllFromSourceTree(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }

            CollectAll(entryLayer);
            scannedInEntryLayer = allFiles.Count;

            string ownLayer = Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(observed)) ?? string.Empty;

            CollectAll(ownLayer);

            /*
             * ===== 卡自证"我是 zip 分卷的一片"时，**再往外找一层**（用户 2026-10-17：底层识别先）=====
             * 现场（新样本 `6666` 的 `recipe.json` 逐字）：同一组三片分处
             * `打包111\打包111-W0-0\`、`打包111(2)\打包111(2)\打包111(2)\`、`打包111(3)\打包111(3)\` ——
             * 名字也全烂（`打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003`）。
             * ⛔ 只在"入口那一份卡上确实是 zip 分片"时才补扫（`Format=Zip` 且 成员/EOCD/本地头 之一）；
             * ⛔ 共用找卷窗口一个字不改（上一轮试放大它打红了既有守门：`CrossLayerVolumeGatherTests…`）；
             * ⛔ 补扫有上限（目录 ≤ 24 个、文件 ≤ 400 个），且收进来的还要过后面那几道闸门。
             */
            bool entryIsZipPiece;

            try
            {
                FileNatureCard entrySeat = FileNature.Inspect(observed);

                /*
                 * ⚠ 2026-10-18 新批 `3333`/`4444`/`7777`/`9999`：**7z / RAR 的分卷片同样会散在
                 * 包装壳的产出目录里**，补扫不能只给 ZIP 开。判据仍读卡：格式认得出，且卡上自证
                 * "是这一族的一片"（BodyKind / 成员位）—— 名字不参与这一道门。
                 */
                entryIsZipPiece = entrySeat.Format != VolumeContentFormat.Unknown
                    && (entrySeat.HasZipEndRecord
                        || entrySeat.HasZipLocalHeader
                        || entrySeat.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                        || entrySeat.BodyKind is ArchiveBodyKind.VolumeFirstPiece or ArchiveBodyKind.VolumePiece);
            }
            catch
            {
                entryIsZipPiece = false;
            }

            /*
             * 补扫那一侧要用的两个判据输入（与本地那一侧同源）：基名 + "第 1 片有多大"的锚点。
             */
            string sweepReferenceBase = FileNameHelper.GetArchiveBaseName(reference);
            long sweepAnchorSize = 0;

            try
            {
                FileNatureCard sweepAnchorSeat = FileNature.Inspect(observed);

                if (sweepAnchorSeat.HasZipLocalHeader && sweepAnchorSeat.Size > 0)
                {
                    sweepAnchorSize = sweepAnchorSeat.Size;
                }
            }
            catch
            {
                sweepAnchorSize = 0;
            }

            if (entryIsZipPiece)
            {
                try
                {
                    string observedFull = SafePathHelper.GetFullPathSafe(observed);
                    string? parentDirectory = Path.GetDirectoryName(observedFull);
                    string? grandParent = string.IsNullOrWhiteSpace(parentDirectory)
                        ? null
                        : Directory.GetParent(parentDirectory)?.FullName;

                    /*
                     * 起点要**再往上两层**（现场 `6666`：入口在 `…\打包111(3)\打包111(3)\`，
                     * 同组另外两片在 `…\打包111\打包111-W0-0\` 与 `…\打包111(2)\打包111(2)\打包111(2)\` ——
                     * 从 `打包111(3)` 起扫永远够不到兄弟目录的下一层）。
                     */
                    /*
                     * ⚠ 2026-10-18（运行期坐实：`run-19191919` 09:20:09 逐字 `补扫根=…\大测试原包-困难
                     * ｜实际扫了 66 个目录 / 2 个文件`）：**往上 3 层会越过样本根**，指到装着 30 份样本的父目录，
                     * 目录预算（40）当场被兄弟样本目录吃光 ⇒ 这一组真正的片一片都没走到。
                     * ⇒ 收窄上界：从祖父目录再往上时，只有当"待上溯的那个目录"里存在**名字以本组基名前缀开头**
                     * 的子目录时才继续往上（`6666` 靠 `打包111(2)`/`打包111(3)` 成立；`19191919` 的那一层
                     * 兄弟叫 `6666`/`8888`/…，一个都对不上 ⇒ 停在样本根本身）。⛔ 判不出前缀 ⇒ 不上溯。
                     */
                    string? scanRoot = grandParent;

                    if (!string.IsNullOrWhiteSpace(grandParent))
                    {
                        string prefix = FileNameHelper.GetArchiveBaseName(referenceName);
                        int dash = prefix.IndexOf('-');

                        if (dash > 0)
                        {
                            prefix = prefix[..dash];
                        }

                        string? candidateRoot = grandParent;

                        for (int up = 0; up < 3; up++)
                        {
                            string? parent = Directory.GetParent(candidateRoot!)?.FullName;

                            if (string.IsNullOrWhiteSpace(parent) || prefix.Length == 0)
                            {
                                break;
                            }

                            bool related = false;

                            try
                            {
                                foreach (string sub in Directory.GetDirectories(parent))
                                {
                                    if (Path.GetFileName(sub).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                    {
                                        related = true;
                                        break;
                                    }
                                }
                            }
                            catch
                            {
                                related = false;
                            }

                            if (!related)
                            {
                                break;
                            }

                            candidateRoot = parent;
                        }

                        scanRoot = candidateRoot;
                    }

                    if (!string.IsNullOrWhiteSpace(scanRoot) && Directory.Exists(scanRoot))
                    {
                        int directories = 0;
                        int files = 0;
                        /*
                         * ⛔ **由近及远**（2026-10-18 困难批 `30303030` 定案）：原来只从 `scanRoot` 向下 BFS，
                         * 而那一轮 `scanRoot` 是**批次根**（30 份样本的父目录）⇒ 40 个目录预算全花在
                         * `10101010`/`1111` 等别的样本上，本组的兄弟目录一个都没轮到（逐字：
                         * `实际扫了 75 个目录 / 8 个文件`，全是别组的文件）⇒ 整组凑不齐。
                         * ⇒ 种子 = **这一单自己那一层 + 往上 3 层**（近的优先）；只有一层都取不到时才退回 `scanRoot`。
                         * ⛔ 判据（`AcceptsAsGroupPiece`）一个字不改，只改"先扫哪儿"。
                         */
                        var sweepSeeds = new List<string>();
                        string? seedCursor = Path.GetDirectoryName(
                            SafePathHelper.GetFullPathSafe(task.CurrentPath ?? string.Empty));

                        for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(seedCursor); up++)
                        {
                            if (Directory.Exists(seedCursor))
                            {
                                sweepSeeds.Add(seedCursor!);
                            }

                            seedCursor = Path.GetDirectoryName(seedCursor);
                        }

                        if (sweepSeeds.Count == 0)
                        {
                            sweepSeeds.Add(scanRoot!);
                        }

                        /*
                         * 源层也要进补扫种子：分卷片可能先在别的包装壳里产出，再落回
                         * `OriginalPath` 那一层；只从 `CurrentPath`（过程物层）往上找够不到
                         * （2026-10-19 `3333`：`.002` / `.003` 在兄弟源目录里）。
                         */
                        string? sourceSeed = Path.GetDirectoryName(
                            SafePathHelper.GetFullPathSafe(task.OriginalPath ?? string.Empty));

                        for (int up = 0; up < 4 && !string.IsNullOrWhiteSpace(sourceSeed); up++)
                        {
                            if (Directory.Exists(sourceSeed)
                                && !sweepSeeds.Any(existing => string.Equals(existing, sourceSeed, StringComparison.OrdinalIgnoreCase)))
                            {
                                sweepSeeds.Add(sourceSeed!);
                            }

                            sourceSeed = Path.GetDirectoryName(sourceSeed);
                        }

                        var frontier = new List<string>(sweepSeeds);
                        var sweepTrace = new List<string>();
                        var visited = new HashSet<string>(sweepSeeds, StringComparer.OrdinalIgnoreCase);

                        for (int level = 0; level < 8 && frontier.Count > 0 && directories < 200; level++)
                        {
                            var next = new List<string>();

                            foreach (string directory in frontier)
                            {
                                try
                                {
                                    foreach (string sub in Directory.GetDirectories(directory))
                                    {
                                        if (directories++ >= 40)
                                        {
                                            break;
                                        }

                                        if (visited.Add(sub))
                                        {
                                            next.Add(sub);
                                        }
                                    }
                                }
                                catch
                                {
                                    // 读不动的目录跳过（不影响已收到的候选）。
                                }
                            }

                            foreach (string directory in frontier.Concat(next))
                            {
                                /*
                                 * ⚠ 2026-10-18（运行期逐份判据输入定案）：这里原来用 `CollectVolumeFilesInDirectory`
                                 * （按**后缀**筛"像不像卷文件"），于是 `bbb-L2.zip-658` 这种"后缀被改坏、内容却是
                                 * 末片"的文件**根本没被枚举**（同一份日志里逐份只出现 3 个名字，第三个名字一次没出现）。
                                 * ⇒ 补扫改枚举**整层所有文件**，由唯一出口 `AcceptsAsGroupPiece`（内容先行）来收/挡；
                                 * 文件预算 400 只当上限用，⛔ 不再用后缀当前置筛子。
                                 */
                                foreach (string file in SafeEnumerateFiles(directory))
                                {
                                    if (++files > 400)
                                    {
                                        break;
                                    }

                                    try
                                    {
                                        FileNatureCard card = FileNature.Inspect(file);

                                        /*
                                         * ===== 补扫锚点：末片没有本地头时，从**本组中段片**取满片尺寸 =====
                                         * （2026-10-18 新批 `24242424` 运行期定案）跨盘 ZIP 的入口是末片
                                         * （`EOCD=True`、没有本地头）⇒ `sweepAnchorSize` 原来是 0 ⇒ 字节数守恒整档
                                         * 不启用 ⇒ 从数据中间切开的第 2 片（`333-L2.pdf`，`Format=Unknown`）永远进不了
                                         * 候选池，组只剩 2 片。
                                         * 判据：只拿**没有 EOCD 的、基名相关的、内容没证明不是成员**的片当锚点
                                         * （末片自己绝不能当锚点）；取第一份这样的片的大小，后续同尺寸中段片就能过 ③。
                                         * ⛔ 只是把"锚点"补上，`AcceptsAsGroupPiece` 的阶梯一个字没改。
                                         */
                                        if (sweepAnchorSize <= 0
                                            && sweepReferenceBase.Length > 0
                                            && !card.HasZipEndRecord
                                            && card.Size > 0
                                            && card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                                            && card.BodyKind != ArchiveBodyKind.SingleComplete
                                            && VolumeGatherStep.BaseNameRelatedToGroup(card.ArchiveBaseName, sweepReferenceBase))
                                        {
                                            sweepAnchorSize = card.Size;
                                        }

                                        /* 与本地那一侧**同一个出口**（含字节数守恒 ⇒ 中间片才收得进来）。 */
                                        bool accepted = VolumeGatherStep.AcceptsAsGroupPiece(card, reference, sweepReferenceBase, sweepAnchorSize);

                                        if (sweepTrace.Count < 20)
                                        {
                                            sweepTrace.Add(
                                                $"{FileNameHelper.GetFileName(file)}〔F={card.Format} EOCD={card.HasZipEndRecord} LH={card.HasZipLocalHeader} "
                                                + $"成员={card.Membership} 种类={card.BodyKind} 大小={card.Size} 准入={accepted}〕");
                                        }

                                        if (accepted
                                            && !allFiles.Any(existing => string.Equals(
                                                existing, file, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            allFiles.Add(file);
                                        }
                                    }
                                    catch
                                    {
                                        // 单份读不出来跳过。
                                    }
                                }
                            }

                            frontier = next;
                        }

                        /*
                         * **判据输入永久留证**（用户 2026-10-17：「判据输入必须写进日志」）：
                         * 补扫到底扫了哪儿、收进几份 —— 没有这一行，"远处那两片到底收没收进来"
                         * 只能靠猜（这一轮就是靠断点读 `paths` 才看出来的）。
                         */
                        AppendLog(
                            "INFO",
                            $"{task.LogName}：收拢补扫 —— 入口卡自证是 zip 分片；补扫根={scanRoot}（入口往上 3 层）"
                            + $"｜下探最多 8 层、目录上限 200、文件上限 400｜实际扫了 {directories} 个目录 / {files} 个文件"
                            + $"｜锚点={sweepAnchorSize} 基名={sweepReferenceBase}"
                            + $"｜候选池里现在 {allFiles.Count} 份"
                            + (sweepTrace.Count > 0 ? $"｜逐份：[{string.Join("；", sweepTrace)}]" : string.Empty));
                    }
                }
                catch
                {
                    // 补扫失败 ⇒ 与没有这一档逐字相同。
                }
            }

            foreach (ArchiveTask candidate in SnapshotTaskTable(Tasks))
            {
                if (candidate == null)
                {
                    continue;
                }

                CollectAll(Path.GetDirectoryName(candidate.CurrentPath ?? string.Empty));
                CollectAll(candidate.OutputPath);
                CollectAll(candidate.ContentDirectoryPath);
                CollectAll(candidate.RestDirectoryPath);
                CollectAllFromSourceLayer(Path.GetDirectoryName(candidate.OriginalPath ?? string.Empty));
                CollectAllFromSourceAncestors(candidate.OriginalPath);
            }

            /*
             * ⛔ **不能拿"参照名自己过不过尺子"当闸门**（2026-10-17 实测）：`pack111-L2.7z.zip` 自己是个合法的
             * zip 名 ⇒ `BelongsToSameGroup(ref, ref)` = **true**，于是兜底永远不触发、层里明明有真成员却判 0 个。
             * 现在的闸门 = **这个参照名在收集到的文件里到底存不存在**（不存在 ⇒ 说明它是拼出来的兜底名）。
             */
            bool referenceIsRealFile = allFiles.Any(file =>
                string.Equals(FileNameHelper.GetFileName(file), reference, StringComparison.OrdinalIgnoreCase));

            if (!referenceIsRealFile)
            {
                /*
                 * 三档递进找参照名（⛔ 都找不到就保持 `<基名>.zip` 的老兜底）：
                 * ① 卡说"我是这一族的一片"的；② **名字读得出卷号**的（7z/RAR 的伪装片按内容认不出，
                 * 只有名字这一条线索）；③ 一个都不满足 ⇒ 老兜底。
                 */
                string? byCard = null;
                string? byVolumeName = null;

                foreach (string file in allFiles)
                {
                    string name = FileNameHelper.GetFileName(file);

                    if (byCard == null && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(file)))
                    {
                        byCard = name;
                    }

                    if (byVolumeName == null && VolumeGroupDetector.TryGetVolumeIndex(name) != null)
                    {
                        byVolumeName = name;
                    }

                    if (byCard != null && byVolumeName != null)
                    {
                        break;
                    }
                }

                string? chosen = byCard ?? byVolumeName;

                if (chosen != null)
                {
                    reference = chosen;
                    referenceName = reference;
                }
            }

            string referenceBase = FileNameHelper.GetArchiveBaseName(reference);

            /*
             * 通用分片族的"第 1 片有多大" —— 文档 §6.6 A 的**字节数守恒**要用的锚点：
             * 从收集到的候选里找"开头是本地文件头"的那一份；找不到 ⇒ 0（这一档不启用）。
             */
            long zipFullSize = 0;

            if (entryIsZipPiece)
            {
                foreach (string file in allFiles)
                {
                    try
                    {
                        FileNatureCard anchorCard = FileNature.Inspect(file);

                        if (anchorCard.HasZipLocalHeader && anchorCard.Size > 0)
                        {
                            zipFullSize = anchorCard.Size;
                            break;
                        }
                    }
                    catch
                    {
                        // 单份读不出来就跳到下一份。
                    }
                }
            }

            /*
             * ⚠ 本地这一侧也补同一个锚点（2026-10-18 新批 `24242424`）：本组可能**一片带本地头的都没有**
             * （跨盘 ZIP 的中段片起在数据中间）⇒ `zipFullSize=0` ⇒ 下面这一遍又把补扫刚收进来的
             * `Unknown` 中段片（`333-L2.pdf`）筛掉。补扫那一侧已经从"没有 EOCD、基名相关"的片取了锚点，
             * 这一侧原样沿用；⛔ 只补锚点，判据阶梯不改。
             */
            if (zipFullSize <= 0)
            {
                /*
                 * 7z/RAR 的首卷卡上有“自述片数 / 自述整包字节数”，用**首卷自己的大小**
                 * 当满片锚点；中间片/末片本身没有魔数，这是唯一能量出来的锚点。
                 */
                try
                {
                    FileNatureCard anchorSeat = FileNature.Inspect(observed);

                    if (anchorSeat.DeclaredVolumeCount > 0
                        && anchorSeat.Size > 0
                        && anchorSeat.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                    {
                        zipFullSize = anchorSeat.Size;
                    }
                }
                catch
                {
                    // 读不出来就维持 0（这一档不启用）。
                }
            }

            if (zipFullSize <= 0)
            {
                /*
                 * `observed` 可能只是包装壳（卡上没有自述）；首卷自述在候选池里的另一份卡上。
                 * 这是唯一一个“内容能给出满片尺寸”的出口，⛔ 不按名字猜大小。
                 */
                foreach (string candidateFile in allFiles)
                {
                    try
                    {
                        FileNatureCard candidateSeat = FileNature.Inspect(candidateFile);

                        if (candidateSeat.DeclaredVolumeCount > 0
                            && candidateSeat.Size > 0
                            && candidateSeat.BodyKind == ArchiveBodyKind.VolumeFirstPiece)
                        {
                            zipFullSize = candidateSeat.Size;
                            break;
                        }
                    }
                    catch
                    {
                        // 单份读不出来就跳过。
                    }
                }
            }

            if (zipFullSize <= 0)
            {
                zipFullSize = sweepAnchorSize;
            }

            foreach (string file in allFiles)
            {
                /*
                 * **只问一个出口**（`AcceptsAsGroupPiece`）：底层识别先行、名字只保底。
                 * ⛔ 原来这里与补扫那一侧各有一套阶梯，于是"中间片"在补扫里永远进不来
                 * （`19191919` 的第二片就是这样被挡掉的）。
                 */
                if (VolumeGatherStep.AcceptsAsGroupPiece(FileNature.Inspect(file), reference, referenceBase, zipFullSize))
                {
                    paths.Add(file);
                    continue;
                }

                /*
                 * ===== 名字不放行时**问卡**（用户 2026-10-17 总则：底层识别优先）=====
                 * 现场（调试器逐字，`10101010`，`VolumeGatherPlanner.Decide` 帧 0）：
                 * `Complete=false｜PieceCount=1｜自述 3 片 / 62919013｜合计 26214400` ——
                 * 卡完全认得出这一组，可决策那一刻候选里**只有 1 片**：`pack111-L2.7z` 的卷标记
                 * 被伪装法吃掉，名字那把尺子把它判成"不是这一组" ⇒ 自述永远对不上。
                 * 判据：卡说它是这一族的一片（`CardSaysItIsOneOfTheGroup`）**且基名与本组相关**
                 * （既有弱证据尺子 `BaseNameRelatedToGroup`）⇒ 收进候选。
                 * ⛔ 后面那些闸门（同号两份 / 体积规律 / 整组自洽 / 试开）一条不落。
                 */
                if (referenceBase.Length > 0
                    && VolumePieceFacts.CardSaysItIsOneOfTheGroup(FileNature.Inspect(file))
                    && VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetArchiveBaseName(file), referenceBase))
                {
                    paths.Add(file);
                    continue;
                }

                /*
                 * ===== 名字与基名**全被改烂**的那一档：只认卡上的 zip 分片事实 =====
                 * （现场新样本 `6666`：三片叫 `打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003`，
                 *  基名一个都对不上，但卡上全是 zip 流：一片有本地头、一片是中间片、一片尾部有 EOCD。）
                 * ⛔ 只在"入口那一份也是 zip 分片"时才这么做（`entryIsZipPiece`），
                 *   免得把同层别的 zip 包卷进来；后面那几道闸门照旧。
                 */
                if (entryIsZipPiece)
                {
                    FileNatureCard candidateCard = FileNature.Inspect(file);

                    /*
                     * ⚠ 单卷 zip 也有 EOCD，但它**可证不是分卷成员**（`BodyKind=SingleComplete`）⇒ 不收。
                     * ⚠ 中间片的名字与头都认不出（现场 `6666`：`打包111-W1-0.png`，头是数据中间切开的）
                     *   ⇒ 用文档 §6.6 A 的**字节数守恒**这条证据：与"第 1 片"同样大的、且没有被内容证明
                     *   不是成员的，才当中间片。
                     */
                    bool isZipPiece = candidateCard.Format == VolumeContentFormat.Zip
                        && candidateCard.BodyKind != ArchiveBodyKind.SingleComplete
                        && (candidateCard.HasZipEndRecord
                            || candidateCard.HasZipLocalHeader
                            || candidateCard.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember);

                    bool isByteConservedMiddle = !isZipPiece
                        && zipFullSize > 0
                        && candidateCard.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
                        && candidateCard.Size > 0
                        && Math.Abs(candidateCard.Size - zipFullSize) <= Math.Max(1, zipFullSize / 100);

                    if (isZipPiece || isByteConservedMiddle)
                    {
                        paths.Add(file);
                    }
                }
            }

            /*
             * **被筛掉的那几份要能看见为什么**（用户 2026-10-17：「判据输入必须写进日志」）。
             * 只在"确实丢了东西"时写（收集到的比收进来的多），否则正常任务不加噪声。
             * 逐条给：文件名 / 名字那把尺子认不认 / 卡上的分卷身份与 BodyKind / 卡上的基名。
             */
            if (allFiles.Count > paths.Count)
            {
                var dropped = new List<string>();

                foreach (string file in allFiles)
                {
                    string candidateName = FileNameHelper.GetFileName(file);

                    if (paths.Contains(file))
                    {
                        continue;
                    }

                    FileNatureCard droppedCard = FileNature.Inspect(file);

                    dropped.Add(
                        candidateName
                        + "〔名字同组=" + (VolumeGroupDetector.BelongsToSameGroup(candidateName, reference) ? "是" : "否")
                        + $"｜分卷身份={droppedCard.Membership}"
                        + $"｜BodyKind={droppedCard.BodyKind}"
                        + $"｜卡上基名={droppedCard.ArchiveBaseName}"
                        + $"｜基名与本组相关={(VolumeGatherStep.BaseNameRelatedToGroup(droppedCard.ArchiveBaseName, referenceBase) ? "是" : "否")}〕");
                }

                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一层收集到 {allFiles.Count} 份、收进候选 {paths.Count} 份 —— 被筛掉的："
                    + string.Join("；", dropped)
                    + $"｜参照名=「{reference}」参照基名=「{referenceBase}」");
            }

            return paths;
        }
        /// <summary>
        /// **手上那一片的取法 = 唯一一处**（用户 2026-10-17：删掉 `memberPath` / `task.CurrentPath`
        /// 两套取法）。调用方给了 `memberPath` 就用它（接片那条路手上就是别人那一份），
        /// 否则用这一单账上的路径；两个都没有 ⇒ 空串（下游按"判不出"办）。
        /// </summary>
        private static string ResolveObservedPiecePath(ArchiveTask? task, string? memberPath)
            => !string.IsNullOrWhiteSpace(memberPath)
                ? memberPath!
                : task?.CurrentPath ?? string.Empty;

        /// <summary>
        /// 收片 / 组读取之间传槽位用的**键**：盘上物理身份（卷序列号:文件索引）；判不出身份才退回完整路径。
        /// <para>⛔ 用身份不用路径（2026-10-18 困难批 `19191919` 实测）：接片会移动 / 建硬链接，
        /// 路径随动作而变 —— 按原路径查表会落空、又退回"名字优先"，两片就撞同一个规范名。</para>
        /// </summary>
        private static string VolumeSlotKey(string path)
        {
            FileNatureCard card = FileNature.Inspect(path);

            return card.HasIdentity
                ? card.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture)
                  + ":"
                  + card.FileIndex.ToString("X", CultureInfo.InvariantCulture)
                : SafePathHelper.GetFullPathSafe(path);
        }

        /// <summary>
        /// **按试开确认的顺序，先清掉入口层里"旧规范名"**（2026-10-18 新批 `24242424` 定案）。
        ///
        /// <para>现场：入口层已有 `.z01`/`.z02` 两个**我们自己早先建的硬链接**，但内容顺序把另一份中段片
        /// 排到槽位 1 ⇒ 接片那一档按内容算出的规范名与旧链接撞名（目标名已被别的文件占着）⇒ 一片接不进、
        /// 整组永远缺卷。旧链接是零字节硬链接（linkCount &gt; 1），删掉名字不丢字节；而用户源包 / 任务源路径
        /// 一律不碰（⛔ 红线不变）。</para>
        ///
        /// <para>清完再由既有接片出口按内容槽位建正确的规范名；⛔ 不在这里建链接、不碰非规范名的原件。</para>
        /// </summary>
        private void PrepareCanonicalVolumeNamesForConfirmedOrder(
            string entryLayer,
            IReadOnlyList<string> orderedPieces,
            string stem,
            VolumeNamingFamily family,
            string logName)
        {
            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(stem, family, orderedPieces.Count);

            if (names.Count != orderedPieces.Count)
            {
                return;
            }

            for (int index = 0; index < names.Count; index++)
            {
                string target = Path.Combine(entryLayer, names[index]);

                if (!File.Exists(target))
                {
                    continue;
                }

                bool samePhysical = FileIdentity.IsSamePhysicalFile(target, orderedPieces[index]);
                bool hasLinks = FileIdentity.TryGetLinkCount(target, out uint linkCount);
                bool userSource = IsUserImportedSourcePath(target);
                bool taskSource = IsAnyTaskSourcePath(target);

                AppendLog(
                    "INFO",
                    $"{logName}：规范名预清理判据 —— {names[index]}"
                    + $"｜同一份={samePhysical}"
                    + $"｜链接数={(hasLinks ? linkCount.ToString(CultureInfo.InvariantCulture) : "判不出")}"
                    + $"｜用户源={userSource}｜任务源={taskSource}");

                if (samePhysical
                    || !hasLinks
                    || linkCount <= 1
                    || userSource
                    || taskSource)
                {
                    continue;
                }

                try
                {
                    File.Delete(target);

                    AppendLog(
                        "INFO",
                        $"{logName}：按试开确认的顺序，收掉入口层里旧的规范名 {names[index]}"
                        + "（零字节硬链接，删名字不丢字节）");
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"{logName}：旧的规范名 {names[index]} 没能收掉（{ex.Message}）");
                }
            }
        }

        private void TryGatherGroupPiecesInto(ArchiveTask task, string entryLayer, string? memberPath = null, bool allowMoveIntoGroup = true, string? cardEntryPath = null, ArchiveFixer.Detection.CardVolumeGroupReading? cardEvidence = null)
        {
            if (task == null || string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return;
            }

            /*
             * 候选 = ①这一单账上那几卷 + ②**全表里同组的每一条**（真机 CCCC：`.z03` 那一单在
             * `111(4)\`，它自己还没轮到重判 ⇒ 只看这一单的账就会漏掉它，整组永远停在 3 卷）。
             * ⛔ "同组"仍由既有尺子回答（`BelongsToSameGroup`），⛔ 接片仍只硬链接、绝不搬用户文件。
             */
            string tailName = ResolveGroupReferenceName(
                task,
                FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath)),
                memberPath);
            var sources = new List<string>(task.VolumePaths);

            /*
             * ⛔ **组读取排出来的那几片本身就是源**（2026-10-18，困难批 `19191919` 实测）：
             * `sources` 原来只收"任务表里的同组成员"，而第 3 片（`bbb-L2.zip-658`）既不是任务、
             * 也不在任何一单的其余物里（它被扫描判成"不是压缩包"⇒ 跳过）⇒ 它永远接不进来
             * ⇒ 整组缺末片 ⇒ `contentBytes=0`。内容已经把它排进槽位 3 ⇒ 原样当源收下，
             * 接片仍走它自己的闸门（硬链接 / 不碰用户源包）。
             */
            if (cardEvidence != null)
            {
                foreach (ArchiveFixer.Detection.CardVolumeSlot slot in cardEvidence.Slots)
                {
                    if (slot.Path.Length > 0 && File.Exists(slot.Path))
                    {
                        sources.Add(slot.Path);
                    }
                }
            }
            string groupBaseForScan = FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath));

            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                string path = other?.CurrentPath ?? string.Empty;

                if (path.Length == 0 || !File.Exists(path))
                {
                    continue;
                }

                if (MemberBelongsToGroup(path, tailName))
                {
                    sources.Add(path);
                    continue;
                }

                /*
                 * ⚠ 2026-10-16（真机 `29292929` 的永久判据输入逐字）：片名被改烂时，**名字那把尺子**
                 * （`MemberBelongsToGroup`）会把**同组的兄弟片**判成"不是这一组"（`pack111-L2` 对
                 * `tailName=pack111-L2.zip`）⇒ 这一趟只收到入口自己 ⇒ 兄弟片永远接不进来，
                 * 而接片那一档又借不到组（同一时刻 `任务表首卷份数=0`，见 §29.25）。
                 *
                 * <para>⇒ 补一条**内容证据**（卡那一条路，用户口径「先看看底层能够识别到文件是什么东西」）：
                 * 卡上自报"我是这一族的成员"（`Membership == VolumeMember` 或 `BodyKind == VolumeFirstPiece`）
                 * **且卡上的基名与这一组相关**（`BaseNameRelatedToGroup`，既有的弱证据尺子）才收进来。
                 * ⛔ 判不出就不收（老口径一个字不改）；后面那三道闸门（同组 / 卷号一份对一份 / 整组自洽）
                 * 照旧一条不落 —— 这一条只是把"兄弟片在哪"这一件事从名字换成内容。</para>
                 */
                FileNatureCard otherCard = FileNature.Inspect(path);

                bool cardSaysMember =
                    otherCard.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || otherCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece;

                if (cardSaysMember
                    && groupBaseForScan.Length > 0
                    && VolumeGatherStep.BaseNameRelatedToGroup(otherCard.ArchiveBaseName, groupBaseForScan))
                {
                    sources.Add(path);
                }
            }

            /*
             * ⚠ 2026-10-16（探针逐字：`[组] 单=pack111-L2 tailName=pack111-L2.zip 层=…\pack111\pack111-W0-0 源数=1 源=pack111-L2.part1.rar`）：
             * **组的入口就躺在成员名单 `sources` 里**，而每一条中段任务自己算出来的 `tailName` 是垃圾
             * （`pack111-L2.zip` / `pack111-L2.part3.zip`）⇒ 接片那一档只看"这一片自己的名字"永远推不出这一组。
             * ⇒ 把**入口那一份的路径**一路交下去（既有先例 = 同一个方法上的 `layerOverride`：
             * "外层已经定过的层，原样交下来用"）。⛔ 判据仍是卡上的 `BodyKind`（§29.8/§29.16 的教训），
             * 拿不到 ⇒ null，接片那一档照旧自己找（老口径一个字不改）。
             */
            string? groupEntryPath = !string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath)
                ?
                /*
                 * **调用方已经把卡上的入口算好了**（`VolumePiecesFromCards.EntryPath`）⇒ 直接用它，
                 * 不再在这里重读一遍卡（同一件事的真值只允许有一个出口；用户 2026-10-17 总则：
                 * 底层识别优先）。⛔ 只在"盘上真有它"时采用。
                 */
                cardEntryPath
                : sources.FirstOrDefault(item =>
                    File.Exists(item) && FileNature.Inspect(item).BodyKind == ArchiveBodyKind.VolumeFirstPiece);

            /*
             * ===== 内容定的槽位 + 内容定的命名族（2026-10-18，困难批 `19191919`）=====
             *
             * <para>组读取（`VolumePiecesFromCards.Read`）已经用**内容**排出槽位（本地文件头 / 中间 / 尾部 EOCD），
             * 也给出了命名族（`ZipEndRecordMeansSplit` ⇒ `ZipNumbered`）。而接片这一档原来只拿**名字**算规范名
             * —— `X.zip` 被读成跨盘族的末片 ⇒ `X.z01` / `X.zip` 撞名 ⇒ 整组接不起来。
             * ⇒ 把这两个内容事实原样交下去；判不出（没有卡证据）⇒ null，接片照旧走名字那条路。</para>
             */
            var contentSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (cardEvidence != null)
            {
                foreach (ArchiveFixer.Detection.CardVolumeSlot slot in cardEvidence.Slots)
                {
                    /*
                     * ⚠ 键用**盘上物理身份**（见 VolumeSlotKey），⛔ 不用路径：接片会移动 / 建硬链接，
                     * 按原路径查表会落空 ⇒ 又退回"名字优先"⇒ 两片撞同一个规范名。
                     */
                    contentSlots[VolumeSlotKey(slot.Path)] = slot.Index;
                }
            }

            VolumeNamingFamily? groupNamingFamily = cardEvidence?.NamingFamily;

            /*
             * **每个槽位"正确规范名"的路径**（给"收旧名字"那一条判据用）：只在这一组有内容槽位、族判得出、
             * 规范名拼得出时才有值；⛔ 判不出 ⇒ null，接片照旧不碰任何旧名字。
             */
            IReadOnlyDictionary<int, string>? slotCanonicalTargets = null;

            if (cardEvidence != null
                && groupNamingFamily is VolumeNamingFamily namingFamily
                && cardEvidence.Slots.Count >= 2)
            {
                string slotStem = FileNameHelper.GetArchiveBaseName(cardEvidence.Slots[0].Path);
                IReadOnlyList<string> slotNames = VolumeNumberFromContent.BuildStandardNames(
                    slotStem,
                    namingFamily,
                    cardEvidence.Slots.Count);

                if (slotNames.Count == cardEvidence.Slots.Count)
                {
                    var slotTargets = new Dictionary<int, string>();

                    for (int slotIndex = 0; slotIndex < cardEvidence.Slots.Count; slotIndex++)
                    {
                        slotTargets[cardEvidence.Slots[slotIndex].Index] =
                            Path.Combine(entryLayer, slotNames[slotIndex]);
                    }

                    slotCanonicalTargets = slotTargets;
                }
            }

            if (cardEvidence != null && contentSlots.Count > 0)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：内容槽位判据输入 —— "
                    + string.Join(
                        "、",
                        cardEvidence.Slots.Select(slot =>
                            slot.Index + ":" + FileNameHelper.GetFileName(slot.Path) + "（" + VolumeSlotKey(slot.Path) + "）")));
            }

            if (contentSlots.Count > 0)
            {
                var srcTrace = sources
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => FileNameHelper.GetFileName(path) + "（" + VolumeSlotKey(path) + "）");

                AppendLog(
                    "INFO",
                    $"{task.LogName}：收片源判据输入 —— 源=[{string.Join("、", srcTrace)}]"
                    + $"｜内容槽位=[{string.Join("、", contentSlots.Select(pair => pair.Value + ":" + pair.Key))}]"
                    + $"｜入口=「{FileNameHelper.GetFileName(groupEntryPath ?? string.Empty)}」"
                    + $"｜族={groupNamingFamily}");
            }

            /*
             * 处理顺序 = **内容槽位从大到小**（2026-10-18 新批 `24242424`）：先处理末片/高槽位，
             * 它们可能正占着低槽位的名字；等高槽位那一片已经在自己的规范名下，低槽位再收旧名字才安全。
             * 判不出槽位的源排在最前（不参与换名，按老口径）。
             */
            var orderedSources = sources
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    int slot = contentSlots.TryGetValue(VolumeSlotKey(path), out int byContent)
                        ? byContent
                        : int.MaxValue;

                    return (Path: path, Slot: slot);
                })
                .OrderByDescending(item => item.Slot)
                .ToList();

            foreach ((string path, int _) in orderedSources)
            {
                try
                {
                    TryAdoptUnresolvedVolumePiece(
                        path,
                        task,
                        entryLayer,
                        groupEntryPath,
                        allowMoveIntoGroup,
                        contentSlots,
                        groupNamingFamily,
                        slotCanonicalTargets);
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                }
            }

            /*
             * ⚠ 再扫一遍**我们自己那几棵树**：成品那一层 / 其余物那一层 / **这一单自己的落点层**。
             *
             * · 成品 + 其余物：单层路里入口包（真机 `111.zip`）是**下一轮的内层包**，正躺在"其余物"里
             *   （那条路承诺"内层包原样留着"，`InnerLayerContinuationTests` 钉着这条红线）⇒ 光看任务表看不见它。
             * · **这一单的落点层（`OutputPath`）**：每一条链解开之后是**它自己**把片"定稿"进自己那一层的
             *   （`PublishUnresolvedPieceLandingCopies` 用的就是这一个事实位）—— 而 `ContentDirectoryPath`
             *   只是**成功那条路**才记的一笔账。
             *
             * ⚠ 为什么必须补第 ③ 个目录（2026-10-11 调试器实测，`TryGatherGroupPiecesInto` 帧里读的变量）：
             * 真机形状「入口包最后到」跑批末那一遍时
             *   `pack3.rar|…\in\pack3\pack3 ;; pack2.rar|（空串） ;; pack1.rar|（空串） ;; 111_.part4.rar|（空串）`
             * —— 四单的 `ContentDirectoryPath` 只有 pack3 有值；而同一帧里 `OutputPath` 四单**全都有值**
             *   （`pack2.rar|…\in\pack2\pack2` 正是 `111_.part2.rar` 躺着的那个目录）。
             * `pack2.rar` 之所以没记上那一笔：它的链**校验未通过**（产物校验未通过：预期 2 个文件 / 2097152 字节，
             * 实际 1 个 / 1048576 字节），在那条 `return false` 上早退了，`ContentDirectoryPath` 的赋值在它下面
             * ⇒ 这一组永远收不到 part2 ⇒ 四片永远凑不齐。**这一档与"谁先到"无关**：片在不在自己落点层，
             * 是那一条链定稿之后盘上的事实。
             *
             * ⛔ 这一步只**硬链接**（零字节、只读），⛔ 其余物里那一份一个字节都不动 ——
             * 它的去留仍由既有口径决定（轮次续解会把剩下的当内层包接着解）。
             */
            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                if (other == null)
                {
                    continue;
                }

                foreach (string directory in new[]
                         {
                             other.ContentDirectoryPath, other.RestDirectoryPath, other.OutputPath
                         })
                {
                    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (string path in CollectVolumeFilesInDirectory(directory))
                    {
                        try
                        {
                            if (VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                            {
                                TryAdoptUnresolvedVolumePiece(
                                    path,
                                    task,
                                    entryLayer,
                                    groupEntryPath,
                                    allowMoveIntoGroup,
                                    contentSlots,
                                    groupNamingFamily);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog("WARN", $"跨链收卷跳过（{ex.Message}）。");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// **按 7z 起始头自述的字节数把缺口补齐**（2026-10-13 困难批 `1111`，用户口径「靠底层识别」）。
        ///
        /// <para><b>现场（运行期事实）</b>：那一组的三片里，入口片 `111-L2.7z.001` 已经由「还原」改回规范名、
        /// 也留在了盘上（第 15 修），链里逐字
        /// `7z 起始头自述这一包应当有 62907634 字节，手上这几卷加起来只有 26214400 字节`；
        /// 而另外两片被伪装成 `111-L2.7z`（吃掉卷标记 = "伪装成单一"）与 `111-L2.7z-924`（本体后缀后粘垃圾尾巴）
        /// ⇒ 名字那一栏读不出卷号 ⇒ 收片（按名字）永远收不到它们 ⇒ 整组凑不齐 ⇒ ①红。
        /// 算术上正好闭合：`62907634 − 26214400 = 36693234 = 26214400 + 10478834`（那两片自己的字节数）。</para>
        ///
        /// <para><b>判据（只用两条既有的硬证据，⛔ 不猜卷号）</b>：
        /// ① <see cref="SevenZipStartHeader.TryReadExpectedTotalBytes"/> 读出"整包应当有多少字节"（明文起始头）；
        /// ② 候选必须**体积正好凑上**那个差额（1–3 份、每份不超过手上最大的那一卷）、
        ///    且按内容**不是可读归档**（<see cref="FileNature.Inspect"/> 说格式未知或 7z —— 中间片本来就没有身份信息）；
        /// ③ 填完之后必须**1..N 连号 + 除末片外等大 + 总字节数正好等于自述值**。
        /// ⇒ 用**规范槽位名**（唯一出口 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>）
        /// 把它们**硬链接**进这一层（⛔ 原件一个字节都不动、⛔ 绝不覆盖已有名字）。
        /// ⛔ 判不出（凑不出正好相等 / 三条有一条不成立）⇒ 一份都不补。</para>
        /// </summary>
        /// <summary>
        /// **照"已经在层里的那几片"的命名规律**，给缺的这一片起一个同族的名字（唯一出口）。
        ///
        /// <para>⚠ 2026-10-14（实测 `3333` 组合 1 定案）：缺片那一档原来一律走
        /// <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>，而那一族**没有**
        /// "`X.zip.001`"这一格 ⇒ 落到 default ⇒ 出来的是 `111-L2.003`，可层里那两片叫
        /// `111-L2.zip.001/.002` ⇒ 引擎按名字找 `.zip.003` **找不到**（日志逐字：链里仍然缺第 3 片）⇒ ①红。
        /// ⇒ 按**既有片的写法**换掉卷号那一段（宽度照旧、前后缀逐字保留）：
        /// `111-L2.zip.002` ⇒ `111-L2.zip.003`、`111-L2.001` ⇒ `111-L2.002`、
        /// `x.part2.rar` ⇒ `x.part3.rar`、`x.z01` ⇒ `x.z03`（跨盘 ZIP 族的反向编号不在这里动，见调用点的过滤）。</para>
        ///
        /// <para>判不出（找不到卷号那一段 / 拼出来的名字自验不过）⇒ 回落到既有那个出口；
        /// 仍然判不出 ⇒ false（调用方什么都不做）。</para>
        /// </summary>
        internal static bool TryNameSlotLikeExistingPiece(string existingPiecePath, int slot, out string name)
        {
            name = string.Empty;

            string existingName = FileNameHelper.GetFileName(existingPiecePath);

            if (existingName.Length == 0)
            {
                return false;
            }

            // ① 数字分片（`…002` / `…002` 前带任何前后缀，宽度照旧）
            System.Text.RegularExpressions.Match digits = System.Text.RegularExpressions.Regex.Match(
                existingName,
                @"(?<head>.*?)(?<number>\d{2,})(?<tail>\D*)$");

            if (digits.Success)
            {
                string width = digits.Groups["number"].Value.Length.ToString(CultureInfo.InvariantCulture);

                name = digits.Groups["head"].Value
                    + slot.ToString("D" + width, CultureInfo.InvariantCulture)
                    + digits.Groups["tail"].Value;

                return name.Length > 0;
            }

            // ② `partN`（RAR 新式）
            System.Text.RegularExpressions.Match part = System.Text.RegularExpressions.Regex.Match(
                existingName,
                @"(?<head>.*?part)(?<number>\d+)(?<tail>.*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (part.Success)
            {
                name = part.Groups["head"].Value
                    + slot.ToString(CultureInfo.InvariantCulture)
                    + part.Groups["tail"].Value;

                return name.Length > 0;
            }

            return false;
        }

        /// <summary>
        /// **给缺的这个槽位起一个名字**（唯一出口；字节路与打分分配路都走这里）。
        ///
        /// <para>① 先照"**已经在层里的那一片**"的写法（同族同宽、前后缀逐字保留，
        /// <see cref="TryNameSlotLikeExistingPiece"/>），拼完**自验** —— 名字读出来的卷号必须正好是这个槽位；
        /// ② 过不了才回落既有命名出口 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/>
        /// （同样自验）；③ 两条都不成 ⇒ 空串，调用方**一份都不补**。</para>
        ///
        /// <para>⚠ 2026-10-14 实测 `28282828`（磁盘逐字）：字节路原来只走 ②，而 `X.7z.001` 这一族的
        /// 规范名出口**给不出 `.7z` 那一段** ⇒ 建出来的链接叫 `111-L2.002`，可层里那一片叫
        /// `111-L2.7z.001` ⇒ 引擎按 `111-L2.7z.002` 找 **找不到** ⇒ 组照样凑不齐（这就是"改对了槽位、
        /// 起错了名字"）。⛔ 与打分路同一条判据，⛔ 不新写第二套命名规则。</para>
        /// </summary>
        /// <summary>
        /// **这一层的命名族（由内容证据定）**：层里只要有一片 `ZipEndRecordMeansSplit`（通用分片末片），
        /// 这一组就是 `ZipNumbered`；判不出 ⇒ null（调用方照旧按名字）。
        ///
        /// <para>2026-10-18 困难批 `30303030` 运行期逐字：`按「打分分配」把 333-L2.zip.001 补成槽位名 333-L2.z01`
        /// —— 样板是 `X.zip` 时命名出口给的是跨盘族形状；而这一组的内容是通用分片 ⇒ 必须用数字样本。</para>
        /// </summary>
        private static VolumeNamingFamily? ResolveLayerNamingFamily(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            try
            {
                /*
                 * ⛔ 这一格**不再自己判一遍族**（2026-10-18 困难批 `15151515` 组合 6 的运行期定案）：
                 * 原来它只看 `ZipEndRecordMeansSplit` 一个布尔 ⇒ 把**跨盘 ZIP 的末片**也归纳成
                 * `ZipNumbered`（跨盘末片同样"中央目录起在这一份之前"，但它的 EOCD 盘号 &gt; 0）
                 * ⇒ 打分分配把第 1 片补成 `X.zip.002` 而不是 `X.z01` ⇒ 引擎找不到 `.z01` ⇒ 整组拼不起来。
                 * ⇒ 族的唯一出口在检测层（`VolumePiecesFromCards.ResolveNamingFamily`，
                 * 判据 = **带 EOCD 的那一片自己的盘号 / `Membership`**）；这一格只负责
                 * "把这一层的卡收集起来交给它"，并按本格的契约只接受 ZIP 那两个子族。
                 */
                List<FileNatureCard> cards = Directory.EnumerateFiles(directory!)
                    .Select(file => FileNature.Inspect(file))
                    .ToList();

                VolumeNamingFamily? family = Detection.VolumePiecesFromCards.ResolveNamingFamily(cards);

                return family is VolumeNamingFamily.ZipNumbered or VolumeNamingFamily.ZipSpanned
                    ? family
                    : null;
            }
            catch
            {
                // 读不动 ⇒ 判不出 ⇒ 照旧名字路。
            }

            return null;
        }
        private static string ResolveMissingSlotName(
            IReadOnlyDictionary<int, string> present,
            string entryName,
            int slot,
            VolumeNamingFamily? namingFamily = null)
        {
            string? existingPiece = present.Values
                .FirstOrDefault(path => VolumeGroupDetector.TryGetVolumeIndex(FileNameHelper.GetFileName(path)) != null);

            if (!string.IsNullOrEmpty(existingPiece)
                && TryNameSlotLikeExistingPiece(existingPiece!, slot, out string likeExisting)
                && VolumeGroupDetector.TryGetVolumeIndex(likeExisting) == slot)
            {
                return likeExisting;
            }

            /*
             * ⛔ **通用分片族：样本要有数字卷标记**（2026-10-18 困难批 `30303030` 运行期逐字：
             * `按「打分分配」把 333-L2.zip.001 补成槽位名 333-L2.z01`）。
             * 样板是 `X.zip` 时命名出口给的是跨盘族形状（槽位 2 = `.z01`）；而这一组的内容
             * 判据（`ZipEndRecordMeansSplit`）说它是通用分片 ⇒ 样本改成 `X.zip.001`，
             * 槽位 2 才会拼成 `X.zip.002`。⛔ 族判不出 ⇒ 照旧名字口径。
             */
            if (namingFamily == VolumeNamingFamily.ZipNumbered
                && !System.Text.RegularExpressions.Regex.IsMatch(entryName, @"\.\d{3}$"))
            {
                entryName += ".001";
            }

            if (VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(entryName, slot, out string canonical)
                && VolumeGroupDetector.TryGetVolumeIndex(canonical) == slot)
            {
                return canonical;
            }

            return string.Empty;
        }

        /// <summary>
        /// **候选池要扫的那几棵树**（唯一出口；两条路 —— 字节路与打分分配路 —— 都从这里取）。
        ///
        /// <para>⚠ 2026-10-14 实测 `28282828`（断点读运行期事实，⛔ 不是读代码推）：真第 3 片
        /// `111-L2.7z.00_!63` 躺在**它自己那一单的源目录**里（`…\28282828\111(3)\111(3)\`），
        /// 而原来这一档只扫"落点 / 内容目录 / 其余物目录" ⇒ `roots` 七条里**没有 `111(3)\111(3)`**
        /// （逐字：`…\111-W0-0`、`…\recipe`、`…\111(2)\111-W1-0`、`…\111(2)\111-W1-0\其余物`），
        /// 于是 `candidates.Count == 0` 而缺口是 `[2,3]` ⇒ 数目不等 ⇒ 判不出 ⇒ 这一组永远缺 2、3。</para>
        ///
        /// <para>⇒ 补上"每一单自己所在的那一层"。它落在**用户源树**里，所以从这一棵树选出来的候选
        /// 额外要求"基名与这一组有关系"（<see cref="BaseNameRelatedToGroup"/>）—— 用户原话
        /// 「两个看不懂就看**大小**、看**后缀有没有相似的地方**」。其余几棵树照旧（判据一个字不放宽）。</para>
        /// </summary>
        private List<(string Directory, ArchiveTask? Owner, bool RequiresBaseNameRelation)> CollectCandidateRoots(
            IEnumerable<ArchiveTask?> tasks,
            string entryLayer)
        {
            var roots = new List<(string, ArchiveTask?, bool)>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? directory, ArchiveTask? owner, bool requiresRelation)
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory!))
                {
                    return;
                }

                string full = SafePathHelper.GetFullPathSafe(directory!);

                if (known.Add(full))
                {
                    roots.Add((full, owner, requiresRelation));
                }
            }

            Add(entryLayer, null, false);

            foreach (ArchiveTask? other in tasks)
            {
                if (other == null)
                {
                    continue;
                }

                Add(other.OutputPath, other, false);
                Add(other.ContentDirectoryPath, other, false);
                Add(other.RestDirectoryPath, other, false);

                string? ownDirectory = null;

                try
                {
                    ownDirectory = string.IsNullOrWhiteSpace(other.CurrentPath)
                        ? null
                        : Path.GetDirectoryName(other.CurrentPath);
                }
                catch
                {
                    ownDirectory = null;
                }

                Add(ownDirectory, other, true);
            }

            return roots;
        }


        /// <summary>
        /// 自述片数是**硬上限**：名字读出 `index &gt; DeclaredVolumeCount` 的片，它的卷号**不可信** ⇒
        /// 从 `present` 里摘掉，并把路径交回给调用方当候选（它其实就是缺口里的那一片）。
        ///
        /// <para>⚠ 2026-10-14 实测 `28282828`（断点逐字）：真第 2 片原该叫 `111-L2.7z.002`，
        /// 套在壳里时成员名被写成 `111-L2.7z.004pv2` ⇒ 名字尺子读成 **4**
        /// （`Tolerant(末段「004pv2」) = True（→004，垃圾「pv2」）`）⇒ `present` 里凭空多出槽位 4、
        /// 缺口算成 `[2,3]`；而 7z 起始头**自述只有 3 片**（`entryCard.DeclaredVolumeCount == 3`）
        /// ⇒ 槽位 4 从内容上讲**不可能存在** ⇒ 那个名字在撒谎。
        /// ⛔ 摘掉的是**名字的解释**，盘上一个字节都不动（文件照旧在那一层、照旧进候选池）。</para>
        /// </summary>
        private static List<string> DetachImpossibleSlots(SortedDictionary<int, string> present, int declaredCount)
        {
            var detached = new List<string>();

            if (declaredCount <= 0 || present.Count == 0)
            {
                return detached;
            }

            foreach (int index in present.Keys.Where(key => key > declaredCount).ToList())
            {
                detached.Add(present[index]);
                present.Remove(index);
            }

            return detached;
        }

        /// <summary>
        /// **打分分配那一档**（用户 2026-10-14 口径 ①③；唯一执行体）：7z 自述字节数给不出证据时，
        /// 由"缺哪几个槽位"与"名字读不出卷号的候选"按三条既有证据投票分配，先试最高分那一版，
        /// 再来一趟就换边；两版都不成 ⇒ 回滚（删掉我们建的链接）。
        ///
        /// <para>三条证据（都是**既有**判据，⛔ 不新写命名规则）：
        /// ① 基名匹配 <see cref="FileNameHelper.GetArchiveBaseName"/>；
        /// ② 后缀相似（<see cref="VolumeSlotScoring.Similarity"/>，治"卷标记夹垃圾"）；
        /// ③ 空间计算 —— "除末卷外等大"（<see cref="VolumeGroupDetector.HasRegularVolumeSizes"/> 同一条规律）。
        /// 规范槽位名仍由 <see cref="VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex"/> 给。</para>
        ///
        /// <para>⛔ 三条红线：数目不等 / 判不出 ⇒ **一份都不补**；只**硬链接**（零字节、原件一个字节不动）；
        /// 失败把这一趟建的链接**删掉**（= 换回原来的样子）。</para>
        /// </summary>
        private void TryLinkMissingPiecesByScoredAssignment(
            ArchiveTask task,
            string entryLayer,
            string firstVolume,
            FileNatureCard entryCard,
            SortedDictionary<int, string> present,
            long largest)
        {
            string entryName = FileNameHelper.GetFileName(firstVolume);
            string groupBaseName = FileNameHelper.GetArchiveBaseName(entryName);

            if (groupBaseName.Length == 0 || present.Count == 0 || largest <= 0)
            {

                return;
            }

            string attemptKey = groupBaseName + "|" + SafePathHelper.GetFullPathSafe(entryLayer);

            /*
             * ⚠ 2026-10-14（实测 `28282828`，断点逐字）：**自述片数是硬上限** —— 名字读出"越界卷号"的那一片
             * 要从 present 摘掉：它其实就是缺口里的那一片，只是成员名被写坏了
             * （真第 2 片 `111-L2.7z.002` 套壳时被写成 `111-L2.7z.004pv2` ⇒ 名字尺子读成 4 号，
             * 而 7z 起始头自述只有 3 片）。摘掉之后它照常被候选池扫到、由打分分配挑槽位。
             * ⛔ 盘上一个字节都不动（摘掉的是"名字的解释"，不是文件）。
             */
            DetachImpossibleSlots(present, entryCard.DeclaredVolumeCount);

            if (present.Count == 0)
            {
                return;
            }

            int maxIndex = present.Keys.Max();

            // 候选池：各单那几棵树 + 这一层（⛔ 不扫全盘、⛔ 不碰用户源目录）
            var candidates = new List<VolumeSlotScoring.Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string presentPath in present.Values)
            {
                seen.Add(SafePathHelper.GetFullPathSafe(presentPath));
            }

            /*
             * ⚠ 2026-10-14（实测 `3333`）：**已经在层里的那几片的硬链接不算候选**。
             * 收片那一档是"零字节硬链接"，同一份字节常常在别的树里还有第二个名字
             * （实测逐字：`111(2)\…\111-W1-0\111-L2.zip.002` 与层里那份 `盘上身份` 逐字相同
             * = `7A7EB341:A00000002E572`）⇒ 只比路径会把**同一个文件的两个名字**当成两份片，
             * 于是缺口被算成 `[3,4]`、还拿 `.002` 去补第 3 槽（日志逐字那种错法）。
             * ⇒ 按**盘上物理身份**（卷序列号 + 文件索引，既有出口 `FileIdentity`）排除。
             */
            var presentIdentities = new HashSet<(ulong VolumeSerial, ulong FileIndex)>();

            foreach (string presentPath in present.Values)
            {
                if (FileIdentity.TryGetIdentity(presentPath, out ulong serial, out ulong index))
                {
                    presentIdentities.Add((serial, index));
                }
            }

            /*
             * ⚠ 2026-10-14（实测 `28282828`，断点逐字）：候选池那几棵树改由唯一出口
             * <see cref="CollectCandidateRoots"/> 给 —— 它比老写法多收"**每一单自己所在的那一层**"
             * （真片常常就躺在它旁边：那一份的真第 3 片在 `…\111(3)\111(3)\`，
             * 而老写法的 `roots` 七条里一条都没覆盖到它 ⇒ `candidates.Count == 0` ⇒ 判不出 ⇒ 组永远缺 2、3）。
             * 从**用户源树**里选出来的候选额外要求"基名与这一组有关系"（那道弱证据闸）。
             */
            foreach ((string root, ArchiveTask? rootOwner, bool requiresBaseNameRelation)
                     in CollectCandidateRoots(SnapshotTaskTable(Tasks), entryLayer))
            {
                _ = rootOwner;

                string[] found;

                try
                {
                    found = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string path in found)
                {
                    string full = SafePathHelper.GetFullPathSafe(path);

                    if (!seen.Add(full))
                    {
                        continue;
                    }

                    long size;

                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    if (size <= 0 || size > largest)
                    {
                        continue;
                    }

                    if (FileIdentity.TryGetIdentity(path, out ulong candidateSerial, out ulong candidateIndex)
                        && presentIdentities.Contains((candidateSerial, candidateIndex)))
                    {
                        continue;   // 这是层里某一已有的片的第二个名字（硬链接）⇒ 不是候选
                    }

                    FileNatureCard card = FileNature.Inspect(path);

                    if (card.BodyKind == ArchiveBodyKind.SingleComplete)
                    {
                        continue;   // 内容自证是完整单卷 ⇒ 绝不当补片
                    }

                    /*
                     * 谁有资格当候选：
                     * ① 内容自证"我是分卷组的一员"（RAR 的 Volume 位 / RAR4 首卷标记 / 跨盘 ZIP 盘号）；
                     * ② 或者"判不出"那一档（7z 中间片按定义读不出格式）—— ⛔ 只收这两档。
                     * 另外：名字的基名与这一组相同也算一条弱证据（走打分，不在这里硬卡）。
                     */
                    bool member = card.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember;
                    bool undecided = card.Format == VolumeContentFormat.Unknown
                        && card.Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;

                    if (!member && !undecided)
                    {
                        continue;
                    }

                    /*
                     * 谁能进候选（⚠ 2026-10-14 实测 `3333` 那一份后放宽了这一格，判据仍是事实）：
                     * 名字**读不出卷号**的 ⇒ 当然进；
                     * 名字**读得出卷号、可那个槽位已经有人占着**的 ⇒ 也进 —— 那正是"同名两份"的歧义
                     * （收片那一档按定义收不进去），必须靠这一档按体积/后缀/基名去补。
                     *
                     * 现场逐字（`3333` 组合 1）：第 3 片被伪装成"单一包名" `111-L2.zip`（原该是 `111-L2.zip.003`），
                     * 名字尺子把它读成"卷号 1" ⇒ 与已经在层的 `.001` **同号冲突** ⇒ 收片收不进、
                     * 这一档又被"读得出卷号就跳过"挡住 ⇒ 候选 0 个 ⇒ 整组永远缺第 3 片 ⇒ ①红。
                     * 而**空间规律**早就能认出它：`.001/.002` 都是 26 214 400，它是 10 490 534 ⇒
                     * "除末卷外等大" ⇒ 它就是**末片**。
                     */
                    /*
                     * ⚠ 2026-10-14（用户口径「专属算法不就是靠识别的吗」）：这里也**先问卡**
                     * （同一个唯一出口 `ResolveVolumeSlotIndexForPiece`）——
                     * 卡说"我不是第一片"而名字读出 1 的那一份，**不当成"已占住 1 号槽"**，
                     * 而是按"判不出号"进候选池，由"缺口 + 体积"去定（⛔ 不再拿假锚点补片）。
                     */
                    /*
                     * ⚠ 2026-10-16（用户拍板「RAR4 用主头 MHD_FIRSTVOLUME 认入口 + RawVolumeField 锚定基数」）：
                     * RAR 1.5–4.x 的头里**没有绝对卷号**，只有卷尾结尾块的原始字段 —— 卡上的 `VolumeNumber`
                     * 因此恒为空、名字尺子也读不出，于是这一档永远把它算成"名字读不出卷号"的候选，
                     * 再跟"缺口数"对不上就整档判不出（真机 `4444` 逐字：这一组缺 5 个槽位，而名字读不出
                     * 卷号的候选有 2 份 ⇒ 数目对不上 ⇒ 判不出）。
                     * ⇒ 用**入口那一份**（主头自报第 1 卷、它的原始字段就是基数）锚定，把绝对卷号翻出来；
                     * 唯一出口 = `VolumePieceFacts.ResolveRar4SlotIndex`（判不出返回 null，照旧什么都不做）。
                     */
                    int? nameIndex = ResolveVolumeSlotIndexForPiece(path, card)
                        ?? VolumePieceFacts.ResolveRar4SlotIndex(card, entryCard);

                    /*
                     * ===== 族不同 ⇒ 不许进这一组（用户 2026-10-17：「一切看底层识别的来」）=====
                     * 现场（新样本 `1111` 日志逐字）：`222-L1.zip.001 → 222-L2.part4.rar`
                     * （`基名 0/4，后缀相似 2/3（0.50），体积 3/3`）—— **内层 zip 组**的片被"打分分配"
                     * 塞进**外层 RAR 组**，多出 `part4/part5.rar` 两个名字 ⇒ "除末卷外等大"判据被带偏
                     * ⇒ 整组判成缺卷 ⇒ ①红。
                     * 判据只用卡上的族（`Format`）：两边都认得出且不同 ⇒ 不参选；判不出则照旧（不拦）。
                     */
                    if (card.Format != VolumeContentFormat.Unknown
                        && entryCard.Format != VolumeContentFormat.Unknown
                        && card.Format != entryCard.Format)
                    {
                        continue;
                    }

                    /*
                     * ⚠ 2026-10-14（实测 `28282828`，断点逐字）：**名字读出越界卷号 ⇒ 名字在撒谎**。
                     * 真第 2 片的成员名被写成 `111-L2.7z.004pv2` ⇒ 读成 4 号，而 7z 起始头自述只有 3 片
                     * （`entryCard.DeclaredVolumeCount == 3`）⇒ 它其实就是缺口里的那一片 ⇒ 必须进候选池。
                     */
                    bool impossibleSlot = nameIndex is int claimedIndex
                        && entryCard.DeclaredVolumeCount > 0
                        && claimedIndex > entryCard.DeclaredVolumeCount;

                    if (requiresBaseNameRelation
                        && !VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetFileName(path), groupBaseName))
                    {
                        continue;   // 用户源树里选出来的候选：基名得跟这一组有关系（弱证据那道闸）
                    }

                    if (impossibleSlot)
                    {
                        // 越界号 ⇒ 名字不可信 ⇒ 收（占哪个槽位由打分分配说了算）
                    }
                    else if (nameIndex is int claimed
                        && present.TryGetValue(claimed, out string? occupant)
                        && !SafePathHelper.PathEquals(occupant, path))
                    {
                        // 同号冲突 ⇒ 收片收不进去，正是这一档要处理的
                    }
                    else if (nameIndex != null)
                    {
                        continue;   // 名字读得出卷号、槽位也没冲突 ⇒ 归收片那一档
                    }

                    candidates.Add(new VolumeSlotScoring.Candidate
                    {
                        Path = path,
                        FileName = FileNameHelper.GetFileName(path),
                        Size = size,
                        ContentNumber = card.VolumeNumber
                    });
                }
            }

            /*
             * ⚠ 槽位推导分两步（用户口径 ①："除末卷等大" + "缺什么补什么"）：
             * ① 1..max 里的**洞**（按名字读得出卷号的那几片留下的缺口）；
             * ② 洞填完还不够（缺的那几片在"末片之后"，名字根本读不出来）⇒ 往 max 后面**续**，
             *    续几个由**候选数**定；内容算得出片数时（7z 的自述值）照旧封顶，⛔ 不越界。
             */
            var slots = new List<int>();

            for (int index = 1; index <= maxIndex; index++)
            {
                if (!present.ContainsKey(index))
                {
                    slots.Add(index);
                }
            }

            while (slots.Count < candidates.Count)
            {
                int next = ++maxIndex;

                if (entryCard.DeclaredVolumeCount > 0 && next > entryCard.DeclaredVolumeCount)
                {
                    break;
                }

                slots.Add(next);
            }

            AppendLog(
                "WARN",
                $"{task.LogName}：这一组的缺口 = [{string.Join(",", slots)}]，名字读不出卷号的候选 = "
                + $"[{string.Join(",", candidates.Select(item => item.FileName))}]");

            /*
             * ⚠ 实测 `24242424`：同一个名字的两份拷贝（不同树里）被当成两份片 ⇒ 缺口被算成 `[2,3]`、
             * 还拿**同一片**去补两个槽。⇒ 按"名字 + 大小"去重（同名的同一片只算一份）。
             */
            candidates = candidates
                .GroupBy(
                    item => item.FileName + "|" + item.Size.ToString(CultureInfo.InvariantCulture),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (slots.Count != candidates.Count)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一组缺 {slots.Count} 个槽位，而名字读不出卷号的候选有 {candidates.Count} 份 —— "
                    + "数目对不上 ⇒ 判不出，一份都不补（⛔ 不猜）。");

                return;
            }

            IReadOnlyList<VolumeSlotScoring.Assignment> ranked = VolumeSlotScoring.Rank(
                slots,
                candidates,
                groupBaseName,
                largest,
                maxIndex,
                slot => VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex(entryName, slot, out string canonical)
                    ? canonical
                    : string.Empty);

            if (ranked.Count == 0)
            {
                return;
            }

            int attempt = _scoredSlotAttempts.TryGetValue(attemptKey, out int done) ? done : 0;

            if (attempt >= ranked.Count)
            {
                AppendLog(
                    "WARN",
                    $"{task.LogName}：这一组的打分分配两版都试过了、仍然不成 ⇒ 不再动手、「保持原样」"
                    + "（我们建的槽位链接已经删掉；原件一个字节都没动）。");

                return;
            }

            // 回滚上一版：只删**我们自己建的**那几个槽位链接（零字节硬链接 ⇒ 删掉 = 回到原样）
            if (_scoredSlotLinks.TryGetValue(attemptKey, out List<string>? previous))
            {
                foreach (string link in previous)
                {
                    try
                    {
                        if (File.Exists(link))
                        {
                            File.Delete(link);
                        }
                    }
                    catch
                    {
                        // 删不掉就留着（它只是我们建的链接）。
                    }
                }

                previous.Clear();
            }

            VolumeSlotScoring.Assignment picked = ranked[attempt];
            var created = new List<string>();
            var linkedPairs = new List<(string Source, string Canonical)>();

            foreach ((VolumeSlotScoring.Candidate candidate, int slot) in picked.Pairs)
            {
                /*
                 * 槽位名：走**唯一出口** <see cref="ResolveMissingSlotName"/> —— 它自己会
                 * ①照"已经在层里的那一片"的写法（样板优先取名字读得出卷号的那一片）、②拼完自验、
                 * ③回落既有命名出口、④两条都不成 ⇒ 空串（这一趟整体回滚、一份都不补）。
                 * ⚠ 实测 `24242424`（垃圾名 `111-L2.part000000002.rar`）与 `28282828`（丢族段 `111-L2.002`）
                 * 两条教训都已经收进那一个出口里。
                 */
                string slotName = ResolveMissingSlotName(present, entryName, slot, ResolveLayerNamingFamily(entryLayer));

                if (slotName.Length == 0)
                {
                    Rollback(created);
                    return;
                }

                string target = Path.Combine(entryLayer, slotName);

                if (File.Exists(target)
                    || !HardLinkHelper.CanHardLink(candidate.Path, entryLayer)
                    || !HardLinkHelper.TryCreateHardLink(target, candidate.Path))
                {
                    Rollback(created);
                    return;
                }

                created.Add(target);
                linkedPairs.Add((candidate.Path, target));
            }

            _scoredSlotAttempts[attemptKey] = attempt + 1;
            _scoredSlotLinks[attemptKey] = created;

            foreach ((string source, string target) in linkedPairs)
            {
                string sourceName = FileNameHelper.GetFileName(source);

                _byteBudgetLinkedSources.Add((task, groupBaseName, source));
                RememberAdoptedLandedPiece(source, task, groupBaseName, renamedInPlace: false);

                AppendLog(
                    "INFO",
                    $"{task.LogName}：按「打分分配」把 `{sourceName}` 补成槽位名 `{FileNameHelper.GetFileName(target)}`"
                    + $"（第 {attempt + 1} 版尝试，零字节硬链接，原件一个字节没动）。"
                    + $"判据输入：{string.Join("；", picked.Evidence)}"
                    + (picked.HasTie ? "｜⚠ 最高分与次高分并列 ⇒ 这一版只是概率，不成会自动换边。" : string.Empty));
            }

            void Rollback(List<string> built)
            {
                foreach (string path in built)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // 删不掉就留着。
                    }
                }

                built.Clear();
            }
        }
        private void TryLinkMissingPiecesByByteBudget(ArchiveTask task, string entryLayer, string? memberPath, string? cardEntryPath = null)
        {
            if (task == null || string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                return;
            }

            string tailName = ResolveGroupReferenceName(
                task,
                FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath)),
                memberPath);

            var present = new SortedDictionary<int, string>(Comparer<int>.Default);
            long largest = 0;

            foreach (string path in CollectVolumeFilesInDirectory(entryLayer))
            {
                if (!File.Exists(path) || !VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                {
                    continue;
                }

                /*
                 * ⚠ 2026-10-14（用户口径「专属算法不就是靠识别的吗」）：槽位号**先问底层识别卡**
                 * （唯一出口 <see cref="ResolveVolumeSlotIndexForPiece"/>），卡上没有才问名字。
                 * 现场 `10101010`：三片里第 1 片与第 3 片**名字一模一样**（都读成 1），
                 * 只有卡分得开（`VolumeFirstPiece` vs `VolumePiece`）。
                 */
                int? index = ResolveVolumeSlotIndexForPiece(path, FileNature.Inspect(path));

                if (!index.HasValue || present.ContainsKey(index.Value))
                {
                    continue;
                }

                present[index.Value] = path;

                try
                {
                    largest = Math.Max(largest, new FileInfo(path).Length);
                }
                catch
                {
                    return;
                }
            }

            if (present.Count == 0 || !present.TryGetValue(1, out string? firstVolume) || largest <= 0)
            {
                return;
            }

            /*
             * ⛔ 判据全部走**底层识别那一张卡**（用户 2026-10-13 口径：「『名字被伪装到读不出卷号 ⇒ 组凑不齐』
             * 这个是**底层识别**的问题」）——这一档只消费，⛔ 不自己读魔数、⛔ 不自己解析起始头：
             *   · `BodyKind == VolumeFirstPiece` ⇒ 内容自证"入口那一份是这一组的第一片"（而不是一份单卷包）；
             *   · `DeclaredTotalBytes` ⇒ 整包应当有多少字节（7z 起始头明文自述）；
             *   · `DeclaredVolumeCount` ⇒ 按内容算出来的片数（整包 ÷ 这一片向上取整）。
             */
            FileNatureCard entryCard = FileNature.Inspect(firstVolume);

            if (entryCard.BodyKind != ArchiveBodyKind.VolumeFirstPiece
                || entryCard.DeclaredTotalBytes <= 0)
            {
                /*
                 * ⚠ 2026-10-14（用户口径 ①③：**"缺什么补什么"是概率，不是"试过没用就算了"**）：
                 * 7z 那条"自述整包字节数"给不出证据时（RAR / ZIP 族、或中间片），**不许就此收手** ——
                 * 改走**打分分配**：由"已知几片 + 缺哪几个槽位"与"名字读不出卷号的候选"，
                 * 按 基名 / 后缀相似 / 空间（除末卷等大）三条既有证据投票，取最高分那一版先试，
                 * 下一趟（同一组再来一次）**换边**试第二版；两版都不成 ⇒ 回滚（删掉我们建的那几个链接）。
                 */
                TryLinkMissingPiecesByScoredAssignment(task, entryLayer, firstVolume, entryCard, present, largest);

                return;
            }

            long expected = entryCard.DeclaredTotalBytes;
            int declaredCount = entryCard.DeclaredVolumeCount;

            /*
             * ⚠ 2026-10-14（实测 `28282828`）："越界卷号"的片（成员名被写坏 ⇒ 读成 4 号，而自述只有 3 片）
             * 从 present 摘掉后再算差额 —— 它其实就是缺口里的那一片，会由下面的候选池收进来。⛔ 盘上不动。
             */
            DetachImpossibleSlots(present, declaredCount);

            long actual = 0;

            foreach (string path in present.Values)
            {
                try
                {
                    actual += new FileInfo(path).Length;
                }
                catch
                {
                    return;
                }
            }

            long difference = expected - actual;

            if (difference <= 0)
            {
                return;
            }

            /*
             * 候选池 = 各单的几棵树 + 这一层（来源纪律唯一出口 <see cref="CollectCandidateRoots"/>：
             * ⛔ 不扫全盘；⚠ 2026-10-14 起**多收"每一单自己所在的那一层"** —— 实测 `28282828` 的真第 3 片
             * 就躺在它自己那一单的源目录里，老写法一条扫描根都没覆盖到它 ⇒ 候选 0 个 ⇒ 组永远缺 2、3）。
             * 只留"体积能派上用场"的那些：未知格式 / 7z 片段（中间片按定义读不出格式）。
             */
            var candidates = new List<(ArchiveTask Owner, string Path)>();
            string scanGroupBaseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(firstVolume));

            foreach ((string scanRoot, ArchiveTask? scanOwner, bool scanRequiresBaseNameRelation)
                     in CollectCandidateRoots(SnapshotTaskTable(Tasks), entryLayer))
            {
                string[] found;

                try
                {
                    found = Directory.GetFiles(scanRoot, "*", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string path in found)
                {
                    long size;

                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch
                    {
                        continue;
                    }

                    /*
                     * ⛔ **已经在组里的那几片，绝不许再拿来填缺口**（2026-10-18 困难批 `21212121` 组合 4
                     * 运行期逐字）：`.002` 还在并行那一单的包装壳里没落地时，这一档用**已经在 slot 1 的
                     * `bbb-L2.7z.001`** 凑了差额（日志：`用 bbb-L2.7z.001 → bbb-L2.7z.002`，零字节硬链接）
                     * ⇒ 这一组的 `.002` 是 `.001` 的字节 ⇒ 引擎对**真密码**也报
                     * `Data Error in encrypted file. Wrong password?` ⇒ 10 个候选全废 ⇒ ① 红。
                     * 判据两件盘上事实：① 路径就在 `present` 里；② 与 `present` 里某一份是**同一物理文件**
                     * （改名/硬链接，按 `FileIdentity` 认）。两条任一成立 ⇒ 它不是缺口候选。
                     * 判不出身份 ⇒ 照旧收（不放开任何既有判据）。
                     */
                    if (present.Values.Any(presentPath => SafePathHelper.PathEquals(presentPath, path)))
                    {
                        continue;
                    }

                    if (ArchiveFixer.Detection.FileIdentity.TryGetIdentity(path, out ulong candidateVolume, out ulong candidateIndex)
                        && present.Values.Any(presentPath =>
                            ArchiveFixer.Detection.FileIdentity.TryGetIdentity(presentPath, out ulong presentVolume, out ulong presentIndex)
                            && presentVolume == candidateVolume
                            && presentIndex == candidateIndex))
                    {
                        continue;
                    }

                    if (size <= 0 || size > largest || size > difference)
                    {
                        continue;
                    }

                    if (scanRequiresBaseNameRelation
                        && !VolumeGatherStep.BaseNameRelatedToGroup(FileNameHelper.GetFileName(path), scanGroupBaseName))
                    {
                        continue;   // 用户源树里选出来的候选：基名得跟这一组有关系（弱证据那道闸）
                    }

                    FileNatureCard card = FileNature.Inspect(path);

                    /*
                     * ⛔ **内容自证是"完整单卷归档"的那一份绝不许当补片用**（它就是一份正常包）；
                     * 只收"分卷组的一片"与"判不出"这两档（中间片按定义读不出格式）。
                     */
                    if (card.BodyKind == ArchiveBodyKind.SingleComplete
                        || (card.Format != VolumeContentFormat.Unknown
                            && card.Format != VolumeContentFormat.SevenZip))
                    {
                        continue;
                    }

                    candidates.Add((scanOwner ?? task, path));
                }
            }

            // 挑**正好凑上差额**的那几份（最多 3 份；体积降序 —— 与"除末卷外等大"那条规律一致）。
            var chosen = new List<(ArchiveTask Owner, string Path)>();
            long remaining = difference;

            foreach ((ArchiveTask owner, string path) in candidates
                         .OrderByDescending(item => new FileInfo(item.Path).Length)
                         .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (chosen.Count == 3)
                {
                    break;
                }

                long size = new FileInfo(path).Length;

                if (size > remaining)
                {
                    continue;
                }

                chosen.Add((owner, path));
                remaining -= size;

                if (remaining == 0)
                {
                    break;
                }
            }

            if (chosen.Count == 0 || remaining != 0)
            {
                /*
                 * ⚠ 2026-10-14（实测 `8888` / `28282828`）：字节数那条路**凑不出"正好相等"**时，
                 * 原来直接收手 —— 可那两片可能就在盘上、只是名字读不出卷号。
                 * ⇒ 落到下面那条打分分配（同一个出口；判据一条不放宽：数目不等照样一份都不补）。
                 */
                TryLinkMissingPiecesByScoredAssignment(task, entryLayer, firstVolume, entryCard, present, largest);

                return;
            }

            // 缺哪几个槽位：1..max 里的洞；不够就往 max 后面续（⛔ 上限 = 内容算出来的片数，算得出时）。
            var slots = new List<int>();
            int maxIndex = present.Keys.Max();

            for (int index = 1; index <= maxIndex; index++)
            {
                if (!present.ContainsKey(index))
                {
                    slots.Add(index);
                }
            }

            while (slots.Count < chosen.Count)
            {
                int next = ++maxIndex;

                if (declaredCount > 0 && next > declaredCount)
                {
                    return;   // 内容说这一组只有 declaredCount 片 ⇒ 判不出 ⇒ 一份都不补
                }

                slots.Add(next);
            }

            /*
             * **命名样本只走唯一出口**（用户 2026-10-17 小目标第③项）：族由卡的 `Format` 定，
             * 名字被伪装法改烂时也能给出本族形状；卡判不出（格式读不出来）才退回文件名当样本。
             * ⛔ 最终卷名仍由 `VolumeGroupResolver.TryBuildCanonicalVolumeNameForIndex` 生成。
             */
            string entryName = !string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath)
                ? FileNameHelper.GetFileName(cardEntryPath!)
                : FileNameHelper.GetFileName(firstVolume);

            if (!string.IsNullOrWhiteSpace(cardEntryPath) && File.Exists(cardEntryPath))
            {
                try
                {
                    string byCardFamily = VolumePiecesFromCards.CanonicalNameForSlot(
                        FileNature.Inspect(cardEntryPath!), 1);

                    if (byCardFamily.Length > 0)
                    {
                        entryName = byCardFamily;
                    }
                }
                catch
                {
                    // 卡读不出来 ⇒ 用文件名当样本（老口径）。
                }
            }
            var linked = new List<string>();
            string groupBaseName = FileNameHelper.GetArchiveBaseName(entryName);

            for (int i = 0; i < chosen.Count; i++)
            {
                /*
                 * ⚠ 2026-10-14（实测 `28282828`）：槽位名走**唯一出口**（照既有片写、拼完自验、回落规范名）
                 * —— 原来这里只用规范名出口 ⇒ `111-L2.7z.001` 那一族被起成 `111-L2.002` ⇒ 引擎找不到。
                 */
                string slotName = ResolveMissingSlotName(present, entryName, slots[i]);

                if (slotName.Length == 0)
                {
                    return;   // 推不出槽位名 ⇒ 一份都不补（已经建的那几个链接由下面回收）
                }

                string target = Path.Combine(entryLayer, slotName);

                if (File.Exists(target)
                    || !HardLinkHelper.CanHardLink(chosen[i].Path, entryLayer)
                    || !HardLinkHelper.TryCreateHardLink(target, chosen[i].Path))
                {
                    foreach (string built in linked)
                    {
                        try
                        {
                            File.Delete(built);
                        }
                        catch
                        {
                            // 删不掉就留着（它只是我们建的链接，不影响任何判据）。
                        }
                    }

                    return;
                }

                linked.Add(target);

                /*
                 * ⚠ 记下"**我亲手从这一棵树里把它链接成了槽位名**"（见 <see cref="_byteBudgetLinkedSources"/>）：
                 * 收场那一档据此按档处理它自己那一份（名字读不出卷号的片子，任何按名字的筛子都认不出它）。
                 */
                _byteBudgetLinkedSources.Add((chosen[i].Owner, groupBaseName, chosen[i].Path));

                /*
                 * ⚠ 2026-10-13（困难批 `1111` 判据 ③，运行期定案）：**同一件事写进既有那唯一一本账**。
                 *
                 * <para>现场（断点 + 插桩实测，`dbg-b6a4ee4a` 夹具那一轮）：这一档按内容把
                 * `111-L2.7z` / `111-L2.7z-924` 链接成 `111-L2.002` / `111-L2.003` 之后，
                 * `_adoptedLandedPieces`（`PurgeGroupLandedPieces` 用来找"产出方那一份"的那本账）**0 条**
                 * ⇒ 收场那一档的 `targets.Count == 0` ⇒ 两份落地副本留在
                 * `111(2)\…\111-W1-0\` / `111(3)\111-W2-0\` 里 ⇒ 判据 ③ 红。</para>
                 *
                 * <para>⛔ 不另造第二套判据：这一调走的就是"接片那一档"记那一份的**同一个出口**
                 * （<see cref="RememberAdoptedLandedPiece"/>），它自己会把"产出方是谁、落点根在哪、
                 * 是不是某一单自己的源"逐条问清；判不出 ⇒ 它什么都不记（照旧红，如实暴露）。</para>
                 */
                RememberAdoptedLandedPiece(chosen[i].Path, chosen[i].Owner, groupBaseName, renamedInPlace: false);

                /*
                 * ⛔ **补片这一档也要记进"谁吐过这一组的片"那本账**（2026-10-18 用户 `&&` 口径）：
                 * 走 `AdoptPieceIntoGroup` 那条路的片会自己记（见那里），而这一档是**按字节数硬链接补片**，
                 * 绕过了那个入口 ⇒ 那个原包在链尾永远收不了场（现场 `3333`：`222-L2.7z.^Z6002`
                 * 被链接成 `222-L2.7z.002`，组解开并校验通过，它却一直留着）。
                 * 片名用**槽位名**（`linked[i]`），⛔ 不用源文件那个脏名 —— 族判据要认得出族。
                 */
                RememberGroupPieceProducer(groupBaseName, FileNameHelper.GetFileName(linked[i]), chosen[i].Owner);
                RememberPieceSupplyingTask(groupBaseName, chosen[i].Path);
            }

            if (linked.Count > 0)
            {
                AppendLog(
                    "INFO",
                    $"{task.LogName}：按 7z 起始头自述的字节数补齐缺口 —— 自述 {expected} 字节、"
                    + $"手上 {actual} 字节、差 {difference} 字节 ⇒ 用 "
                    + string.Join("、", chosen.Select((item, i) => FileNameHelper.GetFileName(item.Path) + " → " + FileNameHelper.GetFileName(linked[i])))
                    + " 照既有片的写法起槽位名补进这一层（零字节硬链接，原件一个字节都不动）。");
            }
        }

        /// <summary>
        /// **整组已经在入口那一层凑齐了吗**（只看那一层，⛔ 不看这一单自己那一层）。
        ///
        /// <para>用户 2026-10-06：「111.zip 的位置已经确定，你把分卷移过来不就是了吗」——
        /// 收片那一档按规范卷名把片接进入口那一层之后，那一层里就是**一个目录里的一整组**；
        /// 而这一单自己那一层永远只有它自己一片。⇒ 判据只在入口那一层问，返回的组直接可以拿去开解。</para>
        ///
        /// <para>⛔ 判据本体一个字没改：仍然由 <see cref="VolumeGroupDetector.Group"/> 的
        /// "完整 / 卷号连续 / 除末片外等大"回答；⛔ 这一层里没有入口包 / 不完整 ⇒ 返回 null，
        /// 由调用方退回既有口径。</para>
        /// </summary>
        /// <summary>
        /// **按内容定的规范名把这一组认出来**（2026-10-18，困难批 `19191919`）。
        ///
        /// <para>族与槽位都来自内容（`CardVolumeGroupReading`）⇒ 入口就是槽位 1 的规范名
        /// （通用分片 = `X.zip.001`）；⛔ 不让"用户那一份原来的名字"（`X.zip`，同一份文件的别名）
        /// 冒充入口 —— 否则引擎按跨盘族的入口名去找 `.z01` ⇒ `Unexpected end of archive`、
        /// 整组判成缺卷（真跑逐字）。</para>
        ///
        /// <para>判不出 / 规范名不齐 ⇒ null（调用方照旧走名字那把尺子，一个字不放宽）。</para>
        /// </summary>
        private static VolumeGroup? ResolveCanonicalGroupFromCardSlots(
            string entryLayer,
            ArchiveFixer.Detection.CardVolumeGroupReading reading)
        {
            if (reading.NamingFamily is not VolumeNamingFamily family
                || reading.Slots.Count < 2
                || string.IsNullOrWhiteSpace(entryLayer))
            {
                return null;
            }

            /*
             * 基名从**卡上**取（唯一出口 `ArchiveBaseName`），⛔ 不从 `EntryPath` 取：
             * 不完整的组读取没有入口（`EntryPath` 为空），而槽位那边有每一片的路径。
             * 取第一个**还在盘上**的槽位；一个都不在 ⇒ 判不出。
             */
            string stem = string.Empty;

            foreach (ArchiveFixer.Detection.CardVolumeSlot slot in reading.Slots)
            {
                if (slot.Path.Length > 0 && File.Exists(slot.Path))
                {
                    stem = FileNature.Inspect(slot.Path).ArchiveBaseName;
                    break;
                }
            }

            if (stem.Length == 0)
            {
                return null;
            }

            IReadOnlyList<string> names = VolumeNumberFromContent.BuildStandardNames(stem, family, reading.Slots.Count);

            if (names.Count < 2)
            {
                return null;
            }

            var candidates = new List<VolumeCandidate>();

            foreach (string name in names)
            {
                string path = Path.Combine(entryLayer, name);

                if (!File.Exists(path))
                {
                    return null;
                }

                long size = -1;

                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    // 量不出大小不影响归组。
                }

                candidates.Add(new VolumeCandidate { Path = path, Size = size });
            }

            return VolumeGroupDetector.Group(candidates)
                .FirstOrDefault(group => group.IsComplete
                    && !string.IsNullOrWhiteSpace(group.FirstVolumePath)
                    && File.Exists(group.FirstVolumePath));
        }

        private VolumeGroup? ResolveGatheredGroupFromEntryLayer(
            ArchiveTask task,
            string? entryLayer,
            string? memberPath = null)
        {
            if (task == null || string.IsNullOrWhiteSpace(entryLayer) || !Directory.Exists(entryLayer))
            {
                AppendLog("WARN", $"收卷判据输入：{(task == null ? "(没有这一单)" : task.LogName)}｜该拼到哪一层=「{entryLayer ?? string.Empty}」｜这一层不在盘上 ⇒ 判不出完整的一组");

                return null;
            }

            try
            {
                string tailName = ResolveGroupReferenceName(
                    task,
                    FileNameHelper.GetArchiveBaseName(ResolveObservedPiecePath(task, memberPath)),
                    memberPath);
                var candidates = new List<VolumeCandidate>();

                foreach (string path in CollectVolumeFilesInDirectory(entryLayer))
                {
                    if (VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                    {
                        long size = -1;

                        try
                        {
                            size = new FileInfo(path).Length;
                        }
                        catch
                        {
                            // 量不出大小不影响归组。
                        }

                        candidates.Add(new VolumeCandidate { Path = path, Size = size });
                    }
                }

                var judged = new List<string>();

                foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
                {
                    judged.Add(
                        $"{group.BaseName}：完整={group.IsComplete} 已找到={group.KnownVolumeCount} 缺卷={(group.MissingVolumeNames.Count > 0 ? string.Join("、", group.MissingVolumeNames) : "（空）")} 说明={group.Note}｜成员={string.Join("、", group.Volumes.Select(item => FileNameHelper.GetFileName(item.Path)))}");

                    if (group.IsComplete
                        && !string.IsNullOrWhiteSpace(group.FirstVolumePath)
                        && File.Exists(group.FirstVolumePath))
                    {
                        AppendLog("INFO", $"收卷判据输入：引用名=「{tailName}」｜这一层同族同基名的候选 {candidates.Count} 个｜⇒ 判出完整的一组：首卷=「{FileNameHelper.GetFileName(group.FirstVolumePath)}」共 {group.KnownVolumeCount} 卷");

                        return group;
                    }
                }
                AppendLog("WARN", $"收卷判据输入：引用名=「{tailName}」｜这一层同族同基名的候选 {candidates.Count} 个（{string.Join("、", candidates.Select(item => FileNameHelper.GetFileName(item.Path)))}）｜判定器结论：{(judged.Count > 0 ? string.Join(" ； ", judged) : "一个分卷组都没归出来")}｜⇒ 判不出完整的一组");
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"收卷判据输入：判的时候抛了异常 ⇒ 判不出完整的一组（{ex.GetType().Name}：{ex.Message}）");
            }

            return null;
        }

        /// <summary>
        /// **这一层里的东西是不是都属于这一组**（"过路层"其余物放行前的**前置闸门**，唯一出口）。
        ///
        /// <para><b>为什么要有它</b>（2026-10-11）：这里原来把引用名写死成 `<组基名>.zip`，
        /// 而"同一组"那把尺子是**族 + 基名** —— 组只要是 RAR 新式（`X.partN.rar`）或数字族（`X.7z.001`），
        /// `BelongsToSameGroup(组内成员, "X.zip")` **恒为 false** ⇒ 闸门永远判"里面有不属于这一组的东西"
        /// ⇒ **那一层其余物永远删不掉**（用户选了「其余物：彻底删除」也不动），
        /// 而 ZIP 族恰好能过 —— 同一个设置两种结果。
        /// 引用名改走唯一出口 <see cref="ResolveGroupReferenceName"/>（= 这一单自己的文件名，它就在这一组里）。</para>
        ///
        /// <para>⛔ 判据只转调既有尺子；**一个文件都不属于 / 读不到 ⇒ false**（判不出就不删，红线不动）。</para>
        /// </summary>
        internal static bool AllRestFilesBelongToGroup(
            ArchiveTask? task,
            string groupBaseName,
            IEnumerable<string>? fileNames)
        {
            if (fileNames == null)
            {
                return false;
            }

            string reference = ResolveGroupReferenceName(task, groupBaseName);

            foreach (string file in fileNames)
            {
                if (string.IsNullOrWhiteSpace(file)
                    || !VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(file), reference))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 把"这一组**其余的片**"记进既有那份"借来用过的源片"账（消费方 = <paramref name="consumer"/>）。
        ///
        /// <para>⛔ 只记**同一组**、且不是消费方自己那一条；⛔ 一个字节都不动 —— 搬运与删除仍旧由既有出口
        /// 在消费方**真的成功**之后按那六道闸门办（见 <see cref="CollectConsumedSourcePiecesIntoRest"/>）。</para>
        /// </summary>
        private void RememberGroupPiecesConsumedBy(ArchiveTask consumer)
        {
            /*
             * ⛔ 引用名必须来自**这一单自己**（`ResolveGroupReferenceName`），⛔ 不许写死 `<基名>.zip`：
             * 组一旦不是跨盘 ZIP 族（`X.partN.rar` / `X.7z.001` / `X.r00`），写死的引用名一个成员都匹配不上
             * ⇒ 这一组"借来用过的源片"账**一条都记不上** ⇒ 那些片永远不会按跟班卷收场（2026-10-11 统一）。
             */
            string tailName = ResolveGroupReferenceName(
                consumer,
                FileNameHelper.GetArchiveBaseName(consumer.CurrentPath));

            foreach (ArchiveTask other in SnapshotTaskTable(Tasks))
            {
                if (other == null || ReferenceEquals(other, consumer))
                {
                    continue;
                }

                string path = other.CurrentPath ?? string.Empty;

                if (path.Length == 0
                    || !VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(path), tailName))
                {
                    continue;
                }

                _consumedVolumeSources[path] = consumer.FileName;
            }
        }

        /// <summary>
        /// 记**一条**"这一份源片借给了谁用"（与 <see cref="RememberGroupPiecesConsumedBy"/> 写的是同一份账）。
        /// 单独一条是为了"补记"那一档：批末才发现某一单的路径没进账，而它的组确实由某个已成功的消费方解开。
        /// </summary>
        private void RememberOneConsumedVolumeSource(string? piecePath, string consumerName)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(consumerName))
            {
                return;
            }

            _consumedVolumeSources[piecePath] = consumerName;
        }

        /// <summary>
        /// **把这一单登记进"缺卷留到最后再判"那份名单** —— 登记这件事的**唯一出口**
        /// （<see cref="RecordDeferredVolumeDeficit"/> 与递归中途撞缺卷那一档都走这里）。
        ///
        /// <para>⛔ 只做两件事：进名单 + 点亮显示/统计事实位
        /// （<see cref="ArchiveTask.IsVolumeDeficitDeferred"/>）。⛔ **不碰** <c>Status</c> /
        /// <c>ErrorMessage</c>：那两个字段是"装箱单"，发布链 / 定稿 / 源包处理都读它
        /// （见 `ApplyRecursionResult` 后面那段说明，实测在那一档改它会整组连入口都不再落地）。
        /// 解前预检那一档要连状态与文案一起写，由 <see cref="RecordDeferredVolumeDeficit"/> 补。</para>
        /// </summary>
        private void RememberDeferredVolumeDeficit(ArchiveTask? task, string? stalledMemberPath = null)
        {
            if (task == null)
            {
                return;
            }

            if (!_volumeDeficitDeferred.Contains(task))
            {
                _volumeDeficitDeferred.Add(task);
            }

            /*
             * ⚠ 2026-10-13 困难批 `11111111`：**停下来的那一组不一定就是这一单自己那一组**
             * （入口包的每一片各自被另一条链解出来）。那种情况把"那一片"记下来 ——
             * 批末那一站要靠它去问**对的那个组**（否则只能拿这一单自己的包基名去问，问出来是空）。
             * ⛔ 判不出（没给 / 空串）⇒ 不记 ⇒ 批末照旧用这一单自己的包基名（老口径一个字节不改）。
             */
            if (!string.IsNullOrWhiteSpace(stalledMemberPath))
            {
                _deferredVolumeGroupMember[task] = stalledMemberPath!;
            }

            task.IsVolumeDeficitDeferred = true;
        }

        /// <summary>
        /// **批首只记缺口**（用户 2026-10-05 原话：「所以你开始就得突破所有的伪装和压缩，这种分卷找不到的
        /// 情况可以留在最后做」）：⛔ 不落 Failed、⛔ 不写"本次不开始"、⛔ 不跳过 —— 只记进名单。
        ///
        /// <para>不变量 7 **一个字都没放松**：这一单现在**确实没有开始解压**（一个字节都没动），
        /// 只是"还缺哪几卷"这句话留到这一批的解压都跑完之后再说 —— 那时缺的第 1 卷可能已经被同批另一个包
        /// 解出来、或者已经被收拢到位（真机 `B250135.7z.002` 16:29:33 就被判死，而它的 `.001`
        /// 正躺在同一个目录树里没被解出来）。</para>
        /// </summary>
        private void RecordDeferredVolumeDeficit(ArchiveTask task, string missingNames)
        {
            if (task == null)
            {
                return;
            }

            RememberDeferredVolumeDeficit(task);

            /*
             * 显示/统计那一层的事实位（⛔ 不是状态）：①页那一格据此说"跳过（缺卷，等批末再判）"，
             * 批末计数据此按「跳过」数 —— 用户 2026-10-08 口径「没有到最后一步都是先跳过」。
             * 批末定稿（FinalizeDeferredVolumeDeficits）会把它清掉。
             */
            task.IsVolumeDeficitDeferred = true;

            /*
             * ⛔ **批中间不许顶着红色「分卷缺失」**（用户 2026-10-06 原话：
             * 「我说了不要显示红色的分卷缺失，你还没有压倒最后就跳过，你是听不懂吗」）。
             *
             * 批首只是"先把缺口记下来"：这一单**这一批确实还没开工**，但它既没失败也不该被判死
             * —— 缺的那几卷可能马上就被同批另一个包解出来（真机 `111.z03` 就是），
             * 也可能在批末"这一组其实已由别单解开"那一档按跟班卷收场。
             * ⇒ 状态用既有的**中性档** `等待解压`（不是 `分卷缺失`：那个进了红色映射与失败名单，
             * 在批中间亮出来就是一句会把用户指错的话）；"还缺哪几个"写在 `ErrorMessage` 里、
             * 日志里也有一条，信息一个字没少。批末那一站仍在缺 ⇒ 才由它落最终结论（跳过）。
             */
            task.Status = StatusText.WaitingExtract;
            task.ErrorMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeDeficitDeferredNoteFormat,
                missingNames);

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeDeficitDeferredLogFormat,
                    task.FileName,
                    missingNames));
        }

        /// <summary>
        /// 批量清账（单测用）。
        ///
        /// <para>为什么需要它：这几份账都是**批首清、批内攒**的，于是"批末那一站该不该给某几单收场"
        /// 这件事在单测里造不出中间态 —— 除非能像批内那样先把账写好、再把消费方的终态摆成成功。
        /// ⛔ 只做"写入既有那几份账"，⛔ 一点判据都不复制。</para>
        /// </summary>
        internal void ResetBatchLedgersForTests()
        {
            _volumeDeficitDeferred.Clear();
            _triedVolumeGroupSignatures.Clear();
            _gatheredUserSourcePieces.Clear();
            _deferredVolumeGroupMember.Clear();
            _byteBudgetLinkedSources.Clear();
            _scoredSlotAttempts.Clear();
            _scoredSlotLinks.Clear();
            _volumeDeficitFinalPass.Clear();
            ClearGroupPieceProducers();
            _groupConsumerByName.Clear();
            _volumeGroupUnits.Clear();
            _consumedVolumeSources.Clear();
            _producerSourcesCollected.Clear();
            _adoptedLandedPieces.Clear();
            _groupLayerByBaseName.Clear();
            _volumeStepTasks.Clear();
            _volumeGroupEntriesThisBatch.Clear();
        }

        /// <summary>单测用：把"这一片是借某个消费方用过的源片"记进既有那份账。</summary>
        internal void RememberConsumedVolumeSourceForTests(string piecePath, string consumerName)
        {
            if (string.IsNullOrWhiteSpace(piecePath) || string.IsNullOrWhiteSpace(consumerName))
            {
                return;
            }

            _consumedVolumeSources[piecePath] = consumerName;
        }

        /// <summary>单测用：把"这一组谁在解"记进既有那份账。</summary>
        internal void RememberGroupConsumerForTests(string baseName, string consumerName)
        {
            if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(consumerName))
            {
                return;
            }

            _groupConsumerByName[baseName] = consumerName;
        }

        /// <summary>
        /// 单测用：走一遍**「接片」那一档的判据**（⛔ 不复制任何判据：唯一执行体就是
        /// <see cref="TryAdoptUnresolvedVolumePiece"/>，这里只是把它露给测试项目）。
        ///
        /// <para>为什么需要这个出口：本轮新加的"内容证据可证不是分卷成员 ⇒ 不接"那一句在**协调器内部**，
        /// 没有这一档就只能靠真机环台验（而环台验的是"整条链的最终盘面"，分不出是哪一句挡下的）。
        /// 有了它，守门用例能直接断言"这一份没有被改名/搬走"。</para>
        /// </summary>
        internal bool TryAdoptUnresolvedVolumePieceForTests(string piecePath, ArchiveTask? owner, string? layerOverride = null)
            => TryAdoptUnresolvedVolumePiece(piecePath, owner, layerOverride);

        /// <summary>单测用：把"这一单吐出了这一组的一片"记进既有那份账（写入点唯一 = <see cref="RememberGroupPieceProducer"/>）。</summary>
        internal void RememberGroupPieceProducerForTests(string baseName, ArchiveTask producer)
            => RememberGroupPieceProducer(baseName, FileNameHelper.GetFileName(producer?.CurrentPath ?? string.Empty), producer);

        /// <summary>
        /// 单测用：把"**产出方那一份已经落地的片**被接进了这一组"记进这本账
        /// （写入点唯一 = <see cref="RememberAdoptedLandedPiece"/>，⛔ 判据一条都不复制：
        /// 落点根、过程物目录、源包路径三道闸门照旧全部生效）。
        ///
        /// <para>⚠ <paramref name="adoptionOwner"/> = **接片那一刻调用方给的那一单**，⛔ 不等于产出方：
        /// 真机上"批量收片"那条路给的是**被重判的那一单**（落点树里没有这一片）——
        /// 产出方由 `ResolveLandedPieceProducer` 按盘上事实认。</para>
        /// </summary>
        internal void RememberAdoptedLandedPieceForTests(
            ArchiveTask adoptionOwner,
            string groupBaseName,
            string piecePath)
            => RememberAdoptedLandedPiece(piecePath, adoptionOwner, groupBaseName, renamedInPlace: false);

        /// <summary>单测用：把一单放进"缺卷留到最后再判"那份名单（写入点唯一 = <see cref="RecordDeferredVolumeDeficit"/>）。</summary>
        internal void RecordDeferredVolumeDeficitForTests(ArchiveTask task)
        {
            if (task == null)
            {
                return;
            }

            // ⛔ 走**真实那条路**（同一个出口）：批首记缺口 = 中性状态 + 缺口写进错误信息 + 一条日志。
            // 只把任务塞进名单的旧写法会让"批中间顶着红色的分卷缺失"这条口径**没有守门**。
            RecordDeferredVolumeDeficit(task, DescribeMissingVolumeNames(task));
        }

        /// <summary>缺卷清单那一句话（任务账上那份；空 ⇒ 如实说"名字上看不出"）。</summary>
        private static string DescribeMissingVolumeNames(ArchiveTask task)
        {
            IReadOnlyList<string> names = task == null ? Array.Empty<string>() : task.MissingVolumeNames;

            return names.Count > 0
                ? string.Join("、", names)
                : StatusText.CrossLayerGatherNoMissingNames;
        }

        /// <summary>
        /// **链停下来的那一组，就是这一单自己这一组吗** —— 中途撞缺卷那一档**进批末名单**的前提（2026-10-10）。
        ///
        /// <para>为什么必须问这一句：批末那一站（<see cref="RecheckDeferredVolumeDeficits"/>）问"这一组齐了没"
        /// 用的是**这一单自己的包基名**（<see cref="ResolveBatchGroupDirectory"/> 与
        /// <see cref="DescribeMissingVolumeNames"/> 读的都是它）⇒ 把"自己名字跟那一组无关、只是恰好解出了
        /// 那一组一片"的单塞进名单，批末就会拿它的名字去问一个不相干的组，然后如实落一句
        /// 「分卷缺失，未开始解压」。</para>
        ///
        /// <para>⛔ <b>判据必须走项目唯一那把"同一组"的尺子</b>
        /// （<see cref="VolumeGroupDetector.BelongsToSameGroup"/> = **族 + 基名**），
        /// ⛔ 不许只比基名。2026-10-10 真机 EEEE 就是只比基名翻的车：`111.rar`（**RarOld 族**）与
        /// `111.zip`（**ZipSpanned 族**）基名都是 `111` ⇒ 被登记进名单 ⇒ 批末拿它的名字去问一个
        /// **永不可能凑齐**的老式 RAR 组 ⇒ **同一单**既在列表里写着「这一片随整组解开（由「111.z03」那单解的）」，
        /// 又被扣一句 `[ERROR] 分卷缺失，未开始解压` + 「按部分完成记」。</para>
        ///
        /// <para><b>断点证据</b>（debug-mcp，停在 `ExtractionCoordinator.cs:9912`）：调用栈 = 
        /// `RememberDeferredVolumeDeficit:9912` ← `RunRecursiveAsync():6846`，`task.FileName = "111.rar"`，
        /// 名单当时 1 条（另一条是解前预检登记的 `111.z03`，那一条是对的）。</para>
        /// </summary>
        /// <param name="task">这一单（读它当前的落点文件名）。</param>
        /// <param name="result">这一次递归的结果（读它没能落地的那几片）。</param>
        internal static bool IsStalledGroupOwnGroup(ArchiveTask? task, RecursionResult? result)
        {
            if (task == null || result == null)
            {
                return false;
            }

            string ownName = FileNameHelper.GetFileName(task.CurrentPath ?? string.Empty);

            if (ownName.Length == 0)
            {
                return false;
            }

            foreach (string piece in result.UnresolvedVolumePieces)
            {
                // 唯一尺子：族 + 基名都要对上（`111.zip` 与 `111.z03` 同组；`111.rar` 与 `111.zip` 不同组）。
                if (VolumeGroupDetector.BelongsToSameGroup(FileNameHelper.GetFileName(piece), ownName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 这一趟递归**停下来的那一片**（可能属于**别人那一组**）—— 登记进批末名单时要用它当参照物。
        ///
        /// <para>判据只有一条：`UnresolvedVolumePieces` 里第一个**自报是分卷片**的名字
        /// （唯一出口 <see cref="VolumeGroupDetector.TryGetVolumeIndex"/> 认得出卷号）。
        /// 一个都没有 ⇒ 空串 ⇒ 调用方照旧不登记（老口径一个字不改）。</para>
        ///
        /// <para>⛔ 不比中文文案、⛔ 不猜族；工作区里那一份与落点那一份名字相同，拿哪一份当参照物都一样。</para>
        /// </summary>
        internal static string FindStalledVolumePiece(RecursionResult? result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            foreach (string? piece in result.UnresolvedVolumePieces)
            {
                string name = FileNameHelper.GetFileName(piece ?? string.Empty);

                if (name.Length == 0)
                {
                    continue;
                }

                /*
                 * ⚠ 2026-10-14（插桩实测两版，`10101010` 那一组，⛔ 记下来免得再踩）：
                 *
                 * <para>第一版**只认名字读得出卷号的片** ⇒ 逐字
                 * `[排障·名单] pack111-W0-0.7z ⇒ 自己那一组=False 记下的片=(空) 自报片数=1`
                 * —— 递归明明记住了那一片，这里却取不出来（名字里那一截卷标记被伪装尾巴盖住）。</para>
                 *
                 * <para>第二版改成"读卡" ⇒ **还是空**：那一刻那一份**已经不在盘上**（它的链收尾把工作区
                 * 清掉了）⇒ `FileNature.Inspect` 读不出格式与专属算法。</para>
                 *
                 * <para>⇒ 判据回到最硬的那条事实：**能进 <c>UnresolvedVolumePieces</c> 的，本身就是
                 * 递归判过"这一份是某个还没凑齐的分组的一片"的** —— 那是**我们自己的结论**、唯一出口
                 * `RecursiveExtractor.IsPieceOfIncompleteGroup`，⛔ 不需要在这里拿名字或卡再推一遍。
                 * 它只是当**参照名**用（下面那条"不许拿其余物里那一份当参照物"的闸门一个字不放宽）。</para>
                 */
                string path = piece!;

                /*
                 * ⛔ **不许拿"其余物"里的那一份当参照物**（2026-10-13 实测，真机形状守门用例
                 * `真机形状_过路层那一单的其余物按档删掉_48MB那处` 当场变红）：那一份的目录会被当成
                 * "这一组该拼到哪一层" ⇒ 逐字 `批末重判 —— 这一组该拼到哪一层 = …\111\其余物`
                 * ⇒ 整组往其余物里凑、随后被删除档清掉，树上留下一堆残渣。
                 *
                 * ⚠ 工作区里的那一份**照旧可以**（困难批 `11111111` 记下来的正是暂存目录里那一份）：
                 * 它只是当**参照名**用的，`GroupVolumeDirectory.Resolve` 自己会跳过"已不在盘上"的成员
                 * （实测：三种位置各记过一次，盘上真正决定落点的是落点树里那几份）。
                 */

                if (path.Contains(ProcessArtifactLayout.ArtifactDirectoryName, StringComparison.Ordinal))
                {
                    continue;
                }

                return path;
            }

            return string.Empty;
        }

    }
}
