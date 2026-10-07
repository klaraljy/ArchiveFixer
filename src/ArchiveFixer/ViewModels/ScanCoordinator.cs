using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
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
             * ===== 导入后的无用物：**导入一完成就自动移出任务列表** =====
             *
             * 用户 2026-09-28 原话：「为什么不在检测到的时候就直接移除」「当选择文件的时候，移除无用物的
             * 弹窗，没有弹出来我还以为你又没弄好，但是在点击一键处理之后，他一出来，但是在我点击之前
             * 他一直还是勾选着，你为什么要这样」。
             *
             * 所以这里**不再弹任何提醒框**（那个框已经退休，见 RemoveJunkTasksFromListAsync 的说明），
             * 也不再等一键处理开工：导入收尾**立刻**把无用物从列表里移掉 —— 复用同一个实现，
             * ⛔ 不另写一套扫描判据（两份必然漂移）。
             *
             * 三条纪律一个字都没变：① 只动**列表**，绝不删 / 改名 / 搬走磁盘上的文件；
             * ② 判据仍是 SourceJunkScanner（名字窄 + 魔数认得出是包就绝不算无用物）；
             * ③ 移了谁、移了几个，逐条写进日志。
             *
             * ⚠ 位置在 IsBusy = false **之后**（也就是 finally 外面）：界面上进度条绑的是 IsBusy，
             * 在忙碌状态里做这一步，用户会看到"我还没确认，进度条已经在动"——那正是他第 17 条点名的事。
             *
             * ⚠ 续解往列表里补内层包那一条路（suppressAutoScan = true）**不做**：
             * 那些包是程序自己刚产出的过程物（而且刚刚逐个按内容确认过是归档），
             * 它们的"源目录"是产物目录（可能刚解出几万个文件），在那儿做无用物体检纯属白烧时间。
             */
            if (!suppressAutoScan)
            {
                await RemoveJunkTasksFromListAsync(importedTasks, StatusText.ImportJunkRemovedLogFormat);
            }

            /*
             * ===== 导入后的**空间体检**（用户 2026-09-27 第 2 条）=====
             *
             * 位置与无用物提醒**刻意同级**（都在 IsBusy = false 之后）：用户自己点的那一次
             * 一键处理永远不会被这两个框打断，而"这批包放不放得下"必须在**动手之前**说 ——
             * 真机上他遇到的是"解到一半才发现盘不够"。
             *
             * 续解往列表里补内层包那一条路（suppressAutoScan = true）**不做**：
             * 那些包是程序自己刚产出的过程物，正在跑的批次里统计它们没有意义（空间门照旧逐个判）。
             */
            if (!suppressAutoScan)
            {
                await _vm.CheckSpaceForTasksAsync(importedTasks, "导入完成");
            }
        }

        /// <summary>
        /// 把「无用物」（对解压没用的说明 / 网址 / 广告之类）从任务列表里移掉。
        ///
        /// <para><b>两个调用点</b>：</para>
        /// <list type="number">
        /// <item><description><b>导入一完成</b>（<see cref="AddPathsAsync"/>）—— 用户 2026-09-28 拍板的
        /// 主路径：他原话「为什么不在检测到的时候就直接移除」；</description></item>
        /// <item><description><b>一键处理开工前</b>（<c>OneClickCoordinator.RunAsync</c>，2026-09-28「1.要」）——
        /// 兜底那一道：列表里若还有无用物（导入那一次没扫到的），开工前顺手清干净。</description></item>
        /// </list>
        ///
        /// <para>⛔ 只动**列表**，绝不删 / 移动任何文件（用户原话：「他们是无用物，只是对你们解压没有用，
        /// 不是垃圾」）。扫不到、扫描出错 → 什么都不做，只写一句日志，绝不影响导入 / 这一批。</para>
        ///
        /// <para>⚠ 2026-09-28：过去这里配着一个「无用物提醒」弹窗（用户点「从列表里移除这些」才移）。
        /// 那个框**已经退休**（用户明确说"检测到就直接移除"，而且他抱怨框在该出现的时候没出现、
        /// 不该出现的时候又冒出来）—— 同一个功能只留自动移出这一条路，⛔ 别再把它做回成弹窗。</para>
        /// </summary>
        /// <param name="tasks">要体检的任务（导入那一次传的是本次导入的任务，一键处理传的是这一批的目标）。</param>
        /// <param name="removedLogFormat">
        /// 移掉之后那句日志的模板（<c>{0}</c> = 个数，<c>{1}</c> = 前几个名字）。
        /// 两个调用点的语境不同（导入 / 一键处理），所以文案由调用方按 <see cref="StatusText"/> 里的常量给；
        /// 传 null 时退回一键处理那一句。
        /// </param>
        /// <returns>移掉的任务个数。</returns>
        internal async Task<int> RemoveJunkTasksFromListAsync(
            IReadOnlyList<ArchiveTask>? tasks,
            string? removedLogFormat = null)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return 0;
            }

            SourceJunkScanResult junk;

            try
            {
                junk = await SourceJunkScanner.ScanAsync(tasks, _junkProber);
            }
            catch (Exception ex)
            {
                // 文案刻意不提"一键处理"：导入那条路也走这里（两条路的语境不同，措辞保持中性）。
                AppendLog("WARN", "无用物扫描失败（不影响这一次，什么都不动）：" + ex.Message);
                return 0;
            }

            /*
             * ⛔ 判据读**唯一出口** `AllHits`（= 全部命中，不是那份"最多列 10 条"的提示名单）：
             * 真机 2026-10-07 10:15 那一批，导入这条路按 `Items` 只移走了 10 个（上限），
             * 剩下的 7 个要等用户点「一键处理」才被批末那一步移掉 ⇒ 他问「为什么一开始只清理 10 个」。
             */
            if (junk == null || !junk.HasAnything || junk.AllHits.Count == 0)
            {
                return 0;
            }

            int removed = _vm.RemoveTasksBySourcePaths(junk.AllHits.Select(item => item.FullPath));

            if (removed > 0)
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        removedLogFormat ?? StatusText.OneClickJunkRemovedLogFormat,
                        removed,
                        string.Join("、", junk.AllHits.Take(5).Select(item => item.FileName))));
            }

            return removed;
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
                        /*
                         * ===== 识别整段挪到线程池（用户 2026-10-02 真机：「导入的文件比较多，一开始就卡死，
                         * 未响应有一分多钟」）=====
                         *
                         * 识别一次要做三件 CPU 密集的事：解析文件头、**扫尾部**（认不出格式时要顺序扫到 512 MiB）、
                         * 加密判读（读头/尾再解析结构）。它们过去全都跑在**调用方线程**上 —— 而调用方就是 UI 线程，
                         * 只有里面的文件 I/O 是 await 出去的。于是每读一个文件、UI 就卡一段；
                         * 几百个文件排队走完，界面上就是"未响应"。
                         *
                         * 放进 `Task.Run` 之后：`await` 一返回就**自动回到 UI 线程**（捕获的同步上下文），
                         * 所以下面那些碰 `Tasks` 集合的动作（并组、写日志、刷汇总）照旧在 UI 线程上做。
                         * ⛔ 识别结论一个字都没改，只是换了个线程去算。
                         */
                        await Task.Run(
                            () => _archiveDetectService.ApplyDetectResultAsync(task, _operationCts.Token),
                            _operationCts.Token);

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

        /// <summary>
        /// **批末把"名字被改过的那些行"重算一遍「检测格式 / 建议后缀 / 后缀状态」**
        /// （用户 2026-10-07 点名的三列过期：「文件名、大小、后缀、检测格式、状态，每个都有问题」）。
        ///
        /// <para>为什么会过期：这三列写的是**扫描那一刻、按当时那个名字**得出的结论。真机上
        /// `111(2)_.zi删除p` 那一刻是「后缀不匹配」，随后我们把它改回了 `111(2)_.zip` ⇒ 不重算，
        /// 列表里就一直挂着一句已经不对的话。</para>
        ///
        /// <para>判据（只看结构化事实）：**最初导入的名字 ≠ 这一行现在显示的那份文件的名字** ⇒ 被改过名。
        /// ⛔ 只重算这三个纯事实字段（走既有出口 <see cref="ArchiveDetectService.GetExtensionStatus"/>，
        /// 与扫描那一刻同一处判据）；⛔ 不碰 Status / Operation / 进度 / 落点 / VolumePaths；
        /// ⛔ 文件不在（判不出）就什么都不做。</para>
        /// </summary>
        public async Task RefreshRenamedRowFactsAsync()
        {
            foreach (ArchiveTask task in Tasks.ToList())
            {
                if (task == null)
                {
                    continue;
                }

                string displayPath = task.DisplayPath;

                if (string.IsNullOrWhiteSpace(displayPath))
                {
                    continue;
                }

                string importName = Path.GetFileName(task.OriginalPath);
                string currentName = Path.GetFileName(displayPath);

                if (importName.Length == 0
                    || string.Equals(importName, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    // 名字没变过 ⇒ 这三列不会过期（绝大多数行都是这一档）。
                    continue;
                }

                try
                {
                    if (!File.Exists(displayPath))
                    {
                        // 判不出 ⇒ 什么都不做（保持扫描那一刻的原样）。
                        continue;
                    }

                    DetectResult result = await _archiveDetectService.DetectAsync(displayPath).ConfigureAwait(true);

                    task.DetectedFormat = result.Format;
                    task.SuggestedExtension = result.SuggestedExtension;
                    task.ExtensionStatus = _archiveDetectService.GetExtensionStatus(
                        currentName,
                        ExtensionHelper.GetLastExtensionDisplay(displayPath),
                        result);
                }
                catch
                {
                    // 这几列只是给人看的：重算失败一律保持原样，⛔ 不许让它影响这一批的结论。
                }
            }
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