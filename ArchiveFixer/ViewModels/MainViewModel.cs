using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
        private readonly ExtractService _extractService;
        private readonly PasswordService _passwordService;
        private readonly LogService _logService;
        private readonly SettingsService _settingsService;
        private readonly PathService _pathService;
        private readonly TaskSummaryService _taskSummaryService;
        private readonly ClipboardService _clipboardService;
        private readonly DialogService _dialogService;

        /// <summary>
        /// 批量操作取消源。
        /// 用于停止后续任务。
        /// 注意：解压时不要直接把它和当前 7z 任务强绑定，
        /// 否则“停止后续”会变成“强制取消当前”。
        /// </summary>
        private CancellationTokenSource? _operationCts;

        /// <summary>
        /// 当前正在执行的单个任务取消源。
        /// 用于取消当前 7z 进程。
        /// </summary>
        private CancellationTokenSource? _currentTaskCts;

        /// <summary>
        /// 是否正在执行批量解压。
        /// 用于避免按钮重复触发造成多个 7z 同时运行。
        /// </summary>
        private bool _isExtracting;

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
            set => SetProperty(ref _settings, value);
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
        public ICommand ExportLogCommand { get; }
        public ICommand CopyFailedListCommand { get; }
        public ICommand OpenOutputDirectoryCommand { get; }
        public ICommand SelectOutputDirectoryCommand { get; }
        public ICommand OpenLogDirectoryCommand { get; }

        public ICommand RemoveTaskCommand { get; }
        public ICommand RescanTaskCommand { get; }
        public ICommand CopyTaskInfoCommand { get; }
        public ICommand CopyTaskPathCommand { get; }
        public ICommand CopyTaskErrorCommand { get; }
        public ICommand OpenTaskDirectoryCommand { get; }
        public ICommand OpenTaskOutputDirectoryCommand { get; }
        public ICommand ToggleShowPasswordCommand { get; }
        public ICommand ResetSettingsCommand { get; }

        public MainViewModel()
            : this(
                  new FileScanService(),
                  new ArchiveDetectService(),
                  new RenameService(),
                  new ExtractService(),
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
            ExtractService extractService,
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
            _extractService = extractService;
            _passwordService = passwordService;
            _logService = logService;
            _settingsService = settingsService;
            _pathService = pathService;
            _taskSummaryService = taskSummaryService;
            _clipboardService = clipboardService;
            _dialogService = dialogService;

            _settings = _settingsService.Load();
            SelectedOutputDirectory = _settings.CustomOutputDirectory ?? string.Empty;

            AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, CanRunNormalCommand);
            AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, CanRunNormalCommand);
            ScanCommand = new AsyncRelayCommand(ScanTasksAsync, CanRunNormalCommand);
            ClearCommand = new RelayCommand(ClearTasks, CanRunNormalCommand);
            RemoveSelectedCommand = new RelayCommand(RemoveSelectedTasks, CanRunNormalCommand);

            SmartRenameCommand = new AsyncRelayCommand(SmartRenameAsync, CanRunNormalCommand);
            AddExtensionCommand = new AsyncRelayCommand(AddExtensionAsync, CanRunNormalCommand);
            ReplaceExtensionCommand = new AsyncRelayCommand(ReplaceExtensionAsync, CanRunNormalCommand);
            DeleteLastExtensionCommand = new AsyncRelayCommand(DeleteLastExtensionAsync, CanRunNormalCommand);
            DeleteMultipleExtensionsCommand = new AsyncRelayCommand(DeleteMultipleExtensionsAsync, CanRunNormalCommand);

            StartExtractCommand = new AsyncRelayCommand(StartExtractAsync, CanStartExtract);
            StopCommand = new RelayCommand(StopAfterCurrent, () => IsBusy);
            CancelCurrentCommand = new RelayCommand(CancelCurrentTask, () => IsBusy);

            OpenSettingsCommand = new RelayCommand(OpenSettings, CanRunNormalCommand);
            OpenPasswordListCommand = new RelayCommand(OpenPasswordList, CanRunNormalCommand);
            ExportLogCommand = new RelayCommand(ExportLog);
            CopyFailedListCommand = new RelayCommand(CopyFailedList);
            OpenOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory);
            SelectOutputDirectoryCommand = new RelayCommand(SelectOutputDirectory, CanRunNormalCommand);
            OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);


            RemoveTaskCommand = new RelayCommand(RemoveTask);
            RescanTaskCommand = new AsyncRelayCommand(RescanTaskAsync);
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

            if (!_extractService.CheckSevenZipExists())
            {
                AppendLog("WARN", "未找到 tools\\7zip\\7z.exe，软件可启动，但解压时会失败。");
            }

            if (!_extractService.CheckSevenZipDllExists())
            {
                AppendLog("WARN", "未找到 tools\\7zip\\7z.dll，请确认 7-Zip 命令行文件完整。");
            }

            UpdateSummary();
        }

        private bool CanRunNormalCommand()
        {
            return !IsBusy;
        }

        private bool CanStartExtract()
        {
            return !IsBusy && Tasks.Any(x => x.IsSelected);
        }

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
                        task.Status = "未知错误";
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
            UpdateSummary();
            AppendLog("INFO", $"已移除选中任务 {selected.Count} 个");
        }

        private async Task SmartRenameAsync()
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = Settings.DefaultExtension,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        private async Task AddExtensionAsync()
        {
            string defaultExt = string.IsNullOrWhiteSpace(Settings.DefaultExtension)
                ? ".zip"
                : Settings.DefaultExtension;

            string? input = ShowTextInputDialog(
                "批量添加后缀",
                "请输入要添加的后缀。\n例如：.zip、.rar、.7z、.jpg、.bin、.001",
                defaultExt);

            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("INFO", "用户取消添加后缀。");
                return;
            }

            string ext = NormalizeUserExtension(input);

            if (string.IsNullOrWhiteSpace(ext))
            {
                _dialogService.ShowWarning("后缀不能为空。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = "AddExtension",
                TargetExtension = ext,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        private async Task ReplaceExtensionAsync()
        {
            string defaultExt = string.IsNullOrWhiteSpace(Settings.DefaultExtension)
                ? ".zip"
                : Settings.DefaultExtension;

            string? input = ShowTextInputDialog(
                "批量替换最后一个后缀",
                "请输入新的后缀。\n例如：.zip、.rar、.7z、.jpg、.bin、.001\n\n示例：test.jpg -> test.zip",
                defaultExt);

            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("INFO", "用户取消替换后缀。");
                return;
            }

            string ext = NormalizeUserExtension(input);

            if (string.IsNullOrWhiteSpace(ext))
            {
                _dialogService.ShowWarning("后缀不能为空。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = "ReplaceLastExtension",
                TargetExtension = ext,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        private async Task DeleteLastExtensionAsync()
        {
            bool confirm = _dialogService.ShowConfirm(
                "确定要删除选中文件的最后一个后缀吗？\n\n例如：\n" +
                "test.rar.jpg -> test.rar\n" +
                "test.zip -> test");

            if (!confirm)
            {
                AppendLog("INFO", "用户取消删除最后一个后缀。");
                return;
            }

            var options = new RenameOptions
            {
                OperationType = "DeleteLastExtension",
                TargetExtension = string.Empty,
                DeleteExtensionCount = 1,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        private async Task DeleteMultipleExtensionsAsync()
        {
            string? input = ShowTextInputDialog(
                "删除多个后缀",
                "请输入要删除的后缀数量。\n\n例如输入 2：\n" +
                "test.rar.pdf.jpg -> test.rar\n" +
                "abc.7z.jpg -> abc",
                "2");

            if (string.IsNullOrWhiteSpace(input))
            {
                AppendLog("INFO", "用户取消删除多个后缀。");
                return;
            }

            if (!int.TryParse(input.Trim(), out int count) || count < 1)
            {
                _dialogService.ShowWarning("删除数量必须是大于 0 的整数。");
                return;
            }

            if (count > 20)
            {
                bool confirmLarge = _dialogService.ShowConfirm(
                    $"你输入的删除数量是 {count}，数值较大，确定继续吗？");

                if (!confirmLarge)
                {
                    return;
                }
            }

            var options = new RenameOptions
            {
                OperationType = "DeleteMultipleExtensions",
                TargetExtension = string.Empty,
                DeleteExtensionCount = count,
                ConflictAction = Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = Settings.UnknownFormatAction
            };

            await RenameByOptionsAsync(options);
        }

        private async Task RenameByOptionsAsync(RenameOptions options)
        {
            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                _dialogService.ShowWarning("请先选择需要改名的任务。");
                return;
            }

            if (options == null)
            {
                _dialogService.ShowWarning("改名参数为空。");
                return;
            }

            options.Normalize();

            AppendLog("INFO",
                $"生成改名预览：操作={options.OperationType}，目标后缀={options.TargetExtension}，删除数量={options.DeleteExtensionCount}");

            List<RenamePreviewItem> previewItems = _renameService.BuildPreview(selectedTasks, options);

            if (previewItems.Count == 0)
            {
                _dialogService.ShowInfo("没有生成任何改名预览项。");
                return;
            }

            var window = new RenamePreviewWindow(previewItems)
            {
                Owner = Application.Current.MainWindow
            };

            bool? dialogResult = window.ShowDialog();

            if (dialogResult != true)
            {
                AppendLog("INFO", "用户取消改名。");
                return;
            }

            RenamePreviewViewModel vm = window.ViewModel;

            List<RenamePreviewItem> selectedPreviewItems = vm.GetSelectedItems();

            if (selectedPreviewItems.Count == 0)
            {
                _dialogService.ShowInfo("当前没有可执行的改名项，请查看改名预览中的状态和错误信息。");
                return;
            }


            if (selectedPreviewItems.Count == 0)
            {
                _dialogService.ShowWarning("没有选择可执行的改名项。");
                return;
            }

            bool finalConfirm = _dialogService.ShowConfirm(
                $"确定要执行改名吗？\n\n将改名 {selectedPreviewItems.Count} 个文件。\n\n注意：这是实际文件重命名操作。");

            if (!finalConfirm)
            {
                AppendLog("INFO", "用户在最终确认时取消改名。");
                return;
            }

            IsBusy = true;

            try
            {
                AppendLog("INFO", $"开始执行改名，共 {selectedPreviewItems.Count} 个文件。");

                await _renameService.ExecuteRenameAsync(
                    selectedPreviewItems,
                    Tasks);

                foreach (RenamePreviewItem item in selectedPreviewItems)
                {
                    AppendLog("INFO",
                        $"{item.OriginalFileName} -> {item.NewFileName}，状态：{item.Status}，错误：{item.ErrorMessage}");
                }

                await ScanTasksAsync();

                RebuildTaskIndex();
                RefreshOutputPaths();
                UpdateSummary();

                AppendLog("INFO", "改名流程完成。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "改名失败：" + ex.Message);
                _dialogService.ShowError("改名失败：" + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string NormalizeUserExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            string value = extension.Trim();

            while (value.StartsWith("*.", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.StartsWith(".", StringComparison.Ordinal))
            {
                return value;
            }

            return "." + value;
        }

        private string? ShowTextInputDialog(string title, string message, string defaultValue)
        {
            var window = new Window
            {
                Title = title,
                Width = 460,
                Height = 230,
                MinWidth = 420,
                MinHeight = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow,
                Background = System.Windows.Media.Brushes.White
            };

            var root = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(16)
            };

            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
            });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });

            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            System.Windows.Controls.Grid.SetRow(textBlock, 0);
            root.Children.Add(textBlock);

            var textBox = new System.Windows.Controls.TextBox
            {
                Text = defaultValue ?? string.Empty,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            System.Windows.Controls.Grid.SetRow(textBox, 1);
            root.Children.Add(textBox);

            var hint = new System.Windows.Controls.TextBlock
            {
                Text = "提示：输入 zip 会自动变成 .zip；输入 .rar 会保持 .rar。",
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            System.Windows.Controls.Grid.SetRow(hint, 2);
            root.Children.Add(hint);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button
            {
                Content = "确定",
                Width = 86,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "取消",
                Width = 86,
                Height = 30,
                IsCancel = true
            };

            okButton.Click += (_, _) =>
            {
                window.DialogResult = true;
                window.Close();
            };

            cancelButton.Click += (_, _) =>
            {
                window.DialogResult = false;
                window.Close();
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            System.Windows.Controls.Grid.SetRow(buttonPanel, 3);
            root.Children.Add(buttonPanel);

            window.Content = root;

            window.Loaded += (_, _) =>
            {
                textBox.Focus();
                textBox.SelectAll();
            };

            bool? result = window.ShowDialog();

            if (result == true)
            {
                return textBox.Text;
            }

            return null;
        }



        public async Task StartExtractAsync()
        {
            if (_isExtracting)
            {
                AppendLog("WARN", "当前已经在批量解压中，忽略重复启动。");
                return;
            }

            var selectedTasks = Tasks.Where(x => x.IsSelected).ToList();

            if (selectedTasks.Count == 0)
            {
                _dialogService.ShowWarning("请先选择需要解压的任务。");
                return;
            }

            if (!_extractService.CheckSevenZipExists())
            {
                _dialogService.ShowError("未找到 tools\\7zip\\7z.exe，无法解压。");
                AppendLog("ERROR", "7z不存在");
                return;
            }

            IsBusy = true;
            IsStopping = false;
            _isExtracting = true;

            try
            {
                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _operationCts = new CancellationTokenSource();

                AppendLog("INFO", "开始批量解压");

                foreach (ArchiveTask task in selectedTasks)
                {
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        AppendLog("WARN", "已停止后续任务，不再启动新的解压任务。");
                        break;
                    }

                    try
                    {
                        _currentTaskCts?.Dispose();
                    }
                    catch
                    {
                    }

                    /*
                     * 关键修正：
                     * 当前任务的 CancellationTokenSource 不再和 _operationCts 直接 Link。
                     *
                     * 原因：
                     * StopAfterCurrent 的语义是“停止后续”，不是一定要强杀当前。
                     * 
                     * 如果用户想强杀当前，会点击 CancelCurrentTask。
                     *
                     * 这样可以保证：
                     * 1. StopAfterCurrent：不再启动后续任务。
                     * 2. CancelCurrentTask：只取消当前 7z。
                     * 3. 两个都点：当前取消后，不再进入下一个任务。
                     */
                    _currentTaskCts = new CancellationTokenSource();

                    try
                    {
                        await ExtractSingleTaskAsync(task, _currentTaskCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        task.Status = "已取消";
                        task.Operation = "等待";
                        task.ProgressText = "完成";
                        task.ErrorMessage = "任务已取消";
                        task.EndTime = DateTime.Now;
                        task.ElapsedText = task.StartTime.HasValue
                            ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                            : "-";
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("WARN", $"任务已取消：{task.FileName}");
                    }
                    catch (Exception ex)
                    {
                        task.Status = "未知错误";
                        task.ErrorMessage = ex.Message;
                        task.Operation = "等待";
                        task.ProgressText = "完成";
                        task.EndTime = DateTime.Now;
                        task.ElapsedText = task.StartTime.HasValue
                            ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                            : "-";
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("ERROR", $"任务失败：{task.FileName}，{ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            _currentTaskCts?.Dispose();
                        }
                        catch
                        {
                        }

                        _currentTaskCts = null;
                        UpdateSummary();
                    }

                    /*
                     * 关键修正：
                     * 当前任务结束后，马上再次判断停止标记。
                     * 防止用户在当前任务执行期间点击“停止后续任务”，
                     * 但循环仍然进入下一个任务。
                     */
                    if (IsStopping || _operationCts.IsCancellationRequested)
                    {
                        AppendLog("WARN", "已停止后续任务，批量解压提前结束。");
                        break;
                    }
                }

                AppendLog("INFO", "批量解压完成");
            }
            finally
            {
                try
                {
                    _currentTaskCts?.Dispose();
                }
                catch
                {
                }

                try
                {
                    _operationCts?.Dispose();
                }
                catch
                {
                }

                _currentTaskCts = null;
                _operationCts = null;

                _isExtracting = false;
                IsBusy = false;
                IsStopping = false;

                UpdateSummary();
            }
        }

        private async Task ExtractSingleTaskAsync(ArchiveTask task, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (task == null)
            {
                return;
            }

            if (!task.IsArchive || task.DetectedFormat == "Unknown")
            {
                task.Status = "已跳过";
                task.Operation = "跳过";
                task.ProgressText = "完成";
                task.ErrorMessage = "格式未知，默认跳过";
                AppendLog("WARN", $"跳过未知格式：{task.FileName}");
                return;
            }

            if (!File.Exists(task.CurrentPath))
            {
                task.Status = "解压失败";
                task.Operation = "等待";
                task.ProgressText = "完成";
                task.ErrorMessage = "文件不存在";
                AppendLog("ERROR", $"文件不存在：{task.CurrentPath}");
                return;
            }

            task.StartTime = DateTime.Now;
            task.EndTime = null;
            task.ElapsedText = "-";
            task.Operation = "准备";
            task.Status = "等待解压";
            task.ProgressText = "处理中";
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;

            var extractOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = SelectedOutputDirectory,
                KeepArchiveNameFolder = Settings.KeepArchiveNameFolder,
                TestBeforeExtract = Settings.TestBeforeExtract,
                OverwriteMode = Settings.OverwriteMode,
                UseGlobalPassword = Settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = GlobalPassword,
                TryPasswordList = true,
                TryEmptyPasswordFirst = Settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = Settings.UnknownFormatAction == "TryExtract",
                MaxParallelExtractCount = Settings.MaxParallelExtractCount
            };

            extractOptions.Normalize();

            string outputPath = _pathService.BuildOutputPath(task, extractOptions);

            try
            {
                if (!string.IsNullOrWhiteSpace(outputPath) &&
                    Directory.Exists(outputPath) &&
                    Directory.EnumerateFileSystemEntries(outputPath).Any())
                {
                    string newOutputPath = _pathService.AutoRenameDirectoryPath(outputPath);

                    AppendLog("WARN", $"输出目录已存在且非空，为避免混入旧文件，自动改用新目录：{newOutputPath}");

                    outputPath = newOutputPath;
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", $"检查输出目录失败，将继续使用原输出目录：{ex.Message}");
            }

            task.OutputPath = outputPath;

            List<PasswordItem> candidates = _passwordService.GetPasswordCandidates(
                task,
                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                _passwordService.Passwords,
                Settings.TryEmptyPasswordFirst);

            if (candidates.Count == 0)
            {
                candidates.Add(new PasswordItem
                {
                    Value = string.Empty,
                    Source = "Empty",
                    IsEnabled = true,
                    Remark = "空密码"
                });
            }

            SevenZipResult? lastResult = null;
            string selectedPassword = string.Empty;
            bool passwordConfirmed = false;

            /*
             * 关键修复：
             *
             * 旧逻辑：
             * candidates.Count > 1 就强制先执行 7z t。
             *
             * 问题：
             * 7z t 只是测试，不会产生解压文件。
             * 所以用户会看到 7-Zip Console 一直运行，但输出目录没有产物。
             *
             * 新逻辑：
             * 只有用户明确开启 TestBeforeExtract，或者任务明确标记 IsEncrypted，
             * 才先测试密码。
             */
            bool shouldTestPasswordBeforeExtract =
                Settings.TestBeforeExtract;

            if (shouldTestPasswordBeforeExtract)
            {
                task.Operation = "测试";
                task.Status = "测试中";
                task.ProgressText = "处理中";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：开始解压前测试。");

                for (int i = 0; i < candidates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    AppendLog("INFO", $"{task.FileName}：测试密码候选 {i + 1}/{candidates.Count}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    SevenZipResult testResult = await _extractService.TestArchiveAsync(
                        task.CurrentPath,
                        password,
                        cancellationToken);

                    lastResult = testResult;

                    if (testResult.DetectedErrorType == "Cancelled" ||
                        testResult.Status == "已取消" ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (testResult.Success)
                    {
                        selectedPassword = password;
                        passwordConfirmed = true;

                        task.Status = "测试通过";
                        task.PasswordStatus = string.IsNullOrEmpty(password) ? "不需要密码" : "密码正确";
                        task.ErrorMessage = string.Empty;
                        task.LastUpdatedTime = DateTime.Now;

                        AppendLog("INFO", $"{task.FileName}：测试通过。");
                        break;
                    }

                    if (testResult.DetectedErrorType == "WrongPassword")
                    {
                        task.PasswordStatus = "密码错误";
                        AppendLog("WARN", $"{task.FileName}：密码错误，继续尝试下一个候选密码。");
                        continue;
                    }

                    task.Status = testResult.Status;
                    task.ErrorMessage = testResult.Message;
                    task.Operation = "等待";
                    task.ProgressText = "完成";
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"{task.FileName}：测试失败，原因：{testResult.Message}");
                    break;
                }

                if (!passwordConfirmed)
                {
                    if (lastResult != null && lastResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = "密码错误";
                        task.PasswordStatus = "密码错误";
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = "测试失败";
                        task.ErrorMessage = "没有可用密码或测试失败";
                    }

                    task.EndTime = DateTime.Now;
                    task.ElapsedText = task.StartTime.HasValue
                        ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                        : "-";

                    task.Operation = "等待";
                    task.ProgressText = "完成";
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("ERROR", $"解压前测试失败：{task.FileName}，原因：{task.ErrorMessage}");

                    if (task.Status == "密码错误")
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _dialogService.ShowWarning($"文件密码错误或缺少正确密码：\n{task.FileName}");
                        });
                    }

                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                task.Operation = "解压";
                task.Status = "解压中";
                task.ProgressText = "处理中";
                task.LastUpdatedTime = DateTime.Now;

                AppendLog("INFO", $"{task.FileName}：测试通过，开始正式解压。");

                SevenZipResult extractResult = await _extractService.ExtractArchiveAsync(
                    task.CurrentPath,
                    outputPath,
                    selectedPassword,
                    extractOptions,
                    cancellationToken);

                lastResult = extractResult;

                if (extractResult.DetectedErrorType == "Cancelled" ||
                    extractResult.Status == "已取消" ||
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (extractResult.Success)
                {
                    task.Status = "解压成功";
                    task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? "不需要密码" : "密码正确";
                    task.ErrorMessage = string.Empty;

                    AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                }
                else
                {
                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        task.Status = "密码错误";
                        task.PasswordStatus = "密码错误";
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");
                }
            }
            else
            {
                /*
                 * 普通模式：
                 * 不先测试，直接解压。
                 *
                 * 好处：
                 * 1. 普通无密码压缩包会立刻产生解压产物。
                 * 2. 不会让用户误以为卡住。
                 * 3. 如果遇到密码错误，再尝试下一个密码。
                 */
                bool extractSuccess = false;
                bool hasWrongPassword = false;

                for (int i = 0; i < candidates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    PasswordItem candidate = candidates[i];
                    string password = candidate.Value ?? string.Empty;

                    selectedPassword = password;

                    task.Operation = "解压";
                    task.Status = "解压中";
                    task.ProgressText = "处理中";
                    task.LastUpdatedTime = DateTime.Now;

                    AppendLog("INFO", $"{task.FileName}：开始解压，密码候选 {i + 1}/{candidates.Count}，{_passwordService.BuildTryPasswordLogText(candidate, i + 1)}");

                    SevenZipResult extractResult = await _extractService.ExtractArchiveAsync(
                        task.CurrentPath,
                        outputPath,
                        selectedPassword,
                        extractOptions,
                        cancellationToken);

                    lastResult = extractResult;

                    if (extractResult.DetectedErrorType == "Cancelled" ||
                        extractResult.Status == "已取消" ||
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (extractResult.Success)
                    {
                        extractSuccess = true;

                        task.Status = "解压成功";
                        task.PasswordStatus = string.IsNullOrEmpty(selectedPassword) ? "不需要密码" : "密码正确";
                        task.ErrorMessage = string.Empty;

                        AppendLog("INFO", $"解压成功：{task.FileName} -> {task.OutputPath}");
                        break;
                    }

                    if (extractResult.DetectedErrorType == "WrongPassword")
                    {
                        hasWrongPassword = true;
                        task.PasswordStatus = "密码错误";

                        AppendLog("WARN", $"{task.FileName}：密码错误，继续尝试下一个候选密码。");
                        continue;
                    }

                    task.Status = extractResult.Status;
                    task.ErrorMessage = extractResult.Message;

                    AppendLog("ERROR", $"解压失败：{task.FileName}，原因：{task.ErrorMessage}");

                    break;
                }

                if (!extractSuccess)
                {
                    if (hasWrongPassword)
                    {
                        task.Status = "密码错误";
                        task.PasswordStatus = "密码错误";
                        task.ErrorMessage = "密码错误或缺少正确密码";
                    }
                    else if (lastResult != null)
                    {
                        task.Status = lastResult.Status;
                        task.ErrorMessage = lastResult.Message;
                    }
                    else
                    {
                        task.Status = "解压失败";
                        task.ErrorMessage = "未知解压失败";
                    }

                    if (task.Status == "密码错误")
                    {
                        AppendLog("ERROR", $"解压失败：{task.FileName}，原因：密码错误或缺少正确密码");

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _dialogService.ShowWarning($"文件密码错误或缺少正确密码：\n{task.FileName}");
                        });
                    }
                }
            }

            task.EndTime = DateTime.Now;
            task.ElapsedText = task.StartTime.HasValue
                ? (task.EndTime.Value - task.StartTime.Value).ToString(@"hh\:mm\:ss")
                : "-";

            task.Operation = "等待";
            task.ProgressText = "完成";
            task.LastUpdatedTime = DateTime.Now;
        }

        public void StopAfterCurrent()
        {
            try
            {
                IsStopping = true;

                /*
                 * 关键修正：
                 * 这里取消的是批量操作 token，用来阻止启动后续任务。
                 *
                 * 注意：
                 * 当前 7z 任务使用的是 _currentTaskCts，
                 * 所以这里只停止后续，不强制杀当前。
                 *
                 * 如果用户还想立即取消当前任务，需要再点“取消当前任务”。
                 */
                _operationCts?.Cancel();

                AppendLog("WARN", "已请求停止后续任务，不再启动新的解压任务。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "停止后续任务失败：" + ex.Message);
            }
        }


        public void CancelCurrentTask()
        {
            try
            {
                if (_currentTaskCts == null)
                {
                    AppendLog("WARN", "当前没有正在执行的任务可取消。");
                    return;
                }

                _currentTaskCts.Cancel();

                AppendLog("WARN", "已请求取消当前任务，正在结束 7-Zip 进程。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "取消当前任务失败：" + ex.Message);
            }
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

        public void RefreshOutputPaths()
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
            UpdateSummary();
        }

        private async Task RescanTaskAsync(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            await _archiveDetectService.ApplyDetectResultAsync(task);
            RefreshOutputPaths();
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

        private void RebuildTaskIndex()
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
