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
            RefreshOutputPaths();
            UpdateSummary();
        }
    }
}
