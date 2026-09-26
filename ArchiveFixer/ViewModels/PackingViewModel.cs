using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
    /// <summary>窗口内提示条的类别（与密码列表窗口同一套写法，样式在 App.xaml 里）。</summary>
    public enum PackingNoticeKind
    {
        None = 0,
        Info = 1,
        Success = 2,
        Warning = 3,
        Error = 4
    }

    /// <summary>结果区分卷表格的一行。</summary>
    public sealed class PackingVolumeRow
    {
        public string FileName { get; init; } = string.Empty;

        public string SizeText { get; init; } = string.Empty;
    }

    /// <summary>
    /// 「打包文件夹为加密分卷」窗口的 ViewModel（用户 2026-09-22 需求第 10 条）。
    ///
    /// <para><b>一屏、没有向导</b>（docs/打包功能.md §7）：源文件夹 A → 输出文件夹 B →
    /// 分卷大小 → 密码（+ 可选的外层独立密码）→ 摘要行 → 开始 → 进度 + 当前步骤 → 结果区。</para>
    ///
    /// <para><b>没有 Rar.exe 时开始按钮不禁用</b>：外层容器是**三选一**（rar / 7z / 不做外层），
    /// 让用户看得见发生了什么、并且有一条**不需要额外安装**的路（外层 7z），
    /// 而不是一个灰着不解释的按钮、也不是程序替他换容器。</para>
    ///
    /// <para><b>密码只存内存</b>：不写设置、不进日志（日志只写"已设置密码"）。界面上的
    /// 「复制密码」是给用户自己用的出口。</para>
    /// </summary>
    public class PackingViewModel : ViewModelBase
    {
        private readonly PackingService _service;
        private readonly DialogService _dialogService;
        private readonly PathService _pathService;
        private readonly ClipboardService _clipboardService;
        private readonly ToolLocator _tools;

        private string _sourceFolder = string.Empty;

        /// <summary>⛔ 第 46 条起只作历史字段：落点在弹窗里选，卷数自动算。</summary>
        private string _outputFolder = string.Empty;

        private string _volumeSizeMiB = "自动";
        private string _password = string.Empty;
        private string _outerPassword = string.Empty;

        /// <summary>两层用同一个密码（默认 true —— 用户 2026-09-26：相同就只输一次）。</summary>
        private bool _passwordsAreSame = true;

        private PackOuterContainer _outerContainer = PackOuterContainer.Rar;

        /// <summary>本次运行的那一套选择（落点 / 原包 / 其余物），由小确认弹窗回填。</summary>
        private PackingRunOptions _runOptions = new();

        /// <summary>最近一次算出来的规划（摘要 / 确认弹窗预览都用它）。</summary>
        private PackingPlan? _lastPlan;

        /// <summary>设置对象（打包那几档要记住；⛔ 与解压侧各存各的）。</summary>
        private AppSettings _settings = new();

        /// <summary>密码列表（"从密码列表里选"那个按钮从这里取；由主界面注入）。</summary>
        private Func<IReadOnlyList<string>>? _passwordListProvider;

        /// <summary>从密码列表挑一条的弹窗（默认走 <see cref="DialogService"/>；测试可注入）。</summary>
        private Func<string, IReadOnlyList<string>, string?>? _passwordPicker;

        private string _summaryText = string.Empty;
        private string _placementText = string.Empty;
        private string _placementSummaryText = string.Empty;
        private bool _placementIsCustom;
        private string _customPlacementDirectory = string.Empty;

        private bool _isRunning;
        private int _progressPercent;
        private string _currentStepText = string.Empty;
        private string _progressDetailText = string.Empty;

        private bool _hasResult;
        private string _resultTitle = string.Empty;
        private string _resultText = string.Empty;
        private string _resultKind = string.Empty;
        private string _resultOuterPath = string.Empty;

        private PackingNoticeKind _noticeKind = PackingNoticeKind.None;
        private string _noticeText = string.Empty;

        private CancellationTokenSource? _cancellation;

        private string _lastUsedPassword = string.Empty;

        public PackingViewModel()
            : this(new PackingService(), new DialogService(), new PathService(), new ClipboardService())
        {
        }

        public PackingViewModel(
            PackingService service,
            DialogService dialogService,
            PathService? pathService = null,
            ClipboardService? clipboardService = null,
            ToolLocator? tools = null)
        {
            _service = service ?? new PackingService();
            _dialogService = dialogService ?? new DialogService();
            _pathService = pathService ?? new PathService();
            _clipboardService = clipboardService ?? new ClipboardService();
            _tools = tools ?? _service.Tools;

            BrowseFolderCommand = new RelayCommand(BrowseSourceFolder);
            BrowseFileCommand = new RelayCommand(BrowseSourceFile);
            PickPlacementDirectoryCommand = new RelayCommand(PickPlacementDirectory);
            RefreshSummaryCommand = new RelayCommand(RefreshSummary);
            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            CopyPasswordCommand = new RelayCommand(CopyPassword);
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);

            LoadPlacementFromSettings();
            RefreshSummary();
        }

        /// <summary>每一步的日志出口（由主界面接上，落到主日志文件里）。</summary>
        public Action<string>? LogSink { get; set; }

        /// <summary>
        /// **真正开始干活**之前的那个钩子（主界面用它写一条"本次操作开始：打包"）。
        ///
        /// <para>时机是硬要求：**用户确认之后、调服务之前** —— 取消时一次都不许触发
        /// （"导出日志（本次操作）"就是从这一行开始导的，第 38 条那套口径）。</para>
        /// </summary>
        public Action? OperationStarted { get; set; }

        /// <summary>设置变了要不要落盘（由主界面接上；打包那几档要记住）。</summary>
        public Action? SettingsChanged { get; set; }

        /// <summary>
        /// 设置对象（打包的落点 / 原包操作 / 其余物操作都存在它上面）。
        /// ⛔ 与解压侧那两档**各存各的**（用户 2026-09-26 原话："两者绝对不能同步"）。
        /// </summary>
        public AppSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value ?? new AppSettings();

                // 设置变了（①页「输出位置」也是一份共享真值）→ 页面那两档与那行「落点」都要跟着重读。
                LoadPlacementFromSettings();
                RefreshPlacementSummary();
            }
        }

        /// <summary>密码列表的来源（④页那份；由主界面注入）。</summary>
        public Func<IReadOnlyList<string>>? PasswordListProvider
        {
            get => _passwordListProvider;
            set => _passwordListProvider = value;
        }

        /// <summary>
        /// 「从密码列表里选」：返回选中的那条（没选 / 列表为空时返回 null）。
        /// 默认实现走 <see cref="DialogService.ShowPasswordPicker"/>；测试可注入。
        /// </summary>
        public string? PickPasswordFromList(string title)
        {
            IReadOnlyList<string> candidates = _passwordListProvider?.Invoke() ?? Array.Empty<string>();

            if (candidates.Count == 0)
            {
                Warn("密码列表里还没有条目（可以在④「密码」页添加，或先导入密码本）。");
                return null;
            }

            string? picked = _passwordPicker != null
                ? _passwordPicker(title, candidates)
                : _dialogService.ShowPasswordPicker(title, candidates);

            if (string.IsNullOrEmpty(picked))
            {
                Info("没有选密码（框里保持原样）。");
                return null;
            }

            return picked;
        }

        /// <summary>测试注入用：替换"从密码列表挑一条"的弹窗。</summary>
        internal void UsePasswordPicker(Func<string, IReadOnlyList<string>, string?> picker) =>
            _passwordPicker = picker;

        // ────────────────────────── 输入 ──────────────────────────

        /// <summary>
        /// 源：用户选的路径（**文件夹或单个文件**，用户 2026-09-26 第 46 条）。
        /// 单文件时程序会在它旁边建同名文件夹再打包（界面上说清这件事）。
        /// </summary>
        public string SourceFolder
        {
            get => _sourceFolder;
            set
            {
                if (SetProperty(ref _sourceFolder, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SourceKindText));
                    OnPropertyChanged(nameof(IsSingleFileSource));

                    RefreshPlacementSummary();
                    RefreshSummary();
                }
            }
        }

        /// <summary>选中的是单文件（界面上要提示"会先建同名文件夹"）。</summary>
        public bool IsSingleFileSource
        {
            get
            {
                try
                {
                    return !string.IsNullOrWhiteSpace(_sourceFolder) && File.Exists(_sourceFolder);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>源那一行的人读文本（文件夹 / 单文件）。</summary>
        public string SourceKindText => IsSingleFileSource
            ? "单文件：会在它旁边建同名文件夹、把文件放进去（不动源文件），再按文件夹打包"
            : "文件夹：直接打包里面的内容";

        /// <summary>
        /// 输出文件夹 B —— ⛔ **2026-09-26 第 46 条起界面不再让用户填**：
        /// 落点在**小确认弹窗**里选（本地 / 指定位置），名字按源名自动算。
        /// 这里保留属性只为旧绑定 / 旧用例不炸。
        /// </summary>
        public string OutputFolder
        {
            get => _outputFolder;
            set
            {
                if (SetProperty(ref _outputFolder, value ?? string.Empty))
                {
                    RefreshSummary();
                }
            }
        }

        /// <summary>
        /// 分卷大小（MiB 文本）—— ⛔ **2026-09-26 第 46 条起界面上没有这一格了**：
        /// 分卷按卷数自动算（&lt;1 GiB → 2 卷、≥1 GiB → 3 卷），这里只为旧绑定留一个字段。
        /// </summary>
        public string VolumeSizeMiB
        {
            get => _volumeSizeMiB;
            set => SetProperty(ref _volumeSizeMiB, value ?? string.Empty);
        }

        /// <summary>两层用同一个密码（**默认**；用户 2026-09-26：相同就只输一次）。</summary>
        public bool PasswordsAreSame
        {
            get => _passwordsAreSame;
            set
            {
                if (SetProperty(ref _passwordsAreSame, value))
                {
                    OnPropertyChanged(nameof(UseSeparateOuterPassword));
                    OnPropertyChanged(nameof(OuterPasswordEnabled));

                    if (value)
                    {
                        // 切回"相同"时把外层那个框清掉，免得留着一个看不见的值。
                        OuterPassword = string.Empty;
                    }
                }
            }
        }

        /// <summary>内层 7z 分卷的密码（"相同"档时两层都用它）。</summary>
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value ?? string.Empty);
        }

        /// <summary>最外层容器的密码（选了"不同"才用）。</summary>
        public string OuterPassword
        {
            get => _outerPassword;
            set => SetProperty(ref _outerPassword, value ?? string.Empty);
        }

        /// <summary>外层用另一个密码（= 上面那个开关取反；旧绑定还认得它）。</summary>
        public bool UseSeparateOuterPassword
        {
            get => !PasswordsAreSame;
            set => PasswordsAreSame = !value;
        }

        /// <summary>外层密码输入框可用（选了"不同"才有意义）。</summary>
        public bool OuterPasswordEnabled => !PasswordsAreSame;

        /// <summary>
        /// 外层容器（用户 2026-09-23 决定：三选一）。**默认 rar**（用户 2026-09-22 的原始要求）。
        ///
        /// <para>界面用三个单选按钮绑下面的 bool 属性；这里是真正的值，建请求时只读它。</para>
        /// </summary>
        public PackOuterContainer OuterContainer
        {
            get => _outerContainer;
            private set
            {
                if (SetProperty(ref _outerContainer, value))
                {
                    OnPropertyChanged(nameof(OuterContainerIsRar));
                    OnPropertyChanged(nameof(OuterContainerIsSevenZip));
                    OnPropertyChanged(nameof(OuterContainerIsNone));

                    RefreshSummary();
                }
            }
        }

        /// <summary>外层容器 = rar（默认；需要本机已装的 WinRAR）。</summary>
        public bool OuterContainerIsRar
        {
            get => OuterContainer == PackOuterContainer.Rar;
            set
            {
                if (value)
                {
                    OuterContainer = PackOuterContainer.Rar;
                }
            }
        }

        /// <summary>外层容器 = 7z（**无需额外安装**：7-Zip 是 LGPL，随程序分发）。</summary>
        public bool OuterContainerIsSevenZip
        {
            get => OuterContainer == PackOuterContainer.SevenZip;
            set
            {
                if (value)
                {
                    OuterContainer = PackOuterContainer.SevenZip;
                }
            }
        }

        /// <summary>不做外层容器（只出 B 里的 7z 分卷）。</summary>
        public bool OuterContainerIsNone
        {
            get => OuterContainer == PackOuterContainer.None;
            set
            {
                if (value)
                {
                    OuterContainer = PackOuterContainer.None;
                }
            }
        }

        /// <summary>外层容器 rar 那一项现在的说法（含"需要本机已装的 WinRAR"与许可边界）。</summary>
        public string OuterRarOptionText => PackOuterContainer.Rar.Describe();

        /// <summary>外层容器 7z 那一项现在的说法（含"无需额外安装（7-Zip 是 LGPL，随程序分发）"）。</summary>
        public string OuterSevenZipOptionText => PackOuterContainer.SevenZip.Describe();

        /// <summary>不做外层那一项现在的说法。</summary>
        public string OuterNoneOptionText => PackOuterContainer.None.Describe();

        // ────────────────────────── 摘要与外部工具状态 ──────────────────────────

        /// <summary>摘要行：内容大小 / 文件数 / 预计分卷数 / 需要空间。</summary>
        public string SummaryText
        {
            get => _summaryText;
            private set => SetProperty(ref _summaryText, value ?? string.Empty);
        }

        /// <summary>
        /// ⑤页上那行「落点：…」—— **最终压缩包会放在哪**（用户 2026-09-26 真机原话：
        /// "怎么没有文件指定的压缩位置……**选择还是得放在页面**"）。
        ///
        /// <para>值与真正用的一样（同一个 <see cref="PackingPaths.ResolveTargetDirectory"/>），
        /// 只报事实：不建目录、不改设置。</para>
        /// </summary>
        public string PlacementSummaryText
        {
            get => _placementSummaryText;
            private set => SetProperty(ref _placementSummaryText, value ?? string.Empty);
        }

        /// <summary>落点 = **默认**（跟①页「输出位置」）。</summary>
        public bool PlacementIsFollowDefault
        {
            get => !PlacementIsCustom;
            set
            {
                if (value && PlacementIsCustom)
                {
                    PlacementIsCustom = false;
                }
            }
        }

        /// <summary>落点 = 指定位置（在⑤页挑一个目录）。</summary>
        public bool PlacementIsCustom
        {
            get => _placementIsCustom;
            set
            {
                if (SetProperty(ref _placementIsCustom, value))
                {
                    OnPropertyChanged(nameof(PlacementIsFollowDefault));
                    OnPropertyChanged(nameof(PlacementIsEditable));

                    PersistPlacement();
                    RefreshPlacementSummary();
                }
            }
        }

        /// <summary>指定位置那个目录（只在 <see cref="PlacementIsCustom"/> 时有意义）。</summary>
        public string CustomPlacementDirectory
        {
            get => _customPlacementDirectory;
            set
            {
                if (SetProperty(ref _customPlacementDirectory, value ?? string.Empty))
                {
                    PersistPlacement();
                    RefreshPlacementSummary();
                }
            }
        }

        /// <summary>指定位置那一格现在能不能编辑。</summary>
        public bool PlacementIsEditable => PlacementIsCustom;

        /// <summary>「选择…」：挑一个目录（⛔ 只改设置，**不建目录** —— 与①页同一口径）。</summary>
        public void PickPlacementDirectory()
        {
            string? picked = _dialogService.ShowFolderBrowserDialog(
                "选择最终压缩包放在哪",
                string.IsNullOrWhiteSpace(CustomPlacementDirectory) ? SourceFolder : CustomPlacementDirectory);

            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            // 挑了目录 = 用户要"指定位置"，顺手把那一档切过去（他不会希望挑了却不生效）。
            _placementIsCustom = true;
            OnPropertyChanged(nameof(PlacementIsCustom));
            OnPropertyChanged(nameof(PlacementIsFollowDefault));
            OnPropertyChanged(nameof(PlacementIsEditable));

            CustomPlacementDirectory = picked;
        }

        /// <summary>把页面上的两档写回设置（"相应的保存记忆操作"）。</summary>
        private void PersistPlacement()
        {
            if (_settings == null)
            {
                return;
            }

            _settings.PackTargetMode = (PlacementIsCustom
                ? PackingTargetMode.Custom
                : PackingTargetMode.FollowOutputDirectory).ToString();

            _settings.PackCustomOutputDirectory = CustomPlacementDirectory ?? string.Empty;

            SettingsChanged?.Invoke();
        }

        /// <summary>把设置里那一套读进页面（打开⑤页 / 换设置对象时）。</summary>
        private void LoadPlacementFromSettings()
        {
            string saved = _settings?.PackTargetMode ?? string.Empty;

            _placementIsCustom = PackingRunOptions.ParseTargetMode(saved) == PackingTargetMode.Custom;
            _customPlacementDirectory = _settings?.PackCustomOutputDirectory ?? string.Empty;

            OnPropertyChanged(nameof(PlacementIsCustom));
            OnPropertyChanged(nameof(PlacementIsFollowDefault));
            OnPropertyChanged(nameof(PlacementIsEditable));
        }

        /// <summary>
        /// 页面上这几档 → 一套请求选项（⛔ 唯一出口：预览、弹窗抬头、真跑都引它）。
        ///
        /// <para>落点取**页面**上那两档；"默认"那一档要落到的目录由
        /// <see cref="PackingRunOptions.ResolveDefaultOutputDirectory"/> 从①页「输出位置」解析
        /// （用户原话："如果用户默认不去选择位置就将压缩至选择的目录位置"）。</para>
        /// </summary>
        internal PackingRunOptions BuildPlacementOptions()
        {
            PackingRunOptions saved = PackingRunOptions.FromSettings(_settings);

            return new PackingRunOptions
            {
                TargetMode = PlacementIsCustom ? PackingTargetMode.Custom : PackingTargetMode.FollowOutputDirectory,
                CustomOutputDirectory = CustomPlacementDirectory ?? string.Empty,
                DefaultOutputDirectory = PackingRunOptions.ResolveDefaultOutputDirectory(_settings),
                SourceHandling = saved.SourceHandling,
                RestHandling = saved.RestHandling
            };
        }

        /// <summary>重算那行「落点」（很便宜：只解析路径，不扫源目录）。</summary>
        private void RefreshPlacementSummary()
        {
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                PlacementSummaryText = "落点：还没选源 —— 先选一个文件夹（或一个文件）。";
                return;
            }

            PackingSourceResolution resolution = PackingSourceResolver.Resolve(SourceFolder);

            if (!resolution.Success)
            {
                PlacementSummaryText = "落点：" + resolution.Reason;
                return;
            }

            PackingRunOptions options = BuildPlacementOptions();
            string directory = PackingPaths.ResolveTargetDirectory(resolution, options);

            PlacementSummaryText = options.TargetMode switch
            {
                PackingTargetMode.Custom => $"落点：指定位置 —— {directory}",
                PackingTargetMode.Local => $"落点：本地（源旁边）—— {directory}",
                _ => $"落点：默认（跟①页「输出位置」）—— {directory}"
            };
        }

        /// <summary>落点：B 与结果 rar 会落在哪儿。</summary>
        public string PlacementText
        {
            get => _placementText;
            private set => SetProperty(ref _placementText, value ?? string.Empty);
        }

        /// <summary>这台机器上能不能做外层 rar。</summary>
        public bool RarAvailable
        {
            get
            {
                try
                {
                    return _tools.RarExists;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>内置 7-Zip 在不在（自动回退到 7z 外层时要用）。</summary>
        public bool SevenZipAvailable
        {
            get
            {
                try
                {
                    return _tools.SevenZipExists;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 外层容器**实际会用哪一个**（第 46 条：没装 WinRAR 就自动改用 7z）。
        ///
        /// <para>⛔ 这一处是**唯一来源**（与 <c>PackingService</c> 引的是同一个
        /// <see cref="PackingOuterContainerResolver"/>）：⑤页摘要、确认弹窗里那句"最终产物"、
        /// 结果区都必须写**实际**会产出的那个文件名 —— 否则没装 WinRAR 的机器上会写着
        /// <c>1111.rar</c>、跑完却得到 <c>1111.7z</c>（界面撒谎）。</para>
        /// </summary>
        public PackingOuterContainerResolution EffectiveOuterContainer =>
            PackingOuterContainerResolver.Resolve(OuterContainer, RarAvailable, SevenZipAvailable);

        /// <summary>实际会做的容器。</summary>
        public PackOuterContainer EffectiveContainer => EffectiveOuterContainer.Effective;

        /// <summary>Rar.exe 的现状那句话（"用的哪一个、从哪来的" / "没找到，期望位置在哪"）。</summary>
        public string RarStatusText
        {
            get
            {
                try
                {
                    return _tools.DescribeRarResolution();
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// 没有 Rar.exe（界面据此把那一格画成警告色）。
        ///
        /// <para>⛔ 第 46 条：原来这里还有 <c>RarMissingText</c> / <c>RarMissingWaysText</c>
        /// （"需要本机已安装 WinRAR" + 三条出路）—— 两条**都已删除**：现在没装 WinRAR 时打包
        /// **自动改用 7z 外层**，界面上那一格直接显示 <see cref="RarStatusText"/>（唯一来源 =
        /// <c>ToolLocator.DescribeNoRarAvailable()</c>），不再有"让用户自己去换容器"这回事。</para>
        /// </summary>
        public bool HasNoRar => !RarAvailable;

        // ────────────────────────── 运行状态 ──────────────────────────

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(IsNotRunning));
                    RaiseCommandStates();
                }
            }
        }

        public bool IsNotRunning => !IsRunning;

        /// <summary>进度条的值（0–100）。</summary>
        public int ProgressPercent
        {
            get => _progressPercent;
            private set => SetProperty(ref _progressPercent, value < 0 ? 0 : value > 100 ? 100 : value);
        }

        /// <summary>当前步骤（<see cref="StatusText"/> 里的常量）。</summary>
        public string CurrentStepText
        {
            get => _currentStepText;
            private set => SetProperty(ref _currentStepText, value ?? string.Empty);
        }

        /// <summary>当前步骤的明细（引擎正在处理什么；拿不到就是空的）。</summary>
        public string ProgressDetailText
        {
            get => _progressDetailText;
            private set => SetProperty(ref _progressDetailText, value ?? string.Empty);
        }

        // ────────────────────────── 结果 ──────────────────────────

        public bool HasResult
        {
            get => _hasResult;
            private set => SetProperty(ref _hasResult, value);
        }

        public string ResultTitle
        {
            get => _resultTitle;
            private set => SetProperty(ref _resultTitle, value ?? string.Empty);
        }

        public string ResultText
        {
            get => _resultText;
            private set => SetProperty(ref _resultText, value ?? string.Empty);
        }

        /// <summary>结果类别：Success / Warning / Error（决定结果区配色）。</summary>
        public string ResultKind
        {
            get => _resultKind;
            private set => SetProperty(ref _resultKind, value ?? string.Empty);
        }

        /// <summary>外层产物的路径（"不做外层" / 失败时为空的）。</summary>
        public string ResultOuterPath
        {
            get => _resultOuterPath;
            private set
            {
                if (SetProperty(ref _resultOuterPath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasOuterResult));
                }
            }
        }

        /// <summary>有外层产物（结果区显示出路径与「打开输出目录」）。</summary>
        public bool HasOuterResult => !string.IsNullOrWhiteSpace(ResultOuterPath);

        /// <summary>分卷清单（名字 + 各自大小）。</summary>
        public ObservableCollection<PackingVolumeRow> Volumes { get; } = new();

        /// <summary>本次打包用过的密码还在内存里（「复制密码」按钮据此可用）。</summary>
        public bool CanCopyPassword => PackingPasswordPolicy.IsUsable(_lastUsedPassword);

        /// <summary>B 会保留的那句提醒。</summary>
        public string KeepFolderHint => StatusText.PackKeepFolderHint;

        // ────────────────────────── 提示条 ──────────────────────────

        public PackingNoticeKind NoticeKind
        {
            get => _noticeKind;
            private set
            {
                if (SetProperty(ref _noticeKind, value))
                {
                    OnPropertyChanged(nameof(HasNotice));
                    OnPropertyChanged(nameof(NoticeGlyph));
                }
            }
        }

        public string NoticeGlyph => NoticeKind switch
        {
            PackingNoticeKind.Success => "✓",
            PackingNoticeKind.Warning => "!",
            PackingNoticeKind.Error => "×",
            PackingNoticeKind.Info => "i",
            _ => string.Empty
        };

        public string NoticeText
        {
            get => _noticeText;
            private set
            {
                if (SetProperty(ref _noticeText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasNotice));
                }
            }
        }

        public bool HasNotice => NoticeKind != PackingNoticeKind.None && !string.IsNullOrWhiteSpace(NoticeText);

        // ────────────────────────── 命令 ──────────────────────────

        /// <summary>选**文件夹**（主用法）。</summary>
        public ICommand BrowseFolderCommand { get; }

        /// <summary>选**单个文件**（第 46 条：会在它旁边建同名文件夹再打包）。</summary>
        public ICommand BrowseFileCommand { get; }

        /// <summary>「选择…」：挑最终压缩包放在哪个目录（**落点的选择在页面上**，用户 2026-09-26 追加）。</summary>
        public ICommand PickPlacementDirectoryCommand { get; }

        public ICommand RefreshSummaryCommand { get; }

        public ICommand StartCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand CopyPasswordCommand { get; }

        public ICommand OpenOutputFolderCommand { get; }

        // ────────────────────────── 动作 ──────────────────────────

        /// <summary>选源文件夹（它里面的内容会被打包）。</summary>
        public void BrowseSourceFolder()
        {
            string picked = _dialogService.ShowFolderBrowserDialog();

            if (!string.IsNullOrWhiteSpace(picked))
            {
                SourceFolder = picked;
            }
        }

        /// <summary>选源**文件**（程序会在它旁边建同名文件夹把它装进去再打包；源文件不动）。</summary>
        public void BrowseSourceFile()
        {
            string picked = _dialogService.ShowOpenSingleFileDialog("选择要打包的文件", "所有文件 (*.*)|*.*");

            if (!string.IsNullOrWhiteSpace(picked))
            {
                SourceFolder = picked;
            }
        }

        /// <summary>
        /// 重算摘要（扫一遍源的文件数与总大小）。
        /// ⚠ 大文件夹上这一次扫描是要花时间的，所以它绑在"选完源 / 点重算"上，**不是**每敲一个字符就跑。
        /// </summary>
        public void RefreshSummary()
        {
            var request = BuildRequest(new PackingRunOptions());

            if (!PackingPlan.TryCreate(request, out PackingPlan? plan, out string error) || plan == null)
            {
                SummaryText = string.IsNullOrWhiteSpace(SourceFolder) ? "先选一个文件夹（或一个文件）。" : error;
                PlacementText = string.Empty;
                _lastPlan = null;
                return;
            }

            _lastPlan = plan;

            PackingOuterContainerResolution outer = EffectiveOuterContainer;

            // ⛔ 要外层却连内置 7-Zip 都没有（程序装坏了那种）：**不许写成"不做外层、结果就是分卷"** ——
            //    服务那边会直接报"没法生成任何外层容器"，界面先说清楚，别让他确认一件做不成的事。
            if (outer.Impossible)
            {
                SummaryText =
                    $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                    + Environment.NewLine
                    + "外层容器：**做不出来** —— 本机既没有 Rar.exe，也没有可用的 7-Zip。";
                PlacementText = $"请先修好 7-Zip（程序目录 tools\\7zip：{_tools.SevenZipExePath}），否则打不了包。";

                return;
            }

            string outerNote = outer.FellBackToSevenZip
                ? "（本机没有 Rar.exe，自动改用 7z —— 无需额外安装）"
                : string.Empty;

            SummaryText =
                $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                + (plan.UnreadableCount > 0 ? $"（其中 {plan.UnreadableCount} 个条目读不到大小）" : string.Empty)
                + Environment.NewLine
                + plan.DescribeVolumePlan()
                + Environment.NewLine
                + $"外层容器：{outer.Effective.ShortName()}{outerNote}"
                + $"；需要空余空间：约 {TaskSpaceEstimate.FormatSize(plan.RequiredSpaceBytes)}";

            PlacementText = outer.Effective.HasOuterArtifact()
                ? $"最终产物：{OuterPathFor(plan, outer.Effective)}"
                : $"不做外层容器：结果就是 {plan.OutputFolder} 里的分卷";
        }

        /// <summary>
        /// 按**实际生效的容器**取最终产物的完整路径（`.rar` 还是 `.7z` 由它决定）。
        /// ⛔ 不许各处自己按 <c>plan.OuterContainer</c>（= 请求里那个）拼路径。
        /// </summary>
        internal static string OuterPathFor(PackingPlan plan, PackOuterContainer effectiveContainer) =>
            effectiveContainer switch
            {
                PackOuterContainer.SevenZip => plan.SevenZipOuterPath,
                PackOuterContainer.None => string.Empty,
                _ => plan.RarPath
            };

        /// <summary>
        /// 开始打包（用户 2026-09-26 第 46 条的流程）：
        /// **先弹小确认弹窗**（落点 / 原包操作 / 其余物操作）→ 用户确认 → 才动任何字节。
        ///
        /// <para>⛔ 顺序是硬要求：没有确认之前**一个字节都不许动**（连输出文件夹都不建）；
        /// 用户取消 = 什么都没发生（设置也不写回，因为他没确认）。</para>
        /// </summary>
        public async Task StartAsync()
        {
            if (IsRunning)
            {
                return;
            }

            ClearNotice();
            ClearResult();

            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                Warn("请先选择要打包的文件夹（或一个文件）。");
                return;
            }

            string passwordError = PackingPasswordPolicy.IsUsable(Password)
                ? string.Empty
                : PackingPasswordPolicy.RequiredMessage;

            if (passwordError.Length > 0)
            {
                Warn(passwordError);
                return;
            }

            if (!PasswordsAreSame && !PackingPasswordPolicy.IsUsable(OuterPassword))
            {
                Warn(PackingPasswordPolicy.OuterRequiredMessage);
                return;
            }

            /*
             * 要外层、却连内置 7-Zip 都拿不到（程序装坏了那种）→ **先拦下**，
             * 别让他确认一件注定失败的事（服务那边也会报同一件事，这里只是不浪费他一次点击）。
             */
            if (EffectiveOuterContainer.Impossible)
            {
                Warn(
                    "本机既没有 Rar.exe，也没有可用的 7-Zip —— 打不了包。"
                    + $"请检查程序目录里的 tools\\7zip（{_tools.SevenZipExePath}），或重新解压一份程序。");

                return;
            }

            /*
             * 先用**页面上那一套**（落点由⑤页选、两档操作由设置记得的那一套）算一遍规划，
             * 弹窗要把"最终产物在哪"显示出来。
             *
             * ⚠ 2026-09-26 追加改口径（用户原话："**选择还是得放在页面**"）：落点的**选择**在⑤页，
             * 弹窗里那块只**显示**（不再有本地/指定位置的单选）。
             */
            PackingRunOptions initial = BuildPlacementOptions();

            if (!PackingPlan.TryCreate(BuildRequest(initial), out PackingPlan? previewPlan, out string planError)
                || previewPlan == null)
            {
                Warn(planError);
                return;
            }

            // ───────── 小确认弹窗（取消 = 什么都不做）─────────
            //
            // ⚠ 弹窗里那句"最终产物"必须按**实际生效的容器**写（没装 WinRAR 时是 `.7z`）。
            PackingRunOptions? confirmed = _dialogService.ShowPackingConfirm(
                PackingConfirmRequest.FromPlan(previewPlan, initial, EffectiveContainer));

            if (confirmed == null)
            {
                Info("已取消：什么都没做（源与其余物一个字节都没动）。");
                return;
            }

            /*
             * 只有弹窗里那**两档操作**来自弹窗；落点始终是⑤页上那一套（⛔ 弹窗不许偷偷改落点）。
             * 合并之后写回设置（"相应的保存记忆操作"）。
             */
            PackingRunOptions merged = new()
            {
                TargetMode = initial.TargetMode,
                CustomOutputDirectory = initial.CustomOutputDirectory,
                DefaultOutputDirectory = initial.DefaultOutputDirectory,
                SourceHandling = confirmed.SourceHandling,
                RestHandling = confirmed.RestHandling
            };

            merged.SaveTo(_settings);
            SettingsChanged?.Invoke();

            _runOptions = merged;

            if (!PackingPlan.TryCreate(BuildRequest(merged), out PackingPlan? plan, out string error) || plan == null)
            {
                Warn(error);
                return;
            }

            _lastUsedPassword = Password;
            OnPropertyChanged(nameof(CanCopyPassword));

            // 到这一步才真的开始干活：给操作日志打一条"本次操作开始：打包"
            // （⛔ 取消 / 规划被拒时一次都不许触发 —— 这条也钉在用例里）。
            OperationStarted?.Invoke();

            IsRunning = true;
            ProgressPercent = 0;
            CurrentStepText = StatusText.PackStepPreparing;

            _cancellation = new CancellationTokenSource();

            var progress = new Progress<PackingProgress>(p =>
            {
                CurrentStepText = p.StepText;

                if (p.Percent >= 0)
                {
                    ProgressPercent = p.Percent;
                }

                ProgressDetailText = p.Detail;
            });

            var request = BuildRequest(merged);

            PackingResult result;

            try
            {
                result = await _service.PackAsync(request, LogSink, progress, _cancellation.Token);
            }
            catch (Exception ex)
            {
                // 引擎层已经把可预期的失败都收成了结果；这里兜的是"连结果都没拿到的意外"。
                result = new PackingResult
                {
                    State = PackingState.Failed,
                    Message = StatusText.PackFailed,
                    FailureReason = "打包过程中出现意外错误：" + PackProcessRunner.DescribeTool(PackToolKind.SevenZip)
                                     + " 调用链抛异常 —— "
                                     + PackPasswordGuard.Sanitize(ex.Message, request.Password)
                };
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                IsRunning = false;
            }

            ApplyResult(result, plan);
        }

        /// <summary>按当前输入 + 那一套选择造请求（**唯一出口**：摘要、预览、真正开跑都用它）。</summary>
        private PackingRequest BuildRequest(PackingRunOptions options) => new()
        {
            SourceFolder = SourceFolder,

            // 分卷大小 0 = 自动（第 46 条：按卷数算），落点与命名由 PackingPaths 推。
            VolumeSizeBytes = 0,
            Password = Password,
            OuterPassword = PasswordsAreSame ? string.Empty : OuterPassword,
            OuterContainer = OuterContainer,
            RunOptions = options ?? new PackingRunOptions()
        };

        /// <summary>取消（只杀自己启动的那个 PID 及其子进程）。</summary>
        public void Cancel()
        {
            try
            {
                _cancellation?.Cancel();
                CurrentStepText = StatusText.PackCancelled;
            }
            catch
            {
                // 令牌可能已经释放：取消是个"尽力而为"的动作，不抛给界面。
            }
        }

        /// <summary>把本次用的密码复制到剪贴板（用户自己也要用）。</summary>
        public void CopyPassword()
        {
            if (!CanCopyPassword)
            {
                Warn("这次还没有设置过密码，没有可复制的内容。");
                return;
            }

            /*
             * ⚠ 这里**刻意不过 PasswordMasker.Sanitize**：它会把长得像 "password: xxx" 的密码
             * 整段换成占位符，用户复制到的就不是自己的密码了。密码进剪贴板是用户主动点的，
             * 也正是设计里给的出口（docs/打包功能.md §3）。
             */
            if (_clipboardService.CopyText(_lastUsedPassword))
            {
                Info("密码已复制到剪贴板（请自行妥善保存；程序不落盘、日志里也不记）。");
            }
            else
            {
                Warn("复制到剪贴板失败（剪贴板被别的程序占着？）。密码仍然留在本窗口内存里。");
            }
        }

        /// <summary>打开输出文件夹（优先打开结果所在的那一层）。</summary>
        public void OpenOutputFolder()
        {
            string? directory = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(ResultOuterPath))
                {
                    directory = Path.GetDirectoryName(ResultOuterPath);
                }

                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = OutputFolder;
                }

                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    Warn("输出文件夹还不存在：" + (directory ?? string.Empty));
                    return;
                }

                _pathService.OpenDirectory(directory);
            }
            catch (Exception ex)
            {
                Warn("打开输出文件夹失败：" + PackPasswordGuard.Sanitize(ex.Message, null));
            }
        }

        /// <summary>数字框里的 MiB → 字节（顺带按界面的 64 MiB – 4096 MiB 范围校验）。</summary>
        internal string ValidateVolumeInput(out long volumeBytes)
        {
            volumeBytes = PackingPlan.DefaultVolumeSizeBytes;

            string text = (VolumeSizeMiB ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                return "请填分卷大小（单位 MiB，默认 512）。";
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long mib) || mib <= 0)
            {
                return $"分卷大小要填一个正整数（单位 MiB），当前填的是「{text}」。";
            }

            volumeBytes = mib * 1024L * 1024L;

            return PackingPlan.ValidateVolumeSize(volumeBytes);
        }

        private void ApplyResult(PackingResult result, PackingPlan plan)
        {
            Volumes.Clear();

            foreach (PackingVolume volume in result.Volumes)
            {
                Volumes.Add(new PackingVolumeRow
                {
                    FileName = volume.FileName,
                    SizeText = TaskSpaceEstimate.FormatSize(volume.Bytes)
                });
            }

            HasResult = true;
            ResultOuterPath = result.OuterPath ?? string.Empty;

            switch (result.State)
            {
                case PackingState.Succeeded:
                    ResultKind = "Success";
                    ResultTitle = result.OuterContainer.HasOuterArtifact()
                        ? StatusText.PackSuccess
                        : StatusText.PackPartialVolumesOnly;
                    Info(result.Describe());
                    break;

                case PackingState.Cancelled:
                    ResultKind = "Warning";
                    ResultTitle = StatusText.PackCancelled;
                    Warn(result.Describe());
                    break;

                default:
                    ResultKind = "Error";
                    ResultTitle = StatusText.PackFailed;
                    Error(result.Describe());
                    break;
            }

            /*
             * 结果区要把**实际产物**说全：外层是什么容器、落在哪个文件；不做外层时也要说清
             * "就是 B 里这几个分卷"，失败时更要说清"外层没生成"—— 而不是留一个空白的路径行让用户猜。
             */
            string outerLine;

            if (!string.IsNullOrWhiteSpace(result.OuterPath))
            {
                outerLine = $"外层产物（{result.OuterContainer.ShortName()}）：{result.OuterPath}";
            }
            else if (result.OuterContainer.HasOuterArtifact())
            {
                outerLine = $"外层产物：没有生成（这次选的容器是「{result.OuterContainer.ShortName()}」）。";
            }
            else
            {
                outerLine = "没有外层容器文件（选的是「不做外层容器」）。";
            }

            /*
             * ⛔ 结果区**不许撒谎**（第 46 条收尾自查抓到的第 6 处）：
             * 原来这里无条件写"分卷在：<文件夹>（N 个）"+「其余物按你选的档留着了，自己删掉就行」——
             * 而**默认档就是把那个文件夹彻底删掉**，于是他跑完看到的是一句"分卷在 X、你可以自己删 X"，
             * 那个 X 已经不存在了。现在按**收尾那句机器结论**分档说。
             */
            string volumesLine;

            if (result.CleanupNote.Contains("已彻底删除", StringComparison.Ordinal))
            {
                volumesLine = "装分卷的文件夹（其余物）：已按你选的档**彻底删除**（不再占空间）。";
            }
            else if (result.CleanupNote.Contains("已移入回收站", StringComparison.Ordinal))
            {
                volumesLine = "装分卷的文件夹（其余物）：已按你选的档**移入回收站**（可还原）。";
            }
            else if (result.Success && Directory.Exists(plan.OutputFolder))
            {
                volumesLine = $"装分卷的文件夹（其余物）：{plan.OutputFolder}（{result.Volumes.Count} 个分卷）";
            }
            else if (Directory.Exists(plan.OutputFolder))
            {
                volumesLine = $"分卷在：{plan.OutputFolder}（{result.Volumes.Count} 个）"
                            + "—— 这次中途停下了，这些分卷**可能不完整**；重试前请先清空它。";
            }
            else
            {
                volumesLine = "没有生成任何分卷。";
            }

            // 「其余物留着」那句提示只在**真的留着**的时候才出现（失败 / 取消时收尾根本没跑）。
            bool restKept = result.Success && result.CleanupNote.Contains("保留", StringComparison.Ordinal);

            ResultText = result.Describe() + Environment.NewLine
                + outerLine + Environment.NewLine
                + volumesLine
                + (restKept ? Environment.NewLine + StatusText.PackKeepFolderHint : string.Empty);

            ProgressPercent = result.Success ? 100 : ProgressPercent;
            CurrentStepText = result.Success ? StatusText.PackSuccess : ResultTitle;
        }

        private void ClearResult()
        {
            HasResult = false;
            ResultTitle = string.Empty;
            ResultText = string.Empty;
            ResultKind = string.Empty;
            ResultOuterPath = string.Empty;
            Volumes.Clear();
        }

        private void ClearNotice()
        {
            NoticeKind = PackingNoticeKind.None;
            NoticeText = string.Empty;
        }

        private void Info(string text) => ShowNotice(PackingNoticeKind.Info, text);

        private void Warn(string text) => ShowNotice(PackingNoticeKind.Warning, text);

        private void Error(string text) => ShowNotice(PackingNoticeKind.Error, text);

        private void ShowNotice(PackingNoticeKind kind, string text)
        {
            NoticeKind = kind;
            NoticeText = text;
        }

        private void RaiseCommandStates()
        {
            (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
