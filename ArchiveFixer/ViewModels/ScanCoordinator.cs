using ArchiveFixer.Models;
using ArchiveFixer.Services;
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
        public async Task AddPathsAsync(IEnumerable<string> paths, ImportMode mode)
        {
            if (paths == null)
            {
                return;
            }

            IsBusy = true;

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

                if (Settings.AutoScanAfterDrop && accepted.Count > 0)
                {
                    // 扫描收尾会再刷一次汇总（识别结果改了格式/状态），所以这里不重复刷。
                    await ScanTasksAsync();
                }
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
                AppendLog("INFO", "开始扫描任务");

                foreach (ArchiveTask task in Tasks)
                {
                    _operationCts.Token.ThrowIfCancellationRequested();

                    try
                    {
                        await _archiveDetectService.ApplyDetectResultAsync(task, _operationCts.Token);

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
        internal async Task RescanTaskAsync(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            await _archiveDetectService.ApplyDetectResultAsync(task);

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
