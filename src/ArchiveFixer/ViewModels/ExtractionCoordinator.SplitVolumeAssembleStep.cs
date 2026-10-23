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
    /// <summary>分卷组装（整组缺卷时按候选拼一份可读的；第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        /// <summary>
        /// **第 42 条的全自动拼装**（用户 2026-09-25 选的方案 A：做，默认关）：
        /// 容器里装的是分卷第 1 卷、后续卷在容器外面时，在**工作区**里接出一套"名字成套、同在一个目录"的卷。
        ///
        /// <para>调用时机是**引擎已经说过"分卷缺失"之后**（不是抠完就拼）—— 这是这一档最重要的安全阀：
        /// 容器里装的是**完整**归档时 list 早就成功了，根本走不到这里；真的装的是"后续卷"时，
        /// 外面那一组里**有**标准首卷名，<see cref="SplitVolumeAssembler"/> 一组都挑不出来。
        /// 拼完还要再让引擎自己验一次，验不过就**退回原路**（结论一个字不变）。</para>
        ///
        /// <para>返回 <c>(null, null)</c> = 没拼 / 拼了但引擎不认 —— 调用方照旧走今天那条路。</para>
        /// </summary>
        private async Task<(string? FirstVolumePath, ArchiveListResult? List)> TryAssembleSplitVolumesAsync(
            ArchiveTask task,
            string carvedPath,
            IReadOnlyList<PasswordItem> candidates,
            CancellationToken cancellationToken)
        {
            try
            {
                string sourceDirectory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;
                string workDirectory = _pathService.BuildTaskWorkDirectory(task);

                if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(workDirectory))
                {
                    return (null, null);
                }

                string assemblyDirectory = Path.Combine(workDirectory, SplitVolumeAssembler.AssemblyDirectoryName);

                /*
                 * 同一个任务被重试时这里可能有上一轮的残留：只清**我们自己造的那一个目录**
                 * （名字与父目录都对得上才清；对不上就一个字节都不动）。
                 */
                if (!TryResetAssemblyDirectory(assemblyDirectory, workDirectory))
                {
                    AppendLog("WARN", $"{task.LogName}：拼装目录里已经有程序没造过的东西，本次不拼（一个字节都不动）。");
                    return (null, null);
                }

                SplitVolumeFamily family = SplitVolumeAssembler.DetectFamilyByMagic(carvedPath);

                SplitVolumeAssemblyPlan plan = SplitVolumeAssembler.Plan(
                    carvedPath,
                    family,
                    sourceDirectory,
                    assemblyDirectory,
                    CollectVolumeFilesInDirectory(sourceDirectory).Select(Path.GetFileName));

                if (!plan.CanAssemble)
                {
                    AppendLog("INFO", $"{task.LogName}：没有自动把两边的分卷接起来 —— {plan.Reason}");
                    return (null, null);
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：{plan.Reason}正在工作区里接起来（你的源文件只读，一个字节都不动）。");

                /*
                 * 进度用 Progress<long>（回调回到捕获的上下文，与"抠取内嵌归档"那处的写法一致）——
                 * ⛔ 绝不在复制循环里直接碰界面/日志：那段代码跑在线程池上。
                 * 每 1 GiB 报一次：跨盘复制几个 5 GB 的卷时，用户要能看出"还在动"。
                 */
                long logged = 0;

                IProgress<long> copyProgress = new Progress<long>(copied =>
                {
                    if (copied - logged >= 1024L * 1024 * 1024)
                    {
                        logged = copied;
                        AppendLog("INFO", $"{task.LogName}：正在复制后续卷 …已复制 {TaskSpaceEstimate.FormatSize(copied)}");
                    }
                });

                SplitVolumeAssemblyResult assembled = await Task.Run(
                    () => SplitVolumeAssembler.TryAssemble(
                        plan,
                        availableSpace: null,
                        progress: copied => copyProgress.Report(copied),
                        cancellationToken),
                    cancellationToken);

                if (!assembled.Success)
                {
                    AppendLog("WARN", $"{task.LogName}：自动拼装没做成 —— {assembled.Reason}照旧按「分卷缺失」处理。");
                    return (null, null);
                }

                foreach (string line in assembled.LogLines)
                {
                    AppendLog("INFO", $"{task.LogName}：{line}");
                }

                /*
                 * 记在任务上：默认档的日志"成功就丢"，而"接过分卷"是**动过盘**的事实，
                 * 用户不改设置也要能看见 → 收尾那一行摘要会带上它（见 BuildTaskSummaryLine）。
                 */
                task.SplitVolumeAssemblyNote =
                    $"接上分卷（硬链接 {assembled.HardLinkCount} / 复制 {assembled.CopyCount}"
                    + (assembled.CopiedBytes > 0 ? $"，{TaskSpaceEstimate.FormatSize(assembled.CopiedBytes)}" : string.Empty)
                    + "）";

                // 拼完**必须让引擎自己验一次**：它认了才算数（与上面那次 list 用的是同一套候选顺序）。
                ArchiveListResult? assembledList = null;

                foreach (PasswordItem candidate in candidates.Take(MaxPreflightPasswordAttempts))
                {
                    ArchiveListResult attempt = await _archiveEngine.ListAsync(
                        ArchiveRequest.For(assembled.FirstVolumePath, candidate.Value),
                        cancellationToken);

                    if (attempt.Success)
                    {
                        assembledList = attempt;
                        break;
                    }
                }

                if (assembledList == null)
                {
                    AppendLog(
                        "WARN",
                        $"{task.LogName}：接上后续卷之后引擎仍然打不开这一套（可能这一组本身就是坏的），" +
                        "退回原路，照旧按「分卷缺失」报。");
                    return (null, null);
                }

                AppendLog(
                    "INFO",
                    $"{task.LogName}：接上后续卷之后引擎能打开了（{assembledList.FileCount} 个文件），" +
                    "本次就用工作区里那一套解压（源目录里一个字节都没动）。");

                return (assembled.FirstVolumePath, assembledList);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 拼装是"锦上添花"：出任何错都退回原路，⛔ 绝不让它改变任务的结论。
                AppendLog("WARN", $"{task.LogName}：自动拼装时出错（{ex.Message}），退回原路（结论不受影响）。");
                return (null, null);
            }
        }

        /// <summary>
        /// 清掉本任务上一轮留下的拼装目录（**只认我们自己造的那一个**：父目录 = 本任务工作区、
        /// 目录名 = <see cref="SplitVolumeAssembler.AssemblyDirectoryName"/>，两条都对得上才删）。
        /// </summary>
        private static bool TryResetAssemblyDirectory(string assemblyDirectory, string workDirectory)
        {
            try
            {
                if (!Directory.Exists(assemblyDirectory))
                {
                    return true;
                }

                string? parent = Path.GetDirectoryName(assemblyDirectory);

                if (parent == null
                    || !SafePathHelper.PathEquals(parent, workDirectory)
                    || !string.Equals(
                        Path.GetFileName(assemblyDirectory),
                        SplitVolumeAssembler.AssemblyDirectoryName,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                Directory.Delete(assemblyDirectory, recursive: true);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 这个子目录名是不是"我们自己造的"（决定清工作区时敢不敢整份删）。
        ///
        /// <para>现在认三个：<c>stage</c>（暂存/入仓）、<c>volumes</c>（第 42 条的拼装目录）、
        /// <c>_密码预检</c>（密码探针 —— 2026-10-05 起单层路也建在**产物目录外面**的兄弟位置，
        /// 名字与递归那侧同一个常量；⛔ 漏了它，异常退出留下的探针目录会把整份清理拦下）。
        /// ⛔ 出现别的子目录一律不删 —— 那最典型的情况是"任务名撞上了递归工作区的 <c>recursive</c>"，
        /// 顺着删会把别人的东西删掉。以后每加一个我们自己造的子目录，都要加到这里。</para>
        ///
        /// <para><c>internal</c>（2026-10-05）：白名单本身要能被守门用例直接钉住
        /// （它决定"删不删"，⛔ 不能只靠间接行为验）。</para>
        /// </summary>
        internal static bool IsOurWorkspaceSubdirectory(string name) =>
            string.Equals(name, PathService.StageDirectoryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, SplitVolumeAssembler.AssemblyDirectoryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, RecursiveExtractor.ProbeDirectoryName, StringComparison.OrdinalIgnoreCase);

    }
}