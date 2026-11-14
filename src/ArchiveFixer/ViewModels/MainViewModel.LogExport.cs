using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels
{
    public partial class MainViewModel
    {        /// <summary>
             /// 导出日志文件的**头部**（用户 2026-09-25 第 44 条追加拍板）：
             /// 时间范围 / 任务数 / 成功失败 / 引擎版本 / 输出根 + "细节在哪看"。
             ///
             /// <para>为什么值得写：这份 txt 的用途就是"发给别人（或未来的自己）排查" ——
             /// 没有头，读的人得先自己找"这是哪一批、跑到几点、成不成、用的哪个引擎"。
             /// ⛔ 这里只写**机器事实**（任务表的终态枚举与引擎标识），不写任何密码相关内容。</para>
             ///
             /// <para>internal 是为了让测试能直接钉住"头部写了哪几件事、顺序对不对"
             /// （产品入口只有「导出日志（本次操作）」这一个，见 <see cref="ExportLog"/>）。</para>
             /// </summary>
        internal IReadOnlyList<string> BuildLogExportHeader()
        {
            var lines = new List<string>
            {
                "================ ArchiveFixer 日志导出 ================"
            };

            DateTime? first = Tasks.Where(task => task.StartTime.HasValue).Min(task => task.StartTime);
            DateTime? last = Tasks.Where(task => task.EndTime.HasValue).Max(task => task.EndTime);

            /*
             * ⛔ 2026-11-01 真机：抬头写 14:49–15:57，正文最后事件却是 16:07 ——
             * 因为 `Tasks.EndTime` 只到最后一单解压结束，批末的第二大步/收尾事件没有任务时间。
             * 导出的正文就是"本次操作"那一段日志 ⇒ 抬头直接用那段日志的首末时间（有就用它）。
             */
            if (Logs.Count > 0)
            {
                int markerIndex = -1;

                for (int index = 0; index < Logs.Count; index++)
                {
                    if (Logs[index].Message.Contains("本次操作开始", StringComparison.Ordinal))
                    {
                        markerIndex = index;
                    }
                }

                if (markerIndex >= 0)
                {
                    first = Logs[markerIndex].Time;
                    last = Logs[^1].Time;
                }
            }

            if (first.HasValue && last.HasValue)
            {
                lines.Add($"时间范围：{first:yyyy-MM-dd HH:mm:ss} → {last:yyyy-MM-dd HH:mm:ss}"
                    + (last.Value > first.Value ? $"（用时 {(last.Value - first.Value).ToString(@"hh\:mm\:ss")}）" : string.Empty));
            }
            else
            {
                lines.Add($"导出时间：{ArchiveFixer.Helpers.Clock.Now:yyyy-MM-dd HH:mm:ss}");
            }

            /*
             * 数法只有一份（`Models/BatchOutcomeTally`）：批末「本批汇总」那一行、一键汇总那一行、
             * 以及这里三处共用，⛔ 别在这里再写一遍 `Count(task => …)`。
             *
             * 旧写法（2026-10-02 真机日志里读出来的）漏了两件事：
             * ① 跟班卷（同一分卷组的后续卷）被算进"跳过" —— 同一份日志里批末说"跳过 0"、
             *    头部却会把每一卷都数成一次"跳过"；
             * ② 只报成功 / 失败 / 跳过三档 —— "既没成功、也没失败、也没跳过"的那些任务
             *    （例如导入完还没跑）一个数都不占，于是头部写着
             *    「任务数：10（成功 0 / 失败 0 / 跳过 0）」，读的人当场对不上账。
             */
            /*
             * ⛔ 2026-11-01：导出正文是"本次操作"（可能跨多批），不能用**最后一批**的计数
             * （真机抬头写任务数 1、明细 13 行）。按当前任务表算，与任务明细同口径。
             */
            BatchOutcomeTally tally = BatchOutcomeTally.Count(Tasks);

            /*
             * 任务数要分两层说（用户 2026-09-26 第 45 条的真机现场）：他跑 38 个包，头部却写"任务数：76" ——
             * 因为续解出来的内层包**也是任务**。不分开说，读的人会以为程序多跑了 38 个包。
             */
            int continuation = Tasks.Count(task => task.IsContinuationTask);

            lines.Add(
                $"任务数：{tally.Total}（{string.Join(" / ", tally.BuildParts())}）"
                + (continuation > 0 ? $"；其中续解出来的内层包 {continuation} 个" : string.Empty));

            /*
             * 跟班卷那一句与两个汇总行**共用同一句**（`DescribeFollowerNote`）：
             * 数字从"跳过"里剔出去之后，它必须在头部里出现一次，否则三个分项加起来又对不上任务数。
             */
            if (tally.FollowerSkipped > 0)
            {
                lines.Add(tally.DescribeFollowerNote());
            }

            if (tally.ContentGroupProcessSkipped > 0)
            {
                lines.Add(tally.DescribeContentGroupProcessNote());
            }

            string engine = DescribeEngineIdentity();

            if (!string.IsNullOrWhiteSpace(engine))
            {
                lines.Add("引擎：" + engine);
            }

            /*
             * ⛔ 读的是①页那一格的**同一份判据**（`ResolveOutputLocationText`），
             * 不是 `SelectedOutputDirectory` 这个字段本身：字段会被"记住的目录"填上，
             * 而这一批的落点可能压根不用它 —— 老写法因此在真机日志里写出上一批的
             * <c>…\测试\BBB</c>，与正文每一行"本次实际输出目录 …\222\…"互相打脸。
             */
            string outputRoot = OutputLocationForLog;

            if (!string.IsNullOrWhiteSpace(outputRoot))
            {
                lines.Add(outputRoot);
            }

            lines.Add(
                "细节在哪看：先看下面的「失败 / 取消 / 部分完成」「分卷组」「源包处理」三个专区；"
                + "完整的逐层过程在日志正文里；末尾只保留本次操作的关键诊断事件（不再倒整份 trace）。"
                + "如果专区里仍判不出，再让用户补 data\\logs 的详细文件。"
                + "⑧设置的「详细日志（排查用）」仍控制屏幕与普通/详细导出。");

            /*
             * 导出文件必须和①页看到的一致：至少把每一单的名字/状态/机器终态列出来。
             * 具体过程（逐层、密码候选、引擎原话）仍看正文与 trace，不在这里复述。
             */
            lines.Add("任务明细（与①页一致）：");

            foreach (ArchiveTask task in Tasks)
            {
                string error = (task.ErrorMessage ?? string.Empty)
                    .Replace("\r", " ")
                    .Replace("\n", " ")
                    .Trim();

                lines.Add(
                    $"{task.LogName} ｜ {task.Status} ｜ {task.Outcome}"
                    + (error.Length > 0 ? $" ｜ {CompactReportLine(error, 500)}" : string.Empty));
            }

            return lines;
        }

        /// <summary>
        /// 单文件报告的**问题专区**（用户 2026-10-26：只给一个 txt 也要能定位）。
        /// 顺序固定：失败/取消/部分完成 → 分卷组 → 源包处理。
        /// </summary>
        internal IReadOnlyList<string> BuildLogExportSections()
        {
            var sections = new List<string>();

            sections.Add("================ 一、失败 / 取消 / 部分完成 ================");

            /*
             * 失败专区只列**真正的根任务问题**：跟班卷（同一分卷组后续卷）即使状态是"跳过"、
             * 甚至机器终态 Succeeded，也不是这一批的失败；把它们列进来会让用户误以为
             * B250135/151 这些完整组也失败了。
             */
            List<ArchiveTask> problemTasks = Tasks
                .Where(task => !task.IsVolumeGroupFollower
                    && task.CountsTowardBatchOutcome
                    && task.Outcome is TaskOutcome.Failed
                        or TaskOutcome.Cancelled
                        or TaskOutcome.PartiallyCompleted)
                .ToList();

            if (problemTasks.Count == 0)
            {
                sections.Add("本批没有失败 / 取消 / 部分完成的根任务。");
            }
            else
            {
                foreach (ArchiveTask task in problemTasks)
                {
                    sections.Add(
                        $"[{task.Outcome}] {task.LogName} ｜ {task.Status}"
                        + $"｜格式={task.DetectedFormat}"
                        + (task.IsContinuationTask ? "｜续解内层包" : "｜根任务"));

                    if (!string.IsNullOrWhiteSpace(task.ParentTaskName))
                    {
                        sections.Add($"  父包：{task.ParentTaskName}");
                    }

                    if (!string.IsNullOrWhiteSpace(task.OutputPath))
                    {
                        sections.Add($"  输出：{task.OutputPath}");
                    }

                    if (!string.IsNullOrWhiteSpace(task.VolumeInfoText))
                    {
                        sections.Add($"  分卷：{CompactReportLine(task.VolumeInfoText, 500)}");
                    }

                    if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
                    {
                        sections.Add($"  原因：{CompactReportLine(task.ErrorMessage, 900)}");
                    }

                    if (!string.IsNullOrWhiteSpace(task.EngineVerdict))
                    {
                        sections.Add($"  引擎：{CompactReportLine(task.EngineVerdict, 500)}");
                    }
                }
            }

            sections.Add(string.Empty);
            sections.Add("================ 二、分卷组 ================");

            /*
             * ⛔ 2026-11-01：优先读第二大步的**组单元**（VolumeGroupUnit）——
             * 旧写法按任务表拼，真机上 `以宇_2/yanje_2/183` 明明是 OwnerAssigned/可证完整，
             * 导出却写「已登记片=0｜完整=False｜状态=未登记」。任务表只是组的成员视图，不是结论。
             */
            IReadOnlyCollection<ArchiveFixer.Extraction.VolumeGroupUnit> volumeUnits =
                _extractionCoordinator.VolumeGroupUnits;

            var volumeTasks = Tasks
                .Where(task => task.IsVolumeGroup
                    || task.IsVolumeGroupFollower
                    || task.VolumePaths.Count > 0)
                .ToList();

            if (volumeUnits.Count > 0)
            {
                foreach (ArchiveFixer.Extraction.VolumeGroupUnit unit in volumeUnits)
                {
                    string entryName = string.IsNullOrWhiteSpace(unit.EntryPath)
                        ? "判不出"
                        : System.IO.Path.GetFileName(unit.EntryPath);
                    string missing = unit.MissingNumbers.Count == 0
                        ? "无"
                        : string.Join(",", unit.MissingNumbers);

                    sections.Add(
                        $"组「{unit.BaseName}」｜族={unit.Family?.ToString() ?? "判不出"}"
                        + $"｜状态={unit.State}｜完整={unit.State is ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Complete
                            or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.OwnerAssigned
                            or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Linked
                            or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Extracting
                            or ArchiveFixer.Extraction.VolumeStep.VolumeGroupState.Succeeded}"
                        + $"｜片={unit.Pieces.Count}｜入口={entryName}"
                        + $"｜owner={unit.OwnerTaskFileName ?? "未定"}"
                        + $"｜缺片={missing}"
                        + $"｜{CompactReportLine(unit.Verdict, 300)}");
                }
            }
            else if (volumeTasks.Count == 0)
            {
                sections.Add("本批没有识别到分卷组。");
            }
            else
            {
                foreach (IGrouping<string, ArchiveTask> group in volumeTasks.GroupBy(DescribeVolumeGroupKey))
                {
                    List<ArchiveTask> members = group.ToList();
                    string state = members
                        .Select(task => task.VolumeInfoText)
                        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "未登记";

                    int declared = members
                        .Select(task => task.VolumePaths.Count)
                        .Max();
                    List<string> missing = members
                        .SelectMany(task => task.MissingVolumeNames)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(10)
                        .ToList();
                    bool complete = members.Any(task => task.IsVolumeGroup
                        && task.IsVolumeComplete);

                    sections.Add(
                        $"组「{group.Key}」｜成员={members.Count}｜已登记片={Math.Max(0, declared)}"
                        + $"｜完整={complete}"
                        + $"｜状态={CompactReportLine(state, 300)}"
                        + $"｜缺片={(missing.Count > 0 ? string.Join("、", missing) : "无")}");

                    foreach (ArchiveTask task in members)
                    {
                        sections.Add(
                            $"  - {task.LogName} ｜ {task.Status} ｜ {task.Outcome}"
                            + (task.VolumePaths.Count > 0 ? $"｜片={task.VolumePaths.Count}" : string.Empty)
                            + (task.MissingVolumeNames.Count > 0 ? $"｜缺={task.MissingVolumeNames.Count}" : string.Empty));
                    }
                }
            }

            sections.Add(string.Empty);
            sections.Add("================ 三、源包处理 ================");

            var roots = Tasks.Where(task => !task.IsContinuationTask).ToList();

            if (roots.Count == 0)
            {
                sections.Add("本批没有根任务。");
            }
            else
            {
                foreach (ArchiveTask task in roots)
                {
                    bool exists = false;

                    try
                    {
                        exists = !string.IsNullOrWhiteSpace(task.CurrentPath) && File.Exists(task.CurrentPath);
                    }
                    catch
                    {
                        // 源包状态只是报告信息，不参与任何结论。
                    }

                    string handling = task.SourcePackageMove switch
                    {
                        SourcePackageMoveState.DeferredToChainEnd => "延期到链尾",
                        SourcePackageMoveState.Done => "已处理",
                        _ => "未处理"
                    };

                    sections.Add(
                        $"{task.LogName} ｜ {task.Outcome} ｜ 源包={(exists ? "仍在原处" : "当前不存在")}"
                        + $"｜处理状态={handling}"
                        + (string.IsNullOrWhiteSpace(task.ErrorMessage) ? string.Empty : $"｜{CompactReportLine(task.ErrorMessage, 600)}"));

                    if (task.SourcePackageMove == SourcePackageMoveState.DeferredToChainEnd)
                    {
                        sections.Add(
                            "  源包保留原因：这一单所在的链/分卷组尚未证明完整，"
                            + "按「整条链全部成功才删原包」规则不动它");
                    }
                }
            }

            return sections;
        }

        private static string DescribeVolumeGroupKey(ArchiveTask task)
        {
            string baseName = FileNameHelper.GetArchiveBaseName(task.CurrentPath);

            if (!string.IsNullOrWhiteSpace(baseName))
            {
                return baseName;
            }

            if (!string.IsNullOrWhiteSpace(task.LogName))
            {
                return task.LogName;
            }

            return "未命名组";
        }

        /// <summary>报告里的长行截断：完整事实仍留在 data/log，txt 只保留可读的一段。</summary>
        private static string CompactReportLine(string? text, int maxLength)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

            return value.Length <= maxLength ? value : value[..maxLength] + "…";
        }

        /// <summary>
        /// 当前生效的引擎标识（名字 + 版本）—— 导出头部与任务详情共用同一份口径。
        ///
        /// ⛔ 问的是 <c>TaskSummaryService.EngineIdentity</c>（报告层那一份），
        /// 不自己 new 引擎、也不自己拼 7z 路径（AGENTS.md §3.1：引擎选择只允许注册表一个来源）；
        /// 版本取不到时它自己只说名字（见 <see cref="EngineIdentity.Describe"/>），这里不补编。
        /// </summary>
        private string DescribeEngineIdentity()
        {
            try
            {
                string engine = _taskSummaryService.EngineIdentity.Describe();

                if (string.IsNullOrWhiteSpace(engine))
                {
                    return string.Empty;
                }

                /*
                 * ⚠ 优先级那一格是 List<string>：直接插进字符串会写成
                 * `System.Collections.Generic.List`1[System.String]`（2026-09-26 真机导出的头部里就是这么写的）。
                 * 这里必须自己用 `→` 连起来（与②页引擎那一栏同一口径）。
                 */
                string priority = Settings.EnginePriority is { Count: > 0 }
                    ? string.Join(" → ", Settings.EnginePriority.Where(value => !string.IsNullOrWhiteSpace(value)))
                    : string.Empty;

                return string.IsNullOrWhiteSpace(priority)
                    ? engine
                    : $"{engine}（优先级：{priority}）";
            }
            catch
            {
                // 报告层不该因为"引擎信息取不到"整个失败：头部少一行，日志照旧导得出来。
                return string.Empty;
            }
        }

        /// <summary>
        /// 「导出日志」对话框的**起始目录**（用户 2026-10-01 真机 `giu910` 那一单问的"为什么这里有个 txt"）。
        ///
        /// <para>现场：导出对话框默认开在"上次用过的那个文件夹"，而那一批的源包正好在
        /// <c>…\giu910\giu910\其余物\</c> 里（他把上一轮被收进其余物的包再跑了一次）⇒ 日志就落在了
        /// **其余物**里。那个文件夹是程序自己会**整份删掉**的地方，日志落在那儿哪天就跟着没了。</para>
        ///
        /// <para>两条口径：① 记住**上一次成功导出到哪个目录**，下次就从那儿开（他习惯把日志放哪儿就一直在那儿，
        /// ⛔ 不在代码里写死任何个人路径 —— §8）；② 记着的目录若落在**其余物 / 工作区**里，**一律不采用**
        /// （退回系统默认），⛔ 绝不把用户往那两个会被清掉的地方引。</para>
        /// </summary>
        private string? ResolveLogExportInitialDirectory()
        {
            string remembered = Settings.LastLogExportDirectory ?? string.Empty;

            if (string.IsNullOrWhiteSpace(remembered) || !Directory.Exists(remembered))
            {
                return null;
            }

            string full;

            try
            {
                full = Path.GetFullPath(remembered);
            }
            catch
            {
                return null;
            }

            if (IsInsideDeletableProcessFolders(full))
            {
                return null;
            }

            return full;
        }

        /// <summary>
        /// 这个目录是不是落在"程序自己会整份删掉"的地方（**其余物** / **工作区**）——
        /// 日志导出**不记**这种目录、也不用它当起始目录（用户 2026-10-01 真机就是导出落进了其余物）。
        /// 判据只有一处：<see cref="ProcessArtifactLayout.IsInsideDeletableProcessFolders"/>。
        /// </summary>
        private static bool IsInsideDeletableProcessFolders(string fullPath) =>
            ProcessArtifactLayout.IsInsideDeletableProcessFolders(fullPath);

        /// <summary>导出成功后记住这个目录（下次对话框从这儿开）。⛔ 其余物 / 工作区那一档一个字节都不记。</summary>
        private void RememberLogExportDirectory(string? exportedFilePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(exportedFilePath ?? string.Empty);

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                string full = Path.GetFullPath(directory);

                if (IsInsideDeletableProcessFolders(full))
                {
                    return;
                }

                if (string.Equals(Settings.LastLogExportDirectory, full, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Settings.LastLogExportDirectory = full;
                WriteSettingsToDisk("记住日志导出目录");
            }
            catch
            {
                // 记不住只是下次少一点方便，⛔ 不许因为它把导出本身搞失败。
            }
        }

        private void ExportDiagnosticBundle()
        {
            string path = _dialogService.ShowSaveFileDialog(
                "导出诊断包",
                "诊断包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                $"ArchiveFixer-诊断包_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.zip",
                ResolveLogExportInitialDirectory());

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string exported = DiagnosticBundleService.Export(
                    _logService,
                    path,
                    Tasks.ToList(),
                    Settings);

                AppendLog("INFO", $"诊断包已导出：{exported}");
                _dialogService.ShowInfo(
                    "诊断包已导出，可直接发给开发者：" + Environment.NewLine + exported);
            }
            catch (Exception ex)
            {
                _logService.Exception("导出诊断包失败", ex, errorCode: DiagnosticCodes.LogWrite);
                _dialogService.ShowError("导出诊断包失败：" + ex.Message);
            }
        }

        private void ExportLog()
        {
            /*
             * 用户 2026-09-25 第 38 条："我想要的是本次操作的日志，也就是我最近一次点开操作的日志，
             * 我看着全部的日志非常的累" —— 所以这里默认只导本次操作（从"本次操作开始"那行分隔线起），
             * 不再把历史日志全拼进来（历史日志另有下面那个显式入口）。
             */
            string path = _dialogService.ShowSaveFileDialog(
                "导出日志（本次操作）",
                "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                $"ArchiveFixer-本次操作_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.txt",
                ResolveLogExportInitialDirectory());

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                (int lineCount, bool fromMarker) = _logService.ExportOperationLog(
                    path,
                    BuildLogExportHeader(),
                    includeDetails: Settings.VerboseLog,
                    extraSections: BuildLogExportSections());

                AppendLog("INFO", $"日志已导出（本次操作）：{path}（{lineCount} 行）");

                RememberLogExportDirectory(path);

                _dialogService.ShowInfo(
                    fromMarker
                        ? $"本次操作的日志已导出：{lineCount} 行。{Environment.NewLine}{path}"
                        : $"这次还没有「操作开始」的标记，已导出本次运行的完整日志：{lineCount} 行。{Environment.NewLine}{path}");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导出日志失败：" + ex.Message);
                _dialogService.ShowError(
                    $"导出日志失败：{ex.Message}{Environment.NewLine}"
                    + "换一个位置（例如桌面或 D 盘）再试；目标文件若正被别的程序打开，先把它关掉。");
            }
        }

        /// <summary>
        /// 导出**全部历史日志**（日志目录里每一份都拼进来；用户 2026-09-25 第 37 条要过一版，
        /// 第 38 条明确了"平时只要本次操作" —— 所以它降级成一个**显式**入口，不再占主按钮）。
        /// </summary>
        private void ExportAllLogs()
        {
            string path = _dialogService.ShowSaveFileDialog(
                "导出全部历史日志",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                $"ArchiveFixer-全部历史日志_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.txt");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                (int fileCount, int lineCount) = _logService.ExportAllLogs(
                    path,
                    includeDetails: Settings.VerboseLog);

                AppendLog("INFO", $"全部历史日志已导出：{path}（{fileCount} 份 / {lineCount} 行）");

                _dialogService.ShowInfo(
                    fileCount > 0
                        ? $"全部历史日志已导出：{fileCount} 份 / {lineCount} 行。{Environment.NewLine}{path}"
                        : "日志目录里还没有任何日志文件。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导出全部历史日志失败：" + ex.Message);
                _dialogService.ShowError($"导出全部历史日志失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 导出失败清单。
        /// 不弹两个窗口：没选路径就什么都不做（用户取消不该被当成错误）。
        /// </summary>
        private void ExportFailedList()
        {
            try
            {
                string path = _dialogService.ShowSaveFileDialog(
                    "导出失败清单",
                    "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    "ArchiveFixer-失败清单.txt");

                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                string text = _taskSummaryService.BuildFailedListText(Tasks);

                // 清单里不能有明文密码：服务层已经做过脱敏，这里再兜一道，避免以后有人改坏。
                File.WriteAllText(path, PasswordMasker.Sanitize(text), new UTF8Encoding(false));

                AppendLog("INFO", $"失败清单已导出：{path}");
                _dialogService.ShowInfo("失败清单已导出："

                    + Environment.NewLine + path);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导出失败清单失败：" + ex.Message);
                _dialogService.ShowException(ex, "导出失败清单失败（换一个位置再试，或先关掉正在打开这个文件的程序）");
            }
        }

        private void CopyFailedList()
        {
            bool ok = CopySanitizedToClipboard(_taskSummaryService.BuildFailedListText(Tasks));
            _dialogService.ShowInfo(ok ? "失败列表已复制。" : "复制失败。");
        }

        /// <summary>
        /// 剪贴板**唯一出口**：任何文本上剪贴板之前都必须先过脱敏
        /// （AGENTS.md §6 第 5 条：密码默认只存内存，日志、报告、剪贴板、详情窗口一律脱敏；
        /// 密码列表那条加密落盘的路也绝不放明文进来）。
        /// 四个入口（失败列表 / 任务信息 / 任务路径 / 错误信息）全部走这里，
        /// 口径与「导出日志」「导出失败清单」一致。
        /// </summary>
        private bool CopySanitizedToClipboard(string? text)
        {
            return WriteClipboardText(PasswordMasker.Sanitize(text));
        }

        /// <summary>
        /// 把**已经脱敏**的文本交给剪贴板服务。
        ///
        /// 做成 virtual 只是为了能在测试里截住实际写出的文本：无头进程里 <c>Clipboard.SetText</c>
        /// 必然不可用（没有 STA / OLE 消息泵），测试无法从系统剪贴板读回内容，只能从这个出口观察。
        /// ⚠ 不要在别处直接调它 —— 那会绕过 <see cref="CopySanitizedToClipboard"/> 里的脱敏。
        /// </summary>
        internal virtual bool WriteClipboardText(string sanitizedText)
        {
            return _clipboardService.CopyText(sanitizedText);
        }
    }
}
