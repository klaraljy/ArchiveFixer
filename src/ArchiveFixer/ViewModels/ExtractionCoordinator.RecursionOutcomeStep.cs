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
    /// <summary>递归结论落地到任务（第 8 块，从 Volume 大杂烩搬出；同一 partial 类）。</summary>
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

    }
}