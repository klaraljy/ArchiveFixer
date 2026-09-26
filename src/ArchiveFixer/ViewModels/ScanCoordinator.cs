using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 导入路径的语义（用户 2026-09-24 第 12 条亲自拍板）。
    ///
    /// <para>用户原话："每次我新选择了其他的，无论是什么，你都要将列表彻底清空" ——
    /// 「添加文件 / 添加文件夹」= **替换**（先把整张表清掉再加），
    /// 想往现有列表里加只能用显式入口「文件 → 追加到列表」（<see cref="Append"/>）。</para>
    ///
    /// <para>为什么替换必须是默认：旧行为是"追加 + 按路径去重"，于是用户上一次选的那批
    /// 一直赖在列表里 —— 他以为"我重新选了，列表就是我刚选的这些"，
    /// 点一键处理却把旧的那批一起跑了（原话："你为什么列表里面还是有"）。</para>
    /// </summary>
    public enum ImportMode
    {
        /// <summary>替换整张表（默认，「添加文件 / 添加文件夹」与拖放都走这条）。</summary>
        Replace = 0,

        /// <summary>追加到现有列表（只有菜单「文件 → 追加到列表」这一条显式入口）。</summary>
        Append = 1
    }

    /// <summary>
    /// 一次「导入后无用物提醒」的用户答案（用户 2026-09-24 第 15 条）。
    ///
    /// <para><see cref="Keep"/> = 主按钮「知道了」（什么都不做）；
    /// false = 次按钮「从列表里移除这些」（只把那些无用物从**任务列表**里去掉，源文件一个字节都不动）。</para>
    /// </summary>
    internal sealed class ImportJunkAnswer
    {
        /// <summary>true = 知道了（默认，最保守：什么都不做）。</summary>
        public bool Keep { get; init; } = true;

        /// <summary>是否勾了「以后不再提醒」（勾了就写进设置）。</summary>
        public bool OptionChecked { get; init; }
    }

    /// <summary>
    /// 扫描流程协调器。
    /// 负责导入文件/文件夹、生成任务、批量格式识别扫描、单任务重扫。
    /// </summary>
    internal sealed class ScanCoordinator
    {
        private readonly MainViewModel _vm;
        private readonly FileScanService _fileScanService;
        private readonly ArchiveDetectService _archiveDetectService;
        private readonly DialogService _dialogService;

        private CancellationTokenSource? _operationCts;

        /// <summary>
        /// "无用物"魔数体检用的识别器：**复用现有那一套**（与 §9.7 解压前提醒同一个实现），
        /// 绝不另写一份魔数表 —— 两份必然漂移，而漂移的代价是把用户的真包（伪装后缀 / 双面文件）
        /// 叫成"无用物"。
        /// </summary>
        private readonly IArchiveProber _junkProber = new MagicArchiveProber();

        /// <summary>
        /// 本次运行里用户勾过「以后不再提醒」（第 15 条）。
        ///
        /// <para>勾的那一下会写进设置（<see cref="AppSettings.RemindJunkAfterImport"/>），
        /// 这个内存标记只是让本次运行**立刻**生效，不用等下一次读设置。</para>
        /// </summary>
        private bool _junkReminderSuppressedThisRun;

        /// <summary>
        /// 导入后无用物提醒的**替身入口**（只给测试；正式路径永远是 null = 走真弹窗）。
        ///
        /// <para>为什么需要它：真窗口在无界面宿主里根本不会弹，而"问了几次、正文里列了什么、
        /// 用户答了「知道了」还是「从列表里移除」"这三件事只有让调用方答一次才测得出来
        /// （与 <c>ExtractionCoordinator.ReminderAnswerOverride</c> 同一套做法）。</para>
        /// </summary>
        internal Func<string, ImportJunkAnswer>? JunkReminderOverride
        {
            get => _junkReminderOverride;
            set => _junkReminderOverride = value;
        }

        private Func<string, ImportJunkAnswer>? _junkReminderOverride;

        public ScanCoordinator(
            MainViewModel vm,
            FileScanService fileScanService,
            ArchiveDetectService archiveDetectService,
            DialogService dialogService)
        {
            _vm = vm;
            _fileScanService = fileScanService;
            _archiveDetectService = archiveDetectService;
            _dialogService = dialogService;
        }

        private AppSettings Settings => _vm.Settings;
        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }
        private void AppendLog(string message) => _vm.AppendLog(message);
        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
        private void UpdateSummary() => _vm.UpdateSummary();
        private void RefreshOutputPaths() => _vm.RefreshOutputPaths();

        /// <summary>「添加文件…」：**替换**整张表（用户 2026-09-24 第 12 条）。</summary>
        public async Task AddFilesAsync() => await AddFilesAsync(ImportMode.Replace);

        /// <summary>「添加文件夹…」：**替换**整张表（用户 2026-09-24 第 12 条）。</summary>
        public async Task AddFolderAsync() => await AddFolderAsync(ImportMode.Replace);

        /// <summary>「追加到列表 → 追加文件…」：保留现有任务。</summary>
        public async Task AppendFilesAsync() => await AddFilesAsync(ImportMode.Append);

        /// <summary>「追加到列表 → 追加文件夹…」：保留现有任务。</summary>
        public async Task AppendFolderAsync() => await AddFolderAsync(ImportMode.Append);

        public async Task AddFilesAsync(ImportMode mode)
        {
            List<string> files = _dialogService.ShowOpenFileDialog();

            if (files.Count == 0)
            {
                return;
            }

            await AddPathsAsync(files, mode);
        }

        public async Task AddFolderAsync(ImportMode mode)
        {
            string folder = _dialogService.ShowFolderBrowserDialog();

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            await AddPathsAsync(new[] { folder }, mode);
        }

        /// <summary>
        /// 导入路径。
        ///
        /// <para><b>替换语义（默认）</b>：先把整张表**彻底清空**再添加（用户 2026-09-24 第 12 条）。
        /// 清空发生在扫描**之前**：扫描要花时间，这期间列表里若还留着上一批任务，
        /// 用户看到的就是"我明明重新选了，列表里还有旧的"，甚至能在扫描途中对旧任务点一键处理。</para>
        ///
        /// <para><b>界面线程纪律</b>（卡死那一条的修法）：扫描在 <see cref="FileScanService.ScanPathsAsync"/>
        /// 里已经整个跑在后台线程；这里只做两件事 —— 用**一次** <c>AddRange</c> 式的批量替换把结果放进
        /// 集合（几百个 <c>Add</c> 会让 DataGrid 逐个生成行、逐次触发集合变更），以及收尾刷一次汇总。</para>
        /// </summary>
        /// <param name="paths">要导入的文件/文件夹。</param>
        /// <param name="mode">
        /// 替换还是追加。**刻意不给默认值**：续解流程也会走这个方法往列表里补内层包
        /// （<c>OneClickCoordinator</c>），默认成"替换"就等于在一次解压途中把用户的任务表清空 ——
        /// 这种事必须让每个调用点**写出来**，编译器盯着。（用户 2026-09-24 第 12 条定的默认语义是替换。）
        /// </param>
        /// <param name="suppressAutoScan">
        /// 这一次导入**不要**顺手做整表重新识别（默认 false = 照设置里那一档走）。
        ///
        /// <para>续解往列表里补内层包时必须传 true：整表重扫会把已经解压完的任务状态冲回「已识别」，
        /// 第 1 层的结果在界面上就没了（识别由续解那一轮自己按任务做）。</para>
        ///
        /// <para>⛔ 这个开关**不许**用"临时改 <c>Settings.AutoScanAfterDrop</c>"来实现 ——
        /// 那是用户看得见、还会被自动保存写进盘的一个设置项：2026-09-26 的同步审计指出，
        /// 万一在那一小段窗口里进程被杀，用户的"导入后自动扫描"就永久变成关的，而他从没动过那个开关。</para>
        /// </param>
        public async Task AddPathsAsync(IEnumerable<string> paths, ImportMode mode, bool suppressAutoScan = false)
        {
            if (paths == null)
            {
                return;
            }

            IsBusy = true;

            // 这次导入实际收进来的任务（导入后的无用物提醒只扫这几个的源目录）。
            var importedTasks = new List<ArchiveTask>();

            try
            {
                int clearedCount = 0;

                if (mode == ImportMode.Replace && Tasks.Count > 0)
                {
                    clearedCount = Tasks.Count;

                    /*
                     * 清空整张表：连同"当前行"一起清（SelectedTask 不能再指着已经被移除的对象）、
                     * 索引重排，全部由 MainViewModel.ReplaceAllTasks 一处收口。
                     *
                     * refreshSummary: false —— 这只是过渡状态，收尾那一次才算汇总
                     *（一次导入只算一遍整表，别为中间态白算一次）。
                     */
                    _vm.ReplaceAllTasks(Array.Empty<ArchiveTask>(), refreshSummary: false);
                }

                AppendLog(
                    "INFO",
                    mode == ImportMode.Replace
                        ? string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.ImportReplaceLogFormat,
                            clearedCount > 0
                                ? string.Format(
                                    System.Globalization.CultureInfo.CurrentCulture,
                                    StatusText.ImportReplacedTasksTextFormat,
                                    clearedCount)
                                : string.Empty)
                        : StatusText.ImportAppendLogFormat);

                var options = new ScanOptions
                {
                    RecursiveScan = Settings.RecursiveScan,
                    ScanMode = Settings.ScanMode,
                    IncludeHiddenFiles = Settings.IncludeHiddenFiles,
                    IncludeSystemFiles = Settings.IncludeSystemFiles,
                    MaxFileSizeLimit = Settings.MaxFileSizeLimit

                };

                List<ArchiveTask> scannedTasks = await _fileScanService.ScanPathsAsync(paths, options);

                /*
                 * 追加模式下要按路径去重（同一批里重复选、或与列表里已有的重复）；
                 * 替换模式下列表刚被清空，去重集合是空的，逻辑同一条路。
                 */
                var existing = new HashSet<string>(
                    Tasks.Select(x => x.CurrentPath),
                    StringComparer.OrdinalIgnoreCase);

                var accepted = new List<ArchiveTask>();

                foreach (ArchiveTask task in scannedTasks)
                {
                    if (existing.Add(task.CurrentPath))
                    {
                        accepted.Add(task);
                    }
                }

                /*
                 * **一次**批量入列（不是逐个 Add）。
                 *
                 * 几百个任务逐个 Add 会让 ObservableCollection 抛几百次 CollectionChanged：
                 * DataGrid 每次都要处理一次行集合变化、绑定逐项求值，界面线程被拖着走。
                 * 这里合并成一次 Reset（MainViewModel.ReplaceAllTasks 内部实现），
                 * 后面再统一重排 Index、统一刷一次汇总。
                 */
                _vm.ReplaceAllTasks(Tasks.Concat(accepted));

                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.ImportFinishedLogFormat,
                        accepted.Count));

                if (!suppressAutoScan && Settings.AutoScanAfterDrop && accepted.Count > 0)
                {
                    // 扫描收尾会再刷一次汇总（识别结果改了格式/状态），所以这里不重复刷。
                    await ScanTasksAsync();
                }

                importedTasks = accepted;
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导入失败：" + ex.Message);
                _dialogService.ShowError("导入失败：" + ex.Message);

                // 失败也可能发生在"已经清空、还没填回来"那一段：汇总必须跟当前列表对上。
                UpdateSummary();
            }
            finally
            {
                IsBusy = false;
            }

            /*
             * ===== 导入后的无用物提醒（用户 2026-09-24 第 15 条）=====
             *
             * 用户原话："每次操作的选完文件夹，就要出一个无用物提醒，用户可以选中关闭以后就不用触发了。"
             *
             * ⚠ 位置必须在 IsBusy = false **之后**（也就是 finally 外面）：
             * 界面上的进度条绑的是 IsBusy，在忙碌状态里弹框就会出现"我还没确认，进度条已经在动"
             * —— 那正是用户第 17 条点名的那件事，别在这里又犯一次。
             */
            await RemindJunkAfterImportAsync(importedTasks);
        }

        /// <summary>
        /// 导入完成后扫一遍源目录里的"疑似无用物"并提醒（用户 2026-09-24 第 15 条）。
        ///
        /// <para>判据与 §9.7「解压前的提醒」**完全同一套**（<see cref="SourceJunkScanner"/>：
        /// 名字在打包者常放的那几类里 + 不属于本批任何任务 + 魔数认不出是压缩包）。
        /// 这里的区别只是**时机**：选完文件夹立刻说，而不是等他点了一键处理才说。</para>
        ///
        /// <para>三条纪律：</para>
        /// <list type="number">
        /// <item><description>程序对无用物**一个都不动**（不删 / 不改名 / 不搬）；</description></item>
        /// <item><description>点"从列表里移除这些"只动**任务列表**，源文件照样留在原地；</description></item>
        /// <item><description>勾了"以后不再提醒"写进设置（<see cref="AppSettings.RemindJunkAfterImport"/>），
        /// 界面上有开关能再打开；无界面宿主只写日志、不弹窗。</description></item>
        /// </list>
        /// </summary>
        private async Task RemindJunkAfterImportAsync(IReadOnlyList<ArchiveTask> imported)
        {
            if (_junkReminderSuppressedThisRun
                || !Settings.RemindJunkAfterImport
                || imported == null
                || imported.Count == 0)
            {
                return;
            }

            SourceJunkScanResult junk;

            try
            {
                // 扫描要枚举目录 + 读文件头（最坏 2000 次），放后台线程；界面线程只更新状态。
                junk = await Task.Run(() => SourceJunkScanner.ScanAsync(imported, _junkProber));
            }
            catch (Exception ex)
            {
                // 提醒绝不允许让导入变成失败（与 §9.7 同一口径）。
                AppendLog("WARN", "导入后的无用物扫描失败（不影响导入）：" + ex.Message);
                return;
            }

            if (!junk.HasAnything)
            {
                return;
            }

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.ImportJunkReminderLogFormat,
                    junk.TotalCount,
                    string.Join("、", junk.SampleNames)));

            if (System.Windows.Application.Current == null && _junkReminderOverride == null)
            {
                /*
                 * 无界面宿主（控制台宿主等）**不弹窗、不死等**：直接按「知道了」放行。
                 *
                 * 这里刻意**不走** DialogService 的降级路径：那一条会写一条"没有界面宿主，按…放行"的
                 * 降级日志，而本方法在每个导入用例里都会被走到 —— 那行日志会污染所有导入测试的日志断言
                 * （与 ExtractionCoordinator.AskReminderAsync 同一口径的处理）。
                 * 注意：放行 = **什么都不做**（不删、不动列表），这与破坏性确认的降级方向正好相反。
                 */
                AppendLog("INFO", StatusText.ImportJunkReminderNoHostLog);
                return;
            }

            string message = BuildJunkReminderMessage(junk);

            bool keepEverything;
            bool optionChecked;

            if (_junkReminderOverride != null)
            {
                // 测试替身优先：无界面宿主里它是唯一能回答"弹了几次、正文是什么"的入口。
                ImportJunkAnswer answer = _junkReminderOverride(message);

                keepEverything = answer.Keep;
                optionChecked = answer.OptionChecked;
            }
            else
            {
                keepEverything = _dialogService.ShowReminderConfirm(
                    StatusText.ImportJunkReminderTitle,
                    message,
                    StatusText.ImportJunkReminderKeepText,
                    StatusText.ImportJunkReminderRemoveText,
                    StatusText.ImportJunkReminderOptionText,
                    optionCheckedByDefault: false,
                    out optionChecked);
            }

            if (optionChecked)
            {
                _junkReminderSuppressedThisRun = true;
                _vm.SaveRemindJunkAfterImport(false);
                AppendLog("INFO", StatusText.ImportJunkReminderSuppressedLog);
            }

            if (keepEverything)
            {
                return;
            }

            // "从列表里移除这些"：只动任务列表，源文件一个字节都不动。
            int removed = _vm.RemoveTasksBySourcePaths(junk.Items.Select(item => item.FullPath));

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.ImportJunkReminderRemovedLogFormat,
                    removed));
        }

        /// <summary>拼提醒正文（三段：这些是什么 → 结果里有几个 → 程序不会动它们）。</summary>
        private static string BuildJunkReminderMessage(SourceJunkScanResult junk)
        {
            var builder = new System.Text.StringBuilder();

            builder.AppendLine(StatusText.ImportJunkReminderIntro);
            builder.AppendLine();
            builder.AppendLine(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.ImportJunkReminderCountFormat,
                junk.TotalCount));

            foreach (IGrouping<string, SourceJunkItem> group in
                     junk.Items.GroupBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("  " + group.Key);

                foreach (SourceJunkItem item in group)
                {
                    builder.AppendLine("    " + item.FileName);
                }
            }

            if (junk.ExtraCount > 0)
            {
                builder.AppendLine(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.ImportJunkReminderMoreFormat,
                    junk.ExtraCount));
            }

            if (junk.Truncated)
            {
                builder.AppendLine(StatusText.ImportJunkReminderTruncatedNote);
            }

            builder.AppendLine();
            builder.Append(StatusText.ImportJunkReminderFooter);

            return builder.ToString();
        }

        public async Task ScanTasksAsync()
        {
            if (Tasks.Count == 0)
            {
                return;
            }

            IsBusy = true;
            _operationCts = new CancellationTokenSource();

            try
            {
                // ⛔ 扫描也是一个"开始干活"的入口：「导出日志（本次操作）」要从这一行开始导，
                //    否则用户扫完就导出，头一行还是上一次操作的标记（第 46 条 ⑬ 抓到的同一类）。
                _vm.MarkOperationStartForLog("扫描任务");

                AppendLog("INFO", "开始扫描任务");

                foreach (ArchiveTask task in Tasks)
                {
                    _operationCts.Token.ThrowIfCancellationRequested();

                    try
                    {
                        await _archiveDetectService.ApplyDetectResultAsync(task, _operationCts.Token);

                        // 「首卷补齐后并组」：这一行若是"只有后续卷"，而它的首卷就在列表里 → 并入首卷那一行。
                        MergeLaterVolumeIntoFirstVolumeRow(task);

                        /*
                         * 源文件快照（AGENTS.md §6 不变量 11 的基准）：**识别一完成就拍**。
                         *
                         * 为什么是这一刻：
                         * ① 快照要保护的正是"识别结果"这个结论 —— 基准必须与它同一时刻产生，
                         *    拍早了（导入时）会把扫描自己耗时也算进去，拍晚了就漏掉中间那段窗口；
                         * ② 必须**早于任何解压动作**：一键处理会在这条路径之后直接开始解压，
                         *    这里拍完，等到协调器开工前再比，中间的变化一个都跑不掉；
                         * ③ 分卷整组也已经归好组（扫描阶段就把 VolumePaths 填好了），
                         *    所以"动的是第二卷"这种情形从第一刻起就在基准里。
                         *
                         * 拍快照只做 stat（不读内容、不碰源文件），失败也不会抛出来 ——
                         * 它只是记账，绝不是门槛；真读不到时下一次比对会如实说"文件不见了"。
                         */
                        task.CaptureSourceSnapshot();

                        AppendLog("INFO", $"检测：{task.FileName}，格式：{task.DetectedFormat}，建议后缀：{task.SuggestedExtension}，状态：{task.ExtensionStatus}");
                    }
                    catch (Exception ex)
                    {
                        task.Status = StatusText.UnknownError;
                        task.ErrorMessage = ex.Message;
                        AppendLog("ERROR", $"扫描失败：{task.FileName}，{ex.Message}");
                    }
                }

                RefreshOutputPaths();
                UpdateSummary();
                AppendLog("INFO", "扫描完成");
            }
            catch (OperationCanceledException)
            {
                AppendLog("WARN", "扫描已取消");
            }
            finally
            {
                IsBusy = false;
                _operationCts = null;
            }
        }

        /// <summary>
        /// 重新扫描单个任务。
        /// </summary>
        /// <summary>
        /// 「首卷补齐后并组」：`set.7z.002` 这种"只有后续卷"的行，如果在**列表里**找得到它那一组的首卷，
        /// 就把它改成「已并入」——那一组由首卷那一行带着一起解，这一行不再单独算一单
        /// （用户 2026-09-26 拍板："标成「分卷（后续卷，缺首卷）」**或**在首卷补齐后并组"）。
        ///
        /// <para>⛔ 判据全在**事实**上：①这一行的状态确实是「分卷缺失」（识别层刚判的）；②从它的名字推得出
        /// 标准首卷名（`VolumeGroupDetector`，与别处同一份实现）；③列表里**真有一行**指着那个文件
        /// （按目录 + 文件名比，不按"像不像"猜）。三条缺一条就一个字都不改。</para>
        ///
        /// <para>为什么不是"把这一行从列表里删掉"：删用户的列表行是破坏性的（他可能就是想看着它），
        /// 而"标成已并入 + 不单独处理"已经让汇总与列表都诚实了。</para>
        /// </summary>
        internal bool MergeLaterVolumeIntoFirstVolumeRow(ArchiveTask? task)
        {
            if (task == null ||
                !string.Equals(task.Status, StatusText.VolumeMissing, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                return false;
            }

            string fileName = Path.GetFileName(task.CurrentPath);
            int? index = VolumeGroupDetector.TryGetVolumeIndex(fileName);

            if (index == null || index.Value < 2)
            {
                return false;
            }

            string? firstVolumeName = VolumeGroupDetector.TryGetFirstVolumeName(fileName);

            if (string.IsNullOrWhiteSpace(firstVolumeName))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

            ArchiveTask? firstVolumeRow = Tasks.FirstOrDefault(candidate =>
                candidate != null &&
                !ReferenceEquals(candidate, task) &&
                !string.IsNullOrWhiteSpace(candidate.CurrentPath) &&
                string.Equals(Path.GetFileName(candidate.CurrentPath), firstVolumeName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(candidate.CurrentPath) ?? string.Empty, directory, StringComparison.OrdinalIgnoreCase));

            if (firstVolumeRow == null)
            {
                return false;
            }

            task.Status = StatusText.Skipped;
            task.Operation = StatusText.OpSkip;
            task.ErrorMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.LaterVolumeMergedNoteFormat,
                firstVolumeName);

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.LaterVolumeMergedLogFormat,
                    fileName,
                    firstVolumeName));

            return true;
        }

        internal async Task RescanTaskAsync(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            await _archiveDetectService.ApplyDetectResultAsync(task);

            // 单任务重扫走同一套：并组这件事不许只在整表扫描时才成立（第 41 条那颗按钮就是按任务重扫的）。
            MergeLaterVolumeIntoFirstVolumeRow(task);

            /*
             * 重扫 = 重新识别，所以基准也必须**跟着重拍**（用户右键「重新扫描此文件」正是
             * "源文件已变化"给的那条出路）。不重拍的话：用户按提示重扫完，
             * 手上还是一份对不上老文件的旧快照，一开工又被拦一次 —— 那句提示就成了死循环。
             */
            task.CaptureSourceSnapshot();

            RefreshOutputPaths();
            UpdateSummary();
        }
    }
}
