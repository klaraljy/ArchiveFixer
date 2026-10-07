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

        /// <summary>
        /// 解压管线协调器（**内部**：同程序集的调用方与单测用）。
        ///
        /// <para>为什么留这个口子：有几件事**只有视图模型这条路上才有**（导入后的空间体检、
        /// 换输出位置后的体检），而它们的判据全在协调器里（<c>SpaceProbeOverride</c> 等注入点）。
        /// 真机上没法把盘写成只剩 4 KiB，不给这个口子，"盘不够时到底报了什么"就只能靠读代码。</para>
        /// </summary>
        internal ExtractionCoordinator ExtractionPipeline => _extractionCoordinator;
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
                     * 设置对象换成新的一份了（恢复默认 / 保存之后）：**两页**设置编辑器都必须指向新对象 ——
                     * ②③④⑥四页绑的是 SettingsEditor，⑤打包页绑的是 PackingEditor.Settings，两者各存一份引用。
                     *
                     * ⚠ 2026-09-26 补 PackingEditor 那一句（真缺陷）：⑥页点「恢复默认设置」时
                     * `Settings = ResetToDefault()` 换掉的是**新对象**，而⑤页那只还咬着旧对象 ——
                     * 用户之后在⑤页选落点 / 原包 / 其余物，写进的是那个**已经没人读的死对象**：
                     * 日志说"已写进设置"，重启却全回到默认（他报的"没有记忆性"的另一种形态）。
                     *
                     * （构造阶段这两只都是 null：编辑器还没建出来，?.就是为它们准备的。）
                     */
                    SettingsEditor?.AttachSharedSettings(_settings);
                    PackingEditor?.AttachSharedSettings(_settings);
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
        /// 只是把"点保存 → 关窗"换成"改哪一项都会自动存"（2026-09-26 起连那个按钮都不需要）。
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

                // 反馈方式也写在「关于」里（用户 2026-09-26 要求写进标题，这里同步 —— 邮箱只有一处字面量）。
                builder.AppendLine("遇到问题请反馈给作者：发邮件 " + StatusText.FeedbackEmail);
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
             * 数据根**固定**是程序目录下的 data（用户 2026-09-30：删掉「缓存根目录」设置项）。
             *
             * ⛔ 这里刻意**不写** `_pathService.DataRootDirectory = ...`：
             * 它本来就是 <see cref="PathService.DefaultDataRootDirectory"/>，而测试装配时会把
             * DataRootDirectory 指到临时目录 —— 在这里"顺手复位成默认值"会把测试的数据根
             * 顶回程序目录（真实后果：用例往测试输出目录里写日志 / 设置）。
             * 数据根只有一个出口：PathService 自己的默认值。
             */

            /*
             * 工作区根（用户 2026-09-30：**唯一来源 = 这一单的目标目录**）。
             *
             * ⚠ 真正"落在目标目录里"的解析发生在**每批开工前**
             * （ExtractionCoordinator.ApplyBatchWorkspaceRoot）—— 只有那时才知道这一批会处理哪些包、
             * 目标目录在哪。启动 / 保存设置时根本还没有这一批的任务，所以这里只能取"当前生效的那个根"
             * （本次会话用过的，见 WorkspaceRootIndex.SessionRoots；没有就是老位置 <数据根>\work）。
             *
             * ⚠ 这里**只服务于 ③ 页的残留扫描与启动日志**，不是这一批真正会用的根。
             * ⛔ 批首解析不出来时整批停手（绝不在这里替它挑一个盘）。
             *
             * ⚠ 递归核心的工作区根是进程级静态（它自己会再挂一层 "recursive"），必须跟着一起换 ——
             * 不换的话递归那几百 MB 又会回到程序盘（那正是早先要改掉的老行为）。
             */
            _pathService.WorkDirectory = ResolveStartupWorkspaceRoot();
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

        /*
         * ⛔ 这里原来还有一个 `ReloadSettingsFromEffectiveDataRoot(loadedFrom)` 的"第二步读"，
         * 2026-09-30 **随「缓存根目录」设置项一起删除**。
         *
         * 它存在的唯一理由是：数据根由设置项决定，于是设置文件可能有两处（程序目录那一份、
         * 缓存根那一份），换了位置就得再读一次。现在数据根固定是程序目录下的 data
         * （唯一出口 = <see cref="PathService.DefaultDataRootDirectory"/>），设置文件只有一处，
         * "启动读旧的、保存写新的"这个缺陷从结构上就不存在了 —— 留着它只会是一条永远返回 false 的死路。
         */

        /// <summary>
        /// 启动 / 保存设置时那个"当前生效的工作区根"。
        ///
        /// <para>⛔ <b>它不参与"这一批该用哪个根"的决策</b>：那个只有一条路 —— 批首按这一单的目标目录派生
        /// （<c>ExtractionCoordinator.ApplyBatchWorkspaceRoot</c>）。这里取的只是"本次会话用过的那一个"
        /// （内存账本 <see cref="WorkspaceRootIndex.SessionRoots"/>），好让 ③ 页与启动日志在**同一趟运行里**
        /// 照样列得出前几批留下的残留（根会随目标目录变，不记住就等于漏报）。</para>
        ///
        /// <para>本趟还没解过任何东西 → 退回**老位置** <c>&lt;数据根&gt;\work</c>：
        /// 那是 2026-09-30 之前的工作区（升级前那批残留还在那儿，用户实测攒过 5.7 GB），
        /// 扫它是为了清得掉，⛔ 绝不是"下一批就放这儿"。</para>
        /// </summary>
        private string ResolveStartupWorkspaceRoot()
        {
            foreach (string remembered in WorkspaceRootIndex.SessionRoots)
            {
                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    return remembered;
                }
            }

            return Path.Combine(
                _pathService.DataRootDirectory,
                WorkspaceRootResolver.LegacyWorkspaceSubDirectoryName);
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
        /// <para>两部分：① 当前生效的根（本次会话用过的那个，没有就是老位置）；② 老位置
        /// <c>&lt;数据根&gt;\work</c>（2026-09-30 之前的工作区，升级前那批残留还在那儿，
        /// 用户实测攒过 5.7 GB）。只扫其中一个是**不够**的 —— 那正是"东西明明在，界面却说没有"的来源。</para>
        ///
        /// <para>⛔ 这里**不读任何历史清单**（用户 2026-09-30 收口）：账本只记本次会话用过的根
        /// （<see cref="WorkspaceRootIndex.SessionRoots"/>），重启之后不会再去猜"上次在哪开过工"。</para>
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
                WorkspaceRootResolver.LegacyWorkspaceSubDirectoryName));

            foreach (string remembered in WorkspaceRootIndex.SessionRoots)
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
        /// **当前生效的工作区根**（唯一来源 = <see cref="PathService.WorkDirectory"/>，批首由
        /// <c>ExtractionCoordinator.ApplyBatchWorkspaceRoot</c> 定下来）。
        ///
        /// <para>为什么要有这个只读口子：工作区默认就落在**目标目录里面**
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，用户 2026-09-30），凡是"扫产物 / 找内层包"
        /// 的地方都必须在扫描时把它排除掉（唯一判据出口 <see cref="WorkspaceTree" />）——
        /// 那些扫描点分布在别的协调器里，而 <c>_pathService</c> 是私有的。</para>
        /// </summary>
        internal string CurrentWorkspaceRoot => _pathService.WorkDirectory;

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
                /*
                 * 忙闲一变就重问所有命令的可用性：动磁盘的那一批要跟着灰 / 亮。
                 * ⚠ 「移除勾选的」**不看忙闲**（判据 = 只看有没有勾选的，见 CanRemoveSelectedTasks），
                 * 所以这里不再单独通知它的 ToolTip —— 那句话是常量，跟忙闲一个字都不差。
                 */
                RaiseAllCommandCanExecuteChanged();
            }
        }

        /// <summary>
        /// 「移除勾选的」那颗按钮的 ToolTip（常量文案，见 <see cref="StatusText.RemoveCheckedTasksHint"/>）。
        ///
        /// <para>2026-09-27 第二次修：它原来写着"处理中不能改列表"（我上一轮加的），用户当场反问
        /// <i>"我就是修改列表删除东西，和正在处理有什么关系"</i> —— 他说得对：这是**纯列表操作**，
        /// 与跑批无关。真正让他觉得"没效果"的是另一件事：**它只认打了勾的行**，
        /// 而他习惯"点中一行 + 点按钮"。所以这句话要把两种用法都写明。</para>
        ///
        /// <para>2026-09-29 补上另一半之后：一个都没勾时按钮是灰的（<c>ShowOnDisabled</c> 让灰着也读得出为什么），
        /// 而**跑批中途只要勾着就照样能点** —— 这句话两头都要说清，⛔ 别再写成"处理中不能用"。</para>
        /// </summary>
        public string RemoveCheckedTasksTooltip => StatusText.RemoveCheckedTasksHint;

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
            /*
             * 用户 2026-09-26 明确要求这个开关**必须记住**（原话："我刚刚测试当点击并发操作的时候，
             * 这个开关就没有保存……而且要有记忆性，下次重启也要有，这点要非常重视"）。
             *
             * 所以它从"主视图模型上的一个字段（只看这一次运行）"改成**设置项**：
             * 勾上就写进 Settings（自动保存那条路会立刻落盘），重启后读回来还是勾着的。
             * ⛔ 语义仍然是"不按最大并发数节流"，只是**记性**变了。
             */
            get => Settings?.RunAtFullSpeed ?? _runAtFullSpeed;
            set
            {
                if (Settings != null)
                {
                    Settings.RunAtFullSpeed = value;
                }

                if (SetProperty(ref _runAtFullSpeed, value))
                {
                    AppendLog(
                        "INFO",
                        value
                            ? "已开启「全速」：不再按「最大并发解压数」节流，能并行多少就并行多少（这个开关会记住）。"
                            : "已关闭「全速」：按设置里的「最大并发解压数」节流（这个开关会记住）。");
                }
            }
        }

        /// <summary>上一次**真的写进磁盘**的那份设置（自动保存的判据；见 <see cref="AutoSaveSettingsIfChanged"/>）。</summary>
        private string _lastSavedSettingsJson = string.Empty;

        private System.Windows.Threading.DispatcherTimer? _settingsAutoSaveTimer;

        /// <summary>自动保存的间隔：够短（改完几乎立刻落盘）又够长（拖滑块/连点勾选框时不会写几十次盘）。</summary>
        internal static readonly TimeSpan SettingsAutoSaveInterval = TimeSpan.FromMilliseconds(800);

        /// <summary>
        /// 起"改了就自动存"的计时器（用户 2026-09-26："我现在是想他们一个要自动保存，
        /// 就是你那个按钮和最下面一个框……都要删除"）。
        ///
        /// <para>为什么用"定时比指纹"而不是"每个控件都去挂事件"：设置项有几十个，绑定的写法有三种
        /// （SettingsEditor 转发、直接绑 Settings、勾选框绑视图模型属性），漏挂一个就是"这个开关又没保存"。
        /// 比指纹是**收口**的做法：只要 Settings 里任何一处变了，下一次 tick 就落盘 —— 不会有漏网的入口。
        /// （JSON 很小，一秒序列化一次的开销可以忽略；⛔ 只有真的不同才会写文件。）</para>
        ///
        /// <para>没有界面宿主（单元测试）时不起计时器：那时由测试直接调
        /// <see cref="AutoSaveSettingsIfChanged"/>，行为完全一样。</para>
        /// </summary>
        private void StartSettingsAutoSaveTimer()
        {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return;
            }

            _settingsAutoSaveTimer = new System.Windows.Threading.DispatcherTimer(
                SettingsAutoSaveInterval,
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => AutoSaveSettingsIfChanged(),
                dispatcher);

            _settingsAutoSaveTimer.Start();
        }

        /// <summary>
        /// 「改了就自动存」的本体：设置与上一次落盘那份不同 → 校验 → 写盘 → 记账。
        ///
        /// <para>⛔ 两条边界：①**校验不过就不存**（工具路径不存在 ——
        /// 与设置页「保存」同一套判据），并写一条 WARN 说清"改好就会立刻存"，绝不把不可用的值写进去；
        /// ②写盘失败只写 WARN，绝不弹框、绝不崩（自动保存不该打断用户手上的事）。</para>
        /// </summary>
        internal bool AutoSaveSettingsIfChanged()
        {
            try
            {
                /*
                 * ⚠ 快照必须在 Serialize **之前**取：Serialize 会顺手 Normalize（那正是它作为唯一序列化出口
                 * 的职责），而 Normalize 会把超范围的数字**夹回**合法区间 —— 夹过就必须让用户看见
                 * （用户 2026-09-25 第 36 条："不许静默改掉你填的数字"）。
                 * 以前这件事是"点保存"那条路在 `SettingsViewModel.Message` 里说的，而现在保存是自动的、
                 * 那个框也已经删掉 —— 所以改到这里：夹回的结论并进底栏那一行（见 DescribeClampNotice）。
                 */
                SettingsViewModel.SettingsClampSnapshot requested = SettingsViewModel.SettingsClampSnapshot.Capture(Settings);

                string json = _settingsService.Serialize(Settings);

                if (string.Equals(json, _lastSavedSettingsJson, StringComparison.Ordinal))
                {
                    return false;
                }

                string? blocked = SettingsEditor.DescribeAutoSaveBlock();

                if (!string.IsNullOrWhiteSpace(blocked))
                {
                    // 只在第一次拦住时写一条（改一次路径、tick 一次，不刷屏）。
                    if (!string.Equals(_lastAutoSaveBlockNote, blocked, StringComparison.Ordinal))
                    {
                        _lastAutoSaveBlockNote = blocked;
                        AppendLog("WARN", $"设置暂时没有自动保存：{blocked}（改好之后会自动存，不用点任何按钮）");
                    }

                    return false;
                }

                _lastAutoSaveBlockNote = string.Empty;

                if (!WriteSettingsToDisk("设置"))
                {
                    return false;
                }

                AppendLog("INFO", "设置已自动保存（改动即时落盘，重启后仍在）");

                string clampNotice = SettingsViewModel.DescribeClampNotice(requested, Settings);

                if (clampNotice.Length > 0)
                {
                    AppendLog("WARN", "设置里的数字超出允许范围，已按合法值生效：" + clampNotice);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "自动保存设置失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把设置写进磁盘并更新指纹 —— **写盘的唯一出口**（自动保存与"用户显式选择"几条路都走它）。
        ///
        /// <para>为什么要有这个出口：写盘点一共有八处（自动保存 + 一键处理面板「存为默认」/
        /// 「以后不再询问」/「无用物提醒」/ 导入密码本 / 记住的密码本 / ①页「选择…」/ 打包那几档），
        /// 以前各自 <c>try { Save } catch</c>，而自动保存靠的是"上一次真的写下去的那份"当指纹 ——
        /// 谁绕过它，定时器就会在 800ms 后再写一遍（内容一样、纯属白写）。</para>
        ///
        /// <para>⛔ 那道校验（工具路径）**只长在自动保存这一条路上**（见
        /// <see cref="AutoSaveSettingsIfChanged"/>）：其余调用点写的是"用户刚在这个界面上做的选择"，
        /// 与那一格无关 —— 把它也拦下会让"我明明点了却没记住"重现，那正是用户 2026-09-26
        /// 第 1 条最反感的事。</para>
        /// </summary>
        private bool WriteSettingsToDisk(string what)
        {
            if (_settingsService.Save(Settings))
            {
                _lastSavedSettingsJson = _settingsService.Serialize(Settings);

                /*
                 * ⚠ 收尾动作必须在这里做（2026-09-26 补）：指纹基线一更新，自动保存那一跳就"看不出变化"、
                 * 于是**不会再走** ApplySettingsSideEffectsAfterSave() —— 而那几条"自己写盘"的路
                 * （一键处理面板「存为默认」/ 导入密码本 / 记住的密码本 / ①页「选择…」/ 打包那几档）
                 * 恰恰都会改到"包在设置外面"的展示属性（落点那一行、并发那句话、密码本摘要）。
                 * 放在这个唯一出口上，五条路就都跟着刷新了。
                 */
                ApplySettingsSideEffectsAfterSave();
                return true;
            }

            AppendLog("WARN", $"{what}没能写进设置（写盘失败：磁盘只读 / 被占用？）—— 下次打开会回到上一次的值。");
            return false;
        }

        /*
         * 「底栏那一行设置状态」整块删除（用户 2026-09-26）：
         *
         * 原话："就是提醒用户设置会自动保存的，这个鬼东西太突兀了，而且这个又相当于是应该，
         * 但你却非要标出来，而且在切换选项卡的时候也会显示出来……这个让用户决定，现在彻底删除。"
         *
         * 于是：界面上**一个字都不留**（连"这一步先没存"也不提示）——
         * 代价是他自己选的，只保留日志里的那两条 WARN：
         * · 某项不合法（工具路径不存在）→ `设置暂时没有自动保存：<原因>`；
         * · 写盘失败 → `<某处>没能写进设置（写盘失败：磁盘只读 / 被占用？）`。
         * ⛔ 不许再把"设置已自动保存"这类报平安的话挂回界面上（那是程序自己的状态）。
         */

        /// <summary>被自动保存拦住的理由（用来"同一句话只说一次"）。</summary>
        private string _lastAutoSaveBlockNote = string.Empty;

        /// <summary>保存成功之后那些"包在设置外面"的东西要跟着重算（与以前点保存按钮之后做的同一件事）。</summary>
        private void ApplySettingsSideEffectsAfterSave()
        {
            ApplyEngineSettings();

            /*
             * ⛔ 这里**只搬值**（走 SyncOutputLocationFromSettings），不许走 SelectedOutputDirectory 的 setter：
             * 那个 setter 会把"未指定位置"改成"指定位置" —— 而这一跳是**每次写盘之后**都跑的，
             * 用户勾完「未指定位置」刚存下去就被它改回来（详见 SyncOutputLocationFromSettings 的说明）。
             */
            SyncOutputLocationFromSettings();

            ApplyRememberedBookPathsFromSettings();

            RefreshOutputPaths();
            UpdateSummary();

            RefreshSpaceModeText();
        }

        /// <summary>
        /// 关窗前的最后一次落盘（自动保存是 800 ms 一跳，用户可能改完就关）。
        /// </summary>
        internal void FlushSettingsAutoSave()
        {
            _settingsAutoSaveTimer?.Stop();
            AutoSaveSettingsIfChanged();
        }

        /// <summary>
        /// ①页「输出位置」那一格显示的那个值（用户 2026-09-25 第 27 条）。
        ///
        /// <para><b>⚠ 这个 setter 是"用户指定了一个位置"这一档专用门</b>：赋一个非空值 =
        /// 把落点切到"指定位置"（顺手写 <c>Settings.CustomOutputDirectory</c> +
        /// <c>ExtractToOriginalDirectory = false</c>）。目前唯一的调用方是①页那颗「选择…」
        /// （<c>SelectOutputDirectory</c>，它还会显式再写一次那两个值，语义一致）。</para>
        ///
        /// <para>⛔ <b>凡是"搬值"（启动填回记住的目录 / 写盘后的收尾 / 恢复默认 / ②页改路径的联动）
        /// 一律走 <see cref="SyncOutputLocationFromSettings"/></b> —— 它只碰界面字段、不改落点档位。
        /// 2026-10-01 真机那条"点了「未指定位置」还是没有记忆"就是踩了这个 setter
        /// （现场与根因见 <see cref="SyncOutputLocationFromSettings"/> 的说明）。</para>
        /// </summary>
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
        public string OutputLocationDisplay => ResolveOutputLocationText(forLog: false);

        /// <summary>
        /// 日志导出头部那一行要用的输出位置（**整行、完整路径不省略**）。
        ///
        /// <para>判据与 <see cref="OutputLocationDisplay"/> 是**同一份**（三档：未指定 / 还没选 / 指定），
        /// 只有"给界面还是给日志"不同。2026-10-02 真机那份日志里头部写着上一批记住的
        /// <c>H:\…\测试\BBB</c>，而这一批 8 个任务的产物全在 <c>H:\…\222\…</c> 底下 ——
        /// 老写法只读 <see cref="SelectedOutputDirectory"/> 这一个字段，压根没看落点档位
        /// （"记住的目录"会把那个字段填上，落点却仍是"每个包自己旁边"）。</para>
        /// </summary>
        internal string OutputLocationForLog => ResolveOutputLocationText(forLog: true);

        /// <summary>
        /// 三档落点的**同一份判据**（①页那一格与日志头部共用）。
        /// ⛔ 别在别处再写一遍这几个分支：两处判据分叉正是上面那个真机缺陷的根因。
        /// </summary>
        private string ResolveOutputLocationText(bool forLog)
        {
            if (Settings == null || Settings.ExtractToOriginalDirectory)
            {
                /*
                 * ①页那一格要短句（它左边就是「输出位置：」那个标签）；
                 * 日志要整行 —— 用①页切档时写进日志的**同一句原话**，不新编一句。
                 */
                return forLog
                    ? StatusText.OutputLocationSwitchedToOriginalLog
                    : StatusText.OutputLocationUnspecifiedText;
            }

            if (string.IsNullOrWhiteSpace(SelectedOutputDirectory))
            {
                return forLog
                    ? StatusText.OutputLocationLabel + StatusText.OutputLocationNotChosenText
                    : StatusText.OutputLocationNotChosenText;
            }

            return forLog
                ? StatusText.OutputLocationLabel + SelectedOutputDirectory
                : PathMiddleEllipsis.Elide(SelectedOutputDirectory);
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
        /// 把①页那一格的「当前输出目录」搬成给定值（不给就取设置里记住的那个），**只搬运**：
        /// ⛔ 不改落点档位（<c>ExtractToOriginalDirectory</c>）、⛔ 不写设置文件。
        ///
        /// <para>为什么必须显式同步：<see cref="AppSettings"/> 是普通 POCO（不实现 INotifyPropertyChanged），
        /// 而②页的「选择」是直接写 <c>Settings.CustomOutputDirectory</c> 的（走 SettingsViewModel），
        /// 不会经过 <see cref="SelectedOutputDirectory"/> 的 setter —— 不同步的话
        /// ①页那一行会一直显示旧路径（用户会以为"改了没反应"）。
        /// 这里只搬运、不回写设置，所以不可能形成两步循环。</para>
        ///
        /// <para><b>⛔ 三条"搬值"的路都不许走 <see cref="SelectedOutputDirectory"/> 的 setter</b>
        /// （用户 2026-10-01 真机连着两轮报的那条：<i>"我单独点击未指定位置还是没有用，
        /// 下次点开依旧没有记忆"</i>）：那个 setter 的语义是"**用户指定了一个位置**"，
        /// 它顺手把 <c>Settings.ExtractToOriginalDirectory</c> 改成 false（切到"指定位置"档）。
        /// 而"启动时填回上次记住的目录 / 设置落盘后收尾 / 恢复默认"这三处只是**搬值** ——
        /// 用户上回明明选的是「未指定位置」、设置文件里也是 <c>true</c>，却会被它悄悄改成 false：
        /// 每次启动界面都变成"指定位置"（①页那一格显示路径、一键处理弹窗预选"解压到指定位置"），
        /// 用户必须再点一次「未指定位置」。</para>
        ///
        /// <para>真机日志（第 5 轮，<c>ArchiveFixer_20261001_155136.log</c>）：设置文件里是
        /// <c>ExtractToOriginalDirectory: true</c>，可启动 7 秒后用户仍要手动点一次
        /// （15:51:43 那一行「输出位置：未指定」正是他点出来的）—— 那次点击就是本缺陷的现场。</para>
        /// </summary>
        /// <param name="rememberedDirectory">
        /// 要搬进那一格的地址；<c>null</c> = 取 <c>Settings.CustomOutputDirectory</c>。
        /// 传空串是合法的（「记住上次输出目录」关掉时就是"这一格空着"，**不是**"把设置抹掉"）。
        /// </param>
        private void SyncOutputLocationFromSettings(string? rememberedDirectory = null)
        {
            string folder = rememberedDirectory ?? Settings?.CustomOutputDirectory ?? string.Empty;

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

            RefreshSpaceTightOverrideText();

            ParallelAdviceText = "点「按空间算建议」可以算出当前可用空间能并行几个";
        }

        /// <summary>
        /// ②③页那条**本次被「空间不足」模式覆盖**的提示（模式关着时为空 = 整行收起）。
        ///
        /// <para>为什么这两页也要显示：②页是"源包 / 其余物怎么处理、并发几档"的那一页，
        /// ③页是"清理与删除"的那一页 —— 正是用户来核对这件事的两个地方。
        /// 只在①页说一句，他翻到②③页看到的还是设置里的档位，就会读成"程序没按我看到的跑"
        /// （2026-09-27 真机上他就是这么对照日志的）。</para>
        /// </summary>
        public string SpaceTightOverrideText
        {
            get => _spaceTightOverrideText;
            private set => SetProperty(ref _spaceTightOverrideText, value ?? string.Empty);
        }

        private string _spaceTightOverrideText = string.Empty;

        /// <summary>那条覆盖提示要不要显示（界面绑定它；与文本**同时**通知，见 §9.5 的值 + 通知）。</summary>
        public bool HasSpaceTightOverride => !string.IsNullOrWhiteSpace(SpaceTightOverrideText);

        /// <summary>把那条覆盖提示按当前开关刷一遍（开关一动、进页面时都要刷）。</summary>
        internal void RefreshSpaceTightOverrideText()
        {
            /*
             * 两条提示**各说各的**（用户 2026-09-27 加了「不删原包」安全档）：
             * 会删源包那一档说"会删"，安全档说"一个字节都不动" —— 一条文案盖两种行为就是撒谎。
             */
            SpaceTightOverrideText = !SpaceTightMode
                ? string.Empty
                : SpaceTightKeepSource
                    ? StatusText.SpaceTightKeepSourceOverrideNotice
                    : StatusText.SpaceTightOverrideNotice;

            OnPropertyChanged(nameof(HasSpaceTightOverride));
        }

        /// <summary>
        /// ①页那个**黄色「空间不足」开关**（用户 2026-09-27 拍板）。
        ///
        /// <para><b>它刻意不是设置项</b>：⛔ 不写 <c>appsettings.json</c>、不记忆、重启后是关着的
        /// （用户原话："只有当出现空间不足的情况才需要"）—— 所以这里是一个纯运行期字段，
        /// 与 <see cref="RunAtFullSpeed"/>（那个是设置项、用户明确要求要记住）刚好相反。
        /// 一旦开跑，它由 <c>ExtractionCoordinator</c> 在**批首钉死**（同一批口径一致）。</para>
        ///
        /// <para>开关本身只改一件事：那两页的覆盖提示与这句话的显示。真正的行为在解压管线里
        /// （并发档 / 排序 / 源包删除 / 其余物删除四处，见 <c>ExtractionCoordinator._spaceTightThisBatch</c>）。</para>
        /// </summary>
        public bool SpaceTightMode
        {
            get => _spaceTightMode;
            set
            {
                if (!SetProperty(ref _spaceTightMode, value))
                {
                    return;
                }

                /*
                 * 关掉「空间不足」= 连安全档一起关（值 + 通知都发）。
                 * 留着「不删原包」单独勾着毫无意义（它只描述"那个模式动不动源包"），
                 * 而界面上孤零零一个勾着会让用户以为"我现在受保护" —— 那是假的。
                 */
                if (!value && _spaceTightKeepSource)
                {
                    _spaceTightKeepSource = false;
                    OnPropertyChanged(nameof(SpaceTightKeepSource));
                }

                RefreshSpaceTightOverrideText();

                AppendLog(
                    "WARN",
                    value
                        ? "已开「空间不足」模式（只对本次运行有效，不写设置）：下一批会按空间自己定并发，"
                          + (SpaceTightKeepSource
                              ? "但源包一个字节都不动（「不删原包」勾着）。"
                              : "并且每个包定稿 + 校验通过后立刻永久删除它的源包。")
                        : "已关「空间不足」模式：恢复按设置里的源包 / 其余物 / 并发档执行。");
            }
        }

        private bool _spaceTightMode;

        /// <summary>
        /// 「空间不足」的**安全档**：并发与排序照旧由空间决定，但**源包一个字节都不动**
        /// （用户 2026-09-27："所有测试的情况下弄一个设置不删除原包的功能，在空间不足旁边弄一个，
        /// 空间不足但是不删除原包的操作"）。
        ///
        /// <para>他的顾虑是原话："源包讲实话，这个功能才刚刚弄我怕会出现意外，导致没成功而且原包也没有了，
        /// 这样的话就太亏了" —— 所以这一档的意义是**先看清空间怎么变，再决定要不要开那个会删源包的档**。
        /// 它与「空间不足」一样是运行期开关（⛔ 不写设置）。</para>
        ///
        /// <para><b>两档的耦合只有一条</b>：勾上安全档 → 自动把「空间不足」也勾上（它只描述"那个模式动不动源包"，
        /// 单独存在没有意义）；反过来关掉「空间不足」→ 安全档一起关。⛔ 不做反向的"取消安全档就关模式"。</para>
        /// </summary>
        public bool SpaceTightKeepSource
        {
            get => _spaceTightKeepSource;
            set
            {
                /*
                 * 发行档没有这个安全档（用户 2026-09-27："发行版要删，我们本地的不用"）：
                 * 界面上那颗勾在发行档里是**收起的**（见 IsSpaceTightKeepSourceAvailable），
                 * 这里再挡一道 —— 万一有别的入口（设置文件、脚本）把它设成 true，也只当没这回事。
                 *
                 * ⚠ 写法上必须让"挡住"这件事在**两种构建下都是同一段代码**：
                 * 写成 `if (!IsAvailable) { value = false; }` 时，发行档里 `KeepSourceOptionEnabled`
                 * 是编译期常量 false → 那个分支恒真 → 编译器把后面整段判成**无法访问的代码**（CS0162 警告），
                 * 而本项目的标准是 0 警告。所以这里让判据**参与运算**（一个与运算），
                 * 效果一样、两种构建下都干净。
                 */
                value = value && BuildEdition.KeepSourceOptionEnabled;

                if (!SetProperty(ref _spaceTightKeepSource, value))
                {
                    return;
                }

                if (value && !SpaceTightMode)
                {
                    SpaceTightMode = true;

                    AppendLog(
                        "WARN",
                        "「不删原包」勾上 → 顺带把「空间不足」也勾上（它只是那个模式的安全档，"
                        + "单独勾着没有意义）。");
                }

                RefreshSpaceTightOverrideText();

                AppendLog(
                    "WARN",
                    value
                        ? "「不删原包」已开：这一批源包一个字节都不动（不搬、不删），"
                          + "只按空间决定并发与顺序；其余物（过程物）仍在成功后彻底删除。"
                          + "⚠ 这一档不回收源包那份空间，所以需要的余量更大。"
                        : "「不删原包」已关：这一批回到「空间不足」的默认档（每个包校验通过后删除源包）。");
            }
        }

        private bool _spaceTightKeepSource;

        /// <summary>
        /// 界面上要不要显示「不删原包」那颗勾（发行档不显示 —— 它是测试期专用的安全档，
        /// 用户 2026-09-27："发行版要删，我们本地的不用"）。
        ///
        /// <para>唯一判据是 <see cref="BuildEdition.KeepSourceOptionEnabled"/>（编译期常量），
        /// ⛔ 不许在别处再判断一次。</para>
        /// </summary>
        public bool IsSpaceTightKeepSourceAvailable => BuildEdition.KeepSourceOptionEnabled;

        /*
         * ===== 解压前的**空间体检**（用户 2026-09-27 第 2 条）=====
         *
         * 他原话的意思是："导入完就把源包大小跟目标盘可用空间对一遍，不够就说一声、
         * 还要弹个窗（因为这个比较危险），单个放不下的包要**点名**"。
         *
         * 三条纪律：
         * ① **自动**（默认就做，不需要他记得点哪个按钮）—— 触发点只有两个：
         *    导入结束（批量任务刚进列表）与**换输出位置**（换了盘就是换了另一块空间）；
         * ② 判据**一处都不新写**：直接调解压管线用的同一个 `BuildSpaceAdvice`
         *    （与开跑时的调度、界面上的「按空间算建议」是三处共用一段实现）；
         * ③ 取不到可用空间时**如实说取不到**，绝不猜"够"或"不够"。
         *
         * ⚠ 它只是**提醒**：不许顺手把任务取消勾选、不许改设置、不许拦着不让跑 ——
         * 真正拦人的仍然是每个任务启动前那道空间门（不变量：空间不足绝不开跑）。
         */

        /// <summary>
        /// 给一批任务做空间体检（导入完成 / 换了输出位置时调）。
        ///
        /// <para>它跑在**后台**（要 stat 每个源包），界面只收结论；任何异常都只写日志，
        /// 绝不让一次"提醒"把导入或选区变成失败。</para>
        /// </summary>
        /// <param name="tasks">要体检的任务（空 / null = 什么都不做）。</param>
        /// <param name="trigger">触发来源（进日志，用户事后要能回答"这句是什么时候说的"）。</param>
        internal async Task CheckSpaceForTasksAsync(IReadOnlyList<ArchiveTask>? tasks, string trigger)
        {
            if (tasks == null || tasks.Count == 0)
            {
                return;
            }

            try
            {
                ExtractionSchedulePlan plan = await Task.Run(() => _extractionCoordinator.BuildSpaceAdvice(tasks))
                    .ConfigureAwait(true);

                ReportSpaceCheck(plan, tasks.Count, trigger);
            }
            catch (Exception ex)
            {
                // 体检失败不影响任何事（它只是提醒）：写一行日志就够了。
                AppendLog("WARN", $"空间体检没能完成（不影响导入与解压）：{ex.Message}");
            }
        }

        private void ReportSpaceCheck(ExtractionSchedulePlan plan, int taskCount, string trigger)
        {
            long sourceTotal = 0;

            foreach (ScheduledExtractionItem item in plan.Ordered)
            {
                sourceTotal = TaskSpaceEstimate.SaturatingSum(sourceTotal, item.Estimate.SourceBytes);
            }

            if (plan.AvailableBytes < 0)
            {
                // 取不到就**说取不到**：不谎报足够，也不谎报不足。
                AppendLog(
                    "WARN",
                    $"空间体检（{trigger}）：没能取到目标盘的可用空间，无法预判这 {taskCount} 个包放不放得下。"
                    + "开跑时每个任务启动前仍会再判一次空间。");

                return;
            }

            string summary =
                $"空间体检（{trigger}）：{taskCount} 个源包共 {TaskSpaceEstimate.FormatSize(sourceTotal)}，"
                + $"目标盘可用 {TaskSpaceEstimate.FormatSize(plan.AvailableBytes)}，"
                + $"其中要保留 {TaskSpaceEstimate.FormatSize(plan.ReserveBytes)} 余量。";

            if (plan.BlockedAtPlanTime.Count == 0)
            {
                /*
                 * 够的时候也要留一句 INFO（用户要的是"能回答当时的判断"，不是只有坏消息才说话）——
                 * 但**不弹窗**：弹一个"没问题"的框只是训练他闭眼点确定。
                 */
                AppendLog("INFO", summary + "按当前可用空间，这一批没有整盘都放不下的包。");

                return;
            }

            var names = new List<string>();
            var detail = new StringBuilder();

            detail.AppendLine(summary);
            detail.AppendLine();
            detail.AppendLine("按当前可用空间，下面这些包整盘都放不下（连单独跑都不够）：");

            foreach (ScheduledExtractionItem blocked in plan.BlockedAtPlanTime)
            {
                /*
                 * "需要"说的是**这次要从可用空间里新写多少**（内容物 + 过程物），不是源包占多少 ——
                 * 源包已经在盘上、不在可用空间里（2026-09-29 真机就是因为这里口径错而被误拦）。
                 * 括号里把那句话说清，用户才能自己复核这个数字。
                 */
                string line =
                    $"{blocked.Estimate.DisplayName} —— 需要 {TaskSpaceEstimate.FormatSize(blocked.RequiredBytes)}"
                    + $"（内容物 + 过程物，源包已经在盘上不算在内），"
                    + $"差 {TaskSpaceEstimate.FormatSize(blocked.ShortfallBytes)}";

                names.Add(blocked.Estimate.DisplayName);
                detail.AppendLine("  " + line);
            }

            const int MaxNamedInPopup = 10;

            string named = string.Join("、", names.Take(MaxNamedInPopup));

            if (names.Count > MaxNamedInPopup)
            {
                named += $"等 {names.Count} 个";
            }

            /*
             * 黄色 WARN + 红色 ERROR 两条都写（用户 2026-09-27 明确要求"日志里两个都要"）：
             * WARN 是"这件事要注意"，ERROR 是"这一批一定会跳过它"—— 级别不同，事后翻日志的用途也不同。
             */
            AppendLog("WARN", summary + $"其中 {plan.BlockedAtPlanTime.Count} 个包整盘都放不下：{named}。");
            AppendLog(
                "ERROR",
                $"空间不足，这一批会跳过 {plan.BlockedAtPlanTime.Count} 个包（一个字节都不会动它们）：{named}。"
                + "处理办法：清理「其余物」腾空间 / 换一个更大的输出盘 / 开①页的「空间不足」模式"
                + "（每个包校验通过后立刻删源包，边解边回收；⚠ 会永久删除源包）。");

            /*
             * 那个弹窗（用户原话："还可以弄一个弹窗，因为这个比较危险"）。
             * 无界面宿主（单元测试 / 控制台宿主）由 DialogService 自己降级成一条记录，不弹、不死等。
             */
            _dialogService.ShowSpaceShortageWarning(
                $"有 {plan.BlockedAtPlanTime.Count} 个包放不下，处理时会跳过它们",
                detail.ToString().TrimEnd());
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
        public ICommand ChangeNameCommand { get; }

        public ICommand StartExtractCommand { get; }

        /// <summary>
        /// 手动档「解压到当前文件夹」（用户 2026-09-27 拍板新加的一个按钮）：
        /// 与 <see cref="StartExtractCommand"/> 同一条管线，只多一个"这次不建包名那一层"的运行期标记。
        ///
        /// <para>⛔ 它**不是**"只解压"的别名：两者的落点差别写在
        /// <see cref="ExtractionCoordinator.StartExtractAsync(bool)"/> 上（<c>111\222.rar</c> →
        /// <c>111\内容物</c> vs <c>111\222\内容物</c>）。一键处理 / 批量**永远不会**走这一档。</para>
        /// </summary>
        public ICommand ExtractIntoSourceFolderCommand { get; }

        public ICommand StopCommand { get; }
        public ICommand CancelCurrentCommand { get; }

        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenPasswordListCommand { get; }

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

        /// <summary>
        /// 「按建议改名并重试」（用户 2026-09-25 第 41 条）：给"名字被改坏的分卷第一卷"按标准名改名，
        /// 只改名字、不覆盖、不删内容，改完立刻重新识别并重试解压。只在真有可改的任务时才亮。
        /// </summary>
        public ICommand RenameBySuggestionAndRetryCommand { get; }
        public ICommand RescanTaskCommand { get; }
        public ICommand CopyTaskInfoCommand { get; }
        public ICommand CopyTaskPathCommand { get; }
        public ICommand CopyTaskErrorCommand { get; }
        public ICommand OpenTaskDirectoryCommand { get; }

        /// <summary>
        /// 「打开源目录」：把**第一个勾选任务**所在的文件夹在资源管理器里打开（手动那一栏那颗按钮，用户 2026-09-26 批准加）。
        ///
        /// <para>为什么要有它：右键菜单里那条「打开文件所在目录」只对"当前行"生效 ——
        /// 批量处理时用户手上往往没有"当前行"，只想顺手看一眼源目录在哪。</para>
        /// </summary>
        public ICommand OpenCheckedSourceDirectoryCommand { get; }

        /// <summary>
        /// 「看内容」：只列清单（条目数 / 总大小 / 前几条），**不解压、不写盘**（用户 2026-09-26 批准加）。
        /// </summary>
        public ICommand InspectArchiveCommand { get; }

        /// <summary>
        /// 「试密码」：拿候选逐个问引擎"这个能不能开"，报出能开的那个（只显示候选说明，不显示密码）。
        /// </summary>
        public ICommand TryPasswordsCommand { get; }
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
             * "这个包实际是哪个引擎解的"接进报告（不变量 14）。
             *
             * 报告层默认只能报"通用引擎"（它不知道每个包走了哪条分派路，而且报告生成那一刻
             * 用户可能已经改了优先级、卸载了 WinRAR —— 那时再去问注册表问的是"现在"，
             * 不是"当时"）。所以由**真正执行的那个引擎**在结果上盖戳（ArchiveOperationResult
             * 的 EngineId / EngineVersion），门面按归档路径记下来，报告逐任务取用；
             * 取不到（比如报告是手搓的、或这一次根本没跑过引擎）就退回通用引擎身份。
             */
            _taskSummaryService.EngineIdentityProvider = task => ResolveEngineIdentity(task);

            /*
             * 读设置只有一步（用户 2026-09-30：数据根固定 = 程序目录下的 data，设置文件只有一处）。
             *
             * 老实现要读三步：① 先读程序目录那一份 → ② 按「缓存根目录」这个设置项把数据根定下来 →
             * ③ 数据根换了地方就再读一次。那个设置项已经删除，②③ 随之消失；
             * 少了 ③ 也不会再出现"启动读旧的、保存写新的"（两者本来就是同一个文件了）。
             */
            _settings = _settingsService.Load();
            ApplyEngineSettings();

            StartSettingsAutoSaveTimer();

            /*
             * 设置编辑器（选项卡②③④⑥绑它）与打包编辑器（⑤绑它）。
             *
             * ⚠ AttachSharedSettings 是**必须**的：SettingsViewModel 的构造函数会克隆一份设置
             * （设置窗口的「取消」靠它回退），而选项卡上同一项可能在多处出现
             * （并发档既在②的输入框里、又决定危险模式凭证的覆盖判定），
             * 两份值一定会打架 —— 共享同一个对象才不会有"界面显示 A、保存写回 B"。
             */
            _settingsEditor = new SettingsViewModel(_settings, _settingsService);

            /*
             * ②页「指定位置 → 选择…」也要做一次空间体检（用户 2026-09-27 第 2 条）：
             * 那一颗按钮与①页的「选择…」是**两个入口、同一件事**（换了一块输出盘），
             * 只挂一边的话，用户在②页挑完盘什么都不会发生。
             * ⚠ 不 await（回调是同步的）：体检自己吞掉所有异常，不存在没人接的异常。
             */
            _settingsEditor.OutputDirectoryPicked = pickedFolder =>
                _ = CheckSpaceForTasksAsync(Tasks.ToList(), "换了输出位置");
            _settingsEditor.AttachSharedSettings(_settings);

            PackingEditor = new PackingViewModel
            {
                LogSink = line => AppendLog("INFO", line),

                /*
                 * ⛔ 打包也要在操作日志里**打一条"本次操作开始"**（第 38 条那套口径）：
                 * 「导出日志（本次操作）」是从最后一条标记开始导的 —— 打包以前不打标记，
                 * 于是用户"刚打完包就导出"，导出来的头一行还是**上一次解压**的"本次操作开始：解压"，
                 * 或者干脆整份运行日志全倒出来。时机同理：**用户确认之后、调服务之前**（第 46 条）。
                 */
                OperationStarted = () => _logService.MarkOperationStart("打包")
            };

            /*
             * 打包那几档（落点 / 原包操作 / 其余物操作）存在**同一份 AppSettings** 上，
             * 但字段与解压侧完全分开（第 46 条：⛔ 两者绝不同步）；改了要落盘（"保存记忆操作"）。
             * 密码列表由④页那份喂进来（「从密码列表里选」按钮用）。
             */
            PackingEditor.Settings = Settings;
            PackingEditor.SettingsChanged = SavePackingSettings; PackingEditor.PasswordListProvider = () => _passwordService.Passwords
                .Where(item => item != null && item.IsEnabled && !string.IsNullOrEmpty(item.Value))
                .Select(item => item.Value)
                .ToList();

            /*
             * 「记住上次输出目录」真正生效的地方（不是留着好看的开关）：
             * 关掉之后，启动时**不**把上次的输出目录填回 SelectedOutputDirectory，
             * 这一次运行按"输出位置"那一档的规则算落点（默认 = 压缩包同目录）。
             * 之前这个开关只存在于界面上，改了什么都不会发生。
             *
             * ⛔ 两条边界（2026-10-01 修；现场见 SyncOutputLocationFromSettings 的说明）：
             * ① **不许走 SelectedOutputDirectory 的 setter** —— 它的语义是"用户指定了一个位置"，
             *    会把用户上回选的「未指定位置」在启动时悄悄改成"指定位置"，于是"下次点开没有记忆"；
             * ② 关掉这个开关只是"这一格空着"，⛔ 不许顺手去动设置里的 CustomOutputDirectory
             *    （搬值的那条路只碰界面字段，绝不写设置）。
             */
            SyncOutputLocationFromSettings(
                _settings.RememberLastOutputDirectory
                    ? _settings.CustomOutputDirectory ?? string.Empty
                    : string.Empty);

            AddFilesCommand = new AsyncRelayCommand(_scanCoordinator.AddFilesAsync, CanRunNormalCommand);
            AddFolderCommand = new AsyncRelayCommand(_scanCoordinator.AddFolderAsync, CanRunNormalCommand);
            AppendFilesCommand = new AsyncRelayCommand(_scanCoordinator.AppendFilesAsync, CanRunNormalCommand);
            AppendFolderCommand = new AsyncRelayCommand(_scanCoordinator.AppendFolderAsync, CanRunNormalCommand);
            ScanCommand = new AsyncRelayCommand(_scanCoordinator.ScanTasksAsync, CanRunNormalCommand);
            ClearCommand = new RelayCommand(ClearTasks, CanRunNormalCommand);
            RemoveSelectedCommand = new RelayCommand(RemoveSelectedTasks, CanRemoveSelectedTasks);

            SmartRenameCommand = new AsyncRelayCommand(_renameCoordinator.SmartRenameAsync, CanRunNormalCommand);
            ChangeNameCommand = new AsyncRelayCommand(_renameCoordinator.ChangeNameAsync, CanRunNormalCommand);

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

            /*
             * 「解压到当前文件夹」（用户 2026-09-27）：与上面那条**同一个本体**，
             * 只把"这次摊平"这一个运行期标记传下去 —— 落点推导、定稿、校验、其余物、清理
             * 全都还是同一条管线（⛔ 不许为它另写一条解压路径）。
             *
             * 可用性守卫与「只解压」逐字相同（闲着 + 至少勾了一个任务）：
             * 它同样按勾选办事，不勾就点不动，免得用户以为"点了没反应"。
             */
            ExtractIntoSourceFolderCommand = new AsyncRelayCommand(
                async () =>
                {
                    _logService.MarkOperationStart(StatusText.ExtractIntoSourceFolderText);
                    await _extractionCoordinator.StartExtractAsync(extractIntoSourceFolder: true).ConfigureAwait(true);
                },
                CanStartExtract);
            StopCommand = new RelayCommand(_extractionCoordinator.StopAfterCurrent, () => IsBusy);
            CancelCurrentCommand = new RelayCommand(_extractionCoordinator.CancelCurrentTask, () => IsBusy);

            OpenSettingsCommand = new RelayCommand(OpenSettings, CanRunNormalCommand);
            OpenPasswordListCommand = new RelayCommand(OpenPasswordList, CanRunNormalCommand);
            OpenPackingCommand = new RelayCommand(OpenPacking, CanRunNormalCommand);

            ImportPasswordBookCommand = new RelayCommand(ImportPasswordBook, CanRunNormalCommand);
            ExportLogCommand = new RelayCommand(ExportLog);
            ExportAllLogsCommand = new RelayCommand(ExportAllLogs);
            CopyFailedListCommand = new RelayCommand(CopyFailedList);
            ExportFailedListCommand = new RelayCommand(ExportFailedList);
            OpenCheckedSourceDirectoryCommand = new RelayCommand(OpenCheckedSourceDirectory, CanRunNormalCommand);
            InspectArchiveCommand = new AsyncRelayCommand(InspectCheckedArchivesAsync, CanRunNormalCommand);
            TryPasswordsCommand = new AsyncRelayCommand(TryPasswordsForCheckedArchivesAsync, CanRunNormalCommand);
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

            /*
             * 「移除勾选的」—— 可用性判据与右键菜单那条 `RemoveSelectedCommand` **共用同一个**
             * `CanRemoveSelectedTasks`（只问"有没有勾选的"），⛔ 不许各写一套。
             *
             * 这条口径被用户改过两次，两半**都**要留着（只照一半改就是错的）：
             *
             * ① 2026-09-27：它以前绑 `!IsBusy`，跑批中途是灰的；而它只是**纯列表操作**（只动内存里那几行，
             *    磁盘一个字节都不碰）—— 用户原话"我就是修改列表删除东西，和正在处理有什么关系"，
             *    于是 `!IsBusy` 这一半被否掉：**跑批中途照样能点**。
             * ② 2026-09-29："永远可点"的结果是**列表空着 / 一个都没勾时它也亮着**，点下去什么都不发生 ——
             *    用户原话："这个勾选行里面，移除勾选的按钮一直亮着没用啊，我都没有导入文件你亮着干什么"，
             *    紧接着又指出"这怎么还是和前面的按钮样式不一样"。于是补上"至少有一条勾着的"这一半。
             *
             * ⛔ 合成口径 = **只看有没有勾选的**（与旁边「全选 / 全不选 / 反选」在"没有可作用的对象就不亮"
             * 这点上同形，但**不看忙闲**，那一点上与它们不同形）—— ⛔ 不许把 `!IsBusy` 加回来。
             *
             * 通知那一条同样关键：判据对而界面不重问 = 用户读成"没生效"。它现在进了
             * `RaiseAllCommandCanExecuteChanged`（`UpdateSummary` 收尾统一发一遍），
             * 于是勾选变化 / 任务增删都会立刻反映到按钮上（忙闲切换也照发一遍，只是这颗按钮的判据不看它）
             * （§9.5「值 + 通知」两条都要钉住）。
             */
            RemoveCheckedTasksCommand = new RelayCommand(RemoveCheckedTasks, CanRemoveSelectedTasks);

            RenameBySuggestionAndRetryCommand = new AsyncRelayCommand(
                RenameBySuggestionAndRetryAsync,
                CanRenameBySuggestionAndRetry);
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
             * 弹窗"要不要响铃闪任务栏"的决定写进日志（用户 2026-09-26）：
             * 现在的口径是**只有窗口没能出现在最前面时才提醒**，所以"这次为什么响 / 为什么没响"
             * 必须答得出来 —— 用户问过一次"这个提醒的作用是什么"，日志就是那个答案。
             * 静默档（已在前台）只记 INFO，提醒档记 WARN（一眼能看见）。
             */
            ArchiveFixer.Helpers.WindowAttention.Note = line => AppendLog(
                line.Contains("安静显示", StringComparison.Ordinal) ? "INFO" : "WARN",
                "窗口提醒：" + line);

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

            /*
             * 自动保存的基线：把"整个构造过程全部走完之后的那一份设置"记成已存指纹 ——
             * 于是启动本身不写盘，只有**用户真的改了东西**才会落盘（见 AutoSaveSettingsIfChanged）。
             *
             * ⚠ 基线必须在**最后**取：②③④⑥四页的编辑器与⑤打包页在构造时会把若干默认档补齐，
             * 挂在构造开头取的话，这些"程序自己的初始化"会被当成用户改动 —— 开机 800ms 后
             * 白白写一次盘（真机上表现为"什么都没动，设置文件的时间戳却变了"）。
             */
            _lastSavedSettingsJson = _settingsService.Serialize(_settings);
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
        /// 把"记住的密码本"从设置推给密码服务（用户在④「密码」页增删过、设置落盘之后由
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
                WriteSettingsToDisk("导入的密码本");

                /*
                 * ④页那份「已记住的密码本」清单要跟着重读（同步审计逮到）：它是 SettingsViewModel
                 * 里的一份快照，导入这条路以前一次都不刷 —— 摘要行"密码本：xxx — N 条"当场就变了，
                 * 下面那份清单还写着「还没有记住任何密码本 —— 启动时不会自动加载任何密码本」（与事实相反）。
                 */
                SettingsEditor?.NotifyRememberedBooksChanged();

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
                _oneClickCoordinator?.RoundLimit ?? 5,
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

        /// <summary>
        /// 「移除勾选的」（①页那颗按钮）与「移除勾选中的任务」（列表右键菜单）**共用**的可用性：
        /// **列表里真有一条勾着的**（空列表 / 一个都没勾 → 灰）。
        ///
        /// <para>这个判据是用户**两轮反馈各管一半**合成的，两半都留在这里，⛔ 只照一半改就是错的：</para>
        ///
        /// <list type="number">
        /// <item><description><b>2026-09-27 否掉的那一半</b>：这里以前绑着 <c>!IsBusy</c>，跑批中途按钮是灰的。
        /// 用户原话：<i>"我就是修改列表删除东西，和正在处理有什么关系"</i>（他当天正是在跑批中间点的它）。
        /// 这话是对的：它是**纯列表操作**（只动内存里那几行，磁盘一个字节都不碰），
        /// **与跑批没有关系** —— 所以跑批中途必须能点。</description></item>
        /// <item><description><b>2026-09-29 补上的那一半</b>：去掉 <c>!IsBusy</c> 之后它变成"永远可点"，
        /// 于是空列表 / 一个都没勾时也亮着、点下去什么都不发生。用户原话：
        /// <i>"这个勾选行里面，移除勾选的按钮一直亮着没用啊，我都没有导入文件你亮着干什么"</i>
        /// —— 可作用的对象就是"勾选"，一个都没有就不该亮（与「只解压」等命令同一条精神）。</description></item>
        /// </list>
        ///
        /// <para>合成口径 = **只问"有没有勾选的"，忙闲根本不进判据**。
        /// ⛔ **不许再加 <c>!IsBusy</c>**：那会推翻用户 2026-09-27 的明确原话，等于把两轮反馈又砍回一半。</para>
        ///
        /// <para>⛔ 两条命令共用这一个判据（2026-09-29）：同一件事只允许有一个出口，
        /// 各写一套必然漂移成"按钮是灰的、右键菜单却能点"。</para>
        /// </summary>
        private bool CanRemoveSelectedTasks()
        {
            return Tasks.Any(task => task?.IsSelected == true);
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
                + $"列表里的 {totalCount} 个任务会全部移除 —— 这是整表操作，与勾选无关"
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
            OnPropertyChanged(nameof(SelectedTasksSummary));
            RaiseAllCommandCanExecuteChanged();
        }

        /// <summary>
        /// 「勾选：」那一排右边那句"已勾选 N 项 · 合计 X"（用户 2026-09-27："应该…显示选中文件的大小…
        /// 这样用户就能更好的察觉"）。
        ///
        /// <para>合计只累加**采到了大小**的那些（<c>SourceSizeBytes</c> 为 0 的算 0）；
        /// 一个都没勾时返回一句空提示，⛔ 不留空白让人以为坏了。</para>
        /// </summary>
        public string SelectedTasksSummary
        {
            get
            {
                List<ArchiveTask> selected = Tasks.Where(task => task != null && task.IsSelected).ToList();

                if (selected.Count == 0)
                {
                    return "（没有勾选任何任务）";
                }

                long total = 0;

                foreach (ArchiveTask task in selected)
                {
                    total += task.SourceSizeBytes;
                }

                return $"已勾选 {selected.Count} 项 · 合计 {TaskSpaceEstimate.FormatSize(total)}";
            }
        }

        public void AppendLog(string message)
        {
            AppendLog("INFO", message);
        }

        /// <summary>
        /// 在操作日志里打一条"======== 本次操作开始：<paramref name="operationName"/> ========"。
        ///
        /// <para>「导出日志（本次操作）」就是从**最后一条**这样的标记开始导的（第 38 条那套口径）。
        /// ⛔ 每个"开始干活"的入口都要打一条，否则用户刚干完就导出，导出来的头一行会是**上一次**操作的名字
        /// （打包与扫描都漏过这一条，见第 46 条 ⑬）。</para>
        /// </summary>
        public void MarkOperationStartForLog(string operationName) => _logService.MarkOperationStart(operationName);

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
        /// 源包处理由 <see cref="AppSettings.ToSourceHandlingValue"/> 序列化 ——
        /// 与②「解压方式」/③「清理与删除」页写的是同一套字符串口径，不新造第三种表示法。
        /// </para>
        /// <para>
        /// 落点无效（选了"指定位置"却没填路径）时**不写落点**，但源包处理照写：
        /// 那一项本来就与路径无关，因为一个空路径把它一起丢掉才是意外。
        /// （终端落法 2026-09-27 已从设置里删除，这里不再有它。）
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

            // 终端落法那一档**已退役**（用户 2026-09-27 删除）：弹窗里不再有它，也不写回设置。
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

            if (!WriteSettingsToDisk("把本次选项存为默认"))
            {
                _dialogService.ShowError("把本次选项存为默认失败：设置文件写不进去（磁盘只读 / 被占用？）。");
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

            if (!WriteSettingsToDisk("「以后不再询问」"))
            {
                return;
            }

            // ②页那个同义开关绑的是 SettingsEditor.Settings（POCO 不发通知）—— 不喊这一声它还显示着"会弹"。
            OnPropertyChanged(nameof(Settings));
            SettingsEditor?.NotifyProcessingOptionsChanged();
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
                     * 这里**只算落点、不碰磁盘**：`BuildOutputPath` → `OutputPlacement` 是纯函数
                     * （2026-09-27 起连"目录里是不是只有这一个包"都不用问了 —— 塌缩退役之后
                     * 它不再需要任何目录枚举），所以本方法放在 UI 线程上也安全。
                     * 真实落点仍以解压管线回写的 task.OutputPath 为准（输出目录已存在时可能是 xxx(1)）。
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
        /// 打包那边改了它自己的两档（原包 / 其余物 / 落点）之后落盘 ——
        /// 用户 2026-09-26 第 46 条："而且相应的保存记忆操作"。
        ///
        /// <para>⛔ 只写打包那三个字段（它们在 <c>PackingEditor.Settings</c> 上，与解压侧各存各的），
        /// 这里不碰任何解压设置；写失败也不弹窗（打包本身已经跑完了，没道理为一个落盘失败打断他）。</para>
        /// </summary>
        private void SavePackingSettings()
        {
            /*
             * 与自动保存**同一道闸门**：设置里有非法值（工具路径指向一个不存在的文件）时先不落盘并说清 ——
             * ⛔ 打包这条路不许变成"把非法设置偷偷写进盘"的后门（那一格决定引擎，写进去下次启动就照它走）。
             */
            string? blocked = SettingsEditor?.DescribeAutoSaveBlock();

            if (!string.IsNullOrEmpty(blocked))
            {
                AppendLog("WARN", "打包的选择还没写进设置（改成合法值之后会自动存）：" + blocked);
                return;
            }

            WriteSettingsToDisk("打包的选择");
        }

        // 「保存设置」这个动作**整块退役**（用户 2026-09-26："你那个按钮和最下面一个框……都要删除"）：
        // 现在改任何一项都会自动落盘（AutoSaveSettingsIfChanged），
        // 校验判据搬到 SettingsEditor.DescribeAutoSaveBlock()，保存后的连带动作搬到
        // ApplySettingsSideEffectsAfterSave()。⛔ 不再留一个"要用户点一下"的入口。



        private void ResetSettings()
        {
            bool confirm = _dialogService.ShowConfirm("确定要恢复默认设置吗？");

            if (!confirm)
            {
                return;
            }

            Settings = _settingsService.ResetToDefault();

            // ⛔ 恢复默认也是"搬值"：走 SyncOutputLocationFromSettings，不许让 setter 把落点档位再改一遍。
            SyncOutputLocationFromSettings();
            RefreshOutputPaths();

            /*
             * 恢复默认会把并发档、落点、源包/其余物那几档一起改回默认值 ——
             * 那几个展示属性是"包在设置外面"的（AppSettings 不发通知），
             * 不显式刷新的话页面上还会停着改之前那句话。
             *
             * ⚠ ②页那个「全速」勾选框也是这一类（2026-09-26 同步审计逮到）：它的 getter 读
             * `Settings.RunAtFullSpeed`，恢复默认把它清成 false —— 不通知的话界面还勾着"全速"，
             * 而真正干活的那一套已经按并发档排队了。
             */
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(RunAtFullSpeed));
            RefreshSpaceModeText();
            NotifyOutputPlacementChangedEverywhere();
            SettingsEditor?.NotifyProcessingOptionsChanged();
            SettingsEditor?.NotifyRememberedBooksChanged();

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

            // 记不住路径不该让关窗这个动作失败，但写失败要说清楚（否则用户下次启动发现没自动加载会以为是 bug）。
            WriteSettingsToDisk("记住的密码本");

            // ④页那份清单跟着重读（同 ImportPasswordBook：它是快照，不刷就与设置里的真值不一样）。
            SettingsEditor?.NotifyRememberedBooksChanged();
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



        /// <summary>
        /// 导出日志文件的**头部**（用户 2026-09-25 第 44 条追加拍板）：
        /// 时间范围 / 任务数 / 成功失败 / 引擎版本 / 输出根 + "细节在哪看"。
        ///
        /// <para>为什么值得写：这份 txt 的用途就是"发给别人（或未来的自己）排查" ——
        /// 没有头，读的人得先自己找"这是哪一批、跑到几点、成不成、用的哪个引擎"。
        /// ⛔ 这里只写**机器事实**（任务表的终态枚举与引擎标识），不写任何密码相关内容。</para>
        ///
        /// <para>internal 是为了让测试能直接钉住"头部写了哪几件事、顺序对不对"
        /// （产品入口只有「导出日志（本次操作）」这一个，见 <see cref="ExportLog"/>）。</para>
        /// </summary>
        internal IReadOnlyList<string> BuildLogExportHeader()
        {
            var lines = new List<string>
            {
                "================ ArchiveFixer 日志导出 ================"
            };

            DateTime? first = Tasks.Where(task => task.StartTime.HasValue).Min(task => task.StartTime);
            DateTime? last = Tasks.Where(task => task.EndTime.HasValue).Max(task => task.EndTime);

            if (first.HasValue && last.HasValue)
            {
                lines.Add($"时间范围：{first:yyyy-MM-dd HH:mm:ss} → {last:yyyy-MM-dd HH:mm:ss}"
                    + (last.Value > first.Value ? $"（用时 {(last.Value - first.Value).ToString(@"hh\:mm\:ss")}）" : string.Empty));
            }
            else
            {
                lines.Add($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }

            /*
             * 数法只有一份（`Models/BatchOutcomeTally`）：批末「本批汇总」那一行、一键汇总那一行、
             * 以及这里三处共用，⛔ 别在这里再写一遍 `Count(task => …)`。
             *
             * 旧写法（2026-10-02 真机日志里读出来的）漏了两件事：
             * ① 跟班卷（同一分卷组的后续卷）被算进"跳过" —— 同一份日志里批末说"跳过 0"、
             *    头部却会把每一卷都数成一次"跳过"；
             * ② 只报成功 / 失败 / 跳过三档 —— "既没成功、也没失败、也没跳过"的那些任务
             *    （例如导入完还没跑）一个数都不占，于是头部写着
             *    「任务数：10（成功 0 / 失败 0 / 跳过 0）」，读的人当场对不上账。
             */
            BatchOutcomeTally tally = BatchOutcomeTally.Count(Tasks);

            /*
             * 任务数要分两层说（用户 2026-09-26 第 45 条的真机现场）：他跑 38 个包，头部却写"任务数：76" ——
             * 因为续解出来的内层包**也是任务**。不分开说，读的人会以为程序多跑了 38 个包。
             */
            int continuation = Tasks.Count(task => task.IsContinuationTask);

            lines.Add(
                $"任务数：{tally.Total}（{string.Join(" / ", tally.BuildParts())}）"
                + (continuation > 0 ? $"；其中续解出来的内层包 {continuation} 个" : string.Empty));

            /*
             * 跟班卷那一句与两个汇总行**共用同一句**（`DescribeFollowerNote`）：
             * 数字从"跳过"里剔出去之后，它必须在头部里出现一次，否则三个分项加起来又对不上任务数。
             */
            if (tally.FollowerSkipped > 0)
            {
                lines.Add(tally.DescribeFollowerNote());
            }

            string engine = DescribeEngineIdentity();

            if (!string.IsNullOrWhiteSpace(engine))
            {
                lines.Add("引擎：" + engine);
            }

            /*
             * ⛔ 读的是①页那一格的**同一份判据**（`ResolveOutputLocationText`），
             * 不是 `SelectedOutputDirectory` 这个字段本身：字段会被"记住的目录"填上，
             * 而这一批的落点可能压根不用它 —— 老写法因此在真机日志里写出上一批的
             * <c>…\测试\BBB</c>，与正文每一行"本次实际输出目录 …\222\…"互相打脸。
             */
            string outputRoot = OutputLocationForLog;

            if (!string.IsNullOrWhiteSpace(outputRoot))
            {
                lines.Add(outputRoot);
            }

            lines.Add(
                "细节在哪看：①页任务行的「详情 / 右键复制任务信息」= 单任务全过程；"
                + "「导出失败清单」= 每个失败包的原因与所用引擎；"
                + "③页「工作区残留」= 失败时留下的中间产物（要先在③页打开「失败时保留中间产物」）；"
                + "要看成功任务的全过程请在⑥设置勾上「详细日志（排查用）」再跑一次。");
            lines.Add("================ 以下是日志正文 ================");

            return lines;
        }

        /// <summary>
        /// 当前生效的引擎标识（名字 + 版本）—— 导出头部与任务详情共用同一份口径。
        ///
        /// ⛔ 问的是 <c>TaskSummaryService.EngineIdentity</c>（报告层那一份），
        /// 不自己 new 引擎、也不自己拼 7z 路径（AGENTS.md §3.1：引擎选择只允许注册表一个来源）；
        /// 版本取不到时它自己只说名字（见 <see cref="EngineIdentity.Describe"/>），这里不补编。
        /// </summary>
        private string DescribeEngineIdentity()
        {
            try
            {
                string engine = _taskSummaryService.EngineIdentity.Describe();

                if (string.IsNullOrWhiteSpace(engine))
                {
                    return string.Empty;
                }

                /*
                 * ⚠ 优先级那一格是 List<string>：直接插进字符串会写成
                 * `System.Collections.Generic.List`1[System.String]`（2026-09-26 真机导出的头部里就是这么写的）。
                 * 这里必须自己用 `→` 连起来（与②页引擎那一栏同一口径）。
                 */
                string priority = Settings.EnginePriority is { Count: > 0 }
                    ? string.Join(" → ", Settings.EnginePriority.Where(value => !string.IsNullOrWhiteSpace(value)))
                    : string.Empty;

                return string.IsNullOrWhiteSpace(priority)
                    ? engine
                    : $"{engine}（优先级：{priority}）";
            }
            catch
            {
                // 报告层不该因为"引擎信息取不到"整个失败：头部少一行，日志照旧导得出来。
                return string.Empty;
            }
        }

        /// <summary>
        /// 「导出日志」对话框的**起始目录**（用户 2026-10-01 真机 `giu910` 那一单问的"为什么这里有个 txt"）。
        ///
        /// <para>现场：导出对话框默认开在"上次用过的那个文件夹"，而那一批的源包正好在
        /// <c>…\giu910\giu910\其余物\</c> 里（他把上一轮被收进其余物的包再跑了一次）⇒ 日志就落在了
        /// **其余物**里。那个文件夹是程序自己会**整份删掉**的地方，日志落在那儿哪天就跟着没了。</para>
        ///
        /// <para>两条口径：① 记住**上一次成功导出到哪个目录**，下次就从那儿开（他习惯把日志放哪儿就一直在那儿，
        /// ⛔ 不在代码里写死任何个人路径 —— §8）；② 记着的目录若落在**其余物 / 工作区**里，**一律不采用**
        /// （退回系统默认），⛔ 绝不把用户往那两个会被清掉的地方引。</para>
        /// </summary>
        private string? ResolveLogExportInitialDirectory()
        {
            string remembered = Settings.LastLogExportDirectory ?? string.Empty;

            if (string.IsNullOrWhiteSpace(remembered) || !Directory.Exists(remembered))
            {
                return null;
            }

            string full;

            try
            {
                full = Path.GetFullPath(remembered);
            }
            catch
            {
                return null;
            }

            if (IsInsideDeletableProcessFolders(full))
            {
                return null;
            }

            return full;
        }

        /// <summary>
        /// 这个目录是不是落在"程序自己会整份删掉"的地方（**其余物** / **工作区**）——
        /// 日志导出**不记**这种目录、也不用它当起始目录（用户 2026-10-01 真机就是导出落进了其余物）。
        /// 判据只有一处：<see cref="ProcessArtifactLayout.IsInsideDeletableProcessFolders"/>。
        /// </summary>
        private static bool IsInsideDeletableProcessFolders(string fullPath) =>
            ProcessArtifactLayout.IsInsideDeletableProcessFolders(fullPath);

        /// <summary>导出成功后记住这个目录（下次对话框从这儿开）。⛔ 其余物 / 工作区那一档一个字节都不记。</summary>
        private void RememberLogExportDirectory(string? exportedFilePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(exportedFilePath ?? string.Empty);

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                string full = Path.GetFullPath(directory);

                if (IsInsideDeletableProcessFolders(full))
                {
                    return;
                }

                if (string.Equals(Settings.LastLogExportDirectory, full, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Settings.LastLogExportDirectory = full;
                WriteSettingsToDisk("记住日志导出目录");
            }
            catch
            {
                // 记不住只是下次少一点方便，⛔ 不许因为它把导出本身搞失败。
            }
        }

        private void ExportLog()
        {
            /*
             * 用户 2026-09-25 第 38 条："我想要的是本次操作的日志，也就是我最近一次点开操作的日志，
             * 我看着全部的日志非常的累" —— 所以这里默认只导本次操作（从"本次操作开始"那行分隔线起），
             * 不再把历史日志全拼进来（历史日志另有下面那个显式入口）。
             */
            string path = _dialogService.ShowSaveFileDialog(
                "导出日志（本次操作）",
                "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                $"ArchiveFixer-本次操作_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                ResolveLogExportInitialDirectory());

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                (int lineCount, bool fromMarker) = _logService.ExportOperationLog(path, BuildLogExportHeader());

                AppendLog("INFO", $"日志已导出（本次操作）：{path}（{lineCount} 行）");

                RememberLogExportDirectory(path);

                _dialogService.ShowInfo(
                    fromMarker
                        ? $"本次操作的日志已导出：{lineCount} 行。{Environment.NewLine}{path}"
                        : $"这次还没有「操作开始」的标记，已导出本次运行的完整日志：{lineCount} 行。{Environment.NewLine}{path}");
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
        /// 由解压管线在"最外层任务 + 内容物已定稿 + 输出校验通过 + 未取消"的收尾处调用，
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

        /// <summary>
        /// 「打开工作区目录」（③ 页与 ⑥ 设置页那个按钮）。
        ///
        /// <para>⛔ <b>这里刻意不建目录</b>（2026-09-30 随「缓存根目录」设置项一起收紧）：
        /// 工作区根只有一个来源 = **这一批的目标目录**，本趟还没解过东西时
        /// <see cref="PathService.WorkDirectory"/> 取的是**升级前的老位置** <c>&lt;数据根&gt;\work</c>
        /// （它只是"③ 页要扫哪些根"的一项）。老实现先 <c>EnsureDirectoryExists</c> 再打开 ——
        /// 于是"点一下按钮"就会在**程序所在那块盘**上凭空造出一个工作区目录，
        /// 正是用户点名不要的那件事（"甚至危险操作固定到了 C 盘"）。</para>
        ///
        /// <para>现在：目录在就打开；不在就**如实说清为什么不在**（工作区只跟目标目录走，
        /// 成功那一趟连壳都会收掉，所以这里通常什么都不会有）。</para>
        /// </summary>
        private void OpenWorkDirectory()
        {
            string workDirectory = _pathService.WorkDirectory;

            try
            {
                if (!Directory.Exists(workDirectory))
                {
                    AppendLog(
                        "INFO",
                        $"工作区目录现在不存在：{workDirectory}（工作区只由这一单的目标目录派生，"
                        + "成功那一趟连空壳都会收掉；本趟还没解过东西时这里是升级前的老位置）。");

                    _dialogService.ShowInfo(
                        $"这里现在没有工作区目录：{Environment.NewLine}{workDirectory}{Environment.NewLine}{Environment.NewLine}"
                        + "工作区只跟目标目录走：这一单成品要落的那个目录下的 .ArchiveFixer.work"
                        + "（点开头 + 隐藏）。解压成功的那一趟会连空壳一起收掉，所以这里通常是空的；"
                        + "失败 / 取消留下的中间产物在「清理与删除」页看得到体积。"
                        + "工作区位置没有任何设置项可以改 —— 想换位置就换这一批的输出位置。");

                    return;
                }

                _pathService.OpenDirectory(workDirectory);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "打开工作区目录失败：" + ex.Message);
                _dialogService.ShowError(
                    $"打开工作区目录失败：{ex.Message}{Environment.NewLine}"
                    + $"目录是：{workDirectory}（可以在资源管理器里手工打开；"
                    + "目标目录不可写时也会出现这种情况 —— 工作区位置只由目标目录决定，"
                    + "换一个能写的输出位置即可）。");
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
                ? "⚠ 彻底删除：内容不会进回收站，无法恢复。"
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

            return "当前任务的输出目录与源包所在目录是同一个（手动档「解压到当前文件夹」之后就是这样），"
                   + "本次只删本任务那一份其余物，不会碰别的包。";
        }

        /// <summary>任务的输出目录是不是就是源包所在目录（**手动档「解压到当前文件夹」之后的形态**）。</summary>
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
                     * 「记住上次输出目录」关掉时，**下次启动不填回**这个目录（启动读设置那一处判的就是它）。
                     *
                     * ⚠ 老代码在这里分了两支：关掉时**不写盘**、并写一句"本次选择不会写入设置" ——
                     * 那句话是**假的**：800 ms 的自动保存按整份 Settings 的 JSON 指纹判"变了没有"
                     * （AutoSaveSettingsIfChanged），CustomOutputDirectory 就在里面，照写不误。
                     * 2026-09-29 复核逮到（同一件事两处判据打架），这里改成一句话说清真实行为：
                     * 盘照落（开关关掉不影响它），只是下次启动不再拿它当默认。
                     */
                    WriteSettingsToDisk("输出位置");

                    AppendLog(
                        "INFO",
                        Settings.RememberLastOutputDirectory
                            ? "输出位置已记住：下次启动会填回这个目录。"
                            : "「记住上次输出目录」已关闭：下次启动不会填回这个目录（设置文件里仍会记下它）。");
                }

                RefreshOutputPaths();

                /*
                 * ⚠ 这一句是 2026-09-25 第 31 条的关键：上面那几行直接写了 Settings，
                 * 而②页「落点（解压到哪）」那几个控件绑的是 SettingsEditor 算出来的属性 ——
                 * 不通知的话，用户从①页选完目录去②页，看到的还是老的单选状态与灰着的路径框。
                 */
                NotifyOutputPlacementChangedEverywhere();

                AppendLog("INFO", "已选择输出目录：" + folder);

                /*
                 * 换了输出位置 = **换了一块盘**（用户 2026-09-27 第 2 条："用户选/换指定位置时也要判"）。
                 * 后台体检一次：新盘要是装不下这批源包，当场说清是哪些、差多少。
                 * 它是 `_ =`（不 await）的：这里在命令的同步路径上，绝不为一次提醒卡住界面切换 ——
                 * 体检自己吞掉所有异常，不存在"没人接的异常"。
                 */
                _ = CheckSpaceForTasksAsync(Tasks.ToList(), "换了输出位置");
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
        /// 移除**勾选的**任务（用户 2026-09-24 第 15 条："列表要能删无用物"）。
        ///
        /// <para>只动任务列表：源文件、输出目录、日志一个字节都不碰 —— 移除错了再"添加"一次就回来了，
        /// 所以这里**不做二次确认**（确认框留给真正不可逆的事：删源、清工作区、清其余物）。</para>
        /// </summary>
        private void RemoveCheckedTasks()
        {
            List<ArchiveTask> checkedTasks = Tasks.Where(task => task.IsSelected).ToList();

            /*
             * 一个都没勾、但用户**点中了一行**（高亮）→ 就按那一行办。
             *
             * ⚠ 2026-09-29 补上"没勾着就不亮"那一半之后（命令判据 = 列表里至少有一条勾着的，⛔ 不看忙闲），
             * 界面**已经点不到**这种状态了（按钮是灰的 + 不显示 hover），所以这一段降级成"兜底网"：
             * 判据在 WPF 重问与用户实际点下之间可能差一拍（例如刚点完「全不选」），
             * 那一下也**不能**变成"点了没反应"——还是照旧按高亮那一行办，并在日志里写明是按哪一行办的。
             * ⛔ 绝不会退化成"删掉用户没打算删的那一行"：只有 SelectedTask 确实还在列表里才走这条。
             */
            if (checkedTasks.Count == 0)
            {
                ArchiveTask? highlighted = SelectedTask;

                if (highlighted != null && Tasks.Contains(highlighted))
                {
                    AppendLog(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.RemoveCheckedTasksHighlightFallbackLogFormat,
                            highlighted.FileName));

                    RemoveTasksCore(new[] { highlighted });
                    return;
                }

                _dialogService.ShowInfo(StatusText.RemoveCheckedTasksNoneText);
                return;
            }

            RemoveTasksCore(checkedTasks);
        }

        /*
         * ==================== 「按建议改名并重试」（用户 2026-09-25 第 41 条） ====================
         *
         * 场景：一组分卷的**第一卷**名字被改坏（`Code Complete-BZ.7z(删掉.001` 那种），
         * 程序只能报「分卷缺失」+ 给一个标准改名建议 —— 源文件它自己一个字节都不能动（不变量 1）。
         * 但这一卷是真的需要改名，让用户对着日志手打一个不规则的名字，既慢又容易打错。
         *
         * 所以：**用户显式点这个按钮**（勾选 + 确认框两道），程序替他改**这一个名字**，然后立刻重试。
         * ⛔ 三条红线：①只在用户点了之后才动；②只 `File.Move` 改名，**绝不覆盖**（目标名被占就拒绝）、
         * 绝不删除、绝不改内容；③改名建议只有一处推法（`Extraction/VolumeNameRepair`），
         * 与失败清单里写给他的是同一个名字。
         */

        /// <summary>「按建议改名并重试」当前能改的那几个任务（勾选 + 这一档的毛病 + 计划成立）。</summary>
        private List<(ArchiveTask Task, VolumeNameRepairPlan Plan)> BuildVolumeRepairCandidates()
        {
            var candidates = new List<(ArchiveTask, VolumeNameRepairPlan)>();

            foreach (ArchiveTask task in Tasks.Where(item => item != null && item.IsSelected))
            {
                /*
                 * 判据是**机器事实**：任务上记着改名建议（= 上一轮判决认定"名字被改坏的第一卷"），
                 * 而且此刻从文件系统真能算出一个成立的计划。
                 * ⛔ 不比对中文状态文案（AGENTS.md §7）；建议为空时这个按钮根本不该亮。
                 */
                if (string.IsNullOrWhiteSpace(task.VolumeRenameSuggestion))
                {
                    continue;
                }

                VolumeNameRepairPlan plan = VolumeNameRepair.Plan(task.CurrentPath, EnumerateDirectoryFileNames(task.CurrentPath));

                if (plan.CanRepair)
                {
                    candidates.Add((task, plan));
                }
            }

            return candidates;
        }

        /// <summary>
        /// 同目录里的文件名（读不了就当作空 —— 计划会因此判"不能改"，绝不抛）。
        ///
        /// <para>实现只有一份：<see cref="VolumeNameRepair.EnumerateFileNamesInDirectory"/>
        /// （识别阶段、改名预览、①页那颗按钮三处共用；⛔ 各写一份必然漂移）。</para>
        /// </summary>
        private static IEnumerable<string?> EnumerateDirectoryFileNames(string? filePath)
        {
            return VolumeNameRepair.EnumerateFileNamesInDirectory(filePath);
        }

        private bool CanRenameBySuggestionAndRetry()
        {
            return !IsBusy && BuildVolumeRepairCandidates().Count > 0;
        }

        /// <summary>
        /// 按建议改名（只改名字）并立刻重试解压。
        /// </summary>
        internal async Task RenameBySuggestionAndRetryAsync()
        {
            List<(ArchiveTask Task, VolumeNameRepairPlan Plan)> candidates = BuildVolumeRepairCandidates();

            if (candidates.Count == 0)
            {
                _dialogService.ShowInfo(StatusText.VolumeRepairNoneText);
                return;
            }

            string list = string.Join(
                Environment.NewLine,
                candidates.Select(item => "· " + item.Plan.Describe()));

            bool confirmed = _dialogService.ShowConfirm(
                StatusText.VolumeRepairConfirmTitle
                + Environment.NewLine + Environment.NewLine
                + string.Format(StatusText.VolumeRepairConfirmBodyFormat, list));

            if (!confirmed)
            {
                AppendLog("INFO", $"用户取消了「{StatusText.VolumeRepairConfirmTitle}」，磁盘上的文件没有动。");
                return;
            }

            _logService.MarkOperationStart(StatusText.VolumeRepairConfirmTitle);

            int renamed = 0;
            int failed = 0;
            var repaired = new List<ArchiveTask>();

            foreach ((ArchiveTask task, VolumeNameRepairPlan plan) in candidates)
            {
                VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

                if (!result.Success)
                {
                    failed++;
                    task.ErrorMessage = result.Message;
                    AppendLog("ERROR", $"{plan.CurrentFileName}：{result.Message}");
                    continue;
                }

                renamed++;
                repaired.Add(task);

                AppendLog("INFO", result.Message);

                /*
                 * 任务要跟着搬到新路径上：文件在磁盘上已经叫新名字了，任务还指着旧名字的话，
                 * 紧接着的"重新识别"第一步就会撞上"源文件不在了"。
                 *
                 * ⚠ 同步走的是**唯一出口** <see cref="ExtractionCoordinator.SyncTasksAfterVolumeRename"/>：
                 * 这次改名动的是**整组**（组里别的卷很可能是同一批里另一个任务的文件，用户 2026-09-30 真机），
                 * 所以它把所有指着被改过名的文件的任务一起搬到新名字上、并重拍它们的源文件快照
                 * （不变量 11 的基准跟着名字走）。以前这里只改自己这一行 → 别的任务被不变量 11
                 * 报成"源文件已变化（文件不见了）"。
                 */
                _extractionCoordinator.SyncTasksAfterVolumeRename(plan.Items, task);

                /*
                 * 重新识别 + **重拍源文件快照**（不变量 11）：路径换了，旧快照对新名字毫无意义，
                 * 不重拍的话紧接着的解压会被"源文件已变化"拦下 —— 那条提示是防"文件被改过"的，
                 * 不该拿来挡用户主动改的名字。
                 */
                await _scanCoordinator.RescanTaskAsync(task).ConfigureAwait(true);
            }

            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.VolumeRepairBatchDoneFormat,
                    renamed,
                    failed));

            if (renamed == 0)
            {
                _dialogService.ShowWarning(StatusText.VolumeRepairNoneText);
                return;
            }

            /*
             * **同一组的其它任务也要重扫**（2026-09-26 真机代跑当场撞到）：
             * 它们的"分卷组"是**改名之前**拍下来的快照 —— 例：首卷名字坏掉时，`set.7z.002/.003/.004`
             * 会各自成组（缺首卷），修好首卷之后这些任务手上还是"缺 set.7z.001"的旧结论，
             * 紧接着的重试就会弹一句"分卷不完整，现在缺：set.7z.001"（可那个文件明明已经在了）。
             * 判据用既有的 BelongsToSameGroup（族 + 基名），⛔ 不自己再写一套名字比较。
             */
            foreach (ArchiveTask sibling in FindSameGroupTasks(repaired))
            {
                await _scanCoordinator.RescanTaskAsync(sibling).ConfigureAwait(true);
            }

            /*
             * 重试 = 与「只解压」**同一条路**（不复制一份解压逻辑出来），但**只解刚刚改过名的那些任务**
             * （2026-09-26 审计补的口径）：
             *
             * 这颗按钮以前只在"解压跑过一轮并报了分卷缺失"之后才亮，那时勾选的就是刚跑的那一批，
             * 所以"按勾选重试"没问题；现在它**扫完就亮**，用户完全可能在还没跑过任何解压的时候点它 ——
             * 那时整张表都勾着，旧写法会把**所有**勾选任务都解一遍（他只想修这一卷）。
             * 用完把勾选原样还回去：勾选是用户的作用域，不该被这一下改掉。
             */
            List<ArchiveTask> previousSelection = Tasks.Where(item => item.IsSelected).ToList();

            RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask item in Tasks)
                {
                    item.IsSelected = repaired.Contains(item);
                }
            });

            try
            {
                await _extractionCoordinator.StartExtractAsync().ConfigureAwait(true);
            }
            finally
            {
                RunBulkSelectionUpdate(() =>
                {
                    foreach (ArchiveTask item in previousSelection)
                    {
                        item.IsSelected = true;
                    }
                });
            }
        }

        /// <summary>
        /// 与刚修好的那几卷**同目录、同组**的其它任务（用来在改名之后把它们一起重扫）。
        ///
        /// <para>判据全用既有的 <c>VolumeGroupDetector.BelongsToSameGroup</c>（族 + 基名）+ 目录相等 ——
        /// ⛔ 不自己拿字符串前缀去猜"像不像一组"（那正是第 33 条踩过的坑）。</para>
        /// </summary>
        private IEnumerable<ArchiveTask> FindSameGroupTasks(IReadOnlyList<ArchiveTask> repaired)
        {
            var repairedSet = new HashSet<ArchiveTask>(repaired);
            var result = new List<ArchiveTask>();

            foreach (ArchiveTask task in Tasks)
            {
                if (task == null || repairedSet.Contains(task) || string.IsNullOrWhiteSpace(task.CurrentPath))
                {
                    continue;
                }

                string otherDirectory = System.IO.Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

                foreach (ArchiveTask fixedTask in repaired)
                {
                    string fixedDirectory = System.IO.Path.GetDirectoryName(fixedTask.CurrentPath) ?? string.Empty;

                    if (!string.Equals(otherDirectory, fixedDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (ArchiveFixer.Detection.VolumeGroupDetector.BelongsToSameGroup(
                            System.IO.Path.GetFileName(task.CurrentPath),
                            System.IO.Path.GetFileName(fixedTask.CurrentPath)))
                    {
                        result.Add(task);
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 右键「智能修正此文件后缀」的入口：**只把这一行纳入本次操作，用完把勾选原样还回去**。
        ///
        /// <para>以前是选项卡的 code-behind 自己"把其余行全取消勾选"就完事 —— 用户批量勾了 20 个、
        /// 右键修其中一个，回来发现勾选全没了，而且没有任何提示（2026-09-26 审计）。
        /// 勾选是"命令的作用域"，不该被一个针对单行的动作悄悄清空。</para>
        /// </summary>
        internal async Task RunSmartRenameForSingleTaskAsync(ArchiveTask? task)
        {
            if (task == null)
            {
                return;
            }

            List<ArchiveTask> previouslySelected = Tasks.Where(item => item.IsSelected).ToList();

            RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask item in Tasks)
                {
                    item.IsSelected = ReferenceEquals(item, task);
                }
            });

            try
            {
                if (SmartRenameRunnerOverride != null)
                {
                    await SmartRenameRunnerOverride().ConfigureAwait(true);
                    return;
                }

                await _renameCoordinator.SmartRenameAsync().ConfigureAwait(true);
            }
            finally
            {
                RunBulkSelectionUpdate(() =>
                {
                    foreach (ArchiveTask item in previouslySelected)
                    {
                        item.IsSelected = true;
                    }
                });
            }
        }

        /// <summary>
        /// 右键单文件改名的**执行替身**（只给测试用；正式路径永远是 null = 走真预览窗口）。
        ///
        /// <para>为什么需要它：真窗口在无界面宿主里不会弹，而"勾选会不会被还回来"这件事
        /// 只有把"执行"这一步换掉才测得到（与 <c>ScanCoordinator.JunkReminderOverride</c> 同一套做法）。</para>
        /// </summary>
        internal Func<Task>? SmartRenameRunnerOverride { get; set; }

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

            /*
             * ⛔ **按规范化路径比，⛔ 不比裸字符串**（真机 2026-10-07：无用物扫描认得出 15 个，
             * 可"移出列表"一个都没匹配上 ⇒ 那些 `.txt` 一直挂在列表里、还顶出"未处理 N"）。
             * 裸 `HashSet<string>` 比 `CurrentPath` 对大小写/分隔符/`..` 一律敏感 ——
             * 用既有出口 `SafePathHelper.PathEquals`（与全项目其它地方同一把尺子）。
             */
            List<ArchiveTask> matched = Tasks
                .Where(task => wanted.Any(path => SafePathHelper.PathEquals(path, task.CurrentPath)))
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

            /*
             * ⛔ 与屏幕上一致（用户 2026-10-07：「这个显示的问题，根本就没有同步」）：
             * 名称 / 体积 / 路径 / 错误信息取**显示口径**那一份；裸值在下面三行里一个字不少。
             */
            builder.AppendLine("文件名：" + (task.DisplayFileName ?? string.Empty));
            builder.AppendLine("大小：" + (task.DisplaySizeText ?? string.Empty));
            builder.AppendLine("界面显示路径：" + (task.DisplayPath ?? string.Empty));
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
            builder.AppendLine("错误信息：" + (task.DisplayErrorMessage ?? string.Empty));

            // 这一行经历过的变换（改名 / 续解每一层 / 谁解开整组）；空着就不写行。
            if (task.DisplayTransformationText.Length > 0)
            {
                builder.AppendLine("过程：" + task.DisplayTransformationText);
            }
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

        // ==================== 「看内容 / 试密码」（用户 2026-09-26 批准加的两个高频功能） ====================

        /// <summary>
        /// 「看内容」：对勾选的任务只 `list` 一遍 —— 回答"这个包里有什么、多大、要不要密码"。
        ///
        /// <para>⛔ 不写盘、不解压（<see cref="ArchiveInspectService"/> 只调 list）；结论走一个可复制的弹窗 + 日志。</para>
        /// </summary>
        private async Task InspectCheckedArchivesAsync()
        {
            await RunInspectAsync(tryPasswords: false).ConfigureAwait(true);
        }

        /// <summary>
        /// 「试密码」：与「看内容」同一条实现，只是**把候选逐个都问一遍**，报出能开的那个。
        /// </summary>
        private async Task TryPasswordsForCheckedArchivesAsync()
        {
            await RunInspectAsync(tryPasswords: true).ConfigureAwait(true);
        }

        /// <summary>
        /// 两个按钮共用的本体（⛔ 一份实现：不然"看内容"说不能开、"试密码"说能开，用户只会更糊涂）。
        /// </summary>
        private async Task RunInspectAsync(bool tryPasswords)
        {
            List<ArchiveTask> targets = Tasks.Where(item => item.IsSelected).ToList();

            if (targets.Count == 0)
            {
                _dialogService.ShowInfo("请先勾选要看的任务（最左侧一列）。");
                return;
            }

            _logService.MarkOperationStart(tryPasswords ? "试密码" : "看内容");
            AppendLog("INFO", $"{(tryPasswords ? "试密码" : "看内容")}：{targets.Count} 个任务（只列目录，不解压、不写盘）。");

            IsBusy = true;

            try
            {
                var inspect = new ArchiveInspectService(_archiveEngine);
                var report = new System.Text.StringBuilder();

                foreach (ArchiveTask task in targets)
                {
                    /*
                     * 候选按**既有那一套顺序**算（与解压完全同一份：本批已成功 → 空密码 → 映射式 → 统一密码 → 列表）——
                     * ⛔ 这里不许自己拼一套，否则界面上给的顺序与真正解压时的顺序会对不上。
                     * 「看内容」不试密码（只回答"要不要密码"）；「试密码」才把候选交下去。
                     */
                    IReadOnlyList<PasswordItem>? candidates = tryPasswords
                        ? _passwordService
                            .GetPasswordCandidates(
                                task,
                                Settings.UseGlobalPasswordForAllTasks ? GlobalPassword : string.Empty,
                                _passwordService.Passwords,
                                Settings.TryEmptyPasswordFirst,
                                Settings.EnableSidecarPassword)
                            .Take(Math.Clamp(Settings.MaxPasswordAttemptsPerLayer, 1, 1000))
                            .ToList()
                        : null;

                    ArchiveInspectResult result = await inspect.InspectAsync(task, candidates).ConfigureAwait(true);

                    report.AppendLine(DescribeInspectResult(result, tryPasswords));
                    report.AppendLine();

                    AppendLog(
                        result.Success ? "INFO" : "WARN",
                        $"{task.FileName}：{(tryPasswords ? "试密码" : "看内容")} —— {SummarizeInspectResult(result, tryPasswords)}");
                }

                _dialogService.ShowInfo(
                    $"{(tryPasswords ? "试密码" : "看内容")}（{targets.Count} 个任务，只列目录、没写盘）："
                    + Environment.NewLine + Environment.NewLine
                    + report.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", $"{(tryPasswords ? "试密码" : "看内容")}失败：{ex.Message}");
                _dialogService.ShowError($"{(tryPasswords ? "试密码" : "看内容")}失败：{ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>弹窗里那一段（人读的：多行、可复制）。</summary>
        internal static string DescribeInspectResult(ArchiveInspectResult result, bool tryPasswords)
        {
            var text = new System.Text.StringBuilder();

            text.AppendLine($"【{result.Task?.FileName ?? "（未知）"}】");

            if (!result.Success)
            {
                text.AppendLine($"  ✗ {result.Message}");
                return text.ToString().TrimEnd();
            }

            string size = TaskSpaceEstimate.FormatSize(result.TotalBytes);

            text.AppendLine(
                $"  格式 {result.Format} ｜ 文件 {result.FileCount} 个"
                + (result.DirectoryCount > 0 ? $" + 文件夹 {result.DirectoryCount} 个" : string.Empty)
                + $" ｜ 解压后约 {size}"
                + (result.IsMultiVolume ? " ｜ 分卷" : string.Empty));

            if (result.NeedsPassword)
            {
                text.AppendLine(result.PasswordFound
                    ? $"  密码：需要 —— 能开的是「{result.PasswordLabel}」（试到第 {result.CandidatesTried} 个）"
                    : $"  密码：需要 —— {result.CandidatesTried}/{result.CandidatesTotal} 个候选都没能开"
                      + (tryPasswords ? string.Empty : "（想要它替你逐个试：点「试密码」）"));
            }
            else
            {
                text.AppendLine("  密码：不需要");
            }

            if (result.TopEntries.Count > 0)
            {
                text.AppendLine("  里面有：");

                foreach (ArchiveInspectEntry entry in result.TopEntries)
                {
                    string suffix = entry.IsDirectory ? "（文件夹）" : "  " + TaskSpaceEstimate.FormatSize(entry.Size);
                    text.AppendLine($"    · {entry.Name}{suffix}");
                }

                int remaining = result.FileCount + result.DirectoryCount - result.TopEntries.Count;

                if (remaining > 0)
                {
                    text.AppendLine($"    … 还有 {remaining} 项没列出来");
                }
            }

            text.AppendLine($"  （用时 {result.ElapsedText}）");

            return text.ToString().TrimEnd();
        }

        /// <summary>日志里那一行（短）。</summary>
        internal static string SummarizeInspectResult(ArchiveInspectResult result, bool tryPasswords)
        {
            if (!result.Success)
            {
                return result.Message;
            }

            string password = !result.NeedsPassword
                ? "不需要密码"
                : result.PasswordFound
                    ? $"密码={result.PasswordLabel}"
                    : $"没试出密码（{result.CandidatesTried}/{result.CandidatesTotal}）";

            return $"格式 {result.Format}，{result.FileCount} 个文件 / {TaskSpaceEstimate.FormatSize(result.TotalBytes)}，{password}";
        }
        /// <summary>
        /// 「打开源目录」（手动那一栏）：第一个勾选任务所在的文件夹；一个都没勾就退回"当前行"。
        ///
        /// <para>⛔ 它只**打开目录**，不动任何文件（不选中、不改名、不删除）。</para>
        /// </summary>
        private void OpenCheckedSourceDirectory()
        {
            ArchiveTask? task = Tasks.FirstOrDefault(item => item.IsSelected) ?? SelectedTask;

            if (task == null)
            {
                _dialogService.ShowInfo("列表里还没有任务：先点「添加文件 / 添加文件夹」（也可以直接把文件拖进窗口）。");
                return;
            }

            OpenTaskDirectory(task);
            AppendLog("INFO", $"{task.FileName}：已在资源管理器里打开它所在的目录。");
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
                 ChangeNameCommand,

                 StartExtractCommand,
                 ExtractIntoSourceFolderCommand,
                 StopCommand,
                 CancelCurrentCommand,

                 SelectAllTasksCommand,
                 SelectNoneTasksCommand,
                 InvertTaskSelectionCommand,
                 SelectSoleTaskCommand,

                 /*
                  * 「移除勾选的」（用户 2026-09-29 第二次反馈：判据对了界面也不动）。
                  * 它以前**不在**这份名单里，而 RelayCommand 自己不发 CanExecuteChanged ——
                  * WPF 只在 CommandManager.RequerySuggested（焦点 / 输入变化）时才重问，
                  * 于是"勾上一个任务"之后按钮要等下一次焦点变化才亮，用户看到的就是"先亮着不动"。
                  * 这份名单由 UpdateSummary 统一发一遍（勾选变化 / 任务增删 / 忙闲切换都会走到它），
                  * ⛔ 不许在别处再写一套通知。
                  */
                 RemoveCheckedTasksCommand,

                 OpenSettingsCommand,
                 OpenPasswordListCommand,
                 OpenPackingCommand,
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
                 RenameBySuggestionAndRetryCommand,
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