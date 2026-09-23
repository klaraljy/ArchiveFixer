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

        public async Task AddFilesAsync()
        {
            List<string> files = _dialogService.ShowOpenFileDialog();

            if (files.Count == 0)
            {
                return;
            }

            await AddPathsAsync(files);
        }

        public async Task AddFolderAsync()
        {
            string folder = _dialogService.ShowFolderBrowserDialog();

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            await AddPathsAsync(new[] { folder });
        }

        public async Task AddPathsAsync(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return;
            }

            IsBusy = true;

            try
            {
                AppendLog("INFO", "开始导入路径");

                var options = new ScanOptions
                {
                    RecursiveScan = Settings.RecursiveScan,
                    ScanMode = Settings.ScanMode,
                    IncludeHiddenFiles = Settings.IncludeHiddenFiles,
                    IncludeSystemFiles = Settings.IncludeSystemFiles,
                    MaxFileSizeLimit = Settings.MaxFileSizeLimit

                };

                List<ArchiveTask> scannedTasks = await _fileScanService.ScanPathsAsync(paths, options);

                int addedCount = 0;
                var existing = new HashSet<string>(
                    Tasks.Select(x => x.CurrentPath),
                    StringComparer.OrdinalIgnoreCase);

                foreach (ArchiveTask task in scannedTasks)
                {
                    if (existing.Add(task.CurrentPath))
                    {
                        task.Index = Tasks.Count + 1;
                        Tasks.Add(task);
                        addedCount++;
                    }
                }

                AppendLog("INFO", $"导入完成，新增任务 {addedCount} 个。");

                if (Settings.AutoScanAfterDrop && addedCount > 0)
                {
                    await ScanTasksAsync();
                }


                UpdateSummary();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导入失败：" + ex.Message);
                _dialogService.ShowError("导入失败：" + ex.Message);
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
