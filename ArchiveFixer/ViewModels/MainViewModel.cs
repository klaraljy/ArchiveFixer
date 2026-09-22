using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
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

        /// <summary>
        /// 删除其余物 / 清理空文件夹的本体（服务层早就实现且测过，本轮把它接到界面上）。
        ///
        /// ⚠ 它里面的每一步都是磁盘活（扫目录、量大小、执行删除），**只允许在后台线程上调用**
        /// （见 <see cref="RunCleanupAsync"/> 里的 Task.Run）。
        /// </summary>
        private readonly MaintenanceCleanupService _cleanupService;

        private readonly ScanCoordinator _scanCoordinator;
        private readonly RenameCoordinator _renameCoordinator;
        private readonly ExtractionCoordinator _extractionCoordinator;
        private readonly OneClickCoordinator _oneClickCoordinator;

        private AppSettings _settings;
        private string _globalPassword = string.Empty;
        private string _passwordBookSummary = string.Empty;
        private string _passwordBookTooltip = string.Empty;
        private bool _showPassword;
        private bool _isBusy;
        private int _busyNesting;
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

            /*
             * 「启用日志文件」真正生效的地方：关掉之后只留屏幕日志，不再往 data\logs 追加。
             * 之前这个开关在设置窗口里躺着，勾不勾都不影响写盘 —— 属于"改了没用"的开关。
             * LogService 内部对写失败也会自行降级（置 false），这里只管用户的选择。
             */
            _logService.EnableFileLog = _settings?.EnableLog ?? true;

            // 密码本侧车文件跟着同一个根走，否则"导入记住了"和"启动读取"会看两个目录。
            _passwordService.DataRootDirectory = _pathService.DataRootDirectory;
            ToolLocator.Default.Invalidate();
        }

        public string GlobalPassword
        {
            get => _globalPassword;
            set => SetProperty(ref _globalPassword, value);
        }

        /// <summary>
        /// 主界面上"当前密码本"的常驻显示。
        ///
        /// 为什么必须有它：用户导入过密码本，但界面上没有任何地方能看出"到底加载了几个密码、来自哪个文件"，
        /// 于是"导入 → 关掉重开 → 什么都没有"这种感受无从判断真假。
        /// 这里常驻一行摘要，导入/自动加载/窗口关闭后都会刷新，看一眼就知道生效没有。
        /// 日志里只写文件名（§8 隐私红线：个人路径不入日志），界面上显示全路径方便核对。
        /// </summary>
        public string PasswordBookSummary
        {
            get => _passwordBookSummary;
            set => SetProperty(ref _passwordBookSummary, value ?? string.Empty);
        }

        public string PasswordBookTooltip
        {
            get => _passwordBookTooltip;
            set => SetProperty(ref _passwordBookTooltip, value ?? string.Empty);
        }

        /// <summary>把"当前密码本"摘要刷新成 PasswordService 的真实状态（不猜、不缓存）。</summary>
        public void RefreshPasswordBookSummary()
        {
            int count = _passwordService.Passwords.Count;
            int enabled = _passwordService.Passwords.Count(p => p.IsEnabled);
            string book = _passwordService.LastImportedBookPath;

            if (count == 0)
            {
                PasswordBookSummary = "密码本：未加载（0 条）——点「密码列表管理」或菜单「工具 → 导入密码本...」";
                PasswordBookTooltip = "空密码仍会按设置尝试；密码本里的密码只存在内存里，不落盘。";
                return;
            }

            string name = string.IsNullOrWhiteSpace(book) ? "（手动添加）" : System.IO.Path.GetFileName(book);

            PasswordBookSummary = $"密码本：{name} — {count} 条（启用 {enabled} 条）";
            PasswordBookTooltip = string.IsNullOrWhiteSpace(book)
                ? "密码只存在内存里；本文件未记录来源路径。"
                : book;
        }

        public bool ShowPassword
        {
            get => _showPassword;
            set => SetProperty(ref _showPassword, value);
        }

        /// <summary>
        /// 是否有操作正在进行（驱动命令可用性：忙的时候「停止后续 / 取消当前」可点、其它入口变灰）。
        ///
        /// ⚠ 这是**嵌套计数**的对外视图，不是普通开关：写入 `true` 进入一层、写入 `false` 退出一层，
        /// 只有最外层退出时才真的变成 false（见 <see cref="EnterBusy"/> / <see cref="ExitBusy"/>）。
        ///
        /// 为什么必须这样：三个协调器各自在 finally 里复位标志，而"一键处理"会在中途调用
        /// 改名协调器与解压协调器 —— 内层的 finally 会把外层还没结束的忙碌状态清掉，
        /// 于是「停止后续 / 取消当前」按钮在一键处理跑一半时变灰（它们只看 IsBusy）、
        /// 守卫失效，用户还能在间隙里再点一次解压，同一个包被解两遍。
        ///
        /// 保留 set 写法并让它参与计数，是为了让负责内层的协调器（本轮不在授权文件清单里）
        /// 无需改动就自动获得嵌套语义：它们的 true/finally false 天然成对。
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (value)
                {
                    EnterBusy();
                }
                else
                {
                    ExitBusy();
                }
            }
        }

        /// <summary>
        /// 进入"忙"的一层。只处理嵌套深度，不改变别的状态。
        /// </summary>
        public void EnterBusy()
        {
            _busyNesting++;

            if (_busyNesting == 1)
            {
                SetBusyFlag(true);
            }
        }

        /// <summary>
        /// 退出"忙"的一层。只有深度回到 0 才真的把标志复位。
        ///
        /// 多退一次（没有对应的 EnterBusy）会被夹住在 0：宁可当成"不忙"，
        /// 也绝不能让计数变成负数 —— 那样之后所有 Enter/Exit 都配不平，界面会**永久灰掉**。
        /// </summary>
        public void ExitBusy()
        {
            if (_busyNesting <= 0)
            {
                _busyNesting = 0;
                return;
            }

            _busyNesting--;

            if (_busyNesting == 0)
            {
                SetBusyFlag(false);
            }
        }

        private void SetBusyFlag(bool value)
        {
            if (SetProperty(ref _isBusy, value, nameof(IsBusy)))
            {
                RaiseAllCommandCanExecuteChanged();
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

        /// <summary>
        /// 删除当前勾选任务自己那一份「其余物」（默认移入回收站，激进档彻底删除）。
        ///
        /// ⚠ 作用域只到本任务那一份：包有自己的目录时是 <c>&lt;输出目录&gt;\其余物\</c>，
        /// 共享输出目录时是 <c>&lt;共享根&gt;\其余物\&lt;本任务包基名&gt;\</c> —— 绝不碰别的包的目录。
        /// </summary>
        public ICommand CleanProcessArtifactsCommand { get; }

        /// <summary>
        /// 删除当前输出目录下的**全部**其余物（会影响同目录里的所有包，确认框里写明个数）。
        /// </summary>
        public ICommand CleanAllProcessArtifactsCommand { get; }

        /// <summary>清理当前任务输出根下"任意层级都没有文件"的空文件夹。</summary>
        public ICommand CleanEmptyFoldersCommand { get; }

        public ICommand RemoveTaskCommand { get; }
        public ICommand RescanTaskCommand { get; }
        public ICommand CopyTaskInfoCommand { get; }
        public ICommand CopyTaskPathCommand { get; }
        public ICommand CopyTaskErrorCommand { get; }
        public ICommand OpenTaskDirectoryCommand { get; }
        public ICommand OpenTaskOutputDirectoryCommand { get; }

        /// <summary>打开当前任务的其余物目录（旧名"过程物"）；没有就提示一句。</summary>
        public ICommand OpenTaskArtifactDirectoryCommand { get; }
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
            DialogService dialogService,
            MaintenanceCleanupService? cleanupService = null)
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

            // 清理服务可注入（测试里换掉执行器，绝不碰真实回收站）；不传就用真实实现。
            _cleanupService = cleanupService ?? new MaintenanceCleanupService();

            _scanCoordinator = new ScanCoordinator(this, fileScanService, archiveDetectService, dialogService);
            _renameCoordinator = new RenameCoordinator(this, _scanCoordinator, renameService, dialogService);
            _extractionCoordinator = new ExtractionCoordinator(this, archiveEngine, passwordService, pathService, dialogService);
            _oneClickCoordinator = new OneClickCoordinator(this, _scanCoordinator, _renameCoordinator, _extractionCoordinator, dialogService);

            _settings = _settingsService.Load();
            ApplyEngineSettings();

            /*
             * 「记住上次输出目录」真正生效的地方（不是留着好看的开关）：
             * 关掉之后，启动时**不**把上次的输出目录填回 SelectedOutputDirectory，
             * 这一次运行按"输出位置"那一档的规则算落点（默认 = 压缩包同目录）。
             * 之前这个开关只存在于界面上，改了什么都不会发生。
             */
            SelectedOutputDirectory = _settings.RememberLastOutputDirectory
                ? _settings.CustomOutputDirectory ?? string.Empty
                : string.Empty;

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

            // 其余物删除入口（菜单「工具」）：预览 → 确认 → 后台执行，全程不阻塞界面。
            // 第一个只作用于**当前勾选任务自己那一份**；第二个是显式的"整个目录"入口（默认不选）。
            CleanProcessArtifactsCommand = new AsyncRelayCommand(
                () => RunCleanupAsync(CleanupScope.Artifacts, ArtifactDeleteScope.SelectedTask),
                CanRunNormalCommand);

            CleanAllProcessArtifactsCommand = new AsyncRelayCommand(
                () => RunCleanupAsync(CleanupScope.Artifacts, ArtifactDeleteScope.EverythingInDirectory),
                CanRunNormalCommand);

            CleanEmptyFoldersCommand = new AsyncRelayCommand(
                () => RunCleanupAsync(CleanupScope.EmptyFolders, ArtifactDeleteScope.SelectedTask),
                CanRunNormalCommand);


            RemoveTaskCommand = new RelayCommand(RemoveTask);
            RescanTaskCommand = new AsyncRelayCommand(_scanCoordinator.RescanTaskAsync);
            CopyTaskInfoCommand = new RelayCommand(CopyTaskInfo);
            CopyTaskPathCommand = new RelayCommand(CopyTaskPath);
            CopyTaskErrorCommand = new RelayCommand(CopyTaskError);
            OpenTaskDirectoryCommand = new RelayCommand(OpenTaskDirectory);
            OpenTaskOutputDirectoryCommand = new RelayCommand(OpenTaskOutputDirectory);
            OpenTaskArtifactDirectoryCommand = new RelayCommand(OpenTaskArtifactDirectory);
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
                // 文件日志开关跟着设置走（默认开）。用户关掉它时不该再产生新日志文件。
                _logService.Initialize(Settings.EnableLog);
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

                // 设置里没有就退回 sidecar：用户可能是从「密码列表」窗口导入的，
                // 那条路以前不写设置。两处都看，才不会"导了等于没导"。
                if (string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        string sidecar = Path.Combine(_pathService.DataRootDirectory, "password-book.path");

                        if (File.Exists(sidecar))
                        {
                            path = File.ReadAllText(sidecar).Trim();
                        }
                    }
                    catch
                    {
                        // 读不到当作没配过。
                    }
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    // 从没配过：摘要要如实显示"未加载"，不能让用户以为已经加载过了。
                    RefreshPasswordBookSummary();
                    return;
                }

                if (!File.Exists(path))
                {
                    // §8 隐私红线：日志只写文件名，个人路径不入日志。
                    AppendLog("WARN", $"上次的密码本找不到了，已跳过自动加载：{System.IO.Path.GetFileName(path)}");
                    RefreshPasswordBookSummary();
                    return;
                }

                int count = _passwordService.ImportPasswordList(path).Count;
                AppendLog("INFO", $"已自动加载密码本：{System.IO.Path.GetFileName(path)}（{count} 条）");
                RefreshPasswordBookSummary();
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
                    // 取消也要留痕：用户点了导入又取消，日志里什么都不写的话，
                    // 事后完全分不清"没导"和"导了没生效"。
                    AppendLog("INFO", "已取消导入密码本（没有选择文件）。");
                    return;
                }

                int count = _passwordService.ImportPasswordList(path).Count;

                Settings.PasswordBookPath = path;
                _settingsService.Save(Settings);

                RefreshPasswordBookSummary();

                string warnings = _passwordService.LastImportWarnings.Count > 0
                    ? $"{Environment.NewLine}{Environment.NewLine}提示：{string.Join(Environment.NewLine, _passwordService.LastImportWarnings)}"
                    : string.Empty;

                AppendLog("INFO", $"已导入密码本：{System.IO.Path.GetFileName(path)}（{count} 条），下次启动会自动加载。");
                _dialogService.ShowInfo($"已导入 {count} 条密码，并记住了这个文件，下次启动会自动加载。{warnings}");
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

            /*
             * 屏幕日志走 BeginInvoke（排队），不是 Invoke（同步等待）。
             *
             * Invoke 在界面线程上会**就地执行**（不需要泵消息），看起来更"实时"；代价是：
             * 从后台线程调用时它会阻塞那条线程直到界面线程空出来 —— 一键处理跑一批包时
             * 每条日志都要和界面上的其它活抢一次，日志一多就变成"界面卡住"的一部分。
             * BeginInvoke 只入队立刻返回，界面按自己的节奏消费；顺序仍是 FIFO，
             * 界面日志的先后不会乱（文件日志本来就先在 _logService 里落好了）。
             */
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
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
            }));
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
                /*
                 * 已经解压完的任务**不再重算**。
                 *
                 * 它们的 OutputPath 是解压管线回写的**实际落点**（输出目录已存在时可能是 xxx(1)），
                 * 按"打算输出到哪"重算会把它写回原定目录：界面"输出目录"列与「打开输出目录」
                 * 都会指向一个空目录，报告里的落点也不再是产物真正所在的地方。
                 * 实测路径：第 2 轮的一键处理会重扫新加进来的内层包，而重扫会对**整个列表**
                 * 调一次这里，把第 1 轮任务刚写回的实际落点冲掉（用户再也看不到 xxx(1)）。
                 * 只有还没产出结果的任务才该跟着设置走。
                 */
                if (HasExtractionResult(task))
                {
                    continue;
                }

                try
                {
                    /*
                     * 这里**不扫目录**（不传 collapseRepeatedFolderLayer / containsOnly）：
                     * 本方法跑在 UI 线程上，而"目录里是不是只有这一个包"要枚举目录（大目录会卡界面）。
                     * 场景 B 的塌缩由解压管线在后台线程上判定，跑完会把真实落点回写进 task.OutputPath，
                     * 所以界面上最终显示的就是实际落点。
                     */
                    task.OutputPath = _pathService.BuildOutputPath(task, options);
                }
                catch
                {
                    task.OutputPath = string.Empty;
                }
            }
        }

        /// <summary>
        /// 这个任务是否已经有解压产物（落点已由解压管线写回，不能再按设置重算）。
        /// 「部分完成」也算：产物确实落在那个目录里，只是不完整。
        /// </summary>
        private static bool HasExtractionResult(ArchiveTask task)
        {
            return task.Status is
                StatusText.ExtractSuccess or
                StatusText.Overwritten or
                StatusText.PartiallyCompleted;
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

            /*
             * 窗口**还开着**的时候，主界面摘要就要跟着变。
             *
             * 以前只在 ShowDialog() 返回后刷新一次：用户在密码列表窗口里导入完密码本，
             * 后面的主界面还是旧条数，必须先关窗才看得见 —— 看起来就像"导入没生效"，
             * 而这恰恰是用户反复抱怨的那个现象。
             *
             * 这里订阅窗口 ViewModel 的密码集合：导入（ReloadFromService）、添加、删除、清空
             * 都会触发它，口径与关窗后那次刷新完全一致。关窗时退订，别让窗口对象挂住主 ViewModel。
             */
            Action unhook = HookPasswordBookSummaryRefresh(vm, RefreshPasswordBookSummary);

            try
            {
                var window = new PasswordListWindow(vm)
                {
                    Owner = Application.Current.MainWindow
                };

                window.ShowDialog();
            }
            finally
            {
                unhook();
            }

            /*
             * 关窗口时把结果落到日志和主界面摘要上。
             * 以前这里只写"窗口已关闭"一句：用户在窗口里导入了密码本、点关闭之后，
             * 日志和界面都没留下任何痕迹，看起来就像"白导了"。
             */
            RefreshPasswordBookSummary();

            string bookName = _passwordService.LastImportedBookPath.Length == 0
                ? "无"
                : System.IO.Path.GetFileName(_passwordService.LastImportedBookPath);

            AppendLog("INFO", $"密码列表管理窗口已关闭（当前 {_passwordService.Passwords.Count} 条密码，密码本：{bookName}）。");
        }

        /// <summary>
        /// 把"密码列表窗口的密码集合发生变化"接到主界面摘要刷新上，返回退订用的回调。
        ///
        /// 单独一个方法是为了能测：窗口本身要 ShowDialog（测试里没人点），但这条接线完全不需要窗口
        /// —— 测试可以拿一个 PasswordListViewModel 直接验证"集合一变、摘要就变"。
        /// </summary>
        internal static Action HookPasswordBookSummaryRefresh(PasswordListViewModel listViewModel, Action refresh)
        {
            if (listViewModel == null || refresh == null)
            {
                return () => { };
            }

            void OnPasswordsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            {
                refresh();
            }

            listViewModel.Passwords.CollectionChanged += OnPasswordsChanged;

            return () => listViewModel.Passwords.CollectionChanged -= OnPasswordsChanged;
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
            bool ok = CopySanitizedToClipboard(_taskSummaryService.BuildFailedListText(Tasks));
            _dialogService.ShowInfo(ok ? "失败列表已复制。" : "复制失败。");
        }

        /// <summary>
        /// 剪贴板**唯一出口**：任何文本上剪贴板之前都必须先过脱敏
        /// （AGENTS.md §6 第 5 条：密码只存内存，日志、报告、剪贴板、详情窗口一律脱敏）。
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

        /// <summary>
        /// 其余物/空文件夹的删除流程（菜单「工具 → 删除其余物… / 删除本目录全部其余物… / 清理空文件夹…」）：
        /// **预览 → 红色确认（默认回收站，勾选框切激进档）→（激进档）二次确认 → 后台执行 → 写日志**。
        ///
        /// 线程纪律（本项目历史上因 UI 线程干重活卡死过）：
        /// 预览统计与删除执行全部在 <see cref="Task.Run(System.Action)"/> 里；
        /// 界面线程只做两件事 —— 弹确认框、把结论写进日志/提示。
        ///
        /// 取消的语义：任何一步没确认，就**什么都不做**（一个字节都不动），只留一条日志。
        /// </summary>
        /// <param name="scope">作用域类型（其余物 / 空文件夹）。</param>
        /// <param name="deleteScope">
        /// 作用域大小：只删当前勾选任务那一份（默认），还是删整个共享目录下的全部。
        /// 后者只由「删除本目录全部其余物…」这个显式入口传进来。
        /// </param>
        private async Task RunCleanupAsync(CleanupScope scope, ArtifactDeleteScope deleteScope)
        {
            bool everything = deleteScope == ArtifactDeleteScope.EverythingInDirectory;

            string title = scope == CleanupScope.EmptyFolders
                ? "清理空文件夹"
                : everything ? "删除本目录全部其余物" : "删除其余物";

            ArchiveTask? task = SelectedTask;

            if (task == null)
            {
                AppendLog("WARN", $"{title}：没有选中任务，已取消（删除只作用于当前选中的那一个任务）。");
                _dialogService.ShowWarning($"请先在列表里选中一个任务，再执行「{title}」。");
                return;
            }

            if (string.IsNullOrWhiteSpace(task.OutputPath))
            {
                AppendLog("WARN", $"{title}：任务「{task.FileName}」还没有输出目录（先解压一次），已取消。");
                _dialogService.ShowWarning($"任务「{task.FileName}」还没有输出目录，无法删除。");
                return;
            }

            EnterBusy();

            try
            {
                // ① 预览（后台）：列出将删除的条目数与总大小；没有可删的就到此为止。
                CleanupPreview preview = await Task.Run(() => scope == CleanupScope.EmptyFolders
                    ? _cleanupService.PreviewEmptyFolders(task.OutputPath)
                    : _cleanupService.PreviewProcessArtifacts(task, deleteScope));

                AppendLog(preview.HasTarget ? "INFO" : "WARN", $"{title}（{task.FileName}）：{preview.Message}");

                if (!preview.HasTarget)
                {
                    _dialogService.ShowInfo(preview.Message);
                    return;
                }

                /*
                 * ② 确认（红色）。默认档就是**移入回收站**，勾选框才是切换到激进档 ——
                 * 默认动作与"最不意外"一致，危险的那一档必须用户主动勾选（规格 §3.2 清理表）。
                 *
                 * ⚠ 必须用 ShowDestructiveConfirmWithOption（**不勾也能确认**）：
                 * 勾选框在这里是"档位选择"而不是"必须承认才能继续"，用错方法会让默认的回收站档走不下去。
                 */
                string scopeNote = BuildSharedScopeNote(scope, task, preview);

                bool confirmed = _dialogService.ShowDestructiveConfirmWithOption(
                    BuildCleanupConfirmText(title, preview, DeleteMode.RecycleBin, scopeNote),
                    "移入回收站",
                    "改为彻底删除，不进回收站",
                    optionCheckedByDefault: false,
                    out bool permanentRequested);

                if (!confirmed)
                {
                    AppendLog("INFO", $"{title}：用户取消，没有删除任何东西。");
                    return;
                }

                DeleteMode mode = DeleteMode.RecycleBin;

                if (permanentRequested)
                {
                    /*
                     * ③ 激进档的**二次确认**：红色 + 必须勾选"我知道不可恢复"。
                     * 只弹一次红色框是不够的 —— 规格 §3.2 要求"红色标识"与"二次确认"两件事同时满足。
                     */
                    bool acknowledged = _dialogService.ShowDestructiveConfirm(
                        BuildCleanupConfirmText(title, preview, DeleteMode.Permanent, scopeNote),
                        "彻底删除",
                        "我知道彻底删除不可恢复，这些内容不会进回收站",
                        out bool irreversibleAcknowledged);

                    if (!acknowledged || !irreversibleAcknowledged)
                    {
                        AppendLog("INFO", $"{title}：没有通过彻底删除的二次确认，没有删除任何东西。");
                        return;
                    }

                    mode = DeleteMode.Permanent;
                }

                // ④ 执行（后台）：删除本身绝不在 UI 线程上跑；作用域用预览算出来的那一份，不重算。
                CleanupOutcome outcome = await Task.Run(() => scope == CleanupScope.EmptyFolders
                    ? _cleanupService.CleanEmptyFolders(task.OutputPath, mode)
                    : _cleanupService.CleanProcessArtifacts(task, mode, preview, deleteScope));

                WriteCleanupOutcomeLog(title, task, preview, outcome);

                string summary = BuildCleanupSummary(title, outcome);

                /*
                 * 有失败就绝不能显示成"全部成功"（不变量 6 的同一口径）：
                 * 只要有任何一个条目没删掉，就用警告框把原因列出来。
                 */
                if (outcome.FailureCount > 0 || outcome.SucceededNothing)
                {
                    _dialogService.ShowWarning(summary);
                }
                else
                {
                    _dialogService.ShowInfo(summary);
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", $"{title}失败：" + ex.Message);
                _dialogService.ShowException(ex, $"{title}失败");
            }
            finally
            {
                ExitBusy();
            }
        }

        /// <summary>
        /// 确认框正文：写清"删什么、多少、能不能撤销"，并把作用域路径原文列出来
        /// （作用域越大越要让用户看见，例如共享输出目录下 其余物 会被多个包共用；
        /// 含源包时还要说清"删掉后需要重新下载"）。
        /// </summary>
        internal static string BuildCleanupConfirmText(
            string title,
            CleanupPreview preview,
            DeleteMode mode,
            string? scopeNote = null)
        {
            var builder = new StringBuilder();

            builder.AppendLine(title);
            builder.AppendLine();

            builder.AppendLine($"作用范围：{preview.ScopePath}");
            builder.AppendLine($"顶层 {preview.ItemCount} 项，共 {preview.EntryCount} 个条目 / {preview.TotalBytes} 字节"
                               + (preview.Determined ? string.Empty : "（部分内容读不到，数字可能不全）"));

            /*
             * 源包提示必须**显著**：其余物里现在也有源包本身（用户 2026-09-22 的新布局），
             * 删掉它意味着要重新下载 —— 这是用户最容易忽略的一个后果。
             */
            if (preview.SourcePackageCount > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"⚠ 含 {preview.SourcePackageCount} 个源包文件（压缩包本身），删掉后需要重新下载：");

                foreach (string name in preview.SourcePackageNames)
                {
                    builder.AppendLine("· " + name);
                }

                if (preview.SourcePackageCount > preview.SourcePackageNames.Count)
                {
                    builder.AppendLine($"…等共 {preview.SourcePackageCount} 个源包");
                }
            }

            if (!string.IsNullOrWhiteSpace(scopeNote))
            {
                builder.AppendLine();
                builder.AppendLine("⚠ " + scopeNote);
            }

            if (preview.Items.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("将删除（最多列 " + CleanupPreview.MaxListedItems + " 项）：");

                foreach (string item in preview.Items)
                {
                    builder.AppendLine("· " + item);
                }

                if (preview.ItemCount > preview.Items.Count)
                {
                    builder.AppendLine($"…等共 {preview.ItemCount} 项");
                }
            }

            builder.AppendLine();

            builder.AppendLine(mode == DeleteMode.Permanent
                ? "⚠ 彻底删除：内容不会进回收站，**无法恢复**。"
                : "默认档：移入回收站，之后可以从回收站还原。回收站不可用时程序不会改删，会直接报错并放弃。");

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 作用域提醒：这次删除覆盖的范围是"只有本任务那一份"还是"整个共享目录"。
        ///
        /// <para>
        /// 其余物（<see cref="CleanupScope.Artifacts"/>）：共享输出目录（模式 B / 指定位置直接放）下
        /// **只删本任务包基名那一份**，所以这里说清"不会碰到别的包的其余物"；
        /// 走「删除本目录全部其余物…」这个显式入口时反过来强调"会影响同目录的所有包"。
        /// </para>
        /// <para>
        /// 空文件夹（<see cref="CleanupScope.EmptyFolders"/>）：按规格 §5 作用域就是"扫这个根"，
        /// 共享模式下会覆盖到同目录里其它包留下的空壳 —— 这必须让用户看见
        /// （它删的只是"任意层级都没有文件"的目录，不含任何文件）。
        /// </para>
        /// </summary>
        internal static string BuildSharedScopeNote(
            CleanupScope scope,
            ArchiveTask? task,
            CleanupPreview? preview = null)
        {
            if (task == null || !IsOutputDirectorySharedWithSource(task.OutputPath, task.CurrentPath))
            {
                return string.Empty;
            }

            if (scope == CleanupScope.EmptyFolders)
            {
                return "当前任务是「解压到压缩包所在目录」模式，输出根就是源包所在目录，"
                       + "本次会清掉这个目录下所有“任意层级都没有文件”的子目录（不只是本任务产出的；不含任何文件）。";
            }

            if (preview?.ResolvedScope?.DeletesEverythingInDirectory == true)
            {
                return "这是「删除本目录全部其余物」：输出目录与同目录的其它包共用，"
                       + "本次会一并删掉它们的其余物（含各自的源包文件），删掉后都需要重新下载。";
            }

            return "当前任务是「解压到压缩包所在目录」模式（多个包共用这个输出目录），"
                   + "本次只删本任务那一份其余物（按包基名分开的子目录），不会碰其它包的其余物。";
        }

        /// <summary>任务的输出目录是不是就是源包所在目录（模式 B 的判据）。</summary>
        internal static bool IsOutputDirectorySharedWithSource(string? outputDirectory, string? sourceArchivePath)
        {
            // 判据只有一处实现（OutputPlacement.LandsInSourceDirectory），这里只是把参数顺序转过来。
            return OutputPlacement.LandsInSourceDirectory(sourceArchivePath, outputDirectory);
        }

        private static string BuildCleanupSummary(string title, CleanupOutcome outcome)
        {
            string modeText = outcome.Mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站";

            string summary = outcome.Attempted
                ? $"{title}：{modeText}成功 {outcome.SuccessCount} 项，失败 {outcome.FailureCount} 项。"
                  + (outcome.Mode == DeleteMode.Permanent
                      ? $"释放 {outcome.FreedBytes} 字节。"
                      : $"移入回收站 {outcome.RecycledBytes} 字节（回收站里的内容未真正释放空间）。")
                : $"{title}：没有执行删除。";

            if (outcome.FailureCount > 0 && outcome.FailureReasons.Count > 0)
            {
                summary += Environment.NewLine + "失败原因：" + Environment.NewLine
                           + string.Join(Environment.NewLine, outcome.FailureReasons.Take(CleanupPreview.MaxListedItems));
            }

            return summary;
        }

        /// <summary>
        /// 结果写日志（规格 §3.2 要求每条删除都留"路径 + 理由 + 条目数 + 总大小"）。
        ///
        /// 服务层已经把每一步整理成 <see cref="CleanupOutcome.LogLines"/>，这里原样落到界面/文件日志，
        /// 不在界面层重写一遍格式 —— 免得两个地方对"一条删除记录长什么样"产生分歧。
        /// </summary>
        private void WriteCleanupOutcomeLog(string title, ArchiveTask task, CleanupPreview preview, CleanupOutcome outcome)
        {
            AppendLog(
                outcome.FailureCount > 0 ? "WARN" : "INFO",
                $"{title}（{task.FileName}）：{outcome.Message}");

            foreach (string line in outcome.LogLines)
            {
                AppendLog(outcome.FailureCount > 0 ? "WARN" : "INFO", line);
            }

            if (!outcome.Attempted)
            {
                return;
            }

            // 预览里报的数字与实际删除的数字可能不同（期间有程序在写），两个都留痕便于核对。
            AppendLog(
                "INFO",
                $"{title}：预览时顶层 {preview.ItemCount} 项 / {preview.EntryCount} 个条目 / {preview.TotalBytes} 字节；" +
                $"实际成功 {outcome.SuccessCount} 项、失败 {outcome.FailureCount} 项。");
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

                    /*
                     * 「记住上次输出目录」：关掉时**不落盘** —— 这一次运行照用，
                     * 但下次启动不会再把这次的目录填回来。开着（默认）时行为和以前一样。
                     */
                    if (Settings.RememberLastOutputDirectory)
                    {
                        try
                        {
                            _settingsService.Save(Settings);
                        }
                        catch (Exception ex)
                        {
                            AppendLog("WARN", "保存输出目录设置失败：" + ex.Message);
                        }
                    }
                    else
                    {
                        AppendLog("INFO", "「记住上次输出目录」已关闭：本次选择不会写入设置。");
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
                CopySanitizedToClipboard(BuildTaskInfoText(task));
            }
        }

        private void CopyTaskPath(object? parameter)
        {
            if (parameter is ArchiveTask task)
            {
                CopySanitizedToClipboard(task.CurrentPath);
            }
        }

        private void CopyTaskError(object? parameter)
        {
            if (parameter is ArchiveTask task)
            {
                CopySanitizedToClipboard(task.ErrorMessage);
            }
        }

        /// <summary>
        /// 任务信息文本。
        ///
        /// ⚠ 字段清单与 <see cref="ClipboardService.CopyTaskInfo"/> 刻意保持一致 ——
        /// 为什么不用那个方法：它直接把文本塞进系统剪贴板，中间没有脱敏的位置，
        /// 而 ClipboardService 本轮不在授权文件清单里（不能给它加脱敏重载）。
        /// 这里自己拼文本再交给 <see cref="CopySanitizedToClipboard"/>，四个剪贴板入口就统一了口径
        /// （与「导出日志 / 导出失败清单」一样都过 PasswordMasker）。
        /// 那边加/减字段时，这里必须同步改，否则复制出来的内容会和菜单里的"复制任务信息"不一致。
        /// </summary>
        internal static string BuildTaskInfoText(ArchiveTask task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            builder.AppendLine("序号：" + task.Index);
            builder.AppendLine("文件名：" + (task.FileName ?? string.Empty));
            builder.AppendLine("原始路径：" + (task.OriginalPath ?? string.Empty));
            builder.AppendLine("当前路径：" + (task.CurrentPath ?? string.Empty));
            builder.AppendLine("当前后缀：" + (task.CurrentExtension ?? string.Empty));
            builder.AppendLine("检测格式：" + (task.DetectedFormat ?? string.Empty));
            builder.AppendLine("建议后缀：" + (task.SuggestedExtension ?? string.Empty));
            builder.AppendLine("后缀状态：" + (task.ExtensionStatus ?? string.Empty));
            builder.AppendLine("密码状态：" + (task.PasswordStatus ?? string.Empty));
            builder.AppendLine("输出目录：" + (task.OutputPath ?? string.Empty));

            /*
             * 其余物路径也带上（用户要求）：用户想手动去看/去删时不必自己拼路径。
             * 用与删除入口同一份作用域解析，"看到的"和"会删的"永远是同一个目录。
             */
            ArtifactCleanupScope artifactScope = MaintenanceCleanupService.ResolveArtifactScope(task);
            builder.AppendLine("其余物目录：" + (artifactScope.ArtifactDirectory ?? string.Empty));

            builder.AppendLine("操作：" + (task.Operation ?? string.Empty));
            builder.AppendLine("状态：" + (task.Status ?? string.Empty));
            builder.AppendLine("进度：" + (task.ProgressText ?? string.Empty));
            builder.AppendLine("错误信息：" + (task.ErrorMessage ?? string.Empty));
            builder.AppendLine("耗时：" + (task.ElapsedText ?? string.Empty));

            return builder.ToString().TrimEnd();
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

        /// <summary>
        /// 打开其余物目录（右键菜单入口）。
        ///
        /// 路径用与「删除其余物」**同一份解析**（<see cref="MaintenanceCleanupService.ResolveArtifactScope"/>）：
        /// 用户在这里看到的目录，就是那条删除入口会动的那一个 —— 两个入口给出不同答案是最容易出事的形态。
        /// </summary>
        private void OpenTaskArtifactDirectory(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            ArtifactCleanupScope scope = MaintenanceCleanupService.ResolveArtifactScope(task);

            if (!scope.IsResolved && string.IsNullOrWhiteSpace(scope.ArtifactDirectory))
            {
                _dialogService.ShowInfo("这个任务还没有其余物目录（还没解压，或已经被删掉了）。");
                return;
            }

            if (SafePathHelper.DirectoryExists(scope.ArtifactDirectory))
            {
                _pathService.OpenDirectory(scope.ArtifactDirectory);
                return;
            }

            _dialogService.ShowInfo("其余物目录不存在（可能已经被删掉了）：" + scope.ArtifactDirectory);
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
                 CleanProcessArtifactsCommand,
                 CleanAllProcessArtifactsCommand,
                 CleanEmptyFoldersCommand,

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
