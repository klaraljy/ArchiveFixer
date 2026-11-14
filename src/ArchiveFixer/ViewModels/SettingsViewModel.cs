using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 设置编辑器：**②解压方式 / ③清理与删除 / ④密码 / ⑥设置**四页共用（原「设置窗口」退休后，
/// 它的逻辑就搬到这里，现在按下面这些协作类分开实现）。
///
/// <para>职责边界（M5 按职责拆分之后）：本类只保留 **XAML 绑定的属性 + 公开 API 的薄转发**，
/// 以及整份设置的持有与去重通知。协作类分别是：</para>
/// <list type="bullet">
/// <item><description><see cref="SettingsContentKeepKeywordEditor"/>：②页「内容物保留关键词」那一组；</description></item>
/// <item><description><see cref="SettingsEnginePreview"/>：⑦页引擎优先级列表与 rar 工具状态预览；</description></item>
/// <item><description><see cref="SettingsRememberedBookEditor"/>：④页「已记住的密码本」那一组；</description></item>
/// <item><description><see cref="SettingsValueValidation"/>：保存前校验与"数字被夹回"提示；</description></item>
/// <item><description><see cref="SettingsGroupNotifications"/>：跨分组"改了一处、另一处重读"的通知清单；</description></item>
/// <item><description><see cref="SettingsDialogCommands"/> / <see cref="SettingsSaveCoordinator"/>：弹窗命令与保存落盘。</description></item>
/// </list>
///
/// <para>⛔ 绑定名一个字都没改；协作类只搬实现，属性仍在主类上（测试与 XAML 看到的成员与拆分前相同）。</para>
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DialogService _dialogService;

    private readonly SettingsGroupNotifications _notifications;
    private readonly SettingsContentKeepKeywordEditor _contentKeepKeywords;
    private readonly SettingsRememberedBookEditor _rememberedBooks;
    private readonly SettingsEnginePreview _enginePreview;
    private readonly SettingsDialogCommands _dialogCommands;
    private readonly SettingsSaveCoordinator _saveCoordinator;

    private AppSettings _settings;
    private bool? _dialogResult;
    private string _message = string.Empty;

    public AppSettings Settings
    {
        get => _settings;
        set
        {
            if (SetProperty(ref _settings, value))
            {
                RaiseOutputPlacementChanged();
            }
        }
    }

    /// <summary>
    /// 输出位置（**二选一**）。读写的就是设置里那两个布尔，没有新增设置项。
    /// </summary>
    public OutputPlacementOption OutputPlacement
    {
        get => ResolveOutputPlacement(
            Settings.ExtractToOriginalDirectory,
            Settings.KeepArchiveNameFolder);

        set
        {
            if (value == OutputPlacement)
            {
                return;
            }

            ApplyOutputPlacement(Settings, value);
            RaiseOutputPlacementChanged();
        }
    }

    /// <summary>
    /// "指定位置"路径。包一层是为了让上面的示例路径跟着输入实时变
    /// （<see cref="AppSettings"/> 不实现 INotifyPropertyChanged，直接绑它不会刷新）。
    /// </summary>
    public string CustomOutputDirectory
    {
        get => Settings.CustomOutputDirectory ?? string.Empty;
        set
        {
            string normalized = value ?? string.Empty;

            if (string.Equals(Settings.CustomOutputDirectory, normalized, StringComparison.Ordinal))
            {
                return;
            }

            Settings.CustomOutputDirectory = normalized;
            OnPropertyChanged();
            RaiseOutputPlacementChanged();
        }
    }

    /// <summary>只有"解压到指定位置"两种模式才用得上路径输入框。</summary>
    public bool IsCustomOutputEnabled => !Settings.ExtractToOriginalDirectory;

    /// <summary>
    /// 源包处理档（决策 D-9，三选一）：源包是移入其余物 / 留在原地 / 校验通过后删除。
    ///
    /// <para>读写的是设置里的字符串（<see cref="AppSettings.SourceHandling"/>），
    /// 解析/序列化都走 <see cref="AppSettings.ParseSourceHandling"/> /
    /// <see cref="AppSettings.ToSourceHandlingValue"/> —— 与解压时的口径是同一份实现
    /// （避免"界面上选了这个、跑起来是那个"）。</para>
    ///
    /// <para>⚠ 一键处理与手动「只解压」**两条路径读的是同一档**："成功 + 校验通过 + 未取消 +
    /// 属于本任务分卷组"四条同时成立才动源包。</para>
    /// </summary>
    public SourceHandlingMode SourceHandling
    {
        get => AppSettings.ParseSourceHandling(Settings.SourceHandling);
        set
        {
            string stored = AppSettings.ToSourceHandlingValue(value);

            if (string.Equals(Settings.SourceHandling, stored, StringComparison.Ordinal))
            {
                return;
            }

            Settings.SourceHandling = stored;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 「删除操作」两档（用户 2026-09-25 第 32 条亲自定的）：不动其余物（默认）/ 彻底删除。
    ///
    /// <para>读写的是设置里的字符串（<see cref="AppSettings.RestHandlingAfterVerify"/>），
    /// 解析/归一化都走 <see cref="RestHandlingModes"/> —— 与解压时的口径是同一份实现。</para>
    ///
    /// <para>⛔ 它取代了原来那套「危险模式 + 自测凭证」：选「彻底删除」不再需要任何凭证，
    /// ③页会在选项下面常驻一条不可关闭的红字提示（见 <see cref="IsRestDeleteSelected"/>）。</para>
    /// </summary>
    public string RestHandling
    {
        get => RestHandlingModes.Normalize(Settings.RestHandlingAfterVerify);
        set
        {
            string stored = RestHandlingModes.Normalize(value);

            if (string.Equals(Settings.RestHandlingAfterVerify, stored, StringComparison.Ordinal))
            {
                return;
            }

            Settings.RestHandlingAfterVerify = stored;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRestDeleteSelected));
            OnPropertyChanged(nameof(RestHandlingSummary));
        }
    }

    /// <summary>③页那条红字提示的可见性：选了「彻底删除」就常驻显示（不能消掉）。</summary>
    public bool IsRestDeleteSelected =>
        string.Equals(RestHandling, RestHandlingModes.Delete, StringComparison.Ordinal);

    /// <summary>这一档的一句话（确认框/界面复用它，免得两处各写一套措辞）。</summary>
    public string RestHandlingSummary => RestHandling switch
    {
        // ⚠ 2026-10-15：「移入回收站」那一档整条下线（详见 RestHandlingModes 的说明）。
        RestHandlingModes.Delete => StatusText.OneClickConfirmRestAutoDelete,
        _ => StatusText.OneClickConfirmRestKeep
    };

    /// <summary>
    /// ②页「内容物保留关键词」的列表（**薄转发**：读写、归一化与通知都在
    /// <see cref="SettingsContentKeepKeywordEditor"/> 里，判据唯一出口仍是
    /// <c>ContentKeepRules</c>）。
    /// </summary>
    public ObservableCollection<string> ContentKeepKeywordItems => _contentKeepKeywords.Items;

    /// <summary>单行输入框里正在敲的那个词（回车 = 添加，见 <c>ExtractionTab.xaml</c> 的 KeyBinding）。</summary>
    public string NewContentKeepKeyword
    {
        get => _contentKeepKeywords.NewKeyword;
        set => _contentKeepKeywords.NewKeyword = value;
    }

    /// <summary>「添加」按钮能不能点（输入框空白 ⇒ 禁用）。</summary>
    public bool CanAddContentKeepKeyword => _contentKeepKeywords.CanAdd;

    /// <summary>那一栏下面那句白话说明 + 当前生效几条（**同一份真值**）。</summary>
    public string ContentKeepKeywordsSummary => _contentKeepKeywords.Summary;

    /*
     * ⛔ 这里原来有 `CacheRootDirectory` 属性 + `ValidateCacheRootDirectory`（"缓存根目录"那一格，
     * 2026-09-30 **彻底删除**）：用户没有定工作区的权力，工作区固定由这一单的目标目录派生。
     * 旧 appsettings.json 里残留的 `CacheRootDirectory` 键由 System.Text.Json 默认行为安静忽略
     * （不抛、不警告、不迁移；用例 `SettingsCacheRootRemovalTests` 钉着）。
     */

    /// <summary>
    /// 外部工具路径的校验（**必须在保存前过这一关**；实现在
    /// <see cref="SettingsValueValidation.ValidateToolExePath"/>，与自动保存共用）。
    ///
    /// <para>为什么不能"先存下来再警告"：<see cref="AppSettings.Normalize"/> 对**不存在的**工具路径
    /// 的处理是**直接清空**（以免留一个失效路径让整个程序找不到引擎）。于是"填了一个不存在的路径
    /// → 点保存 → 看到『设置已保存』→ 关窗"这条路上，用户填的路径被静默丢掉了。
    /// 口径是**拦在保存之前**，停在设置界面上说清"填的这个文件不存在"以及"留空是什么行为"。</para>
    ///
    /// <para>检验的是"文件存不存在"，不是"它是不是 7z" —— 后者由 <c>ToolLocator</c> / 引擎自己判定，
    /// 这里越权猜只会误伤。</para>
    /// </summary>
    public static bool ValidateToolExePath(string? path, string label, string emptyHint, out string message)
        => SettingsValueValidation.ValidateToolExePath(path, label, emptyHint, out message);

    /// <summary>当前落点的一句话说明 + 具体例子。</summary>
    public string OutputPlacementSummary => OutputPlacementSummaryConverter.Describe(
        Settings.ExtractToOriginalDirectory,
        Settings.KeepArchiveNameFolder,
        Settings.CustomOutputDirectory ?? string.Empty);

    /// <summary>
    /// 把编辑器挂到**共享的**设置对象上（选项卡形态：②③④⑥四页共用一个编辑器）。
    ///
    /// <para>与构造函数的区别必须说清：构造函数克隆一份设置，好让"整份回退"成为可能
    /// （原设置窗口的「取消」用它；选项卡形态下改用 <see cref="AttachSharedSettings"/> 共享同一份）。
    /// 选项卡里没有那个"关窗即提交/丢弃"的时刻，而且同一项可能在多处出现，两份值一定会打架，
    /// 所以这里**不克隆** —— 界面改动即时进入内存，由自动保存负责校验与落盘。</para>
    ///
    /// <para>挂了之后要显式通知一轮：<see cref="AppSettings"/> 不实现 INotifyPropertyChanged，
    /// 不通知的话界面还绑在上一份对象上，看起来就是"恢复默认没反应"。</para>
    /// </summary>
    public void AttachSharedSettings(AppSettings settings)
    {
        _settings = settings ?? _settingsService.CreateDefault();

        OnPropertyChanged(nameof(Settings));
        _rememberedBooks.Refresh();
        _contentKeepKeywords.Refresh();
        RaiseOutputPlacementChanged();
    }

    /// <summary>
    /// 引擎优先级列表（界面上的顺序 = 落盘的顺序，见 <see cref="AppSettings.EnginePriority"/>）。
    ///
    /// <para>每行带检测状态、路径、版本与"当前在用"标记 —— 排第一但没装时，
    /// 用户要能一眼看出"它没被用上是因为没检测到，不是因为程序坏了"。读写与预览在
    /// <see cref="SettingsEnginePreview"/> 里。</para>
    /// </summary>
    public ObservableCollection<EngineOptionItem> Engines => _enginePreview.Engines;

    /// <summary>
    /// 设置 → 密码设置 →「已记住的密码本」的一行。
    ///
    /// <para>界面上显示**文件名**（可读），完整路径放在 ToolTip 里（用户要核对时才看）——
    /// 与主界面密码本摘要同一口径；而日志里永远只有文件名（§8：个人路径不入日志）。</para>
    /// </summary>
    public sealed class RememberedBookItem
    {
        public string Path { get; init; } = string.Empty;

        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>这个文件现在还在不在（不在了也要列出来 —— 静默丢掉一项比列着更让人困惑）。</summary>
        public bool Exists => System.IO.File.Exists(Path);

        public string DisplayName => FileName;

        /// <summary>文件不在了时的补充说明。</summary>
        public string Note => Exists ? string.Empty : "（这个文件现在不在了）";
    }

    /// <summary>
    /// "已记住的密码本"列表（顺序 = 启动时的合并顺序）。实例由本类持有（XAML 绑它），
    /// 刷新与逐项移除在 <see cref="SettingsRememberedBookEditor"/> 里。
    /// </summary>
    public ObservableCollection<RememberedBookItem> RememberedBooks { get; } = new();

    /// <summary>记住的密码本一本都没有（界面显示空状态说明，不给用户一个空白框）。</summary>
    public bool HasRememberedBooks => RememberedBooks.Count > 0;

    /// <summary>
    /// 「记住密码列表」开关（读写的是设置里那一个布尔）。
    ///
    /// <para>为什么要绕一层：<see cref="AppSettings"/> 是普通对象（没有 INotifyPropertyChanged），
    /// 直接绑 <c>Settings.RememberPasswordList</c> 的话，勾选不会让下面那段"关掉之后会怎样"的
    /// 说明跟着变 —— 而那句话恰恰是用户最需要看清楚的。走属性就能把通知发出来。</para>
    /// </summary>
    public bool IsRememberingPasswordList
    {
        get => Settings?.RememberPasswordList ?? true;
        set
        {
            if (Settings == null || Settings.RememberPasswordList == value)
            {
                return;
            }

            Settings.RememberPasswordList = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRememberedBooksDisabledHint));
        }
    }

    /// <summary>「记住密码列表」关着时要显示那句"这份清单这次不生效"。</summary>
    public bool ShowRememberedBooksDisabledHint => !IsRememberingPasswordList;

    /// <summary>"当前在用哪个引擎"的一句话（按能力 + 优先级算出来的，与真正执行时同一份逻辑）。</summary>
    public string EngineSelectionSummary => _enginePreview.EngineSelectionSummary;

    /// <summary>用户自选的 UnRAR.exe 路径；留空 = 自动（已装 WinRAR 目录 → 内置 tools\unrar）。</summary>
    public string CustomUnRarExePath
    {
        get => Settings.CustomUnRarExePath ?? string.Empty;
        set
        {
            string normalized = value ?? string.Empty;

            if (string.Equals(Settings.CustomUnRarExePath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            Settings.CustomUnRarExePath = normalized;
            OnPropertyChanged();
            _enginePreview.RefreshEngineList();
        }
    }

    /// <summary>
    /// 用户自选的 <c>Rar.exe</c> 路径（**打包**功能做外层 rar 时用；用户 2026-09-23 决定加这一格）。
    ///
    /// <para>留空 = 自动：本机已装 WinRAR 目录里的 <c>Rar.exe</c>，再退 <c>WinRAR.exe</c>。</para>
    ///
    /// <para>⚠ 它**只**填用户自己安装 / 下载的那一份：<c>Rar.exe</c> 是共享软件，RARLAB 的 EULA
    /// 禁止随其它软件包分发，所以程序绝不内置、绝不复制它。这一格存在的意义是"用户把 WinRAR
    /// 装在非默认目录"时不必去改系统环境变量。</para>
    /// </summary>
    public string CustomRarExePath
    {
        get => Settings.CustomRarExePath ?? string.Empty;
        set
        {
            string normalized = value ?? string.Empty;

            if (string.Equals(Settings.CustomRarExePath, normalized, StringComparison.Ordinal))
            {
                return;
            }

            Settings.CustomRarExePath = normalized;
            OnPropertyChanged();
            _enginePreview.RefreshRarStatus();
        }
    }

    /// <summary>
    /// 工具状态那一行："当前用的是自选的那一份，还是本机已装目录里的那一份"（不变量 14）。
    /// 预览口径在 <see cref="SettingsEnginePreview"/>：临时 ToolLocator，只预览、不产生副作用。
    /// </summary>
    public string RarToolStatusText => _enginePreview.RarToolStatusText;

    /// <summary>那一格的完整说明（**许可边界**写在这里；界面提示与校验失败共用同一份措辞）。</summary>
    public string RarExePathHint => StatusText.SettingsRarExePathHint;

    // ── 「推荐另外装一个 WinRAR（可选）」+ 官方下载页（用户 2026-10-05 拍板） ──

    /// <summary>
    /// 官方下载页地址（**唯一出口**）。
    ///
    /// <para>⛔ 只指路：不下载、不代装、不把安装包打进我们自己的 `setup.exe` / `dist` 里
    /// （RARLAB EULA §3b/§3c 明令禁止 bundling）。打开动作在 <see cref="SettingsDialogCommands"/>。</para>
    /// </summary>
    internal const string WinRarDownloadUrl = "https://www.rarlab.com/download.htm";

    /// <summary>那一格的标题（界面上的小标题，走 StatusText 唯一出口）。</summary>
    public string WinRarRecommendLabel => StatusText.SettingsWinRarRecommendLabel;

    /// <summary>那一格的说明全文（为什么值得装 + 许可边界 + 不装会怎样）。</summary>
    public string WinRarRecommendHint => StatusText.SettingsWinRarRecommendHint;

    /// <summary>按钮文案。</summary>
    public string WinRarDownloadButtonText => StatusText.SettingsWinRarDownloadButtonText;

    /// <summary>保留受损文件（-kb，默认关）。只影响半成品留不留，绝不影响成败判定。</summary>
    public bool KeepBrokenFiles
    {
        get => Settings.KeepBrokenFiles;
        set
        {
            if (Settings.KeepBrokenFiles == value)
            {
                return;
            }

            Settings.KeepBrokenFiles = value;
            OnPropertyChanged();
        }
    }

    public bool? DialogResult
    {
        get => _dialogResult;
        set => SetProperty(ref _dialogResult, value);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value ?? string.Empty);
    }

    /// <summary>
    /// ②页「指定位置」那一格刚**选**完一个目录（只在这一刻触发，改设置 / 恢复默认都不触发）。
    ///
    /// <para>接它的是主视图模型：换盘之后要按新盘重做一次空间体检（用户 2026-09-27 第 2 条）。
    /// 做成一个回调而不是在这里直接算，是因为"这批包多大、盘上还剩多少"这件事的判据
    /// 在主视图模型那一侧（任务列表 + 解压协调器），本类只负责**说一声**。</para>
    /// </summary>
    internal Action<string>? OutputDirectoryPicked { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ResetDefaultCommand { get; }
    public ICommand SelectOutputDirectoryCommand { get; }

    /// <summary>把某个引擎在优先级列表里上移一位。</summary>
    public ICommand MoveEngineUpCommand { get; }

    /// <summary>把某个引擎在优先级列表里下移一位。</summary>
    public ICommand MoveEngineDownCommand { get; }

    /// <summary>选择"结果归集"的目标目录（M3）。</summary>
    public ICommand SelectCollectTargetDirectoryCommand { get; }

    /// <summary>
    /// 浏览选择用户自己装的 <c>Rar.exe</c>（打包做外层 rar 时用）。
    /// 7z / UnRAR 那两格是纯文本框（历史如此），这一格按用户要求配一个"浏览"按钮。
    /// </summary>
    public ICommand SelectRarExeCommand { get; }

    /// <summary>
    /// 打开 WinRAR 官方下载页（用户 2026-10-05：「在设置里面写一个推荐一同安装 WinRAR」）。
    /// ⛔ 只开浏览器指路 —— 不下载、不静默安装、不捆绑（EULA §3b/§3c）。
    /// </summary>
    public ICommand OpenWinRarDownloadPageCommand { get; }

    /// <summary>把一本"已记住的密码本"从清单里移除（**只影响自动加载，磁盘上的文件一个字节都不动**）。</summary>
    public ICommand RemoveRememberedBookCommand { get; }

    /// <summary>把输入框里那个词加进「内容物保留关键词」（②页那一栏）。</summary>
    public ICommand AddContentKeepKeywordCommand { get; }

    /// <summary>从「内容物保留关键词」里移除一个词（参数 = 那个词本身）。</summary>
    public ICommand RemoveContentKeepKeywordCommand { get; }

    public SettingsViewModel()
        : this(new AppSettings(), new SettingsService())
    {
    }

    public SettingsViewModel(AppSettings settings, SettingsService settingsService)
        : this(settings, settingsService, null)
    {
    }

    /// <summary>
    /// 正式构造 + **可注入的对话框服务**（只给测试：无界面宿主里
    /// <see cref="Services.DialogService.ShowFolderBrowserDialog(string, string)"/> 一律返回空串，
    /// 测试就注入不了"用户挑好了哪个目录"——而"挑完目录之后该发生什么"正是要钉住的东西）。
    /// </summary>
    public SettingsViewModel(AppSettings settings, SettingsService settingsService, DialogService? dialogService)
    {
        _settingsService = settingsService ?? new SettingsService();
        _dialogService = dialogService ?? new DialogService();
        _settings = CloneSettings(settings ?? _settingsService.CreateDefault());

        _notifications = new SettingsGroupNotifications(OnPropertyChanged);
        _contentKeepKeywords = new SettingsContentKeepKeywordEditor(() => Settings, OnPropertyChanged);
        _rememberedBooks = new SettingsRememberedBookEditor(
            () => Settings,
            OnPropertyChanged,
            value => Message = value,
            RememberedBooks);
        _enginePreview = new SettingsEnginePreview(() => Settings, OnPropertyChanged, value => Message = value);
        _dialogCommands = new SettingsDialogCommands(
            _dialogService,
            () => Settings,
            () => OutputDirectoryPicked,
            OnPropertyChanged,
            RaiseOutputPlacementChanged,
            value => Message = value,
            value => CustomRarExePath = value);
        _saveCoordinator = new SettingsSaveCoordinator(
            _settingsService,
            () => Settings,
            value => Message = value,
            value => DialogResult = value,
            RefreshEnginePreviews);

        SaveCommand = new RelayCommand(_saveCoordinator.Save);
        CancelCommand = new RelayCommand(Cancel);
        ResetDefaultCommand = new RelayCommand(ResetDefault);
        SelectOutputDirectoryCommand = new RelayCommand(_dialogCommands.SelectOutputDirectory);
        SelectCollectTargetDirectoryCommand = new RelayCommand(_dialogCommands.SelectCollectTargetDirectory);
        SelectRarExeCommand = new RelayCommand(_dialogCommands.SelectRarExe);
        OpenWinRarDownloadPageCommand = new RelayCommand(_dialogCommands.OpenWinRarDownloadPage);
        RemoveRememberedBookCommand = new RelayCommand(_rememberedBooks.Remove);
        AddContentKeepKeywordCommand = new RelayCommand(_contentKeepKeywords.Add);
        RemoveContentKeepKeywordCommand = new RelayCommand(_contentKeepKeywords.Remove);
        MoveEngineUpCommand = new RelayCommand(parameter => _enginePreview.MoveEngine(parameter, -1));
        MoveEngineDownCommand = new RelayCommand(parameter => _enginePreview.MoveEngine(parameter, +1));

        RefreshEnginePreviews();
        _rememberedBooks.Refresh();
        _contentKeepKeywords.Refresh();

        Message = "设置已加载（改哪一项都会自动存，不用点保存）。";
    }

    /// <summary>
    /// 自动保存前的那道校验（用户 2026-09-26："设置要改了就自动存"）。
    ///
    /// <para>返回 <c>null</c> = 可以落盘；否则返回"为什么先不存"的一句话。
    /// 判据与保存命令完全同一套（<see cref="SettingsValueValidation.DescribeAutoSaveBlock"/>）——
    /// ⛔ 自动保存绝不能因为"它是自动的"就绕过"工具路径必须存在"这一条。</para>
    /// </summary>
    internal string? DescribeAutoSaveBlock() => SettingsValueValidation.DescribeAutoSaveBlock(Settings);

    /// <summary>
    /// 落盘前那一份"用户填的数字"（判"Normalize 有没有把它夹回去"就靠它）。
    ///
    /// <para>为什么要留快照：<see cref="AppSettings.Normalize"/> 会把超范围的值**就地**改成合法值，
    /// 归一化之后再想比就已经晚了 —— 两条保存路（设置页那条与自动保存那条）都必须**先**取快照。</para>
    ///
    /// <para>类型仍挂在主类上（<c>SettingsViewModel.SettingsClampSnapshot</c>）是公开 API：
    /// 自动保存协调器按这个名字取快照。</para>
    /// </summary>
    internal readonly record struct SettingsClampSnapshot(
        int PasswordAttempts,
        int SingleFileGiB,
        int TotalGiB,
        int FileCount,
        double Ratio)
    {
        public static SettingsClampSnapshot Capture(AppSettings settings)
        {
            settings ??= new AppSettings();

            return new SettingsClampSnapshot(
                settings.MaxPasswordAttemptsPerLayer,
                settings.MaxSingleExtractedFileGiB,
                settings.MaxExtractedTotalGiB,
                settings.MaxExtractedFileCount,
                settings.MaxExtractionRatio);
        }
    }

    /// <summary>
    /// 归一化之后"有没有哪个数字被夹回"的那句话（实现在
    /// <see cref="SettingsValueValidation.DescribeClampNotice"/>，两条保存路共用同一份措辞）。
    /// </summary>
    internal static string DescribeClampNotice(SettingsClampSnapshot requested, AppSettings settings)
        => SettingsValueValidation.DescribeClampNotice(requested, settings);

    private void Cancel()
    {
        Message = "已取消。";
        DialogResult = false;
    }

    private void ResetDefault()
    {
        Settings = _settingsService.CreateDefault();
        _rememberedBooks.Refresh();
        _contentKeepKeywords.Refresh();
        Message = "已恢复默认设置，点击保存后生效。";
    }

    /// <summary>
    /// 把「已记住的密码本」那一组刷成设置里的真实内容（不猜、不缓存）。
    /// 恢复默认设置、以及移除一项之后都要重来一遍，否则界面上会留着已经移除的项。
    /// </summary>
    internal void NotifyRememberedBooksChanged() => _rememberedBooks.Refresh();

    /// <summary>
    /// 两个布尔 ↔ 两档的**唯一映射处**（用户 2026-09-24 第 13 条之后只剩两档）。
    ///
    /// <para>唯一的判断题是 <c>ExtractToOriginalDirectory</c>（是不是"指定了位置"）。
    /// <c>KeepArchiveNameFolder</c> 已经**不参与判断**（两档都建同名子文件夹）——
    /// 旧的"摊平"组合由设置层的 <see cref="AppSettings.Normalize"/> 迁移掉，界面上再也选不出来。</para>
    /// </summary>
    public static OutputPlacementOption ResolveOutputPlacement(
        bool extractToOriginalDirectory,
        bool keepArchiveNameFolder)
    {
        _ = keepArchiveNameFolder;

        return extractToOriginalDirectory
            ? OutputPlacementOption.ArchiveNamedSubfolder
            : OutputPlacementOption.CustomNamedSubfolder;
    }

    private static void ApplyOutputPlacement(AppSettings settings, OutputPlacementOption option)
    {
        // 落盘仍是那两个布尔（旧版本也读得懂），但第二个**永远是 true**：没有"不建子文件夹"的档了。
        settings.ExtractToOriginalDirectory = option != OutputPlacementOption.CustomNamedSubfolder;
        settings.KeepArchiveNameFolder = true;
    }

    /// <summary>
    /// 让外面（①「任务」页那一格 / 一键处理的弹窗收尾）也能把**设置在界面上的那些派生显示**
    /// 拉回同一份真值（用户 2026-09-25 第 27 条；第 34 条补全）。
    ///
    /// <para>为什么必须有这个公开入口：①页那个「未指定位置」开关直接写的是
    /// <c>Settings.ExtractToOriginalDirectory</c>（两档落点的唯一判据），
    /// 而②页的单选按钮 / 那把「选择」按钮绑的是本类**算出来**的
    /// <see cref="OutputPlacement"/> / <see cref="IsCustomOutputEnabled"/> / <see cref="OutputPlacementSummary"/> ——
    /// AppSettings 是普通 POCO、不发通知，不喊这一声②页就会继续显示老档位。</para>
    ///
    /// <para>它就是 <c>RaiseOutputPlacementChanged</c>，语义一致、不另写一套。</para>
    /// </summary>
    public void NotifyOutputPlacementChanged() => RaiseOutputPlacementChanged();

    /// <summary>
    /// 把"源包操作 / 删除操作"这两个档位在界面上的显示拉回同一份真值（2026-09-25 第 34 条）。
    ///
    /// <para>真机现场：关掉一键处理的弹窗回到③页，「删除操作」那一组三个单选**一个黑点都没有**，
    /// 而切一次选项卡之后「源包操作」的黑点回来了、「删除操作」的还是不在 ——
    /// 差别就是那份通知清单里少了 <see cref="RestHandling"/>。清单在
    /// <see cref="SettingsGroupNotifications"/>（唯一出口），这里只是公开入口。</para>
    /// </summary>
    public void NotifyProcessingOptionsChanged() => _notifications.NotifyProcessingOptions();

    private void RaiseOutputPlacementChanged()
    {
        _notifications.NotifyOutputPlacement();

        // ⚠ 源包操作 / 删除操作也在这张清单里（第 34 条：漏掉 RestHandling 那一档时，
        // ③页「删除操作」的黑点一旦被弹窗取消就再也回不来了）。
        NotifyProcessingOptionsChanged();

        RefreshEnginePreviews();
    }

    /// <summary>
    /// 重算引擎预览（列表 + rar 工具状态）。两个协作类共用这一个刷新入口：
    /// 构造函数、恢复默认 / 重新装载设置、保存、以及改工具路径之后都走它。
    /// </summary>
    private void RefreshEnginePreviews()
    {
        _enginePreview.RefreshEngineList();
        _enginePreview.RefreshRarStatus();
    }

    private static AppSettings CloneSettings(AppSettings source)
    {
        string json = JsonSerializer.Serialize(source);
        AppSettings? cloned = JsonSerializer.Deserialize<AppSettings>(json);

        cloned ??= new AppSettings();
        cloned.Normalize();

        return cloned;
    }
}
