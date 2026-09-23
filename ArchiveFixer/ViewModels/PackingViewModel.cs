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
    /// <para><b>没有 Rar.exe 时开始按钮不禁用</b>：给一个可勾的「只做 7z 分卷」，
    /// 让用户看得见发生了什么，而不是一个灰着不解释的按钮。勾了就不做外层 rar，
    /// 结果直接是 B 里的分卷。</para>
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
        private string _outputFolder = string.Empty;
        private string _volumeSizeMiB = "512";
        private string _password = string.Empty;
        private string _confirmPassword = string.Empty;
        private string _outerPassword = string.Empty;
        private string _outerConfirmPassword = string.Empty;
        private bool _useSeparateOuterPassword;
        private bool _skipOuterRar;

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
        private string _resultRarPath = string.Empty;

        private PackingNoticeKind _noticeKind = PackingNoticeKind.None;
        private string _noticeText = string.Empty;

        private CancellationTokenSource? _cancellation;

        /// <summary>用户有没有自己改过 B（改过就不再自动覆盖他的输入）。</summary>
        private bool _outputTouchedByUser;

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

            BrowseSourceCommand = new RelayCommand(BrowseSource);
            BrowseOutputCommand = new RelayCommand(BrowseOutput);
            RefreshSummaryCommand = new RelayCommand(RefreshSummary);
            StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => IsRunning);
            CopyPasswordCommand = new RelayCommand(CopyPassword);
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);

            RefreshSummary();
        }

        /// <summary>每一步的日志出口（由主界面接上，落到主日志文件里）。</summary>
        public Action<string>? LogSink { get; set; }

        // ────────────────────────── 输入 ──────────────────────────

        /// <summary>源文件夹 A。</summary>
        public string SourceFolder
        {
            get => _sourceFolder;
            set
            {
                if (SetProperty(ref _sourceFolder, value ?? string.Empty))
                {
                    /*
                     * B 默认与 A 同级（<A 的父目录>\<A 名>_打包）。
                     * 只在"用户还没自己改过 B"时自动填 —— 自己填过就被覆盖，那是抢用户输入。
                     */
                    if (!_outputTouchedByUser)
                    {
                        string suggested = PackingPlan.DefaultOutputFolder(SourceFolder);

                        if (suggested.Length > 0)
                        {
                            _outputFolder = suggested;
                            OnPropertyChanged(nameof(OutputFolder));
                        }
                    }

                    RefreshSummary();
                }
            }
        }

        /// <summary>输出文件夹 B。</summary>
        public string OutputFolder
        {
            get => _outputFolder;
            set
            {
                _outputTouchedByUser = true;

                if (SetProperty(ref _outputFolder, value ?? string.Empty))
                {
                    RefreshSummary();
                }
            }
        }

        /// <summary>分卷大小（MiB 文本；界面就是一个数字框）。</summary>
        public string VolumeSizeMiB
        {
            get => _volumeSizeMiB;
            set
            {
                if (SetProperty(ref _volumeSizeMiB, value ?? string.Empty))
                {
                    RefreshSummary();
                }
            }
        }

        /// <summary>内层 7z 分卷的密码（由窗口的 PasswordBox 推进来）。</summary>
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value ?? string.Empty);
        }

        /// <summary>确认密码。</summary>
        public string ConfirmPassword
        {
            get => _confirmPassword;
            set => SetProperty(ref _confirmPassword, value ?? string.Empty);
        }

        /// <summary>外层 rar 的密码（勾了"用另一个密码"才有效）。</summary>
        public string OuterPassword
        {
            get => _outerPassword;
            set => SetProperty(ref _outerPassword, value ?? string.Empty);
        }

        /// <summary>外层 rar 的确认密码。</summary>
        public string OuterConfirmPassword
        {
            get => _outerConfirmPassword;
            set => SetProperty(ref _outerConfirmPassword, value ?? string.Empty);
        }

        /// <summary>外层 rar 用另一个密码（默认不勾：两层同一个）。</summary>
        public bool UseSeparateOuterPassword
        {
            get => _useSeparateOuterPassword;
            set
            {
                if (SetProperty(ref _useSeparateOuterPassword, value))
                {
                    OnPropertyChanged(nameof(OuterPasswordEnabled));
                }
            }
        }

        /// <summary>外层密码输入框可用（勾了上面那个才有意义）。</summary>
        public bool OuterPasswordEnabled => UseSeparateOuterPassword;

        /// <summary>只做 7z 分卷、跳过外层 rar（没有 Rar.exe 时的那条出路）。</summary>
        public bool SkipOuterRar
        {
            get => _skipOuterRar;
            set
            {
                if (SetProperty(ref _skipOuterRar, value))
                {
                    RefreshSummary();
                }
            }
        }

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

        /// <summary>没有 Rar.exe 时要显眼说出来的那段话（含两条出路）。</summary>
        public string RarMissingText => StatusText.PackNeedRar;

        /// <summary>没有 Rar.exe（界面据此把"只做 7z 分卷"这一段显示出来）。</summary>
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

        /// <summary>结果 rar 的路径（没做外层 / 失败时为空）。</summary>
        public string ResultRarPath
        {
            get => _resultRarPath;
            private set
            {
                if (SetProperty(ref _resultRarPath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasRarResult));
                }
            }
        }

        /// <summary>有结果 rar（结果区显示出路径与「打开输出目录」）。</summary>
        public bool HasRarResult => !string.IsNullOrWhiteSpace(ResultRarPath);

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

        public ICommand BrowseSourceCommand { get; }

        public ICommand BrowseOutputCommand { get; }

        public ICommand RefreshSummaryCommand { get; }

        public ICommand StartCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand CopyPasswordCommand { get; }

        public ICommand OpenOutputFolderCommand { get; }

        // ────────────────────────── 动作 ──────────────────────────

        /// <summary>选源文件夹 A。</summary>
        public void BrowseSource()
        {
            string picked = _dialogService.ShowFolderBrowserDialog();

            if (!string.IsNullOrWhiteSpace(picked))
            {
                SourceFolder = picked;
            }
        }

        /// <summary>选输出文件夹 B。</summary>
        public void BrowseOutput()
        {
            string picked = _dialogService.ShowFolderBrowserDialog();

            if (!string.IsNullOrWhiteSpace(picked))
            {
                OutputFolder = picked;
            }
        }

        /// <summary>
        /// 重算摘要（扫一遍 A 的文件数与总大小）。
        /// ⚠ 大文件夹上这一次扫描是要花时间的，所以它绑在"输入框失去焦点 / 点重算"上，
        /// **不是**每敲一个字符就跑一遍。
        /// </summary>
        public void RefreshSummary()
        {
            string volumeError = ValidateVolumeInput(out long volumeBytes);

            var request = new PackingRequest
            {
                SourceFolder = SourceFolder,
                OutputFolder = OutputFolder,
                VolumeSizeBytes = volumeBytes,
                Password = Password,
                SkipOuterRar = SkipOuterRar
            };

            if (volumeError.Length > 0)
            {
                SummaryText = volumeError;
                PlacementText = string.Empty;
                return;
            }

            if (!PackingPlan.TryCreate(request, out PackingPlan? plan, out string error) || plan == null)
            {
                SummaryText = error;
                PlacementText = string.Empty;
                return;
            }

            SummaryText =
                $"内容：{plan.FileCount} 个文件，{TaskSpaceEstimate.FormatSize(plan.ContentBytes)}"
                + (plan.UnreadableCount > 0 ? $"（其中 {plan.UnreadableCount} 个条目读不到大小）" : string.Empty)
                + $"；每个分卷上限 {plan.VolumeSizeText}，预计 {plan.PlannedVolumeCount} 卷"
                + Environment.NewLine
                + $"需要空余空间：约 {TaskSpaceEstimate.FormatSize(plan.RequiredSpaceBytes)}"
                + "（分卷一份 + 外层 rar 再存一份）";

            PlacementText = SkipOuterRar
                ? $"分卷落在：{plan.OutputFolder}"
                : $"分卷落在：{plan.OutputFolder}" + Environment.NewLine + $"结果 rar：{plan.RarPath}";
        }

        /// <summary>
        /// 开始打包。
        ///
        /// <para>顺序刻意是"先判完所有输入 → 再动任何字节"：密码、分卷大小、落点、空间
        /// 任何一条不过都**不开始**，用户不会看到"跑了十分钟才告诉我不行"。</para>
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
                Warn("请先选择源文件夹 A。");
                return;
            }

            string passwordError = PackingPasswordPolicy.Validate(Password, ConfirmPassword);

            if (passwordError.Length > 0)
            {
                Warn(passwordError);
                return;
            }

            string outerError = PackingPasswordPolicy.ValidateOuter(
                UseSeparateOuterPassword,
                OuterPassword,
                OuterConfirmPassword);

            if (outerError.Length > 0)
            {
                Warn(outerError);
                return;
            }

            string volumeError = ValidateVolumeInput(out long volumeBytes);

            if (volumeError.Length > 0)
            {
                Warn(volumeError);
                return;
            }

            var request = new PackingRequest
            {
                SourceFolder = SourceFolder,
                OutputFolder = OutputFolder,
                VolumeSizeBytes = volumeBytes,
                Password = Password,
                OuterPassword = UseSeparateOuterPassword ? OuterPassword : string.Empty,
                SkipOuterRar = SkipOuterRar
            };

            if (!PackingPlan.TryCreate(request, out PackingPlan? plan, out string planError) || plan == null)
            {
                Warn(planError);
                return;
            }

            _lastUsedPassword = request.Password;
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
                if (!string.IsNullOrWhiteSpace(ResultRarPath))
                {
                    directory = Path.GetDirectoryName(ResultRarPath);
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
            ResultRarPath = result.RarPath ?? string.Empty;

            switch (result.State)
            {
                case PackingState.Succeeded:
                    ResultKind = "Success";
                    ResultTitle = result.SkippedOuterRar ? StatusText.PackPartialVolumesOnly : StatusText.PackSuccess;
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

            ResultText = result.Describe() + Environment.NewLine
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
            ResultRarPath = string.Empty;
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
