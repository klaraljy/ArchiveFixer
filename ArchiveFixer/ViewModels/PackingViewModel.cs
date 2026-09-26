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
            RefreshSummaryCommand = new RelayCommand(RefreshSummary);
            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            CopyPasswordCommand = new RelayCommand(CopyPassword);
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);

            RefreshSummary();
        }

        /// <summary>每一步的日志出口（由主界面接上，落到主日志文件里）。</summary>
        public Action<string>? LogSink { get; set; }

        /// <summary>设置变了要不要落盘（由主界面接上；打包那几档要记住）。</summary>
        public Action? SettingsChanged { get; set; }

        /// <summary>
        /// 设置对象（打包的落点 / 原包操作 / 其余物操作都存在它上面）。
        /// ⛔ 与解压侧那两档**各存各的**（用户 2026-09-26 原话："两者绝对不能同步"）。
        /// </summary>
        public AppSettings Settings
        {
            get => _settings;
            set => _settings = value ?? new AppSettings();
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

            SummaryText =
                $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                + (plan.UnreadableCount > 0 ? $"（其中 {plan.UnreadableCount} 个条目读不到大小）" : string.Empty)
                + Environment.NewLine
                + plan.DescribeVolumePlan()
                + Environment.NewLine
                + $"外层容器：{plan.OuterContainer.ShortName()}"
                + $"；需要空余空间：约 {TaskSpaceEstimate.FormatSize(plan.RequiredSpaceBytes)}";

            PlacementText = plan.OuterContainer.HasOuterArtifact()
                ? $"最终产物：{plan.OuterPath}"
                : $"不做外层容器：结果就是 {plan.OutputFolder} 里的分卷";
        }

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

            // 先用"设置里那一套"算一遍规划（弹窗要把"最终产物在哪"显示出来）。
            PackingRunOptions initial = PackingRunOptions.FromSettings(_settings);

            if (!PackingPlan.TryCreate(BuildRequest(initial), out PackingPlan? previewPlan, out string planError)
                || previewPlan == null)
            {
                Warn(planError);
                return;
            }

            // ───────── 小确认弹窗（取消 = 什么都不做）─────────
            PackingRunOptions? confirmed = _dialogService.ShowPackingConfirm(
                PackingConfirmRequest.FromPlan(previewPlan, initial));

            if (confirmed == null)
            {
                Info("已取消：什么都没做（源与其余物一个字节都没动）。");
                return;
            }

            // 确认了才写回设置（"相应的保存记忆操作"）。
            confirmed.SaveTo(_settings);
            SettingsChanged?.Invoke();

            _runOptions = confirmed;

            if (!PackingPlan.TryCreate(BuildRequest(confirmed), out PackingPlan? plan, out string error) || plan == null)
            {
                Warn(error);
                return;
            }

            _lastUsedPassword = Password;
            OnPropertyChanged(nameof(CanCopyPassword));

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

            var request = BuildRequest(confirmed);

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

            ResultText = result.Describe() + Environment.NewLine
                + outerLine + Environment.NewLine
                + $"分卷在：{plan.OutputFolder}（{result.Volumes.Count} 个）" + Environment.NewLine
                + StatusText.PackKeepFolderHint;

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
