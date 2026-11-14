using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels;

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
public partial class MainViewModel : ViewModelBase
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

    internal Task<bool> AutoFixSingleTaskAsync(ArchiveTask task) => _renameCoordinator.AutoFixSingleAsync(task);
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
    /// <para><see cref="PasswordBookCoordinator.AutoLoad"/> 有五个调用点（启动、清空列表、移除选中、
    /// 移除单个任务、保存设置），其中只有启动那一次该读记忆 —— 读记忆会**重建整个列表**，
    /// 反复读会把用户刚做的改动冲掉。后来的调用只做"合并密码本"这件幂等的事。</para>
    /// </summary>
    private bool _showPassword;
    private bool _isBusy;

    /// <summary>工作区残留那一行提示的文本（空 = 没有残留）。见 <see cref="LogLeftoverWorkspaces"/>。</summary>
    private string _workspaceLeftoverBanner = string.Empty;

    private bool _hasWorkspaceLeftovers;

    private long _workspaceLeftoverTotalBytes;

    /// <summary>上一批撞到层数上限后还剩多少个内层包没解（见 <see cref="PendingContinuationCount"/>）。</summary>
    private int _pendingContinuationCount;
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

    /// <summary>诊断日志入口（只读；⛔ 业务判据不许读它，只许写日志）。</summary>
    internal LogService Logger => _logService;

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
    public string AboutText => AboutTextProvider.Build(SettingsEditor.Engines);

    /// <summary>
    /// 把设置里与归档引擎有关的项推给引擎层。
    /// 两个外部工具（7z.exe / UnRAR.exe）的路径、引擎优先级、保留受损文件**只**通过这里生效，
    /// 别处不许再拼路径、也不许再各读一遍设置（AGENTS.md §3.1）。
    /// </summary>

    /*
     * ⛔ 这里原来还有一个 `ReloadSettingsFromEffectiveDataRoot(loadedFrom)` 的"第二步读"，
     * 2026-09-30 **随「缓存根目录」设置项一起删除**。
     *
     * 它存在的唯一理由是：数据根由设置项决定，于是设置文件可能有两处（程序目录那一份、
     * 缓存根那一份），换了位置就得再读一次。现在数据根固定是 %LOCALAPPDATA%\ArchiveFixer
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

    /// <summary>
    /// ③「清理与删除」页与启动日志**实际要扫的**工作区根（**只给测试与排障读**）。
    ///
    /// <para>产品路径只用 <see cref="LogLeftoverWorkspaces"/> 与 <see cref="ClearWorkspaceLeftovers"/>；
    /// 把这个清单露出来是因为"扫了哪些根"决定了界面上报几个残留、以及清理会动哪些目录 ——
    /// 那是必须能被钉住的一条事实（默认跟输出盘之后根会变，多一个根就多一批会被删的目录）。</para>
    /// </summary>
    internal IReadOnlyList<string> WorkspaceScanRoots
        => WorkspaceScanRootProvider.Resolve(_pathService.WorkDirectory, WorkspaceRootIndex.SessionRoots);

    private IReadOnlyList<string> ResolveWorkspaceScanRoots()
        => WorkspaceScanRootProvider.Resolve(_pathService.WorkDirectory, WorkspaceRootIndex.SessionRoots);

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
        => EngineIdentityResolver.Resolve(_archiveEngine, task);

    private static EngineRegistry BuildEngineRegistry(IArchiveEngine? injected)
        => EngineRegistryFactory.Build(injected);

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

    /// <summary>
    /// 记忆读不出来时，在摘要那一行后面挂一句**短**提示（明细在密码列表窗口的提示条上）。
    ///
    /// <para>为什么要挂：解不开时列表是空的，主界面会显示"未加载（0 条）"——
    /// 用户很容易理解成"程序把我密码弄丢了"。这里必须说清"是读不出来、已忽略、文件没被动"。</para>
    /// </summary>

    /// <summary>
    /// 摘要那一行的 ToolTip：**可以写完整路径**（界面是给用户自己核对的），
    /// 但它只显示、绝不进日志（§8：个人路径不入日志）。
    /// </summary>

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

    /// <summary>
    /// 退出"忙"的一层。只有深度回到 0 才真的把标志复位。
    ///
    /// 多退一次（没有对应的 EnterBusy）会被夹住在 0：宁可当成"不忙"，
    /// 也绝不能让计数变成负数 —— 那样之后所有 Enter/Exit 都配不平，界面会**永久灰掉**。
    /// </summary>

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
                        ? "已开启「全速」：不再按「最大并发解压数」节流，但硬顶 8 个（这个开关会记住）。"
                        : "已关闭「全速」：按设置里的「最大并发解压数」节流（这个开关会记住）。");
            }
        }
    }

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
    public ICommand SelectAllTasksCommand => Commands.SelectAllTasksCommand;

    /// <summary>取消全部勾选（Ctrl+D）。</summary>
    public ICommand SelectNoneTasksCommand => Commands.SelectNoneTasksCommand;

    /// <summary>反选（Ctrl+I）。</summary>
    public ICommand InvertTaskSelectionCommand => Commands.InvertTaskSelectionCommand;

    /// <summary>只勾选指定的那一个（右键菜单「只勾选这一个」），其余全部取消勾选。</summary>
    public ICommand SelectSoleTaskCommand => Commands.SelectSoleTaskCommand;

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

    public void UpdateSummary()
    {
        Summary = _taskSummaryService.BuildSummary(Tasks);
        OnPropertyChanged(nameof(SelectedTasksSummary));
        OnPropertyChanged(nameof(CanOpenManualOperation));
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

    internal void RefreshOutputPaths() => OutputPathRefresher.Refresh(this);

    /// <summary>
    /// 「设置」入口（旧菜单「工具 → 设置...」）：设置已经搬进⑥选项卡，
    /// 所以这里只做一件事 —— 请界面切到那一页，并把这件事写进日志。
    ///
    /// <para>为什么保留这个命令而不是删掉：它一直是"打开设置"的公开入口，
    /// 删掉它等于悄悄改掉 MainViewModel 的对外形状；保留成"切页"语义，
    /// 任何旧调用点仍然得到正确结果。</para>
    /// </summary>

    /// <summary>见 <see cref="OpenSettings"/>：打包整页搬进⑤选项卡。</summary>

    /// <summary>
    /// 打包那边改了它自己的两档（原包 / 其余物 / 落点）之后落盘 ——
    /// 用户 2026-09-26 第 46 条："而且相应的保存记忆操作"。
    ///
    /// <para>⛔ 只写打包那三个字段（它们在 <c>PackingEditor.Settings</c> 上，与解压侧各存各的），
    /// 这里不碰任何解压设置；写失败也不弹窗（打包本身已经跑完了，没道理为一个落盘失败打断他）。</para>
    /// </summary>

    internal void OpenSettings() => SettingsCommands.OpenSettings();

    internal void OpenPacking() => SettingsCommands.OpenPacking();

    internal void SavePackingSettings() => SettingsCommands.SavePackingSettings();

    internal void ResetSettings() => SettingsCommands.ResetSettings();

    // 「保存设置」这个动作**整块退役**（用户 2026-09-26："你那个按钮和最下面一个框……都要删除"）：
    // 现在改任何一项都会自动落盘（AutoSaveSettingsIfChanged），
    // 校验判据搬到 SettingsEditor.DescribeAutoSaveBlock()，保存后的连带动作搬到
    // ApplySettingsSideEffectsAfterSave()。⛔ 不再留一个"要用户点一下"的入口。

    internal void OpenPasswordList() => PasswordBook.OpenPasswordList();
    internal static Action HookPasswordBookSummaryRefresh(PasswordListViewModel listViewModel, Action refresh)
        => PasswordBookCoordinator.HookPasswordBookSummaryRefresh(listViewModel, refresh);

    private void SyncRememberedBookPathsToSettings() => PasswordBook.SyncRememberedBookPathsToSettings();

    internal void OpenOutputDirectory() => OutputLocation.OpenOutputDirectory();

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
    internal void OpenWorkDirectory()
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
                    + "失败 / 取消留下的中间产物在③「清理方式」页看得到体积。"
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

    internal void OpenLogDirectory()
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

    internal void SelectOutputDirectory()
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
        => TaskCollection.ReplaceAll(tasks, refreshSummary);

    internal void RaiseAllCommandCanExecuteChanged() => Commands.RaiseAll();

}

