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
using System.Collections.Concurrent;
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
        private bool _runAtFullSpeed;
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
        /// 把设置里与归档引擎有关的项推给引擎层。
        /// 两个外部工具（7z.exe / UnRAR.exe）的路径、引擎优先级、保留受损文件**只**通过这里生效，
        /// 别处不许再拼路径、也不许再各读一遍设置（AGENTS.md §3.1）。
        /// </summary>
        private void ApplyEngineSettings()
        {
            /*
             * 一次调用把三件事推给引擎层：两条外部工具路径 + 引擎优先级 + 保留受损文件。
             *
             * 为什么必须调它（这是上一批交付里**唯一**没接上的那一环）：
             * EngineRuntimeSettings 的默认值虽然"正确可用"，但用户改了优先级之后，
             * 引擎层要能从运行时设置里读到新顺序 —— 不推的话，「先是 UnRAR、再是 7-Zip」
             * 就只是一句写在设置界面上的话，真正干活的仍然是写死的那个引擎。
             * 设置窗口保存时也会推一次（两条路幂等，谁先谁后都不会打架）。
             */
            EngineRuntimeSettings.Apply(_settings);

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

        /// <summary>
        /// 流水线上真正在用的那个引擎对象（生产路径上是 <see cref="EngineRouter"/> 门面）。
        ///
        /// 只给测试与排障读：它是"设置里的优先级到底生效没有"唯一能被断言的地方 ——
        /// 界面上的"当前在用"是设置窗口自己算的预览，不能拿来证明真正跑起来用了谁。
        /// </summary>
        internal IArchiveEngine PipelineEngine => _archiveEngine;

        /// <summary>
        /// 某个任务**实际**用的引擎（报告层问的就是这个）。
        /// 问不到时返回 null，由 <see cref="TaskSummaryService"/> 退回通用引擎身份。
        /// </summary>
        private EngineIdentity? ResolveEngineIdentity(ArchiveTask? task)
        {
            if (task == null || _archiveEngine is not EngineRouter router)
            {
                return null;
            }

            return router.ResolveIdentityFor(task.CurrentPath);
        }

        /// <summary>
        /// 由注入的引擎推出一套注册表。
        ///
        /// 两条规则，区别只在"注入的是什么"：
        /// ① 注入的是**内置引擎之一**（默认构造走这条，也包含"想定制 7-Zip/UnRAR 其中一个"的调用方）
        ///    → 建完整注册表（UnRAR + 7-Zip），并让注入的那个实例**覆盖同 Id 的那一个**，
        ///    这样定制的路径 / runner 不会被丢掉，同时另一个内置引擎照常参与分派；
        /// ② 注入的是别的东西（测试替身、将来第三方实现的引擎）→ **只用它一个**：
        ///    绝不在它背后偷偷拉起真 7z.exe 进程（那会让"假引擎"的测试变成真的解压）。
        /// </summary>
        private static EngineRegistry BuildEngineRegistry(IArchiveEngine? injected)
        {
            var registry = new EngineRegistry();

            if (injected == null)
            {
                return EngineRegistry.CreateDefault();
            }

            bool isBuiltIn = string.Equals(injected.Id, EngineIds.SevenZip, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(injected.Id, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase);

            if (!isBuiltIn)
            {
                registry.Register(injected);
                return registry;
            }

            EngineRegistry defaultRegistry = EngineRegistry.CreateDefault();
            defaultRegistry.Register(injected);

            return defaultRegistry;
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

        /*
         * 「全速」开关 —— 并发队列的"立即继续"出口
         * （docs/WinRAR功能参考.md §2 D 组采纳项：任何"自动等待/节流"都必须配一个"立即继续"的出口）。
         *
         * 为什么需要它：设置里的「最大并发解压数」是个节流旋钮（默认 1 = 串行），
         * 但用户临时想让这一批快点跑完时，以前只能进设置改数字、再回来重跑 ——
         * 而 WinRAR 那边的教训正是"让用户干等却无从干预"。
         *
         * 语义刻意保持最窄：**只看这一次运行**，不写回设置（设置仍是唯一的持久来源）。
         * 关掉它就回到「最大并发解压数」那一档。
         */
        public bool RunAtFullSpeed
        {
            get => _runAtFullSpeed;
            set
            {
                if (SetProperty(ref _runAtFullSpeed, value))
                {
                    AppendLog(
                        "INFO",
                        value
                            ? "已开启「全速」：本批忽略「最大并发解压数」的节流，能并行多少就并行多少。"
                            : "已关闭「全速」：本批按设置里的「最大并发解压数」节流。");
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

        /// <summary>
        /// <paramref name="engineRegistry"/> 不给时按 <paramref name="archiveEngine"/> 推一套（见 <see cref="BuildEngineRegistry"/>）；
        /// 给了就以它为准（测试可以塞一套全是替身的注册表，端到端验证"按格式分派"）。
        /// </summary>
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
            MaintenanceCleanupService? cleanupService = null,
            EngineRegistry? engineRegistry = null)
        {
            _fileScanService = fileScanService;
            _archiveDetectService = archiveDetectService;
            _renameService = renameService;

            /*
             * 流水线拿到的**不是**某一个引擎，而是"按格式分派 + 按优先级决定先用谁"的门面。
             *
             * 为什么必须换掉（这批修的就是这个）：端口本来就是 IArchiveEngine，
             * 但注册的只有 7-Zip —— 于是「先是 winrar、再是 7z」这条设置、
             * 以及辛苦做出来的 UnRarEngine，在界面上**完全没有生效**：
             * 不管优先级怎么排、UnRAR 装没装，干活的永远是 7-Zip。
             *
             * 分派规则一条都不在这里重写：能力筛、优先级 tiebreaker、不可用跳过、回退判据
             * 全在 EngineRegistry / EngineSelector 里（AGENTS.md §3.1），这里只做转发。
             */
            _archiveEngine = new EngineRouter(engineRegistry ?? BuildEngineRegistry(archiveEngine));

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
            _extractionCoordinator = new ExtractionCoordinator(this, _archiveEngine, passwordService, pathService, dialogService);
            _oneClickCoordinator = new OneClickCoordinator(this, _scanCoordinator, _renameCoordinator, _extractionCoordinator, dialogService);

            /*
             * 把"解压前那一遍 list 顺带算出来的提示"接进失败清单的第二级（逐归档缩进行）。
             *
             * 为什么走这个注入口而不是改 TaskSummaryService：那个文件不在本次授权范围内
             * （任务书明确要求"需要什么就写进报告"）。TaskSummaryService 早就留了这个可选钩子
             * （EntryDetailProvider），这里挂上就等价于改了它的输出，而且不改它的代码。
             *
             * 两条提示都是"只提示不阻断"的结论，所以措辞上不写成错误，也不影响任何状态判定：
             * · 可疑条目（可执行 / 脚本类）—— 设置项 ReportDangerousEntries，默认开；
             * · 路径过长预警 —— 7z 报的是英文错，这里给一句中文。
             */
            _taskSummaryService.EntryDetailProvider = task =>
            {
                var lines = new List<string>();

                if (!string.IsNullOrWhiteSpace(task.DangerousEntriesWarning))
                {
                    lines.Add(task.DangerousEntriesWarning);
                }

                if (!string.IsNullOrWhiteSpace(task.PathLengthWarning))
                {
                    lines.Add(task.PathLengthWarning);
                }

                return lines.Count == 0 ? null : lines;
            };

            /*
             * "这个包**实际**是哪个引擎解的"接进报告（不变量 14）。
             *
             * 报告层默认只能报"通用引擎"（它不知道每个包走了哪条分派路，而且报告生成那一刻
             * 用户可能已经改了优先级、卸载了 WinRAR —— 那时再去问注册表问的是"现在"，
             * 不是"当时"）。所以由**真正执行的那个引擎**在结果上盖戳（ArchiveOperationResult
             * 的 EngineId / EngineVersion），门面按归档路径记下来，报告逐任务取用；
             * 取不到（比如报告是手搓的、或这一次根本没跑过引擎）就退回通用引擎身份。
             */
            _taskSummaryService.EngineIdentityProvider = task => ResolveEngineIdentity(task);

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

            /*
             * 把"这一次会按什么顺序分派"写进日志：优先级设置到底生效没有，事后能从这里看到。
             * 以前这件事在界面上完全看不出来 —— 用户改了顺序、跑完一批，
             * 日志里既没有"用了哪个引擎"，也没有"为什么是它"。
             */
            if (_archiveEngine is EngineRouter router)
            {
                AppendLog("INFO", router.DescribeDispatch());
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

        /// <summary>
        /// 「定稿完成后打开输出目录」的落地（设置项 <see cref="AppSettings.OpenOutputFolderWhenDone"/>，**默认关**）。
        ///
        /// <para>
        /// 由解压管线在"**最外层任务** + 内容物已定稿 + 输出校验通过 + 未取消"的收尾处调用，
        /// 而且**整批只调一次**（记账在 <see cref="ExtractionCoordinator"/> 那一侧）：
        /// 一批 50–200 个包每个都开一次资源管理器，那不是"看一眼结果"，是骚扰。
        /// </para>
        /// <para>
        /// <b>只打开文件夹</b>：走既有的 <see cref="PathService.OpenDirectory"/> →
        /// <c>explorer.exe &lt;目录&gt;</c>，**不**调用 <c>SetForegroundWindow</c>、
        /// **不**最大化、**不**抢焦点（AGENTS.md §13 的同一精神）。
        /// 绝不为了"确保用户看到"再加置前逻辑。
        /// </para>
        /// <para>
        /// 失败 / 取消 / 部分完成一律不走这里（打开一个空目录只会误导用户）——
        /// 那条判断在调用点，见 <see cref="ExtractionCoordinator.PostProcessSuccessAsync"/>。
        /// </para>
        /// <para>
        /// <c>virtual</c> 只为单元测试：真实现会 <c>Process.Start("explorer.exe", 目录)</c>，
        /// 测试既不该真的弹资源管理器（AGENTS.md §13），也没法断言"到底开了几次"。
        /// 测试替身把它换成记录器，于是"整批只开一次"变成可断言的事实。
        /// </para>
        /// </summary>
        /// <param name="directory">实际落点（<c>task.OutputPath</c>，归集之后就是归集目录）。</param>
        /// <returns>真的打开了目录返回 true；关着开关或目录不存在返回 false。</returns>
        public virtual bool OpenCompletedOutputDirectory(string directory)
        {
            if (Settings == null || !Settings.OpenOutputFolderWhenDone)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            // 先确认它真的在：否则 explorer.exe 会弹一个"找不到"的系统框 —— 那是另一种抢焦点。
            if (!SafePathHelper.DirectoryExists(directory))
            {
                AppendLog("WARN", $"定稿完成，但输出目录已不存在，没有打开：{directory}");
                return false;
            }

            bool opened = _pathService.OpenDirectory(directory);

            AppendLog(
                opened ? "INFO" : "WARN",
                opened
                    ? $"定稿完成，已打开输出目录：{directory}"
                    : $"定稿完成，但输出目录打不开：{directory}");

            return opened;
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
                 * ② 确认（红色）。默认档取自设置项「其余物清理默认档」（<see cref="AppSettings.RestRemovalDefaultMode"/>），
                 * 勾选框是"切到另一档"的开关 —— 默认动作与"最不意外"一致，另一档必须用户主动勾选
                 * （规格 §3.2 清理表）。
                 *
                 * ⚠ 必须用 ShowDestructiveConfirmWithOption（**不勾也能确认**）：
                 * 勾选框在这里是"档位选择"而不是"必须承认才能继续"，用错方法会让默认档走不下去。
                 *
                 * ⚠⚠ 默认档 = Permanent 时，勾选框的**语义要反过来**（文案变成"改为移入回收站（可恢复）"）：
                 * 否则会出现"默认档是彻底删除、但勾选框写着改为彻底删除"的自相矛盾 ——
                 * 用户不勾反而得到更危险的那一档。这就是 <see cref="DeriveCleanupDecision"/> 存在的理由。
                 */
                string scopeNote = BuildSharedScopeNote(scope, task, preview);

                CleanupDecision decision = DeriveCleanupDecision(
                    Settings.RestRemovalDefaultMode,
                    preview,
                    title,
                    scopeNote);

                bool confirmed = _dialogService.ShowDestructiveConfirmWithOption(
                    decision.ConfirmText,
                    decision.ConfirmButtonText,
                    decision.OptionText,
                    optionCheckedByDefault: decision.OptionCheckedByDefault,
                    out bool optionChecked);

                if (!confirmed)
                {
                    AppendLog("INFO", $"{title}：用户取消，没有删除任何东西。");
                    return;
                }

                DeleteMode mode = decision.Resolve(optionChecked);

                if (mode == DeleteMode.Permanent)
                {
                    /*
                     * ③ 激进档的**二次确认**：红色 + 必须勾选"我知道不可恢复"。
                     * 只弹一次红色框是不够的 —— 规格 §3.2 要求"红色标识"与"二次确认"两件事同时满足。
                     *
                     * 这一条**无论默认档是什么都保留**：把默认档设成"彻底删除"是"少点一次勾"，
                     * 不是"免掉二次确认"（不变量：不可逆操作必须显式确认）。
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
        /// 一次清理确认框的"档位设计"：主按钮文案、勾选框文案、勾选框的默认状态，以及
        /// "用户最终勾没勾 → 到底用哪一档"的唯一推导。
        ///
        /// <para>
        /// 为什么需要它（真实的自相矛盾风险）：设置项「其余物清理默认档」可以是<em>彻底删除</em>，
        /// 而确认框里那个勾选框原来的文案固定是"改为彻底删除，不进回收站"、默认不勾 ——
        /// 于是默认档是彻底删除时，界面会同时说"默认彻底删除"和"勾上才彻底删除"，
        /// 用户不勾反而得到了更危险的那一档。把"档位 ↔ 文案 ↔ 勾选语义"三件事绑在一个对象上，
        /// 就不可能再各写各的。
        /// </para>
        /// </summary>
        internal sealed class CleanupDecision
        {
            public required DeleteMode DefaultMode { get; init; }

            /// <summary>确认框正文（已带上默认档的说明）。</summary>
            public required string ConfirmText { get; init; }

            /// <summary>主按钮文案：就是默认档本身。</summary>
            public required string ConfirmButtonText { get; init; }

            /// <summary>勾选框文案：**永远是"切到另一档"**，所以默认档不同时文案也不同。</summary>
            public required string OptionText { get; init; }

            /// <summary>勾选框初始状态：永远不勾（默认档就是"不勾"的那一档）。</summary>
            public bool OptionCheckedByDefault { get; init; }

            /// <summary>勾选框勾上时的档位（= 另一档）。</summary>
            public required DeleteMode OptionMode { get; init; }

            /// <summary>用户最终勾没勾 → 实际执行哪一档。</summary>
            public DeleteMode Resolve(bool optionChecked) => optionChecked ? OptionMode : DefaultMode;
        }

        /// <summary>
        /// 由设置项「其余物清理默认档」推出确认框的档位设计（唯一判定处）。
        ///
        /// <para>
        /// 判定只有一处实现：<see cref="RestRemovalModes.IsPermanent"/> —— 别在别处再写
        /// <c>== "Permanent"</c>，那种字符串比较散落两处迟早分叉（一个认大小写、一个不认）。
        /// </para>
        /// <para>
        /// ⛔ 两条红线在这里**不受默认档影响**：
        /// ① 二次确认保留 —— 只要最终要彻底删除，调用方仍会再弹一次"我知道不可恢复"，见 RunCleanupAsync；
        /// ② 回收站不可用绝不降级为永久删除 —— 那条在 RecycleBinService 里，跟这里的档位无关。
        /// </para>
        /// </summary>
        internal static CleanupDecision DeriveCleanupDecision(
            string? restRemovalDefaultMode,
            CleanupPreview preview,
            string title,
            string? scopeNote)
        {
            bool defaultIsPermanent = RestRemovalModes.IsPermanent(restRemovalDefaultMode);

            DeleteMode defaultMode = defaultIsPermanent ? DeleteMode.Permanent : DeleteMode.RecycleBin;
            DeleteMode optionMode = defaultIsPermanent ? DeleteMode.RecycleBin : DeleteMode.Permanent;

            return new CleanupDecision
            {
                DefaultMode = defaultMode,
                OptionMode = optionMode,
                ConfirmText = BuildCleanupConfirmText(title, preview, defaultMode, scopeNote),
                ConfirmButtonText = defaultIsPermanent ? "彻底删除" : "移入回收站",

                // 勾选框永远表示"切到另一档"：默认档是彻底删除时，勾上 = 改为可恢复的那一档。
                OptionText = defaultIsPermanent
                    ? "改为移入回收站（可恢复）"
                    : "改为彻底删除，不进回收站",
                OptionCheckedByDefault = false
            };
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

            /*
             * 解压前那一遍 list 顺带算出来的两条提示也要复制得出来（与失败清单的第二级同一份来源）：
             * 用户把任务信息贴给我们排障时，这两句往往是"为什么少了几个文件"的关键线索。
             * 空着就不写行，免得每次复制都带两行没内容的标签。
             */
            if (!string.IsNullOrWhiteSpace(task.DangerousEntriesWarning))
            {
                builder.AppendLine("可疑条目：" + task.DangerousEntriesWarning);
            }

            if (!string.IsNullOrWhiteSpace(task.PathLengthWarning))
            {
                builder.AppendLine("路径预警：" + task.PathLengthWarning);
            }

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

    /// <summary>
    /// 「按格式与优先级分派」的引擎门面 —— 多引擎真正接进流水线的那一环。
    ///
    /// <para><b>它解决什么</b>：流水线要的从来不是"某一个引擎"，而是"这个包该谁解"。
    /// 以前 <see cref="MainViewModel"/> 直接 <c>new SevenZipEngine()</c> 塞给
    /// <c>ExtractionCoordinator</c>，于是 <c>AppSettings.EnginePriority</c> 与
    /// <c>UnRarEngine</c> 在真实流程里**完全没有生效**：不管优先级怎么排、UnRAR 装没装，
    /// 干活的永远是 7-Zip。</para>
    ///
    /// <para><b>规则一条都不在这里重写</b>（AGENTS.md §3.1）：按能力筛、优先级只做 tiebreaker、
    /// 不可用的引擎直接跳过、哪些错误才值得换引擎，全部来自 <see cref="EngineRegistry"/> /
    /// <see cref="EngineSelector"/>。这个类只做三件事：<b>问格式</b> → <b>转发</b> → <b>记账</b>。</para>
    ///
    /// <para><b>它顺带回答一个以前答不出的问题</b>：这个包**实际**是哪个引擎解的。
    /// 每个引擎都会在自己的结果上盖戳（<see cref="ArchiveOperationResult.EngineId"/> /
    /// <see cref="ArchiveOperationResult.EngineVersion"/>），门面按归档路径记下来，
    /// 报告里逐任务那一行读的就是它（不变量 14）—— 而不是"报告生成那一刻注册表里排第一的是谁"。</para>
    ///
    /// <para>⚠ <b>落点说明</b>：按本次任务授权它只能待在
    /// <c>ViewModels/MainViewModel.cs</c> 里（不许新增文件）。从职责上它属于 <c>Engines/</c> ——
    /// 下一轮若放开新增文件，应整体搬到 <c>Engines/EngineRouter.cs</c> 并配独立用例。</para>
    /// </summary>
    internal sealed class EngineRouter : IArchiveEngine
    {
        /// <summary>格式识别结果缓存的条数上限：超了整表清空（缓存只为省掉重复的头部读取，丢掉不心疼）。</summary>
        private const int FormatCacheLimit = 512;

        /// <summary>"这个包用了谁"记账表的条数上限（同上）。</summary>
        private const int UsedEngineLimit = 2048;

        private readonly EngineRegistry _registry;
        private readonly EngineSelector _selector;
        private readonly ArchiveDetectService _detector;

        /// <summary>归档路径 → 实际执行它的引擎身份（每次调用后覆盖写，成功的那一次自然留在里面）。</summary>
        private readonly ConcurrentDictionary<string, EngineIdentity> _usedByPath =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 归档**基名** → 引擎身份。
        ///
        /// 为什么除路径之外还要按基名记一份：双面文件（内嵌归档）是先按偏移抠到
        /// <c>&lt;工作区&gt;\&lt;任务目录&gt;\&lt;源包基名&gt;.zip</c> 再交给引擎的，
        /// 引擎看到的是那个临时文件，而报告要标的是**源包那个任务** ——
        /// 两者的路径不同、扩展名不同，只有基名是同一个（抠包代码用的就是
        /// <see cref="FileNameHelper.GetArchiveBaseName"/>）。
        /// </summary>
        private readonly ConcurrentDictionary<string, EngineIdentity> _usedByBaseName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>归档路径 → 格式识别结果（含文件大小与最后写入时间：源文件变了就重算）。</summary>
        private readonly ConcurrentDictionary<string, FormatCacheEntry> _formatCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal EngineRouter(EngineRegistry registry, ArchiveDetectService? detectService = null)
        {
            _registry = registry ?? EngineRegistry.CreateDefault();
            _selector = new EngineSelector(_registry);
            _detector = detectService ?? new ArchiveDetectService();
        }

        /// <summary>
        /// 门面自己的身份：报**通用引擎**（能处理格式最多的那个可用引擎，通常是 7-Zip）。
        ///
        /// 为什么不编一个"multi-engine"之类的 id：<see cref="Id"/> / <see cref="DisplayName"/> /
        /// <see cref="Version"/> 会被写进工作区 report.json 与界面提示，是**溯源字段**（不变量 14）——
        /// 编一个查不到的引擎 id 比"报通用引擎"更糟。某个包实际用了谁，
        /// 由 <see cref="ResolveIdentityFor"/> 逐任务给出，那才是真正的溯源。
        /// </summary>
        public string Id => General?.Id ?? EngineIds.SevenZip;

        public string DisplayName => General?.DisplayName ?? "未检测到可用引擎";

        public string Version => General?.Version ?? "unknown";

        /// <summary>只要有一个引擎可用，流水线就能跑（格式对不上的包由选择器报"没有引擎能处理"）。</summary>
        public bool IsAvailable => _registry.Engines.Any(e => e.IsAvailable);

        /// <summary>门面的能力位取通用引擎的（它代表"这个程序能干什么"）。</summary>
        public EngineCapabilities Capabilities =>
            General?.Capabilities ?? new EngineCapabilities();

        private IArchiveEngine? General => _registry.Default;

        /// <summary>
        /// 探测：转发给通用引擎，并在它答"格式未知"时补上 Detection 层的魔数结论。
        ///
        /// 两个引擎的 <c>ProbeAsync</c> 都如实回答"没有廉价探测能力"（见各自实现），
        /// 真正便宜的识别一直由 Detection 层负责 —— 门面顺手把这份结论带上，
        /// 免得调用方为了知道格式再去问一遍。
        /// </summary>
        public async Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return new ArchiveProbeResult { IsArchive = false, Format = "Unknown", Confidence = "Unknown", Message = "归档请求为空" };
            }

            IArchiveEngine? engine = General;

            ArchiveProbeResult result = engine == null
                ? new ArchiveProbeResult { IsArchive = false, Format = "Unknown", Confidence = "Unknown", Message = "没有可用引擎" }
                : await engine.ProbeAsync(request, cancellationToken).ConfigureAwait(false);

            if (result != null && string.Equals(result.Format, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                string? format = await DetectFormatAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(format))
                {
                    return new ArchiveProbeResult
                    {
                        IsArchive = result.IsArchive,
                        Format = format!,
                        SuggestedExtension = result.SuggestedExtension,
                        Confidence = "Medium",
                        IsEncrypted = result.IsEncrypted,
                        IsMultiVolume = result.IsMultiVolume,
                        Message = result.Message
                    };
                }
            }

            return result ?? new ArchiveProbeResult { IsArchive = false, Format = "Unknown", Confidence = "Unknown" };
        }

        public async Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return ArchiveListResult.Failure(EngineErrorTypes.UnknownError, "归档请求为空", Id, Version);
            }

            string? format = await DetectFormatAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<IArchiveEngine> candidates = Candidates(format, EngineOperation.List);

            if (candidates.Count == 0)
            {
                return ArchiveListResult.Failure(
                    EngineErrorTypes.EngineUnavailable,
                    DescribeNoEngine(format),
                    Id,
                    Version);
            }

            ArchiveListResult? last = null;

            foreach (IArchiveEngine engine in candidates)
            {
                last = await engine.ListAsync(request, cancellationToken).ConfigureAwait(false);

                RememberUsed(request.ArchivePath, IdentityOf(engine, last?.EngineId, last?.EngineVersion, null));

                if (last != null && last.Success)
                {
                    return last;
                }

                /*
                 * 换不换引擎由既有判据说了算（设计.md §二十）：
                 * 只有"换个引擎可能就行"的错误才继续试下一个；密码错、缺分卷、没权限、磁盘满
                 * 换一百个引擎也一样，继续试只会让用户多等一遍、把真实原因埋掉。
                 */
                if (!EngineSelector.ShouldTryFallback(last?.ErrorType))
                {
                    break;
                }
            }

            return last ?? ArchiveListResult.Failure(
                EngineErrorTypes.EngineUnavailable,
                DescribeNoEngine(format),
                Id,
                Version);
        }

        public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            return RunOperationAsync(
                request,
                EngineOperation.Test,
                (engine, token) => engine.TestAsync(request, token),
                cancellationToken);
        }

        public Task<ArchiveOperationResult> ExtractAsync(
            ArchiveRequest request,
            ExtractOptions options,
            CancellationToken cancellationToken = default)
        {
            return RunOperationAsync(
                request,
                EngineOperation.Extract,
                (engine, token) => engine.ExtractAsync(request, options, token),
                cancellationToken);
        }

        /// <summary>
        /// 这个归档**实际**用的引擎；问不到返回 null（调用方退回通用引擎身份）。
        ///
        /// 先按完整路径找，再按基名找（抠出来的内嵌归档与源包只有基名相同，见
        /// <see cref="_usedByBaseName"/> 的说明）。
        /// </summary>
        internal EngineIdentity? ResolveIdentityFor(string? archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return null;
            }

            if (_usedByPath.TryGetValue(NormalizePath(archivePath!), out EngineIdentity? exact))
            {
                return exact;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(archivePath!);

            return !string.IsNullOrWhiteSpace(baseName) && _usedByBaseName.TryGetValue(baseName, out EngineIdentity? byName)
                ? byName
                : null;
        }

        /// <summary>
        /// "这次会按什么顺序分派"的一句话（启动日志与排障用；与真正执行时同一份选择逻辑）。
        /// </summary>
        internal string DescribeDispatch()
        {
            EngineSelection rar = _selector.Explain("RAR5", EngineOperation.Extract);
            EngineSelection generic = _selector.Explain("ZIP", EngineOperation.Extract);

            string text = $"引擎分派（按格式与优先级，优先级：{string.Join(" → ", _selector.Priority)}）：" +
                          $"RAR → {rar.Describe()}；zip / 7z 等其它格式 → {generic.Describe()}";

            if (rar.SkippedUnavailable.Count > 0 || generic.SkippedUnavailable.Count > 0)
            {
                text += "。排在前面的引擎没检测到时会被自动跳过，不会因此打不开包";
            }

            return text + "。";
        }

        private async Task<ArchiveOperationResult> RunOperationAsync(
            ArchiveRequest request,
            EngineOperation operation,
            Func<IArchiveEngine, CancellationToken, Task<ArchiveOperationResult>> call,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return ArchiveOperationResult.CreateFailure(
                    -1, string.Empty, string.Empty, StatusText.ExtractFailed, "归档请求为空", EngineErrorTypes.UnknownError, TimeSpan.Zero);
            }

            string? format = await DetectFormatAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<IArchiveEngine> candidates = Candidates(format, operation);

            if (candidates.Count == 0)
            {
                string message = DescribeNoEngine(format);

                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    IsAvailable ? StatusText.ExtractFailed : StatusText.SevenZipMissing,
                    message,
                    EngineErrorTypes.EngineUnavailable,
                    TimeSpan.Zero);
            }

            ArchiveOperationResult? last = null;

            foreach (IArchiveEngine engine in candidates)
            {
                last = await call(engine, cancellationToken).ConfigureAwait(false);

                RememberUsed(request.ArchivePath, IdentityOf(engine, last));

                if (last != null && last.Success)
                {
                    return last;
                }

                // 同 ListAsync：换引擎的判据来自 EngineSelector，不在这里另写一套。
                if (!EngineSelector.ShouldTryFallback(last?.DetectedErrorType))
                {
                    break;
                }
            }

            return last ?? ArchiveOperationResult.CreateFailure(
                -1,
                string.Empty,
                string.Empty,
                StatusText.ExtractFailed,
                DescribeNoEngine(format),
                EngineErrorTypes.EngineUnavailable,
                TimeSpan.Zero);
        }

        /// <summary>候选引擎（能力筛 + 优先级 + 可用性），顺序即尝试顺序。</summary>
        private IReadOnlyList<IArchiveEngine> Candidates(string? format, EngineOperation operation)
        {
            return _selector.SelectFallbacks(format, operation, failedEngineId: null);
        }

        /// <summary>
        /// 这个文件到底是什么格式 —— 分派的**唯一前提**。
        ///
        /// 用 Detection 层的魔数识别，不猜扩展名（这个程序存在的意义就是"后缀被改坏"：
        /// 按后缀分派会把一个 RAR 交给 7-Zip，把 zip 交给 UnRAR）。
        /// 识别不出来 / 出任何岔子都返回 null —— 选择器会把"没有格式"落到通用引擎，
        /// **绝不允许**因为识别失败把包卡住。
        /// </summary>
        private async Task<string?> DetectFormatAsync(string? archivePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return null;
            }

            string key = NormalizePath(archivePath!);

            try
            {
                var info = new FileInfo(archivePath!);

                if (_formatCache.TryGetValue(key, out FormatCacheEntry? cached) &&
                    cached != null &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtc == info.LastWriteTimeUtc)
                {
                    return cached.Format;
                }

                if (_formatCache.Count > FormatCacheLimit)
                {
                    _formatCache.Clear();
                }

                Models.DetectResult detected = await _detector.DetectAsync(archivePath!, cancellationToken).ConfigureAwait(false);

                string? format = detected != null && detected.IsKnownFormat && !string.IsNullOrWhiteSpace(detected.Format)
                    ? detected.Format
                    : null;

                _formatCache[key] = new FormatCacheEntry(info.Length, info.LastWriteTimeUtc, format);

                return format;
            }
            catch
            {
                // 识别失败不是"这个包打不开"的理由：交给通用引擎去试（它本来就会给出真实结论）。
                return null;
            }
        }

        /// <summary>
        /// 记下"这个归档这一次是谁干的"。每次尝试都写，于是：
        /// 成功时留下的是成功那个引擎，全失败时留下的是**最后尝试**的那个 ——
        /// 正是任务上那条错误信息来自的引擎。
        /// </summary>
        private void RememberUsed(string? archivePath, EngineIdentity identity)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || identity == null)
            {
                return;
            }

            if (_usedByPath.Count > UsedEngineLimit)
            {
                _usedByPath.Clear();
                _usedByBaseName.Clear();
            }

            _usedByPath[NormalizePath(archivePath!)] = identity;

            string baseName = FileNameHelper.GetArchiveBaseName(archivePath!);

            if (!string.IsNullOrWhiteSpace(baseName))
            {
                _usedByBaseName[baseName] = identity;
            }
        }

        /// <summary>引擎身份：结果上盖了戳就以戳为准，缺哪一项才回落到引擎实例的当前值。</summary>
        private static EngineIdentity IdentityOf(
            IArchiveEngine engine,
            string? resultEngineId,
            string? resultVersion,
            string? resultDisplayName)
        {
            return new EngineIdentity
            {
                EngineId = string.IsNullOrWhiteSpace(resultEngineId) ? engine.Id : resultEngineId!,
                DisplayName = string.IsNullOrWhiteSpace(resultDisplayName) ? engine.DisplayName : resultDisplayName!,
                Version = string.IsNullOrWhiteSpace(resultVersion) ? engine.Version : resultVersion!,
                IsAvailable = engine.IsAvailable
            };
        }

        private static EngineIdentity IdentityOf(IArchiveEngine engine, ArchiveOperationResult? result)
        {
            return result != null && !string.IsNullOrWhiteSpace(result.EngineId)
                ? IdentityOf(engine, result.EngineId, result.EngineVersion, result.EngineDisplayName)
                : IdentityOf(engine, null, null, null);
        }

        /// <summary>"没有引擎能处理这个格式"的一句话（如实说清是格式的问题还是引擎没装）。</summary>
        private string DescribeNoEngine(string? format)
        {
            string what = string.IsNullOrWhiteSpace(format) ? "这个文件（格式未识别出来）" : format!;

            return IsAvailable
                ? $"没有可用引擎能处理 {what}：现有引擎都不声明支持它"
                : $"没有可用引擎能处理 {what}：7-Zip 与 UnRAR 都没检测到（检查 tools 目录或设置里的路径）";
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>格式识别缓存的一项（键是路径，值是"哪一次的识别结果"）。</summary>
        private sealed class FormatCacheEntry
        {
            internal FormatCacheEntry(long length, DateTime lastWriteUtc, string? format)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                Format = format;
            }

            internal long Length { get; }

            internal DateTime LastWriteUtc { get; }

            internal string? Format { get; }
        }
    }
}
