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
using System.Collections.Specialized;
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
        private readonly SettingsViewModel _settingsEditor;

        private AppSettings _settings;
        private string _globalPassword = string.Empty;
        private string _passwordBookSummary = string.Empty;
        private string _passwordBookTooltip = string.Empty;

        /// <summary>
        /// 这次运行里"启动读记忆"是否已经做过了。
        ///
        /// <para><see cref="AutoLoadPasswordBook"/> 有五个调用点（启动、清空列表、移除选中、
        /// 移除单个任务、保存设置），其中只有启动那一次该读记忆 —— 读记忆会**重建整个列表**，
        /// 反复读会把用户刚做的改动冲掉。后来的调用只做"合并密码本"这件幂等的事。</para>
        /// </summary>
        private bool _rememberedListLoaded;
        private bool _showPassword;
        private bool _isBusy;

        /// <summary>工作区残留那一行提示的文本（空 = 没有残留）。见 <see cref="LogLeftoverWorkspaces"/>。</summary>
        private string _workspaceLeftoverBanner = string.Empty;

        private bool _hasWorkspaceLeftovers;

        private long _workspaceLeftoverTotalBytes;

        /// <summary>上一批撞到层数上限后还剩多少个内层包没解（见 <see cref="PendingContinuationCount"/>）。</summary>
        private int _pendingContinuationCount;
        private int _busyNesting;
        private bool _isStopping;
        private bool _runAtFullSpeed;
        private string _selectedOutputDirectory = string.Empty;
        private TaskSummary _summary = new();
        private ArchiveTask? _selectedTask;

        /*
         * ===== 空间规划 + 危险模式的界面状态（用户 2026-09-22 需求）=====
         *
         * 三样东西：
         * · _parallelAdviceText —— "当前可用 X，建议并行 N 个"（按空间算出来的，不是编的）；
         * · _dangerModeBusy    —— 自测/开启过程中禁用入口（自测要跑几分钟，期间再点一次会撞车）；
         * · _spaceModeText     —— 当前档位的一句话（危险模式开没开、有没有自测凭证）。
         */
        private string _parallelAdviceText = "点「按空间算建议」可以算出当前可用空间能并行几个";

        private string _spaceModeText = string.Empty;

        /// <summary>
        /// 任务列表。
        ///
        /// <para>类型是 <see cref="RangeObservableCollection{T}"/>（ObservableCollection 的子类）：
        /// 导入几百个包时用它的批量方法**只发一次集合变更通知**，
        /// 而不是逐项 <c>Add</c> 触发几百次界面刷新（用户 2026-09-24 第 12 条"卡死"的修法之一）。</para>
        /// </summary>
        public RangeObservableCollection<ArchiveTask> Tasks { get; } = new();

        public ObservableCollection<OperationLogItem> Logs { get; } = new();

        public AppSettings Settings
        {
            get => _settings;
            set
            {
                if (SetProperty(ref _settings, value))
                {
                    ApplyEngineSettings();

                    /*
                     * 设置对象换成新的一份了（恢复默认 / 保存之后）：选项卡上那三页设置
                     * 绑的是 SettingsEditor，必须让它**指向同一个对象** ——
                     * 否则界面上还显示着上一份的值，用户改完保存却"看起来没生效"。
                     *
                     * （构造阶段这里是 null：编辑器还没建出来，?.就是为它准备的。）
                     */
                    SettingsEditor?.AttachSharedSettings(_settings);
                }
            }
        }

        /// <summary>
        /// 选项卡上「设置」「解压方式」「清理与删除」「密码」四页共用的设置编辑器
        /// （用户 2026-09-24 第 11 条：设置窗口退休，内容按功能拆进选项卡）。
        ///
        /// <para>
        /// 它仍然是 <see cref="SettingsViewModel"/> —— 保存前的两道拦截
        /// （工具路径指向不存在的文件、危险模式没自测凭证）**一行没动**，
        /// 只是把"点保存 → 关窗"换成"点底栏的保存设置 → 留在原地"。
        /// </para>
        /// </summary>
        public SettingsViewModel SettingsEditor => _settingsEditor;

        /// <summary>
        /// ⑤ 打包页用的编辑器（原打包窗口的 ViewModel，界面整页搬进选项卡，没有第二份）。
        ///
        /// <para>日志通过 <c>LogSink</c> 接到主日志上 —— 打包结果要能追到引擎名与版本（不变量 14），
        /// 切走页面之后日志里还得查得到。</para>
        /// </summary>
        public PackingViewModel PackingEditor { get; }

        /// <summary>
        /// 「跳到某一页」的请求（设置 / 打包搬进选项卡之后，这两个命令只是切页）。
        /// 没有订阅者（测试 / 命令行宿主）时只写一行日志 —— 绝不静默什么都不做。
        /// </summary>
        public event Action<int>? TabRequested;

        /// <summary>打包页在选项卡里的位置。</summary>
        public const int PackingTabIndex = 4;

        /// <summary>设置页在选项卡里的位置。</summary>
        public const int SettingsTabIndex = 5;

        /// <summary>②「解压方式」页在选项卡里的位置。</summary>
        public const int ExtractionTabIndex = 1;

        /// <summary>
        /// 「关于」那一段（帮助 → 关于 与⑥设置页共用同一份文本）：
        /// 程序版本 + 每个引擎的名字 / 版本 / 是否可用 + 许可边界。
        ///
        /// <para>
        /// 为什么把引擎版本摆在这里：结果要能追到具体引擎与版本（不变量 14），
        /// 而"我现在到底在用哪个 7-Zip / UnRAR"是用户排障时第一个要问的问题。
        /// 引擎那一份数据来自 <see cref="SettingsEditor"/> 的检测结果，界面与这里不会分叉。
        /// </para>
        /// </summary>
        public string AboutText
        {
            get
            {
                var builder = new System.Text.StringBuilder();

                string version = typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "（版本读不到）";

                builder.AppendLine("ArchiveFixer " + version);
                builder.AppendLine();

                if (SettingsEditor.Engines.Count == 0)
                {
                    builder.AppendLine("（没有检测到任何引擎）");
                }
                else
                {
                    foreach (EngineOptionItem engine in SettingsEditor.Engines)
                    {
                        builder.AppendLine(engine.HeaderText);
                    }
                }

                builder.AppendLine();
                builder.Append(StatusText.AboutLicenseText);

                return builder.ToString();
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

            /*
             * 工作区根（用户 2026-09-24 拍板：**默认跟着输出盘**走）。
             *
             * 三档，与 WorkspaceRootResolver 同一口径：
             * ① 用户显式设过缓存根目录 → <它>\work（老行为，一个字都不改他的选择）；
             * ② 留空 → 用**账本里记住的那个根**（上一批跟着输出盘定下来的那个，见 WorkspaceRootIndex），
             *    没有记录时才是 <程序目录>\data\work（老位置＝拿不到盘时的回落档）；
             * ③ 真正"跟着输出盘"的解析发生在**每批开工前**
             *    （ExtractionCoordinator.ApplyBatchWorkspaceRoot）—— 只有那时才知道这一批会处理哪些包、
             *    落点在哪块盘。启动 / 保存设置时根本还没有这一批的任务，所以这里只能是"上一批的根"。
             *
             * ⚠ 递归核心的工作区根是进程级静态（它自己会再挂一层 "recursive"），必须跟着一起换 ——
             * 不换的话递归那几百 MB 又会回到程序盘（那正是这一条要改掉的老行为）。
             */
            _pathService.WorkDirectory = ResolveStartupWorkspaceRoot(cacheRoot);
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
        /// 启动 / 保存设置时那个"当前生效的工作区根"（详细口径见 <see cref="ApplyEngineSettings"/> 里的注释）。
        ///
        /// <para>它**不是**这一批真正会用的根 —— 那个要等批首按落点盘算（见
        /// <c>ExtractionCoordinator.ApplyBatchWorkspaceRoot</c>）。这里取的是"上一批留下的那个"
        /// （账本 <see cref="WorkspaceRootIndex"/>），这样 ③ 页与启动日志在**重启之后**
        /// 照样列得出上次那批的残留（默认跟输出盘之后根会随盘变，不记住就等于漏报）。</para>
        /// </summary>
        private string ResolveStartupWorkspaceRoot(string cacheRoot)
        {
            // ① 用户显式设过缓存根目录：以它为准（老行为）。
            if (!string.IsNullOrWhiteSpace(cacheRoot))
            {
                return Path.Combine(
                    _pathService.DataRootDirectory,
                    WorkspaceRootResolver.ConfiguredCacheWorkspaceSubDirectoryName);
            }

            // ② 留空：先看账本里记住的那个根，再退老位置 <数据根>\work。
            foreach (string remembered in WorkspaceRootIndex.Load(_pathService.DataRootDirectory))
            {
                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    return remembered;
                }
            }

            return Path.Combine(
                _pathService.DataRootDirectory,
                WorkspaceRootResolver.ConfiguredCacheWorkspaceSubDirectoryName);
        }

        /// <summary>
        /// ③「清理与删除」页与启动日志**实际要扫的**工作区根（**只给测试与排障读**）。
        ///
        /// <para>产品路径只用 <see cref="LogLeftoverWorkspaces"/> 与 <see cref="ClearWorkspaceLeftovers"/>；
        /// 把这个清单露出来是因为"扫了哪些根"决定了界面上报几个残留、以及清理会动哪些目录 ——
        /// 那是必须能被钉住的一条事实（默认跟输出盘之后根会变，多一个根就多一批会被删的目录）。</para>
        /// </summary>
        internal IReadOnlyList<string> WorkspaceScanRoots => ResolveWorkspaceScanRoots();

        /// <summary>
        /// ③「清理与删除」页 / 启动日志要扫的**所有**工作区根（去重、当前生效的排最前）。
        ///
        /// <para>三部分：① 当前生效的根；② 老位置 <c>&lt;数据根&gt;\work</c>（升级前那批残留还在那儿，
        /// 用户实测攒过 5.7 GB）；③ 账本里用过的根（换了输出盘之后旧盘上的残留也要列得出来）。
        /// 只扫其中一个是**不够**的 —— 那正是"东西明明在，界面却说没有"的来源。</para>
        /// </summary>
        private IReadOnlyList<string> ResolveWorkspaceScanRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string? root)
            {
                if (!string.IsNullOrWhiteSpace(root) && seen.Add(root.Trim()))
                {
                    roots.Add(root.Trim());
                }
            }

            TryAdd(_pathService.WorkDirectory);

            TryAdd(Path.Combine(
                _pathService.DataRootDirectory,
                WorkspaceRootResolver.ConfiguredCacheWorkspaceSubDirectoryName));

            foreach (string remembered in WorkspaceRootIndex.Load(_pathService.DataRootDirectory))
            {
                TryAdd(remembered);
            }

            return roots;
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

            IReadOnlyList<string> books = _passwordService.RememberedBookPaths;

            if (count == 0)
            {
                PasswordBookSummary = "密码本：未加载（0 条）——点「密码列表管理」或在④「密码」页点「导入密码本…」";
                PasswordBookTooltip = "空密码仍会按设置尝试；密码列表按本机加密保存（可在设置里关掉），日志不记明文。";

                AppendRememberedListWarningToSummary();
                return;
            }

            string name;

            if (books.Count > 1)
            {
                // 多本密码本（用户 2026-09-24 要求）：主界面这一行只报本数与总条数，明细在窗口里看。
                name = $"{books.Count} 本密码本";
            }
            else if (books.Count == 1)
            {
                name = System.IO.Path.GetFileName(books[0]);
            }
            else
            {
                name = string.IsNullOrWhiteSpace(book) ? "（手动添加）" : System.IO.Path.GetFileName(book);
            }

            PasswordBookSummary = $"密码本：{name} — {count} 条（启用 {enabled} 条）";
            PasswordBookTooltip = BuildPasswordBookTooltip(book, books);

            AppendRememberedListWarningToSummary();
        }

        /// <summary>
        /// 记忆读不出来时，在摘要那一行后面挂一句**短**提示（明细在密码列表窗口的提示条上）。
        ///
        /// <para>为什么要挂：解不开时列表是空的，主界面会显示"未加载（0 条）"——
        /// 用户很容易理解成"程序把我密码弄丢了"。这里必须说清"是读不出来、已忽略、文件没被动"。</para>
        /// </summary>
        private void AppendRememberedListWarningToSummary()
        {
            if (_passwordService.LastListWarning.Length == 0)
            {
                return;
            }

            PasswordBookSummary += "　⚠ " + StatusText.PasswordListMemoryUnavailableShort;

            PasswordBookTooltip = PasswordBookTooltip.Length == 0
                ? _passwordService.LastListWarning
                : PasswordBookTooltip + Environment.NewLine + Environment.NewLine + _passwordService.LastListWarning;
        }

        /// <summary>
        /// 摘要那一行的 ToolTip：**可以写完整路径**（界面是给用户自己核对的），
        /// 但它只显示、绝不进日志（§8：个人路径不入日志）。
        /// </summary>
        private static string BuildPasswordBookTooltip(string lastImported, IReadOnlyList<string> books)
        {
            if (books.Count == 0)
            {
                return string.IsNullOrWhiteSpace(lastImported)
                    ? "列表按本机加密保存（可在设置里关掉）；本文件未记录来源路径。"
                    : lastImported;
            }

            var builder = new System.Text.StringBuilder();
            builder.Append("已记住的密码本（启动时按这个顺序逐本合并）：");

            foreach (string path in books)
            {
                builder.AppendLine();
                builder.Append(path);
            }

            return builder.ToString();
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
         * 为什么需要它：设置里的「最大并发解压数」是个节流旋钮（默认 4；
         * 2026-09-22 的并行实测把默认值从 1 提到 4 —— 并发 1→4 是 3.08×，4→8 只再快 1.9%
         * 却把单包耗时拉长一倍，根因是 6 物理核超订）。
         * 用户临时想让这一批快点跑完时，以前只能进设置改数字、再回来重跑 ——
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
                    NotifyOutputPlacementChangedEverywhere();
                }
            }
        }

        // ================================================================ ①页「输出位置」那一格（用户 2026-09-25 第 27 条）

        /// <summary>
        /// ①「任务」页主操作条里那一格显示的文本（用户原话："输出的指定位置可以放在主界面进行选择……
        /// 在添加文件夹和全选中间还有那么多的位置……反正我要求的就是我们最好能够看到完整的解压地址"）。
        ///
        /// <para><b>三态，每一种都要说清"东西会落在哪"</b>：</para>
        /// <list type="number">
        /// <item><description>未指定位置（<c>ExtractToOriginalDirectory</c>）→ 明说产物落在每个包自己的目录；</description></item>
        /// <item><description>指定了位置 → 路径**中间省略**（前三段 + <c>\...\</c> + 末两段），完整路径在 ToolTip 与右键菜单里；</description></item>
        /// <item><description>刚取消勾选还没挑目录 → 明说下一步点「选择…」，绝不显示成"已经指定好了"。</description></item>
        /// </list>
        ///
        /// <para>⚠ 它读的**就是**②「解压方式」页那两个值（<see cref="SelectedOutputDirectory"/> +
        /// <c>Settings.ExtractToOriginalDirectory</c>），不新开字段、不各存一份：
        /// 两处改哪一处，另一处与这一行立刻跟着变（SettingsEditor 的属性变更会回来通知这里）。</para>
        /// </summary>
        public string OutputLocationDisplay
        {
            get
            {
                if (Settings == null || Settings.ExtractToOriginalDirectory)
                {
                    return StatusText.OutputLocationUnspecifiedText;
                }

                if (string.IsNullOrWhiteSpace(SelectedOutputDirectory))
                {
                    return StatusText.OutputLocationNotChosenText;
                }

                return PathMiddleEllipsis.Elide(SelectedOutputDirectory);
            }
        }

        /// <summary>完整路径的 ToolTip（界面上那一行是省略过的，这里给全文）。</summary>
        public string OutputLocationToolTip
        {
            get
            {
                if (Settings == null || Settings.ExtractToOriginalDirectory)
                {
                    return StatusText.OutputLocationFollowsArchiveHint;
                }

                return string.IsNullOrWhiteSpace(SelectedOutputDirectory)
                    ? StatusText.OutputLocationNotChosenText
                    : string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.OutputLocationToolTipFormat,
                        SelectedOutputDirectory);
            }
        }

        /// <summary>
        /// ①页那个「未指定位置」开关：勾上 = 产物落在每个包自己所在的目录。
        ///
        /// <para>它就是 <c>Settings.ExtractToOriginalDirectory</c>（两档落点的**唯一判据**，
        /// 见 <c>SettingsViewModel.ResolveOutputPlacement</c>）—— 与②页的单选按钮是同一个值，
        /// 不新开字段。写的时候顺手招呼一下 <see cref="SettingsEditor"/>：②页那几个控件绑的是
        /// 它算出来的属性（<c>OutputPlacement</c> / <c>IsCustomOutputEnabled</c>），
        /// 不通知的话会出现"①页勾了、②页还显示老样子"。</para>
        /// </summary>
        public bool OutputLocationFollowsArchive
        {
            get => Settings?.ExtractToOriginalDirectory ?? true;
            set
            {
                if (Settings == null || Settings.ExtractToOriginalDirectory == value)
                {
                    return;
                }

                Settings.ExtractToOriginalDirectory = value;
                Settings.KeepArchiveNameFolder = true;

                NotifyOutputPlacementChangedEverywhere();
                OnPropertyChanged(nameof(Settings));
                RefreshOutputPaths();

                AppendLog(
                    "INFO",
                    value
                        ? StatusText.OutputLocationSwitchedToOriginalLog
                        : StatusText.OutputLocationSwitchedToCustomLog);
            }
        }

        /// <summary>
        /// 落点变了 → **两页一起**刷新（①页那一行 + ②页「落点（解压到哪）」那几个控件）。
        ///
        /// <para><b>为什么必须有这一个出口</b>（2026-09-25 第 31 条，用户真机报的"两处还是不同步"）：
        /// <see cref="AppSettings"/> 是普通 POCO（不发通知），而②页的控件绑的是
        /// <see cref="SettingsViewModel"/> 算出来的属性（<c>OutputPlacement</c> / <c>IsCustomOutputEnabled</c> /
        /// <c>CustomOutputDirectory</c> / <c>OutputPlacementSummary</c>）。
        /// ①页的「选择…」以前只改设置、**一个通知都不发** —— 值是同一份（两页读同一个对象，所以只看值的测试全绿），
        /// 可②页那几个**界面元素不会重新求值**：用户去②页看到的是"①页已经选了目录，落点还写着『同名子文件夹』，
        /// 路径框灰着写『（不适用）』" —— 正是他说的"很意外"。</para>
        ///
        /// <para>⛔ 凡是写 <c>ExtractToOriginalDirectory</c> / <c>CustomOutputDirectory</c> 的地方，
        /// 收尾都必须走这里（①页选择、①页开关、确认框"保存为默认"、②页各处）。</para>
        /// </summary>
        private void NotifyOutputPlacementChangedEverywhere()
        {
            // ②页：让那几个控件按新值重新求值。
            SettingsEditor?.NotifyOutputPlacementChanged();

            // ①页：那一行文本 / ToolTip / 开关。
            RaiseOutputLocationChanged();
        }

        /// <summary>
        /// 输出位置相关的那几个展示属性一起通知（一处写、三处显示同一份事实）。
        /// </summary>
        private void RaiseOutputLocationChanged()
        {
            OnPropertyChanged(nameof(OutputLocationDisplay));
            OnPropertyChanged(nameof(OutputLocationToolTip));
            OnPropertyChanged(nameof(OutputLocationFollowsArchive));
        }

        /// <summary>
        /// ②页改了落点之后，把①页那一行拉回同一个真值。
        ///
        /// <para>为什么必须显式同步：<see cref="AppSettings"/> 是普通 POCO（不实现 INotifyPropertyChanged），
        /// 而②页的「选择」是直接写 <c>Settings.CustomOutputDirectory</c> 的（走 SettingsViewModel），
        /// 不会经过 <see cref="SelectedOutputDirectory"/> 的 setter —— 不同步的话
        /// ①页那一行会一直显示旧路径（用户会以为"改了没反应"）。
        /// 这里只搬运、不回写设置，所以不可能形成两步循环。</para>
        /// </summary>
        private void SyncOutputLocationFromSettings()
        {
            string folder = Settings?.CustomOutputDirectory ?? string.Empty;

            if (!string.Equals(_selectedOutputDirectory, folder, StringComparison.Ordinal))
            {
                _selectedOutputDirectory = folder;
                OnPropertyChanged(nameof(SelectedOutputDirectory));
                RefreshOutputPaths();
            }

            RaiseOutputLocationChanged();
        }

        /// <summary>
        /// 设置编辑器（②③④⑥四页）里落点相关的属性变了 → 把①页那一行拉回来。
        ///
        /// <para>只认这三个名字（<c>OutputPlacement</c> / <c>CustomOutputDirectory</c> / <c>Settings</c>），
        /// 别的属性一个都不碰 —— 设置页每敲一个字符都会发通知，整片刷新会把界面拖卡。</para>
        /// </summary>
        private void OnSettingsEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(SettingsViewModel.OutputPlacement), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(SettingsViewModel.CustomOutputDirectory), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(SettingsViewModel.Settings), StringComparison.Ordinal))
            {
                SyncOutputLocationFromSettings();
            }
        }

        /// <summary>①页右键菜单那一项：把**完整**路径复制走（界面上显示的是中间省略过的）。</summary>
        private void CopyOutputLocation()
        {
            if (Settings == null || Settings.ExtractToOriginalDirectory ||
                string.IsNullOrWhiteSpace(SelectedOutputDirectory))
            {
                AppendLog("INFO", StatusText.OutputLocationCopyNothingLog);
                return;
            }

            if (CopySanitizedToClipboard(SelectedOutputDirectory))
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.OutputLocationCopyLogFormat,
                        SelectedOutputDirectory));
            }
        }

        // ================================================================ 空间规划 + 其余物处理

        /// <summary>
        /// "按当前可用空间，最多能并行几个"那句话（用户 2026-09-22 需求第 2 条：界面上算出并显示）。
        ///
        /// <para>数字来自 <see cref="ExtractionCoordinator.BuildSpaceAdvice"/> —— 与真正开跑时的调度
        /// 同一段实现，所以界面上显示的建议与日志里那一刻的实际口径永远不会分叉。</para>
        /// </summary>
        public string ParallelAdviceText
        {
            get => _parallelAdviceText;
            private set => SetProperty(ref _parallelAdviceText, value ?? string.Empty);
        }

        /// <summary>
        /// 并发档位（用户 2026-09-22 需求：可选 1 / 2 / 3 / 4 / 8）。
        ///
        /// <para>它是设置项 <see cref="AppSettings.MaxParallelExtractCount"/> 的界面形态：
        /// 读时夹到合法档位（设置里手改成 7 也能正常显示），写时落盘 —— 与其他设置项同一口径，
        /// 不藏在内存里（用户上次选的档位下次启动还在）。</para>
        /// </summary>
        public int MaxParallelChoice
        {
            get => ExtractionScheduler.NormalizeParallelCount(Settings?.MaxParallelExtractCount ?? 1);
            set
            {
                int normalized = ExtractionScheduler.NormalizeParallelCount(value);

                if (Settings == null || Settings.MaxParallelExtractCount == normalized)
                {
                    return;
                }

                Settings.MaxParallelExtractCount = normalized;

                try
                {
                    _settingsService.Save(Settings);
                }
                catch (Exception ex)
                {
                    AppendLog("WARN", $"保存并发档位失败（本次仍生效）：{ex.Message}");
                }

                OnPropertyChanged();
                AppendLog(
                    "INFO",
                    $"最大并发解压数已改成 {normalized}。"
                    + "空间不够时程序仍会在启动前拦下装不下的任务（见「按空间算建议」）。");

                // 档位一动，"自测凭证还盖不盖得住"就可能翻转（4 → 8 直接失效）→ 立刻刷新界面那句话。
                RefreshSpaceModeText();
            }
        }

        /// <summary>并发档位的候选（界面的下拉框直接绑它；1 / 2 / 3 / 4 / 8）。</summary>
        public IReadOnlyList<int> MaxParallelChoices => ExtractionScheduler.AllowedParallelCounts;

        /// <summary>当前档位的一句话说明（源包怎么处理 + 其余物怎么处理），显示在并发那一组旁边。</summary>
        public string SpaceModeText
        {
            get => _spaceModeText;
            private set => SetProperty(ref _spaceModeText, value ?? string.Empty);
        }

        /// <summary>
        /// 算一次并行建议（后台 stat 每个源包，界面不卡）。
        /// 没勾任务时给出提示而不是算一个空结论。
        /// </summary>
        private async Task RefreshParallelAdviceAsync()
        {
            List<ArchiveTask> selected = Tasks.Where(task => task.IsSelected).ToList();

            if (selected.Count == 0)
            {
                ParallelAdviceText = Tasks.Count == 0
                    ? "任务列表是空的：先添加文件，再算并行建议"
                    : "一个任务都没勾：先勾选要处理的任务，再算并行建议";

                AppendLog("WARN", ParallelAdviceText);
                return;
            }

            try
            {
                ExtractionSchedulePlan plan = await Task.Run(
                    () => _extractionCoordinator.BuildSpaceAdvice(selected));

                /*
                 * 建议里同时给出**两个约束**（用户 2026-09-22 追加：并行实测的结论）：
                 * ① 空间侧："当前可用 X，建议并行 N 个"（来自调度器，与真正开跑同一段实现）；
                 * ② 硬件侧："建议不超过物理核心数（当前检测到 N 核）"——
                 *    实测并发 1→4 是 3.08×，4→8 只再快 1.9% 却把单包耗时拉长一倍（6 物理核超订）。
                 * 两个都是**建议**：程序不会因此悄悄改掉用户选的档位，装不下的任务照旧在启动前被拦下。
                 */
                ParallelAdviceText = plan.ParallelAdviceText() + "；" + ProcessorTopology.DescribeConcurrencyAdvice();

                AppendLog("INFO", $"并行建议（按空间）：{ParallelAdviceText}");
                AppendLog("INFO", "依据：" + plan.Basis);

                foreach (ScheduledExtractionItem blocked in plan.BlockedAtPlanTime)
                {
                    AppendLog(
                        "WARN",
                        $"按当前可用空间，这个任务排不上：{blocked.Estimate.DisplayName} —— "
                        + $"需要 {TaskSpaceEstimate.FormatSize(blocked.RequiredBytes)}，"
                        + $"差 {TaskSpaceEstimate.FormatSize(blocked.ShortfallBytes)}");
                }
            }
            catch (Exception ex)
            {
                ParallelAdviceText = "算并行建议失败：" + ex.Message;
                AppendLog("ERROR", "算并行建议失败：" + ex.Message);
            }
        }

        /// <summary>把"当前档位"那句话刷新一遍（界面绑定它）。</summary>
        internal void RefreshSpaceModeText()
        {
            if (Settings == null)
            {
                return;
            }

            // 这一句现在只说"源包 + 其余物"两档（2026-09-25 第 32 条：危险模式与自测凭证整块退役）。
            SpaceModeText = _extractionCoordinator?.DescribeSpaceMode() ?? string.Empty;

            ParallelAdviceText = "点「按空间算建议」可以算出当前可用空间能并行几个";
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

        /// <summary>
        /// 「文件 → 追加到列表 → 追加文件…」（用户 2026-09-24 第 12 条补拍）。
        ///
        /// <para>语义 = **旧行为**（保留现有任务，只把新选的加到末尾）。它有意做成菜单里的一条
        /// **显式**入口：默认的「添加」是替换，想追加就必须自己说出来 ——
        /// 用户原话是"每次我新选择了其他的，无论是什么，你都要将列表彻底清空"，
        /// 所以"追加"只能是显式动作，不能是默认动作。</para>
        /// </summary>
        public ICommand AppendFilesCommand { get; }

        /// <summary>「文件 → 追加到列表 → 追加文件夹…」。</summary>
        public ICommand AppendFolderCommand { get; }

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

        /// <summary>
        /// 底栏的「保存设置」：校验 → 归一化 → 落盘 → 把引擎相关设置推给引擎层。
        ///
        /// <para>选项卡上的设置页改的是**共享的那一份设置对象**，所以在按下这个按钮之前，
        /// 改动只在本次运行内有效（界面上写着这句话）；按下之后才写进 appsettings.json。
        /// 校验不过（工具路径不存在 / 危险模式没凭证）就停在原地并把改法说清 —— 与设置窗口同一份实现。</para>
        /// </summary>
        public ICommand SaveSettingsCommand { get; }

        /// <summary>
        /// 打包入口（**⑤「打包」选项卡**，整页在那儿；用户 2026-09-22 需求第 10 条）。
        /// 打包是独立的一条流水线（7z 分卷 → 外层加密 rar），不依赖任务列表，所以没有勾选之类的前置条件。
        ///
        /// <para>2026-09-24 第 11 条之后它不再打开窗口，只负责把界面切到⑤页
        /// （原来的打包窗口已退休，界面整页搬进了选项卡）。</para>
        /// </summary>
        public ICommand OpenPackingCommand { get; }

        /// <summary>③页那句"其余物现状"旁边的按钮：跳到②页底部红区（危险模式）。</summary>


        /// <summary>导入密码本（记住路径，下次启动自动加载）。用户已多次要求：导入一次就够。</summary>
        public ICommand ImportPasswordBookCommand { get; }
        public ICommand ExportLogCommand { get; }

        /// <summary>导出**全部历史日志**（日志目录里每一份；用户 2026-09-25 第 38 条降级成显式入口）。</summary>
        public ICommand ExportAllLogsCommand { get; }
        public ICommand CopyFailedListCommand { get; }

        /// <summary>把失败清单导出成 txt（M3：能说清每个失败为什么失败，且能带走）。</summary>
        public ICommand ExportFailedListCommand { get; }
        public ICommand OpenOutputDirectoryCommand { get; }
        public ICommand SelectOutputDirectoryCommand { get; }

        /// <summary>
        /// ①页「输出位置」那一格右键菜单的「复制完整路径」（用户 2026-09-25 第 27 条）。
        ///
        /// <para>为什么要有它：那一行显示的是**中间省略**过的路径（"我们最好能够看到完整的解压地址"，
        /// 但一行放不下 200 个字符），要拿去粘贴的人得能拿到全文 —— ToolTip 能看，这个能复制。</para>
        /// </summary>
        public ICommand CopyOutputLocationCommand { get; }
        public ICommand OpenLogDirectoryCommand { get; }

        /// <summary>打开递归解压的工作区目录（中断后残留的中间产物在这里）。</summary>
        public ICommand OpenWorkDirectoryCommand { get; }

        /// <summary>
        /// 清理工作区残留（用户 2026-09-24 第 22 条）：列出每个残留的名字 / 体积，**确认之后**才删。
        ///
        /// <para>为什么值得单开一条：残留是刻意保留的（失败 / 取消时那是用户唯一的一份产物线索），
        /// 但以前只有一行日志、没有入口，用户实测攒到 5.7 GB 才发现。</para>
        /// </summary>
        public ICommand ClearWorkspaceLeftoversCommand { get; }

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

        /// <summary>
        /// 移除**勾选的**任务（用户 2026-09-24 第 15 条："列表要能删无用物"）。
        /// 只动列表，源文件不动；移除错了再添加一次就回来，所以不做二次确认。
        /// </summary>
        public ICommand RemoveCheckedTasksCommand { get; }
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

        /// <summary>
        /// 「继续解」：上一批撞到 10 层硬上限时，接着把剩下那些内层包解下去（用户 2026-09-24 第 16 条追加）。
        ///
        /// <para>它复用的就是一键处理本身（<see cref="OneClickCoordinator.RunAsync"/>）——
        /// 剩下的内层包在上一批结束时已经加进列表并勾好，所以"继续解"不需要另一条流水线。</para>
        /// </summary>
        public ICommand ContinueOneClickCommand { get; }

        public ICommand ResetSettingsCommand { get; }

        /// <summary>
        /// 按当前可用空间算一次"建议并行几个"（用户 2026-09-22 需求第 2 条）。
        /// 结果落在 <see cref="ParallelAdviceText"/> 上 —— 界面直接显示那句话。
        /// </summary>
        public ICommand RefreshParallelAdviceCommand { get; }

        /// <summary>
        /// **危险模式**入口（红色按钮）：确认 → 自测（并发数 × 2 个文件）→ 通过才开启。
        /// 已经开着时点它 = 关闭（关闭是安全方向，不需要二次确认）。
        /// </summary>


        /*
         * ============================ 勾选的批量操作（用户 2026-09-22 追加需求） ============================
         *
         * 用户原话："识别了文件夹，所有的文件都会选中，但是我们要去解压其中的一个就得一个个点，
         * 这种冗余操作违背了我的初心……就可以像文件夹选中一样，有一个全选的选项和点击一次就可以成功的选项。"
         *
         * 三个命令的分工（都在**勾选框**上动作，不碰 DataGrid 的行高亮 —— 那两套"选中"的口径
         * 由 Models/StatusText.SelectionScopeHint 统一声明）：
         * · 全选 —— 整个列表都参与处理（最常用：导入后先全不选，再挑一个）；
         * · 全不选 —— "从一堆已勾选里只留一个"的第一步，也是用户点名要的那个键；
         * · 反选 —— 挑三五个时比逐个点快。
         *
         * ⚠ 全不选**不动"当前点中的那一行"**（行高亮留着给右键菜单用），但命令型入口
         * **一律只认勾选**：一个都没勾就什么都不做、只提示（用户 2026-09-24 第 12 条）——
         * ⛔ 所以这里**不存在**"清掉行高亮会让清理类命令失去兜底对象"这种顾虑。
         */

        /// <summary>勾选整个列表（Ctrl+A）。</summary>
        public ICommand SelectAllTasksCommand { get; }

        /// <summary>取消全部勾选（Ctrl+D）。</summary>
        public ICommand SelectNoneTasksCommand { get; }

        /// <summary>反选（Ctrl+I）。</summary>
        public ICommand InvertTaskSelectionCommand { get; }

        /// <summary>只勾选指定的那一个（右键菜单「只勾选这一个」），其余全部取消勾选。</summary>
        public ICommand SelectSoleTaskCommand { get; }

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
             * 设置编辑器（选项卡②③④⑥绑它）与打包编辑器（⑤绑它）。
             *
             * ⚠ AttachSharedSettings 是**必须**的：SettingsViewModel 的构造函数会克隆一份设置
             * （设置窗口的「取消」靠它回退），而选项卡上同一项可能在多处出现
             * （并发档既在②的输入框里、又决定危险模式凭证的覆盖判定），
             * 两份值一定会打架 —— 共享同一个对象才不会有"界面显示 A、保存写回 B"。
             */
            _settingsEditor = new SettingsViewModel(_settings, _settingsService);
            _settingsEditor.AttachSharedSettings(_settings);

            PackingEditor = new PackingViewModel { LogSink = line => AppendLog("INFO", line) };

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
            AppendFilesCommand = new AsyncRelayCommand(_scanCoordinator.AppendFilesAsync, CanRunNormalCommand);
            AppendFolderCommand = new AsyncRelayCommand(_scanCoordinator.AppendFolderAsync, CanRunNormalCommand);
            ScanCommand = new AsyncRelayCommand(_scanCoordinator.ScanTasksAsync, CanRunNormalCommand);
            ClearCommand = new RelayCommand(ClearTasks, CanRunNormalCommand);
            RemoveSelectedCommand = new RelayCommand(RemoveSelectedTasks, CanRunNormalCommand);

            SmartRenameCommand = new AsyncRelayCommand(_renameCoordinator.SmartRenameAsync, CanRunNormalCommand);
            AddExtensionCommand = new AsyncRelayCommand(_renameCoordinator.AddExtensionAsync, CanRunNormalCommand);
            ReplaceExtensionCommand = new AsyncRelayCommand(_renameCoordinator.ReplaceExtensionAsync, CanRunNormalCommand);
            DeleteLastExtensionCommand = new AsyncRelayCommand(_renameCoordinator.DeleteLastExtensionAsync, CanRunNormalCommand);
            DeleteMultipleExtensionsCommand = new AsyncRelayCommand(_renameCoordinator.DeleteMultipleExtensionsAsync, CanRunNormalCommand);

            /*
             * 四个"开始干活"的入口都打一个操作标记（用户 2026-09-25 第 38 条）：
             * 日志里会出现一行 `======== 本次操作开始：<名字> ========`，
             * 「导出日志」默认就从那一行开始导 —— 用户拿到的是**最近一次操作**的日志，不是一堆历史。
             */
            OneClickProcessCommand = new AsyncRelayCommand(
                async () =>
                {
                    _logService.MarkOperationStart("一键处理");
                    await _oneClickCoordinator.RunAsync().ConfigureAwait(true);
                },
                CanRunNormalCommand);

            /*
             * 「继续解」（第 16 条追加）：只在"上一批撞到层数上限、还剩内层包没解"时可点。
             * 与一键处理同一道忙碌守卫（CanRunNormalCommand）——两者不能同时跑。
             */
            ContinueOneClickCommand = new AsyncRelayCommand(
                async () =>
                {
                    _logService.MarkOperationStart("继续解");
                    await _oneClickCoordinator.RunAsync().ConfigureAwait(true);
                },
                CanContinueOneClick);

            StartExtractCommand = new AsyncRelayCommand(
                async () =>
                {
                    _logService.MarkOperationStart("解压");
                    await _extractionCoordinator.StartExtractAsync().ConfigureAwait(true);
                },
                CanStartExtract);
            StopCommand = new RelayCommand(_extractionCoordinator.StopAfterCurrent, () => IsBusy);
            CancelCurrentCommand = new RelayCommand(_extractionCoordinator.CancelCurrentTask, () => IsBusy);

            OpenSettingsCommand = new RelayCommand(OpenSettings, CanRunNormalCommand);
            OpenPasswordListCommand = new RelayCommand(OpenPasswordList, CanRunNormalCommand);
            OpenPackingCommand = new RelayCommand(OpenPacking, CanRunNormalCommand);

            SaveSettingsCommand = new RelayCommand(SaveSettings, CanRunNormalCommand);
            ImportPasswordBookCommand = new RelayCommand(ImportPasswordBook, CanRunNormalCommand);
            ExportLogCommand = new RelayCommand(ExportLog);
            ExportAllLogsCommand = new RelayCommand(ExportAllLogs);
            CopyFailedListCommand = new RelayCommand(CopyFailedList);
            ExportFailedListCommand = new RelayCommand(ExportFailedList);
            OpenOutputDirectoryCommand = new RelayCommand(OpenOutputDirectory);
            SelectOutputDirectoryCommand = new RelayCommand(SelectOutputDirectory, CanRunNormalCommand);
            CopyOutputLocationCommand = new RelayCommand(CopyOutputLocation, CanRunNormalCommand);

            /*
             * ②③④⑥四页共用的设置编辑器改了落点之后，把①页「输出位置」那一行拉回同一个真值。
             *
             * 为什么必须挂这条线：AppSettings 是普通 POCO（不发通知），而②页的「选择 / 单选」
             * 走的是 SettingsViewModel（直接写 Settings.CustomOutputDirectory / ExtractToOriginalDirectory），
             * 不经过 MainViewModel.SelectedOutputDirectory 的 setter —— 不挂线的话，
             * 用户在②页改完回到①页会看到旧路径（"改了没反应"那类 bug 的典型形态）。
             * 只在这些属性上搬运，别的属性一律不碰（避免 SaveSettings 之后的整片刷新）。
             */
            _settingsEditor.PropertyChanged += OnSettingsEditorPropertyChanged;
            OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
            OpenWorkDirectoryCommand = new RelayCommand(OpenWorkDirectory);

            /*
             * 工作区清理：只在"确实有残留"时可点（CanExecute 读扫描出来的那个布尔）。
             * 扫描发生在启动与每次批末（LogLeftoverWorkspaces），所以按钮的可用性跟着真实情况走。
             */
            ClearWorkspaceLeftoversCommand = new RelayCommand(ClearWorkspaceLeftovers, () => HasWorkspaceLeftovers);

            // 其余物删除入口（③「清理与删除」页）：预览 → 确认 → 后台执行，全程不阻塞界面。
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

            // 「移除勾选的」：没有勾选就没有可移除的（CanExecute 跟着选择状态走）。
            RemoveCheckedTasksCommand = new RelayCommand(
                RemoveCheckedTasks,
                () => !IsBusy && Tasks.Any(task => task.IsSelected));
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

            /*
             * ===== 空间规划（用户 2026-09-22 需求）=====
             *
             * 一个入口：RefreshParallelAdviceCommand —— 按当前可用空间算出"建议并行几个"
             * （纯计算 + stat，放后台）。
             * ⛔ 原来这里还有一个红色按钮命令（危险模式 + 自测 + 凭证）：那整套已按
             * 用户 2026-09-25 第 32 条整块退役 —— 那条需求现在是③页「2 删除操作」的第三档。
             */
            RefreshParallelAdviceCommand = new AsyncRelayCommand(RefreshParallelAdviceAsync, CanRunNormalCommand);


            SelectAllTasksCommand = new RelayCommand(SelectAllTasks, CanChangeTaskSelection);
            SelectNoneTasksCommand = new RelayCommand(SelectNoneTasks, CanChangeTaskSelection);
            InvertTaskSelectionCommand = new RelayCommand(InvertTaskSelection, CanChangeTaskSelection);
            SelectSoleTaskCommand = new RelayCommand(SelectSoleTask);

            HookTaskSelectionTracking();

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

            /*
             * "一个引擎都用不了"的提示必须现算，**不许写死 7z 的路径**。
             *
             * 判据是"任一引擎可用"（EngineRouter.IsAvailable），而默认优先级是 UnRAR → 7-Zip：
             * 旧文案写死"未找到 tools\7zip\7z.exe"，于是真正缺 UnRAR 的人被引去修一个本来没问题的
             * 7z 目录，自己配了外部 7z 的人更是被指向一个他根本没在用的路径。
             * 现在由 ToolLocator 现算两条期望路径 + "任装其一即可" + 当前优先级（路径只有它一个来源）。
             */
            if (!_archiveEngine.IsAvailable)
            {
                AppendLog("WARN", "软件可启动，但解压时会失败。" + ToolLocator.Default.DescribeNoEngineAvailable());
            }

            if (!ToolLocator.Default.SevenZipDllExists)
            {
                // 这一条判定的就是 7z.dll，所以路径要写**真实的那一个**（不再拼一个可能过期的字面量）。
                AppendLog(
                    "WARN",
                    $"未找到 {ToolLocator.Default.SevenZipDllPath}（7-Zip 命令行解压需要它），请确认 7-Zip 的文件完整；" +
                    "只解 RAR 的话，装一个 UnRAR 也能用。");
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
        ///
        /// <para>拖放一批新文件 = 一次"新的选择"，所以默认与「添加文件 / 添加文件夹」同为
        /// <b>替换</b>语义（用户 2026-09-24 第 12 条："每次我新选择了其他的，无论是什么，
        /// 你都要将列表彻底清空"）。要往现有列表里加，走菜单「文件 → 追加到列表」
        /// （<c>ScanCoordinator.AppendFilesAsync / AppendFolderAsync</c>）。</para>
        /// </summary>
        public Task AddPathsAsync(
            IEnumerable<string> paths,
            ImportMode mode = ImportMode.Replace)
        {
            return _scanCoordinator.AddPathsAsync(paths, mode);
        }

        /// <summary>
        /// 启动时报告上次没做完的工作区。
        ///
        /// 递归解压被中断或取消时，产物会留在工作区（刻意不发布、不清理，见不变量 12）。
        /// 启动时不提醒一句，用户永远不会知道这些东西还在占磁盘。
        /// 这里只报告，**不自动删**：删工作区必须先经用户确认（不变量 13）。
        /// </summary>
        /// <summary>
        /// 启动时自动加载密码本。
        /// 用户反复强调过：导入一次就该一直有效，不该每次重导。
        /// 文件不在了只写一条 WARN，不弹窗打扰。
        ///
        /// <para><b>2026-09-24 起顺序改了（用户真机反馈）</b>：以前是"直接从密码本 txt 重建列表"，
        /// 于是手工加的条目与手工调的顺序重启即丢，而且导入第二本会把"自动加载哪一本"换成第二本
        /// （第一本的条目全不见）。现在的顺序是：</para>
        /// <list type="number">
        /// <item><description><b>先加载记忆</b>（列表内容 / 顺序 / 启用状态 / 手工条目 / 墓碑 / 记住的密码本）；</description></item>
        /// <item><description><b>再按记住的顺序逐本合并</b>密码本：只补"列表里还没有的值"，跳过墓碑。</description></item>
        /// </list>
        /// <para>日志只写条数与文件名（§8：个人路径不入日志；明文任何情况下都不写）。</para>
        /// </summary>
        internal void AutoLoadPasswordBook()
        {
            try
            {
                /*
                 * 开关随设置走（默认开）。关掉 = 既不写也不读记忆文件 ——
                 * 用户明确要求"关掉后重启回到纯内存"，所以这里连读都不读，不能"关了还偷偷读一下"。
                 */
                _passwordService.RememberPasswordList = Settings?.RememberPasswordList ?? true;

                if (!_rememberedListLoaded)
                {
                    _rememberedListLoaded = true;
                    LoadRememberedPasswordListAtStartup();
                }

                // 本次要按顺序合并的书：服务里那份（记忆 + 设置 + 导入共同维护）。
                IReadOnlyList<string> books = _passwordService.RememberedBookPaths;

                var mergedNames = new List<string>();
                int addedTotal = 0;

                foreach (string book in books)
                {
                    if (!File.Exists(book))
                    {
                        // §8 隐私红线：日志只写文件名，个人路径不入日志。
                        AppendLog(
                            "WARN",
                            string.Format(
                                System.Globalization.CultureInfo.CurrentCulture,
                                StatusText.PasswordListMemoryBookMissingLogFormat,
                                System.IO.Path.GetFileName(book)));

                        continue;
                    }

                    int added = _passwordService.MergePasswordBook(
                        book,
                        out int _,
                        out int _,
                        out IReadOnlyList<string> _);

                    addedTotal += added;
                    mergedNames.Add(System.IO.Path.GetFileName(book));
                }

                if (books.Count > 0 || _passwordService.RememberedEntryCount > 0)
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordListMemoryRestoreLogFormat,
                            _passwordService.RememberedEntryCount,
                            mergedNames.Count,
                            mergedNames.Count == 0
                                ? StatusText.PasswordListMemoryNoBooksText
                                : string.Join("、", mergedNames),
                            addedTotal));
                }

                RefreshPasswordBookSummary();
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "自动加载密码本失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 启动时那一次"读记忆"（**只做一次**）。
        ///
        /// <para>为什么只做一次：<see cref="AutoLoadPasswordBook"/> 在清空任务列表、移除任务、
        /// 保存设置之后都会被调一次，而"读记忆"是会**重建整个列表**的动作 ——
        /// 每次触发都重来一遍，用户在该窗口里刚做的改动就会被记忆里的旧版本覆盖回去。
        /// 后面那些调用只需要"把（新记住的）密码本再合并一次"，那是幂等的。</para>
        /// </summary>
        private void LoadRememberedPasswordListAtStartup()
        {
            IReadOnlyList<string> settingsBooks = Settings?.PasswordBookPaths ?? new List<string>();

            if (!_passwordService.RememberPasswordList)
            {
                AppendLog("INFO", StatusText.PasswordListMemoryDisabledLog);

                // 关掉记忆只是"不落盘"：设置里记住的密码本照样要自动加载（那是另一件事）。
                _passwordService.SetRememberedBookPaths(settingsBooks);
                return;
            }

            PasswordListLoadStatus status = _passwordService.LoadRememberedList();

            if (status == PasswordListLoadStatus.Loaded)
            {
                /*
                 * 书的清单两边都有，合并顺序按"记忆优先、设置兜底"：
                 * ① 记忆里的顺序是用户实际用出来的顺序，要保留；
                 * ② 设置里那份是权威（用户在④「密码」页能逐项移除），所以**以设置的集合为准**，
                 *    只是顺序尽量沿用记忆 —— 移除过的书不许因为记忆里还有就被加回来；
                 * ③ 设置里一本都没有（被清掉 / 换了一份配置）而记忆里有：按记忆恢复，等于自愈一次。
                 */
                _passwordService.SetRememberedBookPaths(
                    OrderBookPaths(_passwordService.RememberedBookPaths, settingsBooks));

                return;
            }

            if (status != PasswordListLoadStatus.NoFile)
            {
                // 解不开 / 半截 / 不是我们的格式：**忽略并提示**，不崩、不覆盖、不删（用户 2026-09-24 定的）。
                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordListMemoryLoadFailedLogFormat,
                        _passwordService.LastListWarning));
            }

            /*
             * 没有可用记忆（第一次运行，或刚被忽略）时，清单只能来自设置：
             * 设置里一本都没有才退回老的侧车文件 password-book.path ——
             * 用户可能是从「密码列表」窗口导入的，那条路以前只写侧车、不写设置列表。
             * 这一步保证老用户的"导入一次就一直有效"不因为这次改动而失效。
             */
            var paths = new List<string>(settingsBooks);

            if (paths.Count == 0)
            {
                string sidecarBook = ReadPasswordBookSidecar();

                if (sidecarBook.Length > 0)
                {
                    paths.Add(sidecarBook);
                }
            }

            _passwordService.SetRememberedBookPaths(paths);
        }

        /// <summary>
        /// 合并两份"记住的密码本"清单：**集合以设置那份为准，顺序尽量沿用记忆那份**。
        ///
        /// <para>设置里是空的时候按记忆恢复（appsettings.json 被重置 / 手改坏时的自愈）。</para>
        /// </summary>
        internal static List<string> OrderBookPaths(
            IReadOnlyList<string> remembered,
            IReadOnlyList<string> fromSettings)
        {
            var result = new List<string>();

            if (fromSettings == null || fromSettings.Count == 0)
            {
                foreach (string path in remembered ?? (IReadOnlyList<string>)Array.Empty<string>())
                {
                    AddIfNew(result, path);
                }

                return result;
            }

            foreach (string path in remembered ?? (IReadOnlyList<string>)Array.Empty<string>())
            {
                if (ContainsBook(fromSettings, path))
                {
                    AddIfNew(result, path);
                }
            }

            foreach (string path in fromSettings)
            {
                AddIfNew(result, path);
            }

            return result;
        }

        private static void AddIfNew(List<string> target, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            foreach (string existing in target)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            target.Add(path);
        }

        private static bool ContainsBook(IReadOnlyList<string> paths, string? path)
        {
            if (path == null)
            {
                return false;
            }

            foreach (string existing in paths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 读老侧车文件 <c>password-book.path</c>（只读，**不写**）。
        /// 读不到就当作没有 —— 这个文件是历史遗留，丢了不影响。
        /// </summary>
        private string ReadPasswordBookSidecar()
        {
            try
            {
                string sidecar = Path.Combine(_pathService.DataRootDirectory, "password-book.path");

                return File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 把"记住的密码本"从设置推给密码服务（用户在④「密码」页增删过、点底栏「保存设置」之后由
        /// <see cref="SaveSettings"/> 调它）。
        /// </summary>
        private void ApplyRememberedBookPathsFromSettings()
        {
            /*
             * 开关也要跟着设置走，而且**必须在这里先落定**：
             * 用户刚把「记住密码列表」关掉时，下面的 SetRememberedBookPaths 会走一次落盘 ——
             * 开关还是旧值（开）的话，这一下会在他关掉之后**又写一份**记忆。
             * 关掉 = 不写也不读，一次都不该写。
             */
            _passwordService.RememberPasswordList = Settings?.RememberPasswordList ?? true;

            IReadOnlyList<string> settingsBooks = Settings?.PasswordBookPaths ?? new List<string>();

            _passwordService.SetRememberedBookPaths(settingsBooks);
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

                /*
                 * ⚠ 失败**必须**弹出来：这一条以前只写日志 —— 用户选完文件什么都没发生，
                 * 只能怀疑"是不是没导进去"（而日志区还停在别的行上）。
                 * 提示里带上下一步可做的事，而不是只丢一条异常文本。
                 */
                _dialogService.ShowError(
                    $"导入密码本失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                    + "可以检查：① 文件是不是纯文本（UTF-8 或 ANSI 编码）；"
                    + "② 文件有没有被别的程序占用；③ 换一个位置的文件再试。"
                    + "也可以直接在「密码列表管理」里手工添加密码。");
            }
        }

        private void LogLeftoverWorkspaces()
        {
            try
            {
                /*
                 * 扫描根 = **当前生效的工作区根 + 老位置 + 账本里用过的根**（见 ResolveWorkspaceScanRoots）。
                 *
                 * 为什么不是一个根（用户 2026-09-24 拍板"工作区跟输出盘"之后的必然后果）：
                 * 根会随输出盘变，而老位置里可能还压着升级前那批的残留 —— 只扫一个就会出现
                 * "东西明明在盘上，界面却报没有"。每个残留条目自己带着它在哪个根下，清理时按它回到各自的根校验。
                 */
                IReadOnlyList<string> scanRoots = ResolveWorkspaceScanRoots();

                IReadOnlyList<WorkspaceLeftover> leftovers = WorkspaceCleanupService.ScanMany(scanRoots);

                /*
                 * 用户 2026-09-24 第 22 条：他实测攒了 10 个目录 / 5.7 GB，问"为什么失败后你会留下这个残留"。
                 * 保留本身是**故意的**（失败 / 取消时工作区里是他唯一的一份产物线索，不变量 12/13），
                 * 但以前只写一句"发现 N 个" —— 没体积、界面上也没有清理入口，等于没告诉他。
                 * 现在：① 日志带上体积**与实际位置**；② 界面上留一行常驻提示 + 一条清理命令（删前二次确认）。
                 */
                WorkspaceLeftoverTotalBytes = WorkspaceCleanupService.TotalBytesOf(leftovers);
                HasWorkspaceLeftovers = leftovers.Count > 0;
                WorkspaceLeftoverBanner = leftovers.Count > 0
                    ? string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.WorkspaceLeftoverBannerFormat,
                        leftovers.Count,
                        WorkspaceCleanupService.FormatSize(WorkspaceLeftoverTotalBytes))
                    : string.Empty;

                // 「清理工作区」的可用性跟着真实扫描结果走（没有残留就点不动）。
                RaiseAllCommandCanExecuteChanged();

                if (leftovers.Count == 0)
                {
                    return;
                }

                AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.WorkspaceLeftoverLogFormat,
                        leftovers.Count,
                        WorkspaceCleanupService.FormatSize(WorkspaceLeftoverTotalBytes),
                        DescribeLeftoverLocations(leftovers)));

                // 把"这次一共看了哪几个根"也写清楚：用户照着这一行就能自己去盘上核对。
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.WorkspaceScannedRootsLogFormat,
                        string.Join("；", scanRoots)));

                AppendLog("INFO", StatusText.WorkspaceLeftoverHintLog);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "检查工作区失败：" + ex.Message);
            }
        }

        /// <summary>残留**实际**在哪些根下（去重，按首次出现顺序）—— 日志里那一格就是它。</summary>
        private static string DescribeLeftoverLocations(IReadOnlyList<WorkspaceLeftover> leftovers)
        {
            var locations = new List<string>();

            foreach (WorkspaceLeftover leftover in leftovers)
            {
                bool seen = locations.Any(existing => string.Equals(
                    existing,
                    leftover.RootDirectory,
                    StringComparison.OrdinalIgnoreCase));

                if (!seen && !string.IsNullOrWhiteSpace(leftover.RootDirectory))
                {
                    locations.Add(leftover.RootDirectory);
                }
            }

            return locations.Count == 0 ? "（未知）" : string.Join("；", locations);
        }

        /// <summary>
        /// 界面上那一行"工作区残留：N 个目录，共 X"（空 = 没有残留，整行不显示）。
        /// </summary>
        public string WorkspaceLeftoverBanner
        {
            get => _workspaceLeftoverBanner;
            private set => SetProperty(ref _workspaceLeftoverBanner, value);
        }

        /// <summary>有没有工作区残留（界面据此显示/隐藏那一行与「清理工作区」）。</summary>
        public bool HasWorkspaceLeftovers
        {
            get => _hasWorkspaceLeftovers;
            private set => SetProperty(ref _hasWorkspaceLeftovers, value);
        }

        /// <summary>残留一共占多少字节（界面提示用）。</summary>
        public long WorkspaceLeftoverTotalBytes
        {
            get => _workspaceLeftoverTotalBytes;
            private set => SetProperty(ref _workspaceLeftoverTotalBytes, value);
        }

        /// <summary>
        /// 上一批一键处理撞到层数上限、还剩多少内层包没解（用户 2026-09-24 第 16 条追加）。
        ///
        /// <para>值来自 <c>OneClickCoordinator</c> 的结论（<c>OneClickOutcome.PendingContinuationCount</c>）：
        /// 到顶时那些内层包已经加进任务列表并勾好，界面上给一行提示 + 一个「继续解」按钮。
        /// 到顶**必须提示**（他要的就是这个）——静默停下正是他抱怨的那件事。</para>
        /// </summary>
        public int PendingContinuationCount => _pendingContinuationCount;

        /// <summary>要不要显示「继续解」那一行 / 那个按钮。</summary>
        public bool HasPendingContinuation => _pendingContinuationCount > 0;

        /// <summary>「继续解」那一行的文案（按钮与提示共用一份措辞：<c>StatusText.ContinueOneClick*</c>）。</summary>
        public string PendingContinuationText => !HasPendingContinuation
            ? string.Empty
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.ContinueOneClickHintFormat,
                OneClickCoordinator.MaxRounds,
                _pendingContinuationCount);

        /// <summary>「继续解」按钮上的文案。</summary>
        public string ContinueOneClickButtonText => !HasPendingContinuation
            ? string.Empty
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.ContinueOneClickTextFormat,
                _pendingContinuationCount);

        /// <summary>一键处理收尾时把"还剩几个内层包"报进来（0 = 没有待续解的）。</summary>
        internal void ReportPendingContinuation(int count)
        {
            int normalized = Math.Max(0, count);

            if (_pendingContinuationCount == normalized)
            {
                return;
            }

            _pendingContinuationCount = normalized;

            OnPropertyChanged(nameof(PendingContinuationCount));
            OnPropertyChanged(nameof(HasPendingContinuation));
            OnPropertyChanged(nameof(PendingContinuationText));
            OnPropertyChanged(nameof(ContinueOneClickButtonText));

            RaiseAllCommandCanExecuteChanged();
        }

        /// <summary>「继续解」能不能点：有剩下的内层包，而且现在不忙。</summary>
        private bool CanContinueOneClick() => !IsBusy && HasPendingContinuation;

        /// <summary>
        /// 清理工作区残留（用户 2026-09-24 第 22 条）：**先确认、再删**，而且只删工作区根目录的直属子目录。
        ///
        /// <para>为什么必须有这一步确认（不变量 13）：工作区里可能是那一批唯一解出来的一份产物，
        /// 删掉不可逆。所以这里把要删的目录**逐条列进确认框的明细区**，用户点确定才动手。</para>
        ///
        /// <para>没有任何残留时只提示一句，不弹确认框（空确认框只会训练用户闭眼点确定）。</para>
        /// </summary>
        private void ClearWorkspaceLeftovers()
        {
            IReadOnlyList<WorkspaceLeftover> leftovers =
                WorkspaceCleanupService.ScanMany(ResolveWorkspaceScanRoots());

            if (leftovers.Count == 0)
            {
                WorkspaceLeftoverTotalBytes = 0;
                HasWorkspaceLeftovers = false;
                WorkspaceLeftoverBanner = string.Empty;

                _dialogService.ShowInfo(StatusText.WorkspaceCleanupNothingText);

                return;
            }

            long totalBytes = WorkspaceCleanupService.TotalBytesOf(leftovers);

            string detail = StatusText.WorkspaceCleanupConfirmDetailHeader + Environment.NewLine
                            + string.Join(
                                Environment.NewLine,
                                leftovers.Select(item =>
                                    "  " + item.Name
                                    + "（" + WorkspaceCleanupService.FormatSize(item.TotalBytes)
                                    + "，" + item.FileCount + " 个文件）"
                                    + "　" + item.RootDirectory))
                            + Environment.NewLine + Environment.NewLine
                            + StatusText.WorkspaceCleanupConfirmDetailFooter;

            bool confirmed = _dialogService.ShowConfirm(
                StatusText.WorkspaceCleanupConfirmTitle
                + "："
                + string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceCleanupConfirmFormat,
                    leftovers.Count,
                    WorkspaceCleanupService.FormatSize(totalBytes)),
                optionText: string.Empty,
                optionCheckedByDefault: false,
                detail: detail,
                out _);

            if (!confirmed)
            {
                AppendLog("INFO", "工作区清理已取消：一个目录都没删。");

                return;
            }

            /*
             * 清理**按根分组**做：每个根各自做一次容器内校验（不变量 13）。
             *
             * 为什么不能把一堆路径丢给某一个根去做：默认跟输出盘之后残留可能散在多个根下
             * （当前生效的根、老位置、账本里用过的根），拿 A 根去校验 B 根下面的目录
             * 会被正确判成"越界"从而一个都删不掉 —— 那不是保守，是坏了。
             */
            var outcomes = new List<WorkspaceCleanupOutcome>();

            foreach (IGrouping<string, WorkspaceLeftover> group in leftovers.GroupBy(
                         item => item.RootDirectory,
                         StringComparer.OrdinalIgnoreCase))
            {
                outcomes.AddRange(WorkspaceCleanupService.Cleanup(
                    group.Key,
                    group.Select(item => item.DirectoryPath)));
            }

            int deleted = outcomes.Count(item => item.Deleted);
            int failed = outcomes.Count - deleted;

            foreach (WorkspaceCleanupOutcome outcome in outcomes)
            {
                if (!outcome.Deleted)
                {
                    AppendLog("WARN", "工作区：" + outcome.Message);
                }
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceCleanupResultLogFormat,
                    deleted,
                    failed,
                    WorkspaceCleanupService.FormatSize(totalBytes)));

            // 删完重新扫一遍（失败的那几个会留在提示里，不假装清空了）。
            LogLeftoverWorkspaces();

            if (failed == 0)
            {
                _dialogService.ShowInfo(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.WorkspaceCleanupResultLogFormat,
                        deleted,
                        failed,
                        WorkspaceCleanupService.FormatSize(totalBytes)));
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

        /// <summary>三个批量勾选命令的可用性：闲着 + 列表里确实有东西可勾。</summary>
        private bool CanChangeTaskSelection()
        {
            return !IsBusy && Tasks.Count > 0;
        }

        /*
         * ============================ 勾选跟随汇总（缺陷修正） ============================
         *
         * 以前这里**没有任何人**监听 ArchiveTask.IsSelected：
         * 用户点一下勾选框，勾选确实变了、命令执行时也确实按新的勾选走，
         * 但汇总区那个「选中：N」（Summary.SelectedCount）以及依赖勾选的命令可用性
         * （例如「只解压」要求"至少勾了一个"）**一直停在旧值上** —— 界面在撒谎：
         * 屏幕上写着"选中：3"，实际勾了 5 个；或者一个都没勾时「只解压」还是亮着的。
         *
         * 修法是"只在勾选变化时重算"，**不监听全部属性**：Status / ProgressText / ElapsedText
         * 在一次解压里会被刷新成百上千次，每次都重算汇总（还要遍历整张任务表）纯属自伤。
         */

        /// <summary>批量改勾选时挂起逐条刷新（50–200 个任务的"全选"不该触发 200 次汇总）。</summary>
        private bool _bulkSelectionUpdate;

        private void HookTaskSelectionTracking()
        {
            Tasks.CollectionChanged += Tasks_CollectionChanged;

            foreach (ArchiveTask task in Tasks)
            {
                task.PropertyChanged -= Task_SelectionPropertyChanged;
                task.PropertyChanged += Task_SelectionPropertyChanged;
            }
        }

        /// <summary>
        /// 集合变化只负责**换挂**逐条的监听，不在这里刷汇总：
        /// 批量导入会逐个 <c>Add</c>（用户场景是 50–200+ 个包），逐个刷就是 O(n²)；
        /// 而所有 Add / Remove / Clear 的调用点本来就会在收尾时各刷一次（见各自的 UpdateSummary）。
        /// </summary>
        private void Tasks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            /*
             * Reset（批量替换 / 清空）**不带** OldItems / NewItems —— 逐项挂监听的那两条路都拿不到东西。
             * 批量导入走的正是 Reset（见 RangeObservableCollection），所以必须在这里按当前列表整批重挂，
             * 否则导入进来的任务勾选一下，汇总里的「选中：N」和「只解压」的可用性就不会跟着变
             *（那正是 2026-09-22 修过的那个"界面在撒谎"的缺陷，换个入口复活）。
             */
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (ArchiveTask task in Tasks)
                {
                    task.PropertyChanged -= Task_SelectionPropertyChanged;
                    task.PropertyChanged += Task_SelectionPropertyChanged;
                }

                return;
            }

            if (e.OldItems != null)
            {
                foreach (ArchiveTask task in e.OldItems.OfType<ArchiveTask>())
                {
                    task.PropertyChanged -= Task_SelectionPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (ArchiveTask task in e.NewItems.OfType<ArchiveTask>())
                {
                    task.PropertyChanged -= Task_SelectionPropertyChanged;
                    task.PropertyChanged += Task_SelectionPropertyChanged;
                }
            }
        }

        private void Task_SelectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_bulkSelectionUpdate || e.PropertyName != nameof(ArchiveTask.IsSelected))
            {
                return;
            }

            RefreshSummaryAfterSelectionChange();
        }

        /// <summary>
        /// 勾选变化之后重算汇总（汇总里带着「选中：N」，同时负责刷新命令可用性）。
        ///
        /// <para>
        /// 为什么要挑线程：勾选通常发生在界面线程（用户点的），但**续解流程也会改 IsSelected**
        /// （<c>OneClickCoordinator</c> 把新加进来的内层包显式勾上，那段代码可能跑在后台线程上）。
        /// 后台线程直接碰绑定会有跨线程风险，所以有 UI 宿主时统一回界面线程刷。
        /// </para>
        /// </summary>
        public void RefreshSummaryAfterSelectionChange()
        {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;

            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(UpdateSummary));
                return;
            }

            UpdateSummary();
        }

        /// <summary>
        /// 配合 <see cref="_bulkSelectionUpdate"/>：批量动作结束后只刷一次汇总。
        ///
        /// <para><b>凡是"循环改一批任务的勾选"的代码都必须走它</b>（用户 2026-09-24 第 12 条"卡死"
        /// 的修法之一）：逐项改勾选会逐项触发全表汇总重算 + 38 条命令可用性重查 ——
        /// 几百项的任务列表就是几百次 O(N) 全表扫描（O(N²)）。跨类调用的例子见
        /// <c>OneClickCoordinator</c>（续解把已处理的任务取消勾选、把新内层包勾上）
        /// 与 <c>MainWindow.SmartRenameCurrentTaskMenuItem_Click</c>（只留当前这一个）。</para>
        /// </summary>
        internal void RunBulkSelectionUpdate(Action changeSelection)
        {
            _bulkSelectionUpdate = true;

            try
            {
                changeSelection();
            }
            finally
            {
                _bulkSelectionUpdate = false;
            }

            RefreshSummaryAfterSelectionChange();
        }

        /// <summary>全选：整个列表都参与处理。</summary>
        public void SelectAllTasks()
        {
            if (Tasks.Count == 0)
            {
                AppendLog("INFO", "「全选」：任务列表是空的，没有可勾选的任务。");
                return;
            }

            RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask task in Tasks)
                {
                    task.IsSelected = true;
                }
            });

            AppendLog("INFO", $"已全选：{Tasks.Count} 个任务都会参与处理（「只解压 / 一键处理」按勾选执行）。");
        }

        /// <summary>全不选：一个都不勾，方便"只处理其中某一个"。</summary>
        public void SelectNoneTasks()
        {
            if (Tasks.Count == 0)
            {
                AppendLog("INFO", "「全不选」：任务列表是空的。");
                return;
            }

            int before = Tasks.Count(x => x.IsSelected);

            RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask task in Tasks)
                {
                    task.IsSelected = false;
                }
            });

            /*
             * 行高亮这里**刻意保留**：右键菜单、复制路径这类"这一行"的操作仍然用它。
             * 但命令型入口（一键处理 / 只解压 / 清理类）**一律只认勾选** ——
             * 一个都没勾时它们只提示、什么都不做（用户 2026-09-24 第 12 条），
             * 所以"清掉行高亮会拆掉兜底"这个老顾虑已经不存在了。
             */
            AppendLog(
                "INFO",
                $"已取消全部勾选（原有 {before} 个勾选被清掉）：现在一个都没勾。" +
                "接着点某一行左边的勾选框，或者用右键「只勾选这一个」。");
        }

        /// <summary>反选：勾上的取消、没勾的勾上。</summary>
        public void InvertTaskSelection()
        {
            if (Tasks.Count == 0)
            {
                AppendLog("INFO", "「反选」：任务列表是空的。");
                return;
            }

            RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask task in Tasks)
                {
                    task.IsSelected = !task.IsSelected;
                }
            });

            AppendLog("INFO", $"已反选：现在有 {Tasks.Count(x => x.IsSelected)} 个任务被勾选（共 {Tasks.Count} 个）。");
        }

        /// <summary>
        /// 只勾选指定的那一个，其余全部取消勾选。
        ///
        /// 这是"识别完一个文件夹、几十上百个包全被勾上，但这次只想解其中一个"最短的一条路：
        /// 右键那一行 → 点一下，不必先"全不选"再去找那一个。
        /// </summary>
        private void SelectSoleTask(object? parameter)
        {
            if (parameter is not ArchiveTask target || !Tasks.Contains(target))
            {
                return;
            }

            RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask task in Tasks)
                {
                    task.IsSelected = ReferenceEquals(task, target);
                }
            });

            AppendLog(
                "INFO",
                $"只勾选了「{target.FileName}」：其余 {Math.Max(0, Tasks.Count - 1)} 个任务的勾选已取消。");
        }

        /// <summary>
        /// 「清空列表」：**整表操作**，与勾选无关（名字本身就是这个意思）。
        ///
        /// <para>
        /// 缺陷 1 顺手核对到这里时，查出来的不是"代码看错了选中含义"，而是**界面在自相矛盾**：
        /// 列表上方那条蓝字把它和「移除选中」列在一起，说"都只作用于已勾选的任务"，
        /// 而它从第一版起就是清整张表。
        /// </para>
        /// <para>
        /// 处置：**没有**把行为改成"只清勾选的" —— 那样"清空列表"这个名字就失去了唯一的出口
        /// （勾了 3 个时点它，清 3 个还是清全部？），而是把话说明白：
        /// 蓝字里单独写明它是整表操作，确认框里点名总数、勾选数、"只移除勾选的请用「移除选中」"。
        /// 于是"界面说的"和"代码做的"重新对齐，用户点之前就知道会发生什么。
        /// </para>
        /// </summary>
        public void ClearTasks()
        {
            if (Tasks.Count == 0)
            {
                return;
            }

            int totalCount = Tasks.Count;
            int checkedCount = Tasks.Count(x => x.IsSelected);

            string confirmText =
                $"确定要清空整个任务列表吗？{Environment.NewLine}{Environment.NewLine}"
                + $"列表里的 {totalCount} 个任务会全部移除 —— 这是**整表操作**，与勾选无关"
                + (checkedCount > 0
                    ? $"（当前勾选的 {checkedCount} 个也会一起移除）。"
                    : "。")
                + $"{Environment.NewLine}{Environment.NewLine}"
                + $"只移除勾选的任务请用「移除选中」；这里只清空列表，磁盘上的文件一个都不会动。";

            bool confirm = _dialogService.ShowConfirm(confirmText);

            if (!confirm)
            {
                AppendLog("INFO", "用户取消了「清空列表」，任务列表没有变化。");
                return;
            }

            Tasks.Clear();
            LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

            UpdateSummary();
            AppendLog(
                "INFO",
                $"已清空任务列表（整表操作，与勾选无关）：共移除 {totalCount} 个任务（其中勾选 {checkedCount} 个）；磁盘上的文件没有动。");
        }

        /// <summary>
        /// 「移除选中」：与清理类命令**同一套选中口径**（勾选为准 → 退化为当前行 → 都没有才提示）。
        ///
        /// <para>
        /// 以前一个都没勾时它静默什么也不做（只在日志里留一句"已移除选中任务 0 个"），
        /// 用户点完只会以为程序坏了 —— 那正是缺陷 1 的同一类问题：**界面说作用于勾选，
        /// 实际却要求另一个东西**。现在两者都被 <see cref="ResolveCleanupTargets"/> 管着。
        /// </para>
        /// </summary>
        public void RemoveSelectedTasks()
        {
            const string title = "移除选中";

            CleanupTargetSelection selection = ResolveCleanupTargets(Tasks);

            AppendLog(selection.HasTarget ? "INFO" : "WARN", DescribeCleanupTargets(title, selection));

            if (!selection.HasTarget)
            {
                // 只提示、什么都不做（用户 2026-09-24 第 12 条：一律只认勾选）。
                _dialogService.ShowWarning(PromptNoCheckedTask(title));
                return;
            }

            List<ArchiveTask> targets = selection.Tasks.ToList();

            foreach (ArchiveTask task in targets)
            {
                Tasks.Remove(task);
            }

            RebuildTaskIndex();
            LogLeftoverWorkspaces();
            AutoLoadPasswordBook();

            UpdateSummary();
            AppendLog("INFO", $"{title}：以勾选为准，已移除 {targets.Count} 个任务。");
        }

        /// <summary>
        /// "一个都没勾"时的**唯一**提示句（用户 2026-09-24 第 12 条定的口径：只提示、不动作）。
        ///
        /// <para>措辞来自 <see cref="StatusText.NoCheckedTaskPromptFormat"/> —— 界面蓝字、命令提示、
        /// 日志三处共用同一句话，谁也不能再各写一套（§7）。</para>
        /// </summary>
        private string PromptNoCheckedTask(string title)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.NoCheckedTaskPromptFormat,
                title,
                Tasks.Count);
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

        /// <summary>
        /// 把「一键处理 · 本次选项」里勾了「存为默认」的那一组选择写回设置（规格 §9.2 硬要求②）。
        ///
        /// <para>
        /// ⚠ 这是**整个本次选项功能里唯一**写 <c>appsettings.json</c> 的地方，
        /// 而且只有用户显式勾选之后才会被调用（<c>OneClickCoordinator.RunAsync</c> 里那一个分支）。
        /// 没勾时这条路径根本不会执行 —— 所以"不勾就不许改设置文件"不是靠自觉，是靠调用图。
        /// </para>
        ///
        /// <para>
        /// 翻译只走既有实现：落点两个布尔由 <see cref="OutputPlacement.ToLegacyFlags"/> 给出、
        /// 终端落法与源包处理由 <see cref="OutputPlacement.ToSettingValue"/> /
        /// <see cref="AppSettings.ToSourceHandlingValue"/> 序列化 —— 与②「解压方式」/③「清理与删除」页写的是同一套字符串口径，
        /// 不新造第三种表示法。
        /// </para>
        /// <para>
        /// 落点无效（选了"指定位置"却没填路径）时**不写落点**，但终端落法 / 源包处理照写：
        /// 那两项本来就与路径无关，因为一个空路径把另外两项一起丢掉才是意外。
        /// </para>
        /// </summary>
        internal void SaveOneClickOptionsAsDefaults(OneClickRunOptions options)
        {
            if (options == null || Settings == null)
            {
                return;
            }

            if (options.IsPlacementValid)
            {
                (bool extractToOriginalDirectory, bool keepArchiveNameFolder) =
                    OutputPlacement.ToLegacyFlags(options.PlacementMode);

                Settings.ExtractToOriginalDirectory = extractToOriginalDirectory;
                Settings.KeepArchiveNameFolder = keepArchiveNameFolder;

                /*
                 * 自定义根只在"指定位置"两档下才有意义，也只在非空时才写 ——
                 * 空串写进去等于把用户上次选的目录抹掉（而落点那两档并不需要它）。
                 */
                if (OutputPlacement.UsesCustomRoot(options.PlacementMode) &&
                    !string.IsNullOrWhiteSpace(options.CustomRoot))
                {
                    Settings.CustomOutputDirectory = options.CustomRoot.Trim();

                    // 主界面"输出目录"那一格与设置保持同一个值（它同时也是解压管线用的那个）。
                    _selectedOutputDirectory = Settings.CustomOutputDirectory;
                    OnPropertyChanged(nameof(SelectedOutputDirectory));
                }
            }

            Settings.TerminalLayoutMode = OutputPlacement.ToSettingValue(options.TerminalLayout);
            Settings.SourceHandling = AppSettings.ToSourceHandlingValue(options.SourceHandling);

            /*
             * 「删除操作」那一档（2026-09-25 第 33 条补）：弹窗的折叠区里也能改它，
             * 勾了「存为默认」就一起写回 —— 否则用户下次会发现"我在弹窗里选的没被记住"。
             */
            Settings.RestHandlingAfterVerify = RestHandlingModes.Normalize(options.RestHandling);

            Settings.Normalize();

            /*
             * 确认框里改过落点、又选了「保存为默认」→ ①页那一行与②页那几个控件都要跟着走
             * （这里也是直接写 Settings 的路径之一，2026-09-25 第 31 条同一口径）。
             */
            NotifyOutputPlacementChangedEverywhere();

            try
            {
                _settingsService.Save(Settings);
            }
            catch (Exception ex)
            {
                /*
                 * 兜底：SettingsService.Save 自己就吞写盘异常（既有行为，本批不改它），
                 * 所以这里只会接住"别的东西炸了"——写失败本身不会走到这来，也就不会被误报成成功提示。
                 */
                AppendLog("ERROR", "把本次选项存为默认失败：" + ex.Message);
                _dialogService.ShowError("把本次选项存为默认失败：" + ex.Message);
                return;
            }

            OnPropertyChanged(nameof(Settings));
            RefreshOutputPaths();
            UpdateSummary();

            AppendLog("INFO", $"本次选项已写入设置（{_settingsService.SettingsFilePath}）。");
        }

        /// <summary>
        /// 记下"一键处理的确认框以后不再弹"（用户 2026-09-24 第 17 条："这个可以选中以后不弹出"）。
        ///
        /// <para>与 <see cref="SaveOneClickOptionsAsDefaults"/> 是两条独立的写设置路径：
        /// 那一条写的是落点 / 终端落法 / 源包处理，这一条只写
        /// <see cref="AppSettings.SkipOneClickConfirm"/> 一个布尔。两者都**只在用户显式勾选之后**执行。</para>
        ///
        /// <para>写失败不让流程停：确认框已经点过「开始处理」，用户要的是把这一批跑完 ——
        /// 记不下"不再问"最多让他下次再点一次确认，不该变成"一键处理失败"。</para>
        /// </summary>
        internal void SaveSkipOneClickConfirm(bool skip)
        {
            if (Settings == null || Settings.SkipOneClickConfirm == skip)
            {
                return;
            }

            Settings.SkipOneClickConfirm = skip;

            try
            {
                _settingsService.Save(Settings);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "「以后不再询问」没能写进设置：" + ex.Message);
                return;
            }

            OnPropertyChanged(nameof(Settings));
        }

        /// <summary>
        /// 记下"导入之后不再弹无用物提醒"（用户 2026-09-24 第 15 条："用户可以选中关闭以后就不用触发了"）。
        ///
        /// <para>写失败只写 WARN：导入本身已经成功，不该因为记不住一个偏好就变成失败；
        /// 界面上（③ 清理与删除 页）留着一个开关可以再打开。</para>
        /// </summary>
        internal void SaveRemindJunkAfterImport(bool remind)
        {
            if (Settings == null || Settings.RemindJunkAfterImport == remind)
            {
                return;
            }

            Settings.RemindJunkAfterImport = remind;

            try
            {
                _settingsService.Save(Settings);
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "「无用物提醒」开关没能写进设置：" + ex.Message);
                return;
            }

            OnPropertyChanged(nameof(Settings));
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

        /// <summary>
        /// 「设置」入口（旧菜单「工具 → 设置...」）：设置已经搬进⑥选项卡，
        /// 所以这里只做一件事 —— 请界面切到那一页，并把这件事写进日志。
        ///
        /// <para>为什么保留这个命令而不是删掉：它一直是"打开设置"的公开入口，
        /// 删掉它等于悄悄改掉 MainViewModel 的对外形状；保留成"切页"语义，
        /// 任何旧调用点仍然得到正确结果。</para>
        /// </summary>
        private void OpenSettings()
        {
            RequestTab(SettingsTabIndex, "设置已经搬进「设置」选项卡（原来的设置窗口已退休）。");
        }

        /// <summary>见 <see cref="OpenSettings"/>：打包整页搬进⑤选项卡。</summary>
        private void OpenPacking()
        {
            RequestTab(PackingTabIndex, "打包已经搬进「打包」选项卡（原来的打包窗口已退休）。");
        }

        private void RequestTab(int index, string log)
        {
            AppendLog("INFO", log);

            TabRequested?.Invoke(index);
        }

        /// <summary>
        /// 底栏的「保存设置」：走 <see cref="SettingsEditor"/> 那一套校验与落盘
        /// （**唯一**的保存实现，与原设置窗口完全相同），成功后把界面与引擎层一起刷新。
        ///
        /// <para>
        /// 为什么要在这里刷新这么多东西：设置一落盘，落点、缓存根、日志开关、引擎优先级、
        /// 记住的密码本清单都会影响已经跑起来的这套服务 —— 少刷一样就会变成
        /// "设置里改了、这次运行还是老的"。
        /// </para>
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                SettingsEditor.SaveCommand.Execute(null);

                // 校验没过（工具路径不存在 / 危险模式没凭证）：SettingsEditor.Message 里已经写明改法，
                // 这里**什么都不做** —— 绝不把一次失败的保存说成成功。
                if (SettingsEditor.DialogResult != true)
                {
                    return;
                }

                Settings = SettingsEditor.Settings;
                ApplyEngineSettings();

                SelectedOutputDirectory = Settings.CustomOutputDirectory ?? string.Empty;

                /*
                 * 设置里那份"记住的密码本"是权威清单（用户能逐项移除），保存后立刻推给密码服务：
                 * 推完记忆文件里那份也跟着变成同一份 —— 下一轮启动的合并就不会把用户刚移除的书
                 * 又从记忆里翻出来（那样"移除"这个按钮就是句空话）。
                 */
                ApplyRememberedBookPathsFromSettings();

                RefreshOutputPaths();
                LogLeftoverWorkspaces();
                AutoLoadPasswordBook();
                UpdateSummary();

                // 并发档、危险模式那几句话是"包在设置外面"的展示状态：设置变了必须重算。
                OnPropertyChanged(nameof(MaxParallelChoice));
                RefreshSpaceModeText();

                AppendLog("INFO", "设置已保存");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "保存设置失败：" + ex.Message);
                _dialogService.ShowError("保存设置失败：" + ex.Message);
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

            /*
             * 恢复默认会把危险模式、并发档、自测凭证一起改回默认值 ——
             * 那几个展示属性是"包在设置外面"的（AppSettings 不发通知），
             * 不显式通知的话⑥设置页会继续显示"危险模式已开启"而实际已经关掉了。
             */
            OnPropertyChanged(nameof(MaxParallelChoice));
            RefreshSpaceModeText();

            AppendLog("INFO", "已恢复默认设置");
        }

        /// <summary>
        /// 打开密码列表窗口（④密码页的「密码列表管理…」）。
        /// </summary>
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

            /*
             * 窗口里导入过的书要同步回设置（**两处口径必须一致**）：
             * 那个窗口自己写的是 appsettings.json，而主 ViewModel 手里这份 Settings 是旧的 ——
             * 不同步的话，下次保存设置就会用旧清单把新导入的书覆盖掉（用户看到"导入了、重启又没了"）。
             */
            SyncRememberedBookPathsToSettings();

            string bookName = _passwordService.LastImportedBookPath.Length == 0
                ? "无"
                : System.IO.Path.GetFileName(_passwordService.LastImportedBookPath);

            AppendLog("INFO", $"密码列表管理窗口已关闭（当前 {_passwordService.Passwords.Count} 条密码，密码本：{bookName}）。");
        }

        /// <summary>
        /// 把密码服务里那份"记住的密码本"同步回设置并落盘（窗口里导入之后由 <see cref="OpenPasswordList"/> 调它）。
        ///
        /// <para>判据是"服务里有多出来的一本"：服务那份是**并集**（导入会追加、设置保存会整份替换），
        /// 所以只在它确实带了新东西时才写设置 —— 不做无谓的文件写入。</para>
        /// </summary>
        private void SyncRememberedBookPathsToSettings()
        {
            if (Settings == null)
            {
                return;
            }

            IReadOnlyList<string> fromService = _passwordService.RememberedBookPaths;

            if (fromService.Count == 0)
            {
                return;
            }

            var merged = OrderBookPaths(fromService, Settings.PasswordBookPaths ?? new List<string>());

            if (merged.Count == (Settings.PasswordBookPaths?.Count ?? 0))
            {
                bool same = true;

                for (int i = 0; i < merged.Count && same; i++)
                {
                    same = string.Equals(merged[i], Settings.PasswordBookPaths![i], StringComparison.OrdinalIgnoreCase);
                }

                if (same)
                {
                    return;
                }
            }

            Settings.PasswordBookPaths = merged;
            Settings.PasswordBookPath = merged.Count > 0 ? merged[merged.Count - 1] : string.Empty;

            try
            {
                _settingsService.Save(Settings);
            }
            catch (Exception ex)
            {
                // 记不住路径不该让关窗这个动作失败，但要说清楚（否则用户下次启动发现没自动加载会以为是 bug）。
                AppendLog("WARN", "把记住的密码本写进设置失败：" + ex.Message);
            }
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
            /*
             * 用户 2026-09-25 第 38 条："我想要的是本次操作的日志，也就是我最近一次点开操作的日志，
             * 我看着全部的日志非常的累" —— 所以这里**默认只导本次操作**（从"本次操作开始"那行分隔线起），
             * 不再把历史日志全拼进来（历史日志另有下面那个显式入口）。
             */
            string path = _dialogService.ShowSaveFileDialog(
                "导出日志（本次操作）",
                "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                $"ArchiveFixer-本次操作_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                (int lineCount, bool fromMarker) = _logService.ExportOperationLog(path);

                AppendLog("INFO", $"日志已导出（本次操作）：{path}（{lineCount} 行）");

                _dialogService.ShowInfo(
                    fromMarker
                        ? $"本次操作的日志已导出：{lineCount} 行。{Environment.NewLine}{path}"
                        : $"这次还没有「操作开始」的标记，已导出**本次运行**的完整日志：{lineCount} 行。{Environment.NewLine}{path}");
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
                $"ArchiveFixer-全部历史日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                (int fileCount, int lineCount) = _logService.ExportAllLogs(path);

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

        private void OpenOutputDirectory()
        {
            string directory = SelectedOutputDirectory;

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Tasks.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.OutputPath))?.OutputPath ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                _dialogService.ShowWarning(
                    "还没有可打开的输出目录。"
                    + "先在「设置 → 输出位置」里选一档（或先对某个包跑一次解压），再回来打开。");
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
                _dialogService.ShowError(
                    $"打开工作区目录失败：{ex.Message}{Environment.NewLine}"
                    + $"目录是：{_pathService.WorkDirectory}（可以在资源管理器里手工打开；"
                    + "缓存根目录不可写时也会出现这种情况，可在「设置 → 高级设置」里换一个位置）。");
            }
        }

        /// <summary>一次清理/列表命令"作用于谁"的来源。</summary>
        internal enum CleanupTargetSource
        {
            /// <summary>一个都没勾 —— 什么都不做（提示用户先勾选）。</summary>
            None = 0,

            /// <summary>以**勾选**为准（界面上写的那一套，**唯一**会执行的来源）。</summary>
            Checked = 1
        }

        /// <summary>
        /// 一次清理命令的目标解析结果。
        ///
        /// <para>
        /// 抽成对象 + <see cref="ResolveCleanupTargets"/> 这一个静态判定处，是为了让
        /// "这次用的是哪一种选中含义"能被**直接断言**（测试不必去猜弹了什么框、写了哪行日志）。
        /// </para>
        /// </summary>
        internal sealed class CleanupTargetSelection
        {
            /// <summary>本次要处理的任务（按列表顺序；只看勾选）。</summary>
            public IReadOnlyList<ArchiveTask> Tasks { get; init; } = Array.Empty<ArchiveTask>();

            /// <summary>作用来源：勾选 / 一个都没勾。</summary>
            public CleanupTargetSource Source { get; init; }

            /// <summary>有没有可处理的目标。</summary>
            public bool HasTarget => Tasks.Count > 0;
        }

        /// <summary>
        /// 解析一次命令作用于谁（**唯一判定处**）。
        ///
        /// <para><b>一律只认勾选</b>（用户 2026-09-24 第 12 条亲自拍板，原话：
        /// "你只需要操作我选中的文件，其他的不用管"）：</para>
        /// <list type="number">
        /// <item><description>有勾选任务 → 就作用于勾选的那些；</description></item>
        /// <item><description>一个都没勾 → 返回空，调用方按
        /// <see cref="StatusText.NoCheckedTaskPromptFormat"/> **只提示、什么都不做**。</description></item>
        /// </list>
        ///
        /// <para>⛔ <b>这里曾经有一条"一个都没勾时退回当前点中的那一行"的兜底 —— 已经删掉，
        /// 不要再加回来</b>。用户原话："我即使没有特地的没有去选中，你为什么还要去操作，
        /// 这个操作导致我彻底的卡死了"。两套"选中"（行高亮 / 勾选框）并存时，兜底等于
        /// **替他决定要动哪些文件**：他以为"我没勾就是不动"，程序却拿高亮那一行开了工。
        /// 代价（用户点一行但没勾 → 收到提示）远小于代价的另一面（动了没打算动的文件）。</para>
        /// </summary>
        internal static CleanupTargetSelection ResolveCleanupTargets(IEnumerable<ArchiveTask>? tasks)
        {
            List<ArchiveTask> checkedTasks = tasks?
                .Where(task => task != null && task.IsSelected)
                .ToList()
                ?? new List<ArchiveTask>();

            if (checkedTasks.Count > 0)
            {
                return new CleanupTargetSelection
                {
                    Tasks = checkedTasks,
                    Source = CleanupTargetSource.Checked
                };
            }

            return new CleanupTargetSelection { Source = CleanupTargetSource.None };
        }

        /// <summary>
        /// 把"这次用的是哪一种选中含义"写进日志（**用了哪一种必须留痕**）。
        ///
        /// 用户事后要能回答"刚才为什么只动了这一个 / 为什么动了这 7 个" —— 只写"已取消"
        /// 或什么都不写，排查就只能靠猜（那次真机验收就是这么被误导的）。
        /// </summary>
        internal static string DescribeCleanupTargets(string title, CleanupTargetSelection selection)
        {
            if (selection.Source == CleanupTargetSource.Checked)
            {
                return selection.Tasks.Count == 1
                    ? $"{title}：以勾选为准，本次作用于勾选的 1 个任务「{selection.Tasks[0].FileName}」。"
                    : $"{title}：以勾选为准，本次作用于勾选的 {selection.Tasks.Count} 个任务"
                      + "（逐个处理，每个任务都会先给你看预览与确认框）。";
            }

            return $"{title}：没有勾选任何任务，已取消（一个文件、一个字节都没动）。";
        }

        /// <summary>
        /// 把解析出来的目标展开成"真正要跑几次"。
        ///
        /// <para>
        /// 「删除其余物」「清理空文件夹」是**每个任务各删自己那一份**，所以逐个任务各跑一次；
        /// 「删除本目录全部其余物」的作用域本来就是**整个输出目录**，
        /// 于是同一个输出目录只算一次 —— 勾了同一个源目录下的 5 个包时，
        /// 不该把"整个目录都删掉"这件事问 5 遍（那 5 遍的答案还必然是一样的）。
        /// </para>
        /// </summary>
        internal static List<ArchiveTask> ExpandCleanupTargets(
            CleanupTargetSelection selection,
            bool everythingInDirectory)
        {
            if (!everythingInDirectory)
            {
                return selection.Tasks.ToList();
            }

            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expanded = new List<ArchiveTask>();

            foreach (ArchiveTask task in selection.Tasks)
            {
                string directory = string.IsNullOrWhiteSpace(task.OutputPath)
                    ? string.Empty
                    : SafePathHelper.GetFullPathSafe(task.OutputPath);

                // 路径认不出来时也要留一条：下面会明确报"这个任务还没有输出目录"，比静默跳过好。
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = task.OutputPath ?? string.Empty;
                }

                if (seenDirectories.Add(directory))
                {
                    expanded.Add(task);
                }
            }

            return expanded;
        }

        /// <summary>
        /// 其余物/空文件夹的删除流程（③「清理与删除」页那三个按钮）：
        /// **判定作用于谁 → 预览 → 红色确认（默认回收站，勾选框切激进档）→（激进档）二次确认 → 后台执行 → 写日志**。
        ///
        /// <para>
        /// <b>选中口径（2026-09-24 用户第 12 条亲自拍板：一律只认勾选）</b>：这三个入口统一走
        /// <see cref="ResolveCleanupTargets"/> —— 只作用于**勾选**的任务；一个都没勾就只提示
        /// （<see cref="StatusText.NoCheckedTaskPromptFormat"/>），一个字节都不动。
        /// ⛔ 曾经那条"一个都没勾时退回当前点中的那一行"的兜底**已删除**：用户原话
        /// "你只需要操作我选中的文件，其他的不用管"，兜底等于替他决定要动哪些文件。
        /// （早期还有一个反向缺陷：这三个入口只看当前行，勾了任务的用户反而收到"请先选中一个任务"。）
        /// </para>
        /// <para>
        /// 多任务时**合并成一次确认**（用户 2026-09-22 拍板，原话："要合并成 1 次确认，不要有冗余操作"）：
        /// 先把**全部**目标各预览一遍（后台），把"每个任务的作用范围 + 合计条目数/字节数 +
        /// 哪些任务含源包"合成一个确认框，用户点一次就算数；确认之后逐个执行，
        /// 单个任务失败不中断整批（不变量 9），最后给一次汇总（成功 N / 失败 M + 原因 + 释放字节数）。
        /// 以前是"勾了 5 个就弹 5 次预览 + 5 次确认"，那正是用户说的冗余。
        /// </para>
        /// <para>
        /// 「删除本目录全部其余物」保持整目录语义：同目录只算一次（<see cref="ExpandCleanupTargets"/>），
        /// 所以它天然只会有一次确认，且确认框里点名"影响该目录下 N 个包"。
        /// </para>
        /// <para>
        /// 线程纪律（本项目历史上因 UI 线程干重活卡死过）：
        /// 预览统计与删除执行全部在 <see cref="Task.Run(System.Action)"/> 里；
        /// 界面线程只做两件事 —— 弹确认框、把结论写进日志/提示。
        /// </para>
        /// <para>
        /// 取消的语义：任何一步没确认，那一个任务就**什么都不做**（一个字节都不动），只留一条日志。
        /// </para>
        /// </summary>
        /// <param name="scope">作用域类型（其余物 / 空文件夹）。</param>
        /// <param name="deleteScope">
        /// 作用域大小：只删每个目标任务自己那一份（默认），还是删整个共享目录下的全部。
        /// 后者只由「删除本目录全部其余物…」这个显式入口传进来。
        /// </param>
        private async Task RunCleanupAsync(CleanupScope scope, ArtifactDeleteScope deleteScope)
        {
            bool everything = deleteScope == ArtifactDeleteScope.EverythingInDirectory;

            string title = scope == CleanupScope.EmptyFolders
                ? "清理空文件夹"
                : everything ? "删除本目录全部其余物" : "删除其余物";

            CleanupTargetSelection selection = ResolveCleanupTargets(Tasks);

            AppendLog(selection.HasTarget ? "INFO" : "WARN", DescribeCleanupTargets(title, selection));

            if (!selection.HasTarget)
            {
                // 一律只认勾选：一个都没勾就只提示、什么都不做（用户 2026-09-24 第 12 条）。
                _dialogService.ShowWarning(PromptNoCheckedTask(title));
                return;
            }

            List<ArchiveTask> targets = ExpandCleanupTargets(selection, everything);

            EnterBusy();

            try
            {
                // ① 预览**全部**目标（每个任务各一条日志，便于事后核对"这次动了哪些目录"）。
                var plans = new List<CleanupTaskPlan>(targets.Count);

                foreach (ArchiveTask task in targets)
                {
                    plans.Add(await BuildCleanupPlanAsync(scope, deleteScope, task));
                }

                List<CleanupTaskPlan> actionable = plans.Where(plan => plan.IsActionable).ToList();

                if (actionable.Count == 0)
                {
                    // 没有可删的：一次提示说清每个任务为什么没得删（以前是每个任务一个提示框）。
                    string nothing = BuildNothingToCleanText(title, plans);

                    AppendLog("WARN", nothing.Replace(Environment.NewLine, " "));
                    _dialogService.ShowInfo(nothing);
                    return;
                }

                CleanupPreview merged = MergeCleanupPreviews(scope, actionable);
                string scopeNote = BuildBatchScopeNote(scope, actionable);

                /*
                 * ② **一次**确认（红色）。档位语义与单任务时完全一样：
                 * 默认档取自设置项「其余物清理默认档」，勾选框永远是"切到另一档"
                 * —— 这一层判断仍然只有 DeriveCleanupDecision 一处实现。
                 */
                CleanupDecision decision = DeriveCleanupDecision(
                    Settings.RestRemovalDefaultMode,
                    merged,
                    title,
                    scopeNote,
                    actionable);

                bool confirmed = _dialogService.ShowDestructiveConfirmWithOption(
                    decision.ConfirmText,
                    decision.ConfirmButtonText,
                    decision.OptionText,
                    optionCheckedByDefault: decision.OptionCheckedByDefault,
                    out bool optionChecked);

                if (!confirmed)
                {
                    AppendLog(
                        "INFO",
                        $"{title}：用户取消，{actionable.Count} 个任务一个字节都没动"
                        + "（本次只弹这一次确认，取消即整批不做）。");
                    return;
                }

                DeleteMode mode = decision.Resolve(optionChecked);

                if (mode == DeleteMode.Permanent)
                {
                    /*
                     * ③ 激进档的**二次确认**：整批只再来这一次（红色 + 必须勾"我知道不可恢复"）。
                     * 批里每删一个都问一遍不是更安全，只是更烦；真正的红线是"不可逆操作必须显式确认"。
                     */
                    bool acknowledged = _dialogService.ShowDestructiveConfirm(
                        BuildCleanupConfirmText(title, actionable, merged, DeleteMode.Permanent, scopeNote),
                        "彻底删除",
                        "我知道彻底删除不可恢复，这些内容不会进回收站",
                        out bool irreversibleAcknowledged);

                    if (!acknowledged || !irreversibleAcknowledged)
                    {
                        AppendLog(
                            "INFO",
                            $"{title}：没有通过彻底删除的二次确认，{actionable.Count} 个任务一个字节都没动。");
                        return;
                    }
                }

                // ④ 逐个执行（后台）。**单个任务失败不中断整批**：这一条记 ERROR 之后继续下一个。
                var tally = new BatchCleanupTally();

                foreach (CleanupTaskPlan plan in actionable)
                {
                    ArchiveTask task = plan.Task!;

                    try
                    {
                        CleanupPreview preview = plan.Preview!;

                        CleanupOutcome outcome = await Task.Run(() => scope == CleanupScope.EmptyFolders
                            ? _cleanupService.CleanEmptyFolders(task.OutputPath, mode)
                            : _cleanupService.CleanProcessArtifacts(task, mode, preview, deleteScope));

                        WriteCleanupOutcomeLog(title, task, preview, outcome);
                        tally.Record(task, outcome);
                    }
                    catch (Exception ex)
                    {
                        tally.RecordException(task, ex.Message);
                        AppendLog("ERROR", $"{title}（{task.FileName}）失败：" + ex.Message);
                    }
                }

                // ⑤ 汇总一次：成功 N / 失败 M（各自原因）/ 释放字节数。
                string summary = BuildBatchCleanupSummary(title, mode, actionable.Count, tally);

                /*
                 * 有任何一项没删掉（或整批一项都没成功）就绝不能显示成"全部成功"
                 * （不变量 6 的同一口径）：用警告框把原因列出来。
                 */
                if (tally.HasAnyFailure)
                {
                    _dialogService.ShowWarning(summary);
                }
                else
                {
                    _dialogService.ShowInfo(summary);
                }
            }
            finally
            {
                ExitBusy();
            }
        }

        /// <summary>
        /// 一个清理目标"这次到底会删什么"的预览结论（多任务合并确认的输入）。
        ///
        /// <para>
        /// <see cref="Preview"/> 为 null 时 <see cref="SkipReason"/> 说明为什么这个任务没得删
        /// （例如"还没有输出目录（先解压一次）"）—— 那种任务不参与确认框，但要在
        /// "没有可删的"提示里点名，免得用户以为程序把它漏了。
        /// </para>
        /// </summary>
        internal sealed class CleanupTaskPlan
        {
            public ArchiveTask? Task { get; init; }

            public CleanupPreview? Preview { get; init; }

            /// <summary>共享输出目录的作用域提醒（单任务那份，合并时汇总成一句）。</summary>
            public string ScopeNote { get; init; } = string.Empty;

            /// <summary>没得删的原因（为空表示这是可执行的目标）。</summary>
            public string SkipReason { get; init; } = string.Empty;

            public bool IsActionable => Preview is { HasTarget: true };

            /// <summary>这个任务那一份的作用范围（给确认框里逐个列出用）。</summary>
            public string DescribeTarget()
            {
                string name = Task?.FileName ?? "（未知任务）";

                return Preview == null
                    ? $"· {name}：{SkipReason}"
                    : $"· {name} → {Preview.ScopePath}（顶层 {Preview.ItemCount} 项 / {Preview.EntryCount} 个条目 / {Preview.TotalBytes} 字节）";
            }
        }

        /// <summary>预览一个清理目标（后台线程干活；界面线程只拿结论）。</summary>
        private async Task<CleanupTaskPlan> BuildCleanupPlanAsync(
            CleanupScope scope,
            ArtifactDeleteScope deleteScope,
            ArchiveTask task)
        {
            if (string.IsNullOrWhiteSpace(task.OutputPath))
            {
                const string reason = "还没有输出目录（先解压一次）";

                AppendLog("WARN", $"清理：任务「{task.FileName}」{reason}，已跳过。");

                return new CleanupTaskPlan { Task = task, SkipReason = reason };
            }

            CleanupPreview preview = await Task.Run(() => scope == CleanupScope.EmptyFolders
                ? _cleanupService.PreviewEmptyFolders(task.OutputPath)
                : _cleanupService.PreviewProcessArtifacts(task, deleteScope));

            AppendLog(preview.HasTarget ? "INFO" : "WARN", $"清理（{task.FileName}）：{preview.Message}");

            return new CleanupTaskPlan
            {
                Task = task,
                Preview = preview,
                SkipReason = preview.HasTarget ? string.Empty : preview.Message,
                ScopeNote = BuildSharedScopeNote(scope, task, preview)
            };
        }

        /// <summary>
        /// 把多个目标的预览合成一份（确认框上的"合计"就取它）。
        ///
        /// 合口径：条目数 / 字节数 / 顶层项数**相加**；"读不全"只要有一个不完整就整体标注；
        /// 作用范围同目录时写那一个目录，否则写"N 个任务（下面逐个列出）"。
        /// </summary>
        internal static CleanupPreview MergeCleanupPreviews(CleanupScope scope, IReadOnlyList<CleanupTaskPlan> plans)
        {
            var actionable = plans.Where(plan => plan.IsActionable).ToList();

            int itemCount = 0;
            int entryCount = 0;
            long totalBytes = 0;
            bool determined = true;
            int sourcePackageCount = 0;
            int affectedPackageCount = 0;

            var items = new List<string>();
            var sourceNames = new List<string>();
            var affectedNames = new List<string>();
            var scopePaths = new List<string>();

            ArtifactCleanupScope? resolvedScope = null;

            foreach (CleanupTaskPlan plan in actionable)
            {
                CleanupPreview preview = plan.Preview!;

                itemCount += preview.ItemCount;
                entryCount += preview.EntryCount;
                totalBytes += preview.TotalBytes;
                determined &= preview.Determined;
                sourcePackageCount += preview.SourcePackageCount;
                affectedPackageCount += preview.AffectedPackageCount;

                foreach (string item in preview.Items)
                {
                    if (items.Count < CleanupPreview.MaxListedItems)
                    {
                        items.Add(item);
                    }
                }

                foreach (string name in preview.SourcePackageNames)
                {
                    if (sourceNames.Count < CleanupPreview.MaxListedItems * 2)
                    {
                        sourceNames.Add(name);
                    }
                }

                foreach (string name in preview.AffectedPackageNames)
                {
                    if (affectedNames.Count < CleanupPreview.MaxListedItems * 2)
                    {
                        affectedNames.Add(name);
                    }
                }

                if (!string.IsNullOrWhiteSpace(preview.ScopePath) && !scopePaths.Contains(preview.ScopePath))
                {
                    scopePaths.Add(preview.ScopePath);
                }

                if (preview.ResolvedScope?.DeletesEverythingInDirectory == true)
                {
                    resolvedScope = preview.ResolvedScope;
                }
            }

            string scopePath = scopePaths.Count == 1
                ? scopePaths[0]
                : $"{actionable.Count} 个任务各自的目录（下面逐个列出）";

            string message = actionable.Count == 1
                ? actionable[0].Preview!.Message
                : $"{actionable.Count} 个任务：合计顶层 {itemCount} 项 / {entryCount} 个条目 / {totalBytes} 字节。";

            return new CleanupPreview
            {
                Scope = scope,
                ScopePath = scopePath,
                AllowedRoot = actionable.Count == 1 ? actionable[0].Preview!.AllowedRoot : string.Empty,
                ResolvedScope = resolvedScope,
                HasTarget = actionable.Count > 0,
                ItemCount = itemCount,
                EntryCount = entryCount,
                TotalBytes = totalBytes,
                Determined = determined,
                Items = items,
                SourcePackageNames = sourceNames,
                SourcePackageCount = sourcePackageCount,
                AffectedPackageCount = affectedPackageCount,
                AffectedPackageNames = affectedNames,
                Message = message
            };
        }

        /// <summary>
        /// 多任务时的作用域提醒：把各任务那句"只删自己那一份 / 会影响整个目录"合并成一句。
        ///
        /// <para>
        /// 为什么要合并：单任务那句提醒（<see cref="BuildSharedScopeNote"/>）在批里会出现 N 次，
        /// 而确认框只有一次 —— 直接拼 N 遍就是复读。去重之后保留**不同**的说法：
        /// "只删自己那一份"和"会影响同目录的其它包"是两种完全不同的后果，都得留下。
        /// </para>
        /// </summary>
        internal static string BuildBatchScopeNote(CleanupScope scope, IReadOnlyList<CleanupTaskPlan> plans)
        {
            if (plans.Count <= 1)
            {
                return plans.Count == 1 ? plans[0].ScopeNote : string.Empty;
            }

            var notes = new List<string>();

            foreach (CleanupTaskPlan plan in plans)
            {
                if (!string.IsNullOrWhiteSpace(plan.ScopeNote) && !notes.Contains(plan.ScopeNote))
                {
                    notes.Add(plan.ScopeNote);
                }
            }

            string shared = $"本次一次确认、覆盖 {plans.Count} 个任务："
                            + (scope == CleanupScope.EmptyFolders
                                ? "每个任务的输出根都会清掉其中“任意层级都没有文件”的子目录。"
                                : "每个任务各删自己那一份其余物（下面逐个列出各自的目录）。");

            return notes.Count == 0 ? shared : shared + Environment.NewLine + string.Join(Environment.NewLine, notes);
        }

        /// <summary>"一个都没得删"时的合并提示（点名每个任务的原因，而不是弹出 N 个提示框）。</summary>
        internal static string BuildNothingToCleanText(string title, IReadOnlyList<CleanupTaskPlan> plans)
        {
            if (plans.Count == 0)
            {
                return $"{title}：没有可处理的任务。";
            }

            if (plans.Count == 1)
            {
                return $"{title}（{plans[0].Task?.FileName}）：{plans[0].SkipReason}";
            }

            var builder = new StringBuilder();

            builder.AppendLine($"{title}：{plans.Count} 个任务都没有可删的东西，一个字节都没动。");

            foreach (CleanupTaskPlan plan in plans)
            {
                builder.AppendLine("· " + (plan.Task?.FileName ?? "（未知任务）") + "：" + plan.SkipReason);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>整批清理的计数器（成功/失败任务数、条目数、释放字节数、失败原因）。</summary>
        internal sealed class BatchCleanupTally
        {
            private readonly List<string> _failureReasons = new();

            public int SucceededTaskCount { get; private set; }

            public int FailedTaskCount { get; private set; }

            public int SucceededItemCount { get; private set; }

            public int FailedItemCount { get; private set; }

            public long FreedBytes { get; private set; }

            public long RecycledBytes { get; private set; }

            /// <summary>整批有没有任何一项没删掉（含"任务直接抛异常"）。</summary>
            public bool HasAnyFailure => FailedTaskCount > 0 || FailedItemCount > 0;

            public IReadOnlyList<string> FailureReasons => _failureReasons;

            public void Record(ArchiveTask task, CleanupOutcome outcome)
            {
                if (outcome.FailureCount > 0 || outcome.SucceededNothing)
                {
                    FailedTaskCount++;
                    FailedItemCount += outcome.FailureCount;
                    _failureReasons.Add($"{task.FileName}：{outcome.Message}");
                }
                else
                {
                    SucceededTaskCount++;
                }

                SucceededItemCount += outcome.SuccessCount;
                FreedBytes += outcome.FreedBytes;
                RecycledBytes += outcome.RecycledBytes;

                foreach (string reason in outcome.FailureReasons)
                {
                    if (_failureReasons.Count < CleanupPreview.MaxListedItems * 2)
                    {
                        _failureReasons.Add($"{task.FileName}：{reason}");
                    }
                }
            }

            public void RecordException(ArchiveTask task, string message)
            {
                FailedTaskCount++;
                _failureReasons.Add($"{task.FileName}：{message}");
            }
        }

        /// <summary>
        /// 整批清理的收尾报告：**成功 N / 失败 M（含各自原因）/ 释放字节数**。
        ///
        /// 只要有任何一项没删掉就不说"全部成功"（<see cref="BatchCleanupTally.HasAnyFailure"/> 的调用方据此选提示框）。
        /// </summary>
        internal static string BuildBatchCleanupSummary(
            string title,
            DeleteMode mode,
            int targetCount,
            BatchCleanupTally tally)
        {
            string modeText = mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站";

            var builder = new StringBuilder();

            builder.Append(
                $"{title}：{targetCount} 个任务，{modeText}成功 {tally.SucceededTaskCount} 个 / 失败 {tally.FailedTaskCount} 个；"
                + $"条目成功 {tally.SucceededItemCount} 项、失败 {tally.FailedItemCount} 项。");

            builder.Append(
                mode == DeleteMode.Permanent
                    ? $"释放 {tally.FreedBytes} 字节。"
                    : $"移入回收站 {tally.RecycledBytes} 字节（回收站里的内容未真正释放空间）。");

            if (tally.HasAnyFailure && tally.FailureReasons.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("没删掉的原因：");

                foreach (string reason in tally.FailureReasons.Take(CleanupPreview.MaxListedItems))
                {
                    builder.AppendLine("· " + reason);
                }
            }

            return builder.ToString().TrimEnd();
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
            return DeriveCleanupDecision(restRemovalDefaultMode, preview, title, scopeNote, plans: null);
        }

        /// <summary>
        /// 同上，但确认框正文按**整批**渲染（多任务合并成一次确认时用）。
        /// 档位判定一模一样 —— 变的只有正文里多出来的"各任务作用范围"那一段。
        /// </summary>
        internal static CleanupDecision DeriveCleanupDecision(
            string? restRemovalDefaultMode,
            CleanupPreview preview,
            string title,
            string? scopeNote,
            IReadOnlyList<CleanupTaskPlan>? plans)
        {
            bool defaultIsPermanent = RestRemovalModes.IsPermanent(restRemovalDefaultMode);

            DeleteMode defaultMode = defaultIsPermanent ? DeleteMode.Permanent : DeleteMode.RecycleBin;
            DeleteMode optionMode = defaultIsPermanent ? DeleteMode.RecycleBin : DeleteMode.Permanent;

            return new CleanupDecision
            {
                DefaultMode = defaultMode,
                OptionMode = optionMode,
                ConfirmText = plans == null
                    ? BuildCleanupConfirmText(title, preview, defaultMode, scopeNote)
                    : BuildCleanupConfirmText(title, plans, preview, defaultMode, scopeNote),
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
            builder.Append(BuildCleanupBody(preview, mode, scopeNote));

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// **多任务合并确认**的正文（用户 2026-09-22 拍板："要合并成 1 次确认，不要有冗余操作"）。
        ///
        /// <para>
        /// 与单任务版本共用同一段正文（<see cref="BuildCleanupBody"/>：合计条目数/字节数、源包清单、
        /// 作用域提醒、档位说明），只在前面多一段"各任务的作用范围"清单 ——
        /// 于是"确认框里到底说了什么"仍然只有一处实现，不会两套措辞各说各的。
        /// </para>
        /// <para>
        /// 批里只有一个目标时**逐字节等同**旧格式（不该凭空多出一段清单）。
        /// </para>
        /// </summary>
        internal static string BuildCleanupConfirmText(
            string title,
            IReadOnlyList<CleanupTaskPlan> plans,
            CleanupPreview merged,
            DeleteMode mode,
            string? scopeNote = null)
        {
            if (plans.Count <= 1)
            {
                return BuildCleanupConfirmText(
                    title,
                    plans.Count == 1 ? plans[0].Preview ?? merged : merged,
                    mode,
                    scopeNote);
            }

            var builder = new StringBuilder();

            builder.AppendLine(title);
            builder.AppendLine();
            builder.AppendLine($"本次只确认这一次：将清理以下 {plans.Count} 个任务（确认后逐个执行，任何一个失败都不会中断整批）。");

            foreach (CleanupTaskPlan plan in plans)
            {
                builder.AppendLine(plan.DescribeTarget());
            }

            builder.AppendLine();

            // 含源包的任务要点名（删了要重新下载）—— 逐个任务一条，比一个总数更能让人停一下。
            List<CleanupTaskPlan> withSources = plans
                .Where(plan => plan.Preview is { SourcePackageCount: > 0 })
                .ToList();

            if (withSources.Count > 0)
            {
                builder.AppendLine($"⚠ 其中 {withSources.Count} 个任务含源包文件（压缩包本身），删掉后需要重新下载：");

                foreach (CleanupTaskPlan plan in withSources)
                {
                    builder.AppendLine(
                        $"· {plan.Task?.FileName}：{plan.Preview!.SourcePackageCount} 个"
                        + (plan.Preview.SourcePackageNames.Count > 0
                            ? "（" + string.Join("、", plan.Preview.SourcePackageNames) + "）"
                            : string.Empty));
                }

                builder.AppendLine();
            }

            builder.Append(BuildCleanupBody(merged, mode, scopeNote));

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// 确认框正文的公共部分（单任务 / 多任务两版共用，避免"同一个确认框两套措辞"）。
        /// </summary>
        private static string BuildCleanupBody(CleanupPreview preview, DeleteMode mode, string? scopeNote)
        {
            var builder = new StringBuilder();

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
                return "当前任务的输出目录就是源包所在目录（包名与目录同名时合并了重复的一层），"
                       + "本次会清掉这个目录下所有“任意层级都没有文件”的子目录（不只是本任务产出的；不含任何文件）。";
            }

            if (preview?.ResolvedScope?.DeletesEverythingInDirectory == true)
            {
                /*
                 * 「删除本目录全部其余物」是三个入口里作用域最大的一个（整目录、含别的包的源包），
                 * 所以**必须点名会影响几个包**：只说"会影响同目录的所有包"是一句形容词，
                 * 用户没法判断这次动的是 2 个还是 20 个（预览里的包名清单来自
                 * CleanupPreview.AffectedPackageNames，数不出来时保持 0，绝不编数字）。
                 */
                string affected = preview.AffectedPackageCount > 0
                    ? "受影响：" + preview.AffectedPackageCount + " 个包的其余物会一起被删"
                      + (preview.AffectedPackageNames.Count > 0
                          ? "（" + string.Join("、", preview.AffectedPackageNames)
                            + (preview.AffectedPackageCount > preview.AffectedPackageNames.Count ? " …" : string.Empty)
                            + "）。"
                          : "。")
                    : "受影响：这个目录下的其余物（分不出包名，整目录一起算）。";

                return "这是「删除本目录全部其余物」：输出目录与同目录的其它包共用，"
                       + "本次会一并删掉它们的其余物（含各自的源包文件），删掉后都需要重新下载。"
                       + affected;
            }

            return "当前任务的输出目录与同目录的其它包共用（包名与目录同名时合并了重复的一层），"
                   + "本次只删本任务那一份其余物（按包基名分开的子目录），不会碰其它包的其余物。";
        }

        /// <summary>任务的输出目录是不是就是源包所在目录（场景 B 塌缩之后的形态）。</summary>
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

                /*
                 * ⚠ 刻意**不建目录**（用户 2026-09-25 第 28 条的解释落地）：选择输出位置只是"记住一个地址"，
                 * 这里一个 Directory.CreateDirectory 都不能有 —— 用户挑了一个位置又改主意时，
                 * 盘上不该多出一个他没让程序建的空文件夹（用户实际遇到过：设置里的"指定位置"
                 * 在磁盘上真冒出来一个目录，他问"这个又是什么鬼"）。
                 * 目录只在**真正往它里面定稿**那一步才建（ExtractionCoordinator 的 stage commit：
                 * EnsureDirectoryExists(destinationDirectory)），失败 / 取消时它根本不会被建出来。
                 */
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

                /*
                 * ⚠ 这一句是 2026-09-25 第 31 条的关键：上面那几行直接写了 Settings，
                 * 而②页「落点（解压到哪）」那几个控件绑的是 SettingsEditor 算出来的属性 ——
                 * 不通知的话，用户从①页选完目录去②页，看到的还是老的单选状态与灰着的路径框。
                 */
                NotifyOutputPlacementChangedEverywhere();

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

        /// <summary>
        /// 导入后无用物提醒的替身入口（**只给测试**；正式路径永远是 null = 走真弹窗）。
        /// 见 <see cref="ScanCoordinator.JunkReminderOverride"/>。
        /// </summary>
        internal Func<string, ImportJunkAnswer>? JunkReminderOverride
        {
            get => _scanCoordinator.JunkReminderOverride;
            set => _scanCoordinator.JunkReminderOverride = value;
        }

        /// <summary>
        /// 移除**勾选的**任务（用户 2026-09-24 第 15 条："列表要能删无用物"）。
        ///
        /// <para>只动任务列表：源文件、输出目录、日志一个字节都不碰 —— 移除错了再"添加"一次就回来了，
        /// 所以这里**不做二次确认**（确认框留给真正不可逆的事：删源、清工作区、清其余物）。</para>
        /// </summary>
        private void RemoveCheckedTasks()
        {
            List<ArchiveTask> checkedTasks = Tasks.Where(task => task.IsSelected).ToList();

            if (checkedTasks.Count == 0)
            {
                _dialogService.ShowInfo(StatusText.RemoveCheckedTasksNoneText);
                return;
            }

            RemoveTasksCore(checkedTasks);
        }

        /// <summary>
        /// 按源路径移除任务（导入后的无用物提醒里点"从列表里移除这些"走这条）。
        /// </summary>
        /// <returns>真的移掉了几个（路径对不上的不算）。</returns>
        internal int RemoveTasksBySourcePaths(IEnumerable<string>? paths)
        {
            if (paths == null)
            {
                return 0;
            }

            var wanted = new HashSet<string>(
                paths.Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);

            if (wanted.Count == 0)
            {
                return 0;
            }

            List<ArchiveTask> matched = Tasks
                .Where(task => wanted.Contains(task.CurrentPath ?? string.Empty))
                .ToList();

            if (matched.Count == 0)
            {
                return 0;
            }

            RemoveTasksCore(matched);

            return matched.Count;
        }

        /// <summary>移除一批任务并收尾（顺序、索引、汇总、日志一处收口）。</summary>
        private void RemoveTasksCore(IReadOnlyList<ArchiveTask> toRemove)
        {
            foreach (ArchiveTask task in toRemove)
            {
                Tasks.Remove(task);
            }

            RebuildTaskIndex();
            UpdateSummary();

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RemoveTasksLogFormat,
                    toRemove.Count));
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

            /*
             * 「本次选项」那一行（规格 §9.2 硬要求⑥：要能回答"这次为什么解到这里"）。
             * 与 OutputPath 的分工：那个是"落到哪"，这个是"为什么落那儿"。
             * 空着就不写行 —— 手动「只解压」的任务本来就没有这一项。
             */
            if (!string.IsNullOrWhiteSpace(task.RunOptionsNote))
            {
                builder.AppendLine("本次选项：" + task.RunOptionsNote);
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

            if (string.IsNullOrWhiteSpace(directory))
            {
                // 点了没反应是最难排查的一类"界面在撒谎"：说清为什么 + 下一步做什么。
                _dialogService.ShowInfo(
                    $"「{task.FileName}」还没有可打开的目录（路径信息不完整）。"
                    + "可以先右键「重新扫描此文件」，或把文件重新添加一次。");
                return;
            }

            _pathService.OpenDirectory(directory);
        }

        private void OpenTaskOutputDirectory(object? parameter)
        {
            if (parameter is not ArchiveTask task)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(task.OutputPath))
            {
                _dialogService.ShowInfo(
                    $"「{task.FileName}」还没有输出目录（先解压一次才会有）。"
                    + "可以先勾选它并点「只解压」或「一键处理」。");
                return;
            }

            _pathService.OpenDirectory(task.OutputPath);
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

        /// <summary>
        /// 整份替换任务列表（导入路径、以及"先清空再加"那两步）的**唯一收口**。
        ///
        /// <para>为什么必须收口（用户 2026-09-24 第 12 条"卡死"的修法）：</para>
        /// <list type="number">
        /// <item><description><b>集合层面只通知一次</b> —— 走
        /// <see cref="RangeObservableCollection{T}.ReplaceAll"/>，几百项不再触发几百次界面刷新；</description></item>
        /// <item><description><b>索引重排一次</b>（<see cref="RebuildTaskIndex"/>）—— 替换与追加两种语义
        /// 都需要（扫描给每项编的是它自己那一批的号）；</description></item>
        /// <item><description><b>当前行不能指着已经不在列表里的对象</b>：把
        /// <see cref="SelectedTask"/> 清成 null，否则右键菜单/详情会拿到一个幽灵任务；</description></item>
        /// <item><description><b>汇总只重算一次</b>（除非调用方明确说不要，见
        /// <paramref name="refreshSummary"/>）—— 「批量添加 N 项只触发 1 次全量统计重算」有测试钉住。</description></item>
        /// </list>
        /// </summary>
        /// <param name="tasks">新的整份列表。</param>
        /// <param name="refreshSummary">
        /// 是否在这里重算汇总。默认 true；导入路径在"先清空再加"的第一步传 false ——
        /// 那一步只是过渡状态，最终那一次才需要算（否则一次导入会算两遍整表）。
        /// </param>
        internal void ReplaceAllTasks(IEnumerable<ArchiveTask>? tasks, bool refreshSummary = true)
        {
            Tasks.ReplaceAll(tasks);

            RebuildTaskIndex();

            // 清空/替换之后，DataGrid 的当前行可能还指着已经被移除的对象。
            if (SelectedTask != null && !Tasks.Contains(SelectedTask))
            {
                SelectedTask = null;
            }

            if (refreshSummary)
            {
                UpdateSummary();
            }
        }

        private void RaiseAllCommandCanExecuteChanged()
        {
            foreach (ICommand command in new[]
                     {
                 AddFilesCommand,
                 AddFolderCommand,
                 AppendFilesCommand,
                 AppendFolderCommand,
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

                 SelectAllTasksCommand,
                 SelectNoneTasksCommand,
                 InvertTaskSelectionCommand,
                 SelectSoleTaskCommand,

                 OpenSettingsCommand,
                 OpenPasswordListCommand,
                 OpenPackingCommand,
                 SaveSettingsCommand,
                 ExportLogCommand,
                 ExportAllLogsCommand,
                 CopyFailedListCommand,
                 OpenOutputDirectoryCommand,
                 SelectOutputDirectoryCommand,
                 CopyOutputLocationCommand,
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
