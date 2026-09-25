using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 一键处理的**那一个**确认框（用户 2026-09-24 第 17 条；旧名字叫「本次选项」面板）。
    ///
    /// <para><b>它做什么</b>：点「一键处理」时先说清两件事 —— **内容物会生成在什么地方**、
    /// **其余物会不会被自动删掉**（用户点名要的正文），外加需要时的两行提醒（疑似无用物 /
    /// 没有可用密码的包）。用户确认后产出一个 <see cref="OneClickRunOptions"/> **运行期快照**交给解压管线；
    /// 要临时改落点 / 终端落法 / 源包处理，展开下面那个折叠区（默认收起 —— 旧版把这一整套摊在正文里，
    /// 用户原话："这么的啰嗦"）。</para>
    ///
    /// <para><b>它绝不写设置</b>：只有用户勾了「把本次选择存为默认」，才由
    /// <c>MainViewModel.SaveOneClickOptionsAsDefaults</c> 写一次 <c>appsettings.json</c>
    /// （§9.2 硬要求②：不勾就一个字节都不改）。唯一的例外是那个「以后不再询问」勾选项 ——
    /// 它按用户要求（"这个可以选中以后不弹出"）由调用方写进
    /// <see cref="AppSettings.SkipOneClickConfirm"/>，而且界面上留了开关能再打开。</para>
    ///
    /// <para><b>线程与降级</b>（与 <c>DialogService</c> 同一口径，历史"卡死"就出在这一段）：</para>
    /// <list type="bullet">
    /// <item><description><b>无 UI 宿主</b>（<c>Application.Current == null</c>：单元测试 / 控制台宿主）：
    /// 返回 <see cref="OneClickOptionsOutcome.NotShown"/> —— <b>不弹窗、不死等</b>，
    /// 调用方直接按设置值继续（§9.2 硬要求④）；</description></item>
    /// <item><description><b>UI 线程</b>：直接 <c>ShowDialog()</c>（内部是嵌套消息泵，界面照常刷新）；</description></item>
    /// <item><description><b>后台线程</b>：<c>Dispatcher.InvokeAsync</c>（**不是**同步 <c>Invoke</c>）
    /// + <b>有界等待</b>；等待超时就按"没问到选择"处理，绝不无限期挂着。</description></item>
    /// </list>
    ///
    /// <para><b>不 Show 也能验</b>：控件都带名字，结论由 <see cref="ReadResult"/> 直接读出来，
    /// 所以无界面 XAML 校验宿主可以在**不显示窗口**的前提下验证"控件状态 ↔ 快照"的映射
    /// （AGENTS.md §13：不许抢焦点、不许置前）。</para>
    /// </summary>
    public partial class OneClickOptionsWindow : Window, INotifyPropertyChanged
    {
        /// <summary>
        /// 后台线程等待用户点确认框的上限（与 <c>DialogService.BackgroundWaitTimeout</c> 同一量级）。
        /// 之所以要有上限：这个 API 是同步的（要拿用户的答案），**不能无限等** ——
        /// 超时按"没弹面板"处理，于是行为退回"按设置走"（既有行为），而不是把整批卡住。
        /// </summary>
        public static readonly TimeSpan BackgroundWaitTimeout = TimeSpan.FromMinutes(5);

        private readonly AppSettings _settings;

        /// <summary>
        /// 折叠区里改了落点之后重算"内容物会生成在…"那一行（可空：没有它就只显示调用方给的那一句）。
        ///
        /// <para>为什么必须能重算：那一行是用户点名要看的东西，他改了落点却还看着旧路径，
        /// 比不显示更糟。重算走**调用方**（它手里有 <c>PathService</c> 与真实任务），
        /// 这里一个字都不拼路径 —— 落点仍然只有一处实现。</para>
        /// </summary>
        private readonly Func<OneClickRunOptions, Task<string>>? _destinationEchoFactory;

        private string _destinationEcho = string.Empty;
        private string _restEcho = string.Empty;
        private string _sourceEcho = string.Empty;
        private string _noticeEcho = string.Empty;

        /// <summary>
        /// 「特定解压：&lt;规则名&gt;」那一行（用户 2026-09-24）；空 = 整行不显示（没开特定解压）。
        /// 它是**事实**不是选项：值由调用方按同一个快照算好传进来，本窗口一个字都不推。
        /// </summary>
        private string _specialExtractionEcho = string.Empty;

        /// <summary>初始化期间不刷新界面：避免在控件还没填完时把半成品状态写进正文。</summary>
        private bool _initializing = true;

        /// <summary>重算落点那一行的并发编号（连点几下时只认最后一次算出来的结果）。</summary>
        private int _echoRevision;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>无参构造：供 XAML 宿主 / 设计器使用（等价于"按默认设置、按设置值"）。</summary>
        public OneClickOptionsWindow()
            : this(null, null)
        {
        }

        /// <param name="settings">只用于取默认值；本窗口**从不写它**。</param>
        /// <param name="seed">
        /// 打开时的初值。null = 按设置值（§9.1：每一项的默认值都取设置里的当前值 → §9.2 硬要求⑤
        /// "默认档行为与现在完全一致"）。
        /// </param>
        public OneClickOptionsWindow(AppSettings? settings, OneClickRunOptions? seed)
            : this(settings, seed, null, null, null)
        {
        }

        /// <summary>
        /// 正式路径：正文里的两件事（内容物落点 / 其余物）由调用方算好传进来，外加可选的提醒两行。
        /// </summary>
        /// <param name="destinationEcho">「内容物会生成在…」那一行（调用方用落点唯一实现算出来的）。</param>
        /// <param name="restEcho">「其余物…」那一行（自动删除 / 不自动删除）。</param>
        /// <param name="sourceEcho">「源包…」那一行。</param>
        /// <param name="noticeEcho">需要时的提醒（疑似无用物 / 没有可用密码）；空 = 不显示。</param>
        /// <param name="destinationEchoFactory">折叠区改了落点之后重算第一行（可空）。</param>
        /// <param name="specialExtractionEcho">
        /// 「特定解压：&lt;规则名&gt;」那一行（用户 2026-09-24）；空 = 不显示（没开特定解压）。
        /// </param>
        public OneClickOptionsWindow(
            AppSettings? settings,
            OneClickRunOptions? seed,
            string? destinationEcho,
            string? restEcho,
            string? sourceEcho,
            string? noticeEcho = null,
            Func<OneClickRunOptions, Task<string>>? destinationEchoFactory = null,
            string? specialExtractionEcho = null)
        {
            InitializeComponent();

            _settings = settings ?? AppSettings.CreateDefault();
            _settings.Normalize();

            DataContext = this;

            _destinationEcho = destinationEcho ?? string.Empty;
            _restEcho = restEcho ?? string.Empty;
            _sourceEcho = sourceEcho ?? string.Empty;
            _noticeEcho = noticeEcho ?? string.Empty;
            _specialExtractionEcho = specialExtractionEcho ?? string.Empty;
            _destinationEchoFactory = destinationEchoFactory;

            ApplySpecialExtractionEcho();
            ApplySeed(seed ?? OneClickRunOptions.FromSettings(_settings));

            _initializing = false;
            RefreshUi();
        }

        /// <summary>「内容物会生成在…」（用户点名要看的第一件事）。</summary>
        public string DestinationEcho
        {
            get => _destinationEcho;
            private set => SetEcho(ref _destinationEcho, value, nameof(DestinationEcho));
        }

        /// <summary>「其余物…」（用户点名要看的第二件事）。</summary>
        public string RestEcho
        {
            get => _restEcho;
            private set => SetEcho(ref _restEcho, value, nameof(RestEcho));
        }

        /// <summary>「源包…」。</summary>
        public string SourceEcho
        {
            get => _sourceEcho;
            private set => SetEcho(ref _sourceEcho, value, nameof(SourceEcho));
        }

        /// <summary>需要时才出现的那段提醒（空 = 整块收起）。</summary>
        public string NoticeEcho
        {
            get => _noticeEcho;
            private set
            {
                if (SetEcho(ref _noticeEcho, value, nameof(NoticeEcho)))
                {
                    NoticeText.Visibility = string.IsNullOrWhiteSpace(_noticeEcho)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            }
        }

        /// <summary>用户是否按了「开始处理」（✕ 关窗 / 「取消」都是 false）。</summary>
        public bool IsConfirmed { get; private set; }

        /// <summary>
        /// 「特定解压：&lt;规则名&gt;」（空 = 这一行整块收起）。
        ///
        /// <para>用户在动手前必须能看见这一次到底按哪条特定规则跑 —— 他最恨"我以为它按默认跑的"。</para>
        /// </summary>
        public string SpecialExtractionEcho
        {
            get => _specialExtractionEcho;
            private set => SetEcho(ref _specialExtractionEcho, value, nameof(SpecialExtractionEcho));
        }

        /// <summary>
        /// 把「特定解压」那一行刷进界面，并决定它显不显示。
        ///
        /// <para>为什么单独一个方法而不是塞进 <c>RefreshUi</c>：它**不随折叠区里的选择变化**
        /// （那些选项里没有特定解压 —— 规则在②页挑、总开关在①页），是纯粹的"这是事实"，
        /// 构造时刷一次就够；塞进 <c>RefreshUi</c> 反而会让人以为它跟着落点走。</para>
        /// </summary>
        private void ApplySpecialExtractionEcho()
        {
            SpecialText.Visibility = string.IsNullOrWhiteSpace(_specialExtractionEcho)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// 把界面上的选择读成快照（**唯一**的读取入口；不依赖窗口是否显示过）。
        ///
        /// <para>
        /// public 是为了让"无界面 XAML 校验宿主"（`_tmp/ArchiveFixer/xaml-check`，另一个程序集）
        /// 能在**不 Show 窗口**的前提下核对"控件状态 ↔ 快照"的映射（AGENTS.md §13：
        /// 不许抢焦点、不许置前）；XAML 里那些 <c>x:Name</c> 也因此显式加了
        /// <c>x:FieldModifier="public"</c>。产品代码只从本程序集内调它。
        /// </para>
        ///
        /// 勾选项的语义：
        /// · <see cref="OneClickRunOptions.SaveAsDefault"/> —— 是否写设置（调用方负责写，本窗口不写）；
        /// · <see cref="OneClickRunOptions.SuppressPanelNextTime"/> —— 以后不再问（**调用方负责落盘**）。
        /// </summary>
        public OneClickRunOptions ReadResult()
        {
            return new OneClickRunOptions
            {
                PlacementMode = CurrentPlacementMode,
                CustomRoot = (CustomRootBox.Text ?? string.Empty).Trim(),
                TerminalLayout = TerminalUseArchiveNameOption.IsChecked == true
                    ? TerminalLayoutMode.UseArchiveName
                    : TerminalLayoutMode.KeepLastFolder,
                SourceHandling = ResolveSourceHandling(),
                RestHandling = ResolveRestHandling(),
                SaveAsDefault = SaveAsDefaultBox.IsChecked == true,
                SuppressPanelNextTime = SuppressPanelBox.IsChecked == true
            };
        }

        /// <summary>
        /// 显示确认框并拿结果（**唯一的入口**）。任何"弹不出来 / 问不到"的情况都返回
        /// <see cref="OneClickOptionsOutcome.NotShown"/>，由调用方按设置继续。
        /// </summary>
        internal static OneClickOptionsPrompt Show(
            OneClickRunOptions seed,
            AppSettings? settings = null,
            OneClickConfirmFacts? facts = null,
            Func<OneClickRunOptions, Task<string>>? destinationEchoFactory = null)
        {
            Application? app = Application.Current;

            if (app == null)
            {
                // 无 UI 宿主：不弹窗、不死等（§9.2 硬要求④）。不写降级日志文件 ——
                // 这条路径在单元测试里每个用例都会走到，写文件只会污染 data\logs。
                return OneClickOptionsPrompt.NotShown();
            }

            Dispatcher? dispatcher = app.Dispatcher;

            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return OneClickOptionsPrompt.NotShown();
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    return ShowModal(seed, settings, facts, destinationEchoFactory);
                }

                DispatcherOperation<OneClickOptionsPrompt> operation =
                    dispatcher.InvokeAsync(() => ShowModal(seed, settings, facts, destinationEchoFactory));

                if (operation.Task.Wait(BackgroundWaitTimeout))
                {
                    return operation.Task.GetAwaiter().GetResult();
                }

                // 超时：按"没问到选择"处理，行为退回"按设置走"，绝不把整批卡在这里。
                return OneClickOptionsPrompt.NotShown();
            }
            catch
            {
                // 确认框自己出问题绝不允许变成"一键处理失败"：退回按设置走。
                return OneClickOptionsPrompt.NotShown();
            }
        }

        private static OneClickOptionsPrompt ShowModal(
            OneClickRunOptions seed,
            AppSettings? settings,
            OneClickConfirmFacts? facts,
            Func<OneClickRunOptions, Task<string>>? destinationEchoFactory)
        {
            var window = new OneClickOptionsWindow(
                settings,
                seed,
                facts?.DestinationEcho,
                facts?.RestEcho,
                facts?.SourceEcho,
                facts?.NoticeEcho,
                destinationEchoFactory,
                facts?.SpecialExtractionEcho);

            Window? owner = Application.Current?.MainWindow;

            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            bool? result = window.ShowDialog();

            return result == true && window.IsConfirmed
                ? OneClickOptionsPrompt.Confirmed(window.ReadResult())
                : OneClickOptionsPrompt.Cancelled();
        }

        // ------------------------------------------------------------------ 初值与刷新

        private void ApplySeed(OneClickRunOptions seed)
        {
            // 用户 2026-09-24 第 13 条之后只剩两档；旧快照里的两档由 NormalizeLegacyMode 迁过来。
            OutputPlacementMode mode = OutputPlacement.NormalizeLegacyMode(seed.PlacementMode);

            if (mode == OutputPlacementMode.CustomRootPerArchive)
            {
                PlacementCustomPerArchiveOption.IsChecked = true;
            }
            else
            {
                PlacementPerArchiveOption.IsChecked = true;
            }

            CustomRootBox.Text = seed.CustomRoot ?? string.Empty;

            TerminalUseArchiveNameOption.IsChecked = seed.TerminalLayout == TerminalLayoutMode.UseArchiveName;
            TerminalKeepLastFolderOption.IsChecked = seed.TerminalLayout != TerminalLayoutMode.UseArchiveName;

            // 2026-09-25 第 32 条之后只剩两档（原地不动 / 放入其余物）：第三档"校验通过后删除"已删掉，
            // 要删源包改成"放入其余物 + 删除操作"（语义更清楚、选项少一个）。
            SourceKeepInPlaceOption.IsChecked = seed.SourceHandling == SourceHandlingMode.KeepInPlace;
            SourceMoveToRestOption.IsChecked = seed.SourceHandling != SourceHandlingMode.KeepInPlace;

            /*
             * 「删除操作」三档（第 33 条补进弹窗）：初值取当前设置 —— 用户 2026-09-25 的原话是
             * "一键处理的弹窗也是要随着现在的设置进行更新的"，所以弹窗里必须**看得见也改得了**
             * 这一次的其余物会怎么处理，不能只让它在正文里被动显示一行。
             */
            string restHandling = RestHandlingModes.Normalize(seed.RestHandling);

            RestKeepOption.IsChecked = string.Equals(restHandling, RestHandlingModes.Keep, StringComparison.Ordinal);
            RestRecycleOption.IsChecked = string.Equals(restHandling, RestHandlingModes.RecycleBin, StringComparison.Ordinal);
            RestDeleteOption.IsChecked = string.Equals(restHandling, RestHandlingModes.Delete, StringComparison.Ordinal);

            UpdateRestDeleteNotice();

            /*
             * 两个勾选项的预置口径（2026-09-24 第 17 条之后）：
             * · 「存为默认」**不预置** = 默认不写设置文件（§9.2 硬要求②）；
             * · 「以后不再询问」预置成**设置里的当前值**：用户上次勾过就还是勾着的
             *   （他明确要的是"选中以后不弹出"，而不是"每次重新勾一遍"），
             *   但它只在用户真的按了「开始处理」时才由调用方写回设置 —— 打开又关掉不留痕迹。
             */
            SaveAsDefaultBox.IsChecked = false;
            SuppressPanelBox.IsChecked = _settings.SkipOneClickConfirm;
        }

        private OutputPlacementMode CurrentPlacementMode
        {
            get
            {
                if (PlacementCustomPerArchiveOption.IsChecked == true)
                {
                    return OutputPlacementMode.CustomRootPerArchive;
                }

                return OutputPlacementMode.PerArchiveSubfolder;
            }
        }

        private SourceHandlingMode ResolveSourceHandling()
        {
            /*
             * ⚠ 判据写在**会动源文件**的那一档上，兜底留在"什么都不做"那一档（2026-09-25 第 32 条）：
             * 反过来写（"没勾留在原地就当放入其余物"）时，任何一次界面状态错乱都会变成
             * "用户没同意过，源包却被搬走了"—— 而搬走是不可逆的。默认档也已经是「留在原地」。
             */
            if (SourceMoveToRestOption.IsChecked == true)
            {
                return SourceHandlingMode.MoveToRest;
            }

            return SourceHandlingMode.KeepInPlace;
        }

        /// <summary>
        /// 折叠区里选的「删除操作」是哪一档。
        ///
        /// <para>兜底同样落在"什么都不做"那一档（<see cref="RestHandlingModes.Keep"/>）——
        /// 三档里只有它不可能删掉任何东西。</para>
        /// </summary>
        private string ResolveRestHandling()
        {
            if (RestDeleteOption.IsChecked == true)
            {
                return RestHandlingModes.Delete;
            }

            if (RestRecycleOption.IsChecked == true)
            {
                return RestHandlingModes.RecycleBin;
            }

            return RestHandlingModes.Keep;
        }

        /// <summary>
        /// 选「彻底删除」时那条红字提示常驻显示（与 ③页 同一条口径：**没有关闭按钮**，
        /// 只有把选项改回去它才消失）。
        /// </summary>
        private void UpdateRestDeleteNotice()
        {
            RestDeleteNotice.Visibility = RestDeleteOption.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void RestHandling_Checked(object sender, RoutedEventArgs e)
        {
            UpdateRestDeleteNotice();
            Option_Changed(sender, e);
        }

        /// <summary>
        /// 界面随选择刷新（源包那一行 / 「指定位置」可用性 / 能不能点「开始处理」）。
        ///
        /// "选了指定位置却没填路径"必须在**点按钮之前**就看出来：
        /// 空根会被落点实现解释成"未指定位置"那一档，静默换个地方落盘是最不该有的形态
        /// （见 <see cref="OneClickRunOptions.IsPlacementValid"/>）。
        /// </summary>
        private void RefreshUi()
        {
            OneClickRunOptions current = ReadResult();
            bool customMode = OutputPlacement.UsesCustomRoot(current.PlacementMode);
            bool placementValid = current.IsPlacementValid;

            CustomRootBox.IsEnabled = customMode;
            BrowseButton.IsEnabled = customMode;

            StartButton.IsEnabled = placementValid;

            ValidationText.Text = placementValid
                ? string.Empty
                : "选了「指定位置」但还没填路径：点「浏览…」选一个目录，或改用上面那一档。";

            // 源包那一行跟着折叠区里的选择实时变（它是"这次会不会动我的源包"的答案）。
            SourceEcho = StatusText.OneClickConfirmSourceLabel + OneClickRunOptions.DescribeSourceHandling(current.SourceHandling);

            RefreshDestinationEcho();
        }

        /// <summary>
        /// 重算「内容物会生成在…」那一行。
        ///
        /// <para>挂起条件：初始化期间不算（控件还没填完）；没有工厂时保持调用方给的那一句
        /// （无界面宿主与单测走这条，绝不在这里现拼路径）。</para>
        /// </summary>
        private void RefreshDestinationEcho()
        {
            if (_initializing || _destinationEchoFactory == null)
            {
                return;
            }

            int revision = ++_echoRevision;
            OneClickRunOptions current = ReadResult();

            _ = Task.Run(async () =>
            {
                string text;

                try
                {
                    text = await _destinationEchoFactory(current).ConfigureAwait(false);
                }
                catch
                {
                    // 算不出来就保留上一句：绝不在这里编一个假路径出来。
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    // 连点几下时只认最后一次（旧结果回来晚了会覆盖新结果）。
                    if (revision == _echoRevision)
                    {
                        DestinationEcho = text;
                    }
                });
            });
        }

        private bool SetEcho(ref string field, string value, string propertyName)
        {
            string normalized = value ?? string.Empty;

            if (string.Equals(field, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            field = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            return true;
        }

        // ------------------------------------------------------------------ 事件

        private void Placement_Checked(object sender, RoutedEventArgs e) => OnSelectionChanged();

        private void TerminalLayout_Checked(object sender, RoutedEventArgs e) => OnSelectionChanged();

        private void SourceHandling_Checked(object sender, RoutedEventArgs e) => OnSelectionChanged();

        private void Option_Changed(object sender, RoutedEventArgs e) => OnSelectionChanged();

        private void CustomRootBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            OnSelectionChanged();

        private void OnSelectionChanged()
        {
            if (_initializing)
            {
                return;
            }

            RefreshUi();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 复用既有的文件夹选择（无 UI 宿主时它自己返回空串，不会抛）。
                string folder = new DialogService().ShowFolderBrowserDialog();

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    CustomRootBox.Text = folder;
                }
            }
            catch (Exception)
            {
                // 选目录失败不需要打扰用户：路径框会保持原样，他可以手填。
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ReadResult().IsPlacementValid)
            {
                // 兜底（按钮平时是禁用的）：绝不让一个空根的"指定位置"出门。
                RefreshUi();
                return;
            }

            IsConfirmed = true;

            if (IsLoaded)
            {
                DialogResult = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsConfirmed = false;

            if (IsLoaded)
            {
                DialogResult = false;
            }
        }
    }
}
