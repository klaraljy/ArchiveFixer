using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;


namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// ViewModel 基类。
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 普通命令。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action execute)
        {
            _execute = _ => execute();
        }

        public RelayCommand(Action execute, Func<bool> canExecute)
        {
            _execute = _ => execute();
            _canExecute = _ => canExecute();
        }

        public RelayCommand(Action<object?> execute)
        {
            _execute = execute;
        }

        public RelayCommand(Action<object?> execute, Predicate<object?> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter)
        {
            _execute(parameter);
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 异步命令。
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute)
        {
            _execute = _ => execute();
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
        {
            _execute = _ => execute();
            _canExecute = _ => canExecute();
        }

        public AsyncRelayCommand(Func<object?, Task> execute)
        {
            _execute = execute;
        }

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (_isExecuting)
            {
                return false;
            }

            return _canExecute?.Invoke(parameter) ?? true;
        }

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            try
            {
                _isExecuting = true;
                RaiseCanExecuteChanged();
                await _execute(parameter);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 主窗口 ViewModel。
    /// 
    /// 职责：
    /// 1. 持有任务列表。
    /// 2. 持有日志列表。
    /// 3. 持有设置。
    /// 4. 暴露界面命令。
    /// 5. 调用各 Service 完成业务。
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly FileScanService _fileScanService;
        private readonly ArchiveDetectService _archiveDetectService;
        private readonly RenameService _renameService;
        private readonly IArchiveEngine _archiveEngine;
        private readonly PasswordService _passwordService;
        private readonly LogService _logService;
        private readonly SettingsService _settingsService;
        private readonly PathService _pathService;
        private readonly TaskSummaryService _taskSummaryService;
        private readonly ClipboardService _clipboardService;
        private readonly DialogService _dialogService;

        private readonly ScanCoordinator _scanCoordinator;
        private readonly RenameCoordinator _renameCoordinator;
        private readonly ExtractionCoordinator _extractionCoordinator;
        private readonly OneClickCoordinator _oneClickCoordinator;

        private AppSettings _settings;
        private string _globalPassword = string.Empty;
        private bool _showPassword;
        private bool _isBusy;
        private bool _isStopping;
        private string _selectedOutputDirectory = string.Empty;
        private TaskSummary _summary = new();
        private ArchiveTask? _selectedTask;

        public ObservableCollection<ArchiveTask> Tasks { get; } = new();

        public ObservableCollection<OperationLogItem> Logs { get; } = new();

        public AppSettings Settings
        {
            get => _settings;
            set
            {
                if (SetProperty(ref _settings, value))
                {
                    ApplyEngineSettings();
                }
            }
        }

        /// <summary>
        /// 把设置里与归档引擎有关的项推给 ToolLocator。
        /// 7z.exe 的路径**只**通过这里生效，别处不许再拼路径（AGENTS.md §3.1）。
        /// </summary>
        private void ApplyEngineSettings()
        {
            ToolLocator.Default.CustomSevenZipExePath = _settings?.CustomSevenZipExePath ?? string.Empty;

            /*
             * 缓存根目录：留空就用程序目录下的 data。
             * 绝不能默认回落到 %AppData%（C 盘）—— 用户明确要求绿色软件跟着安装位置走。
             */
            string cacheRoot = _settings?.CacheRootDirectory ?? string.Empty;

            _pathService.DataRootDirectory = string.IsNullOrWhiteSpace(cacheRoot)
                ? PathService.DefaultDataRootDirectory
                : cacheRoot;

            // 递归工作区也必须跟着走：它动辄几百 MB，不能落到 %TEMP%（C 盘）。
            RecursiveExtractor.ConfiguredWorkspaceRoot = _pathService.WorkDirectory;
            ToolLocator.Default.Invalidate();
        }

        public string GlobalPassword
        {
            get => _globalPassword;
            set => SetProperty(ref _globalPassword, value);
        }

        public bool ShowPassword
        {
            get => _showPassword;
            set => SetProperty(ref _showPassword, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaiseAllCommandCanExecuteChanged();
                }
            }
        }

        public bool IsStopping
        {
            get => _isStopping;
            set
            {
                if (SetProperty(ref _isStopping, value))
                {
                    RaiseAllCommandCanExecuteChanged();
                }
            }
        }

        public string SelectedOutputDirectory
        {
            get => _selectedOutputDirectory;
            set
            {
                if (SetProperty(ref _selectedOutputDirectory, value ?? string.Empty))
                {
                    if (Settings != null)
                    {
                        Settings.CustomOutputDirectory = _selectedOutputDirectory;

                        if (!string.IsNullOrWhiteSpace(_selectedOutputDirectory))
                        {
                            Settings.ExtractToOriginalDirectory = false;
                            OnPropertyChanged(nameof(Settings));
                        }
                    }

                    RefreshOutputPaths();
                }
            }
        }


        public TaskSummary Summary
        {
            get => _summary;
            set => SetProperty(ref _summary, value);
        }

        public ArchiveTask? SelectedTask
        {
            get => _selectedTask;
            set => SetProperty(ref _selectedTask, value);
        }


        public ICommand AddFilesCommand { get; }
        public ICommand AddFolderCommand { get; }
        public ICommand ScanCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand RemoveSelectedCommand { get; }

        public ICommand SmartRenameCommand { get; }
        public ICommand AddExtensionCommand { get; }
        public ICommand ReplaceExtensionCommand { get; }
        public ICommand DeleteLastExtensionCommand { get; }
        public ICommand DeleteMultipleExtensionsCommand { get; }

        public ICommand StartExtractCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand CancelCurrentCommand { get; }

        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPasswordListCommand { get; }

        /// <summary>导入密码本（记住路径，下次启动自动加载）。用户已多次要求：导入一次就够。</summary>
        public ICommand ImportPasswordBookCommand { get; }
        public ICommand ExportLogCommand { get; }
        public ICommand CopyFailedListCommand { get; }

        /// <summary>把失败清单导出成 txt（M3：能说清每个失败为什么失败，且能带走）。</summary>
        public ICommand ExportFailedListCommand { get; }
        public ICommand OpenOutputDirectoryCommand { get; }
        public ICommand SelectOutputDirectoryCommand { get; }
        public ICommand OpenLogDirectoryCommand { get; }

        /// <summary>打开递归解压的工作区目录（中断后残留的中间产物在这里）。</summary>
        public ICommand OpenWorkDirectoryCommand { get; }

        public ICommand RemoveTaskCommand { get; }
        public ICommand RescanTaskCommand { get; }
        public ICommand CopyTaskInfoCommand { get; }
        public ICommand CopyTaskPathCommand { get; }
        public ICommand CopyTaskErrorCommand { get; }
        public ICommand OpenTaskDirectoryCommand { get; }
        public ICommand OpenTaskOutputDirectoryCommand { get; }
        public ICommand ToggleShowPasswordCommand { get; }
        /// <summary>一键处理：识别 → 修正伪装后缀（一次预览确认）→ 按密码本解压 → 一行汇总。</summary>
        public ICommand OneClickProcessCommand { get; }

        public ICommand ResetSettingsCommand { get; }

        public MainViewModel()
            : this(
                  new FileScanService(),
                  new ArchiveDetectService(),
                  new RenameService(),
                  new SevenZipEngine(),
                  new PasswordService(),
                  new LogService(),
                  new SettingsService(),
                  new PathService(),
                  new TaskSummaryService(),
                  new ClipboardService(),
                  new DialogService())
        {
        }

        public MainViewModel(
            FileScanService fileScanService,
            ArchiveDetectService archiveDetectService,
            RenameService renameService,
            IArchiveEngine archiveEngine,
            PasswordService passwordService,
            LogService logService,
            SettingsService settingsService,
            PathService pathService,
            TaskSummaryService taskSummaryService,
            ClipboardService clipboardService,
            DialogService dialogService)
        {
            _fileScanService = fileScanService;
            _archiveDetectService = archiveDetectService;
            _renameService = renameService;
            _archiveEngine = archiveEngine;
            _passwordService = passwordService;
            _logService = logService;
            _settingsService = settingsService;
            _pathService = pathService;
            _taskSummaryService = taskSummaryService;
            _clipboardService = clipboardService;
            _dialogService = dialogService;

            _scanCoordinator = new ScanCoordinator(this, fileScanService, archiveDetectService, dialogService);
            _renameCoordinator = new RenameCoordinator(this, _scanCoordinator, renameService, dialogService);
            _extractionCoordinator = new ExtractionCoordinator(this, archiveEngine, passwordService, pathService, dialogService);
            _oneClickCoordinator = new OneClickCoordinator(this, _scanCoordinator, _renameCoordinator, _extractionCoordinator, dialogService);

            _settings = _settingsService.Load();
            ApplyEngineSettings();
            SelectedOutputDirectory = _settings.CustomOutputDirectory ?? string.Empty;

            AddFilesCommand = new AsyncRelayCommand(_scanCoordinator.AddFilesAsync, CanRunNormalCommand);
            AddFolderCommand = new AsyncRelayCommand(_scanCoordinator.AddFolderAsync, CanRunNormalCommand);
            ScanCommand = new AsyncRelayCommand(_scanCoordinator.ScanTasksAsync, CanRunNormalCommand);
            ClearCommand = new RelayCommand(ClearTasks, CanRunNormalCommand);
            RemoveSelectedCommand = new RelayCommand(RemoveSelectedTasks, CanRunNormalCommand);

            SmartRenameCommand = new AsyncRelayCommand(_renameCoordinator.SmartRenameAsync, CanRunNormalCommand);
            AddExtensionCommand = new AsyncRelayCommand(_renameCoordinator.AddExtensionAsync, CanRunNormalCommand);
            ReplaceExtensionCommand = new AsyncRelayCommand(_renameCoordinator.ReplaceExtensionAsync, CanRunNormalCommand);
            DeleteLastExtensionCommand = new AsyncRelayCommand(_renameCoordinator.DeleteLastExtensionAsync, CanRunNormalCommand);
            DeleteMultipleExtensionsCommand = new AsyncRelayCommand(_renameCoordinator.DeleteMultipleExtensionsAsync, CanRunNormalCommand);

            OneClickProcessCommand = new AsyncRelayCommand(_oneClickCoordinator.RunAsync, CanRunNormalCommand);

            StartExtractCommand = new AsyncRelayCommand(_extractionCoordinator.StartExtractAsync, CanStartExtract);
            StopCommand = new RelayCommand(_extractionCoordinator.StopAfterCurrent, () => IsBusy);
            CancelCurrentCommand = new RelayCommand(_extractionCoordinator.CancelCurrentTask, () => IsBusy);

            OpenSettingsCommand = new RelayCommand(OpenSettings, CanRunNormalCommand);
            OpenPasswordListCommand = new RelayCommand(OpenPasswordList, CanRunNormalCommand);
            ImportPasswordBookCommand = new RelayCommand(ImportPasswordBook, CanRunNormalCommand);
            ExportLogCommand = new RelayCommand(ExportLog);
            CopyFailedListCommand = new RelayCommand(CopyFailedList);
            ExportFailedListCommand = new RelayCommand(ExportFailedList);
            OpenOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory);
            SelectOutputDirectoryCommand = new RelayCommand(SelectOutputDirectory, CanRunNormalCommand);
            OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
            OpenWorkDirectoryCommand = new RelayCommand(OpenWorkDirectory);


            RemoveTaskCommand = new RelayCommand(RemoveTask);
            RescanTaskCommand = new AsyncRelayCommand(_scanCoordinator.RescanTaskAsync);
            CopyTaskInfoCommand = new RelayCommand(CopyTaskInfo);
            CopyTaskPathCommand = new RelayCommand(CopyTaskPath);
            CopyTaskErrorCommand = new RelayCommand(CopyTaskError);
            OpenTaskDirectoryCommand = new RelayCommand(OpenTaskDirectory);
            OpenTaskOutputDirectoryCommand = new RelayCommand(OpenTaskOutputDirectory);
            ToggleShowPasswordCommand = new RelayCommand(() =>
            {
                ShowPassword = !ShowPassword;
                OnPropertyChanged(nameof(GlobalPassword));
            });

            ResetSettingsCommand = new RelayCommand(ResetSettings, CanRunNormalCommand);

            Initialize();
        }

        private void Initialize()
        {
            try
            {
                _logService.Initialize();
            }
            catch
            {
                // 日志初始化失败不能导致程序无法启动。
            }

            AppendLog("INFO", "软件启动");

            if (!_archiveEngine.IsAvailable)
            {
                AppendLog("WARN", "未找到 tools\\7zip\\7z.exe，软件可启动，但解压时会失败。");
            }

            if (!ToolLocator.Default.SevenZipDllExists)
            {
                AppendLog("WARN", "未找到 tools\\7zip\\7z.dll，请确认 7-Zip 命令行文件完整。");
            }

            LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

            UpdateSummary();
        }

        /// <summary>
        /// 供界面拖拽等入口调用，实际导入逻辑在扫描协调器中。
        /// </summary>
        public Task AddPathsAsync(IEnumerable<string> paths)
        {
            return _scanCoordinator.AddPathsAsync(paths);
        }

        /// <summary>
        /// 启动时报告上次没做完的工作区。
        ///
        /// 递归解压被中断或取消时，产物会留在工作区（刻意不发布、不清理，见不变量 12）。
        /// 启动时不提醒一句，用户永远不会知道这些东西还在占磁盘。
        /// 这里只报告，**不自动删**：删工作区必须先经用户确认（不变量 13）。
        /// </summary>
        /// <summary>
        /// 启动时自动加载上次的密码本。
        /// 用户反复强调过：导入一次就该一直有效，不该每次重导。
        /// 文件不在了只写一条 WARN，不弹窗打扰。
        /// </summary>
        private void AutoLoadPasswordBook()
        {
            try
            {
                string path = Settings?.PasswordBookPath ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                if (!File.Exists(path))
                {
                    AppendLog("WARN", $"上次的密码本找不到了，已跳过自动加载：{path}");
                    return;
                }

                int count = _passwordService.ImportPasswordList(path).Count;
                AppendLog("INFO", $"已自动加载密码本：{path}（{count} 条）");
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "自动加载密码本失败：" + ex.Message);
            }
        }

        /// <summary>导入密码本，并把路径记进设置，下次启动自动加载。</summary>
        private void ImportPasswordBook()
        {
            try
            {
                string path = _dialogService.ShowOpenSingleFileDialog(
                    "选择密码本文件（一行一个密码；也支持 名称:密码）",
                    "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");

                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                int count = _passwordService.ImportPasswordList(path).Count;

                Settings.PasswordBookPath = path;
                _settingsService.Save(Settings);

                AppendLog("INFO", $"已导入密码本：{path}（{count} 条），下次启动会自动加载。");
                _dialogService.ShowInfo($"已导入 {count} 条密码，并记住了这个文件，下次启动会自动加载。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导入密码本失败：" + ex.Message);
            }
        }

        private void LogLeftoverWorkspaces()
        {
            try
            {
                string workRoot = _pathService.WorkDirectory;

                if (!Directory.Exists(workRoot))
                {
                    return;
                }

                string[] leftovers = Directory.GetDirectories(workRoot);

                if (leftovers.Length == 0)
                {
                    return;
                }

                AppendLog("WARN", $"发现 {leftovers.Length} 个未完成的工作区（上次中断或取消留下的），位置：{workRoot}");
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "检查工作区失败：" + ex.Message);
            }
        }

        private bool CanRunNormalCommand()
        {
            return !IsBusy;
        }

        private bool CanStartExtract()
        {
            return !IsBusy && Tasks.Any(x => x.IsSelected);
        }

        public void ClearTasks()
        {
            if (Tasks.Count == 0)
            {
                return;
            }

            bool confirm = _dialogService.ShowConfirm("确定要清空任务列表吗？");

            if (!confirm)
            {
                return;
            }

            Tasks.Clear();
            LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

            UpdateSummary();
            AppendLog("INFO", "已清空任务列表");
        }

        public void RemoveSelectedTasks()
        {
            var selected = Tasks.Where(x => x.IsSelected).ToList();

            foreach (ArchiveTask task in selected)
            {
                Tasks.Remove(task);
            }

            RebuildTaskIndex();
            LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

            UpdateSummary();
            AppendLog("INFO", $"已移除选中任务 {selected.Count} 个");
        }

        public void UpdateSummary()
        {
            Summary = _taskSummaryService.BuildSummary(Tasks);
            RaiseAllCommandCanExecuteChanged();
        }

        public void AppendLog(string message)
        {
            AppendLog("INFO", message);
        }

        public void AppendLog(string level, string message)
        {
            string safeMessage = message ?? string.Empty;

            try
            {
                switch (level)
                {
                    case "ERROR":
                        _logService.WriteError(safeMessage);
                        break;

                    case "WARN":
                    case "WARNING":
                        _logService.WriteWarning(safeMessage);
                        break;

                    default:
                        _logService.WriteInfo(safeMessage);
                        break;
                }
            }
            catch
            {
                // 文件日志失败不影响屏幕日志。
            }

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                Logs.Add(new OperationLogItem
                {
                    Time = DateTime.Now,
                    Level = level,
                    Message = safeMessage
                });

                while (Logs.Count > 1000)
                {
                    Logs.RemoveAt(0);
                }
            });
        }

        internal void RefreshOutputPaths()
        {
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = SelectedOutputDirectory,
                KeepArchiveNameFolder = Settings.KeepArchiveNameFolder,
                TestBeforeExtract = Settings.TestBeforeExtract,
                OverwriteMode = Settings.OverwriteMode,
                UseGlobalPassword = Settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = GlobalPassword,
                TryPasswordList = true,
                CancelOnFirstSuccess = true
            };

            options.Normalize();

            foreach (ArchiveTask task in Tasks)
            {
                try
                {
                    task.OutputPath = _pathService.BuildOutputPath(task, options);
                }
                catch
                {
                    task.OutputPath = string.Empty;
                }
            }
        }

        private void OpenSettings()
        {
            try
            {
                var window = new SettingsWindow(Settings, _settingsService)
                {
                    Owner = Application.Current.MainWindow
                };

                bool? result = window.ShowDialog();

                if (result == true)
                {
                    Settings = window.ResultSettings;
                    Settings.Normalize();

                    SelectedOutputDirectory = Settings.CustomOutputDirectory ?? string.Empty;

                    _settingsService.Save(Settings);

                    RefreshOutputPaths();
                    LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

                    UpdateSummary();

                    AppendLog("INFO", "设置已保存");
                }
                else
                {
                    AppendLog("INFO", "用户取消设置修改");
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "打开设置窗口失败：" + ex.Message);
                _dialogService.ShowError("打开设置窗口失败：" + ex.Message);
            }
        }


        private void ResetSettings()
        {
            bool confirm = _dialogService.ShowConfirm("确定要恢复默认设置吗？");

            if (!confirm)
            {
                return;
            }

            Settings = _settingsService.ResetToDefault();
            SelectedOutputDirectory = Settings.CustomOutputDirectory ?? string.Empty;
            RefreshOutputPaths();
            AppendLog("INFO", "已恢复默认设置");
        }

        private void OpenPasswordList()
        {
            var vm = new PasswordListViewModel(_passwordService, _dialogService);

            var window = new PasswordListWindow(vm)
            {
                Owner = Application.Current.MainWindow
            };

            window.ShowDialog();

            AppendLog("INFO", "密码列表管理窗口已关闭");
        }



        private void ExportLog()
        {
            string path = _dialogService.ShowSaveFileDialog(
                "导出日志",
                "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "ArchiveFixer.log");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                _logService.ExportLog(path);
                _dialogService.ShowInfo("日志已导出。");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("导出日志失败：" + ex.Message);
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
                _dialogService.ShowException(ex, "导出失败清单失败");
            }
        }

        private void CopyFailedList()
        {
            bool ok = _clipboardService.CopyFailedList(Tasks);
            _dialogService.ShowInfo(ok ? "失败列表已复制。" : "复制失败。");
        }

        private void OpenOutputDirectory()
        {
            string directory = SelectedOutputDirectory;

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Tasks.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.OutputPath))?.OutputPath ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                _dialogService.ShowWarning("没有可打开的输出目录。");
                return;
            }

            _pathService.OpenDirectory(directory);
        }

        private void OpenWorkDirectory()
        {
            try
            {
                SafePathHelper.EnsureDirectoryExists(_pathService.WorkDirectory);
                _pathService.OpenDirectory(_pathService.WorkDirectory);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "打开工作区目录失败：" + ex.Message);
            }
        }

        private void OpenLogDirectory()
        {
            try
            {
                _logService.OpenLogDirectory();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "打开日志目录失败：" + ex.Message);
                _dialogService.ShowError("打开日志目录失败：" + ex.Message);
            }
        }


        private void SelectOutputDirectory()
        {
            try
            {
                string folder = _dialogService.ShowFolderBrowserDialog();

                if (string.IsNullOrWhiteSpace(folder))
                {
                    AppendLog("INFO", "用户取消选择输出目录。");
                    return;
                }

                SelectedOutputDirectory = folder;

                if (Settings != null)
                {
                    Settings.CustomOutputDirectory = folder;
                    Settings.ExtractToOriginalDirectory = false;
                    OnPropertyChanged(nameof(Settings));

                    try
                    {
                        _settingsService.Save(Settings);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("WARN", "保存输出目录设置失败：" + ex.Message);
                    }
                }

                RefreshOutputPaths();

                AppendLog("INFO", "已选择输出目录：" + folder);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "选择输出目录失败：" + ex.Message);
                _dialogService.ShowError("选择输出目录失败：" + ex.Message);
            }
        }


        private void RemoveTask(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            Tasks.Remove(task);
            RebuildTaskIndex();
            LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

            UpdateSummary();
        }

        private void CopyTaskInfo(object? parameter)
        {
            if (parameter is ArchiveTask task)
            {
                _clipboardService.CopyTaskInfo(task);
            }
        }

        private void CopyTaskPath(object? parameter)
        {
            if (parameter is ArchiveTask task)
            {
                _clipboardService.CopyTaskPath(task);
            }
        }

        private void CopyTaskError(object? parameter)
        {
            if (parameter is ArchiveTask task)
            {
                _clipboardService.CopyErrorMessage(task);
            }
        }

        private void OpenTaskDirectory(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            string directory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(directory))
            {
                _pathService.OpenDirectory(directory);
            }
        }

        private void OpenTaskOutputDirectory(object? parameter)
        {
            if (parameter is ArchiveTask task && !string.IsNullOrWhiteSpace(task.OutputPath))
            {
                _pathService.OpenDirectory(task.OutputPath);
            }
        }

        internal void RebuildTaskIndex()
        {
            for (int i = 0; i < Tasks.Count; i++)
            {
                Tasks[i].Index = i + 1;
            }
        }

        private void RaiseAllCommandCanExecuteChanged()
        {
            foreach (ICommand command in new[]
                     {
                 AddFilesCommand,
                 AddFolderCommand,
                 ScanCommand,
                 ClearCommand,
                 RemoveSelectedCommand,

                 SmartRenameCommand,
                OneClickProcessCommand,
                 AddExtensionCommand,
                 ReplaceExtensionCommand,
                 DeleteLastExtensionCommand,
                 DeleteMultipleExtensionsCommand,

                 StartExtractCommand,
                 StopCommand,
                 CancelCurrentCommand,

                 OpenSettingsCommand,
                 OpenPasswordListCommand,
                 ExportLogCommand,
                 CopyFailedListCommand,
                 OpenOutputDirectoryCommand,
                 SelectOutputDirectoryCommand,
                 OpenLogDirectoryCommand,
                 ResetSettingsCommand,

                 RemoveTaskCommand,
                 RescanTaskCommand,
                 CopyTaskInfoCommand,
                 CopyTaskPathCommand,
                 CopyTaskErrorCommand,
                 OpenTaskDirectoryCommand,
                 OpenTaskOutputDirectoryCommand,
                 ToggleShowPasswordCommand
             })
            {
                if (command is RelayCommand relay)
                {
                    relay.RaiseCanExecuteChanged();
                }
                else if (command is AsyncRelayCommand asyncRelay)
                {
                    asyncRelay.RaiseCanExecuteChanged();
                }
            }
        }

    }
}
