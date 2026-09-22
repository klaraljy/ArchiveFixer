using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 一键处理的「本次选项」面板（规格 <c>docs/输出与整理模型.md</c> §9）。
    ///
    /// <para><b>它做什么</b>：点「一键处理」时先问一次"这一次按什么落点 / 终端落法 / 源包处理跑"，
    /// 用户确认后产出一个 <see cref="OneClickRunOptions"/> **运行期快照**交给解压管线。
    /// 面板自己**不写任何设置** —— 只有用户勾了「把本次选择存为默认」，
    /// 才由 <c>MainViewModel.SaveOneClickOptionsAsDefaults</c> 写一次 <c>appsettings.json</c>
    /// （§9.2 硬要求②：不勾就一个字节都不改）。</para>
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
        /// 后台线程等待用户点面板的上限（与 <c>DialogService.BackgroundWaitTimeout</c> 同一量级）。
        /// 之所以要有上限：这个 API 是同步的（要拿用户的答案），**不能无限等** ——
        /// 超时按"没弹面板"处理，于是行为退回"按设置走"（既有行为），而不是把整批卡住。
        /// </summary>
        public static readonly TimeSpan BackgroundWaitTimeout = TimeSpan.FromMinutes(5);

        private readonly AppSettings _settings;

        private string _summary = string.Empty;

        /// <summary>初始化期间不刷新界面：避免在控件还没填完时把半成品状态写进摘要。</summary>
        private bool _initializing = true;

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
        {
            InitializeComponent();

            _settings = settings ?? AppSettings.CreateDefault();
            _settings.Normalize();

            DataContext = this;

            ApplySeed(seed ?? OneClickRunOptions.FromSettings(_settings));

            _initializing = false;
            RefreshUi();
        }

        /// <summary>「这一次按什么跑」的摘要（绑定到窗口顶部那一行；随选择实时变化）。</summary>
        public string Summary
        {
            get => _summary;
            private set
            {
                if (string.Equals(_summary, value, StringComparison.Ordinal))
                {
                    return;
                }

                _summary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            }
        }

        /// <summary>用户是否按了「开始处理」（✕ 关窗 / 「取消」都是 false）。</summary>
        public bool IsConfirmed { get; private set; }

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
        /// · <see cref="OneClickRunOptions.SuppressPanelNextTime"/> —— 本次运行内不再问。
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
                SaveAsDefault = SaveAsDefaultBox.IsChecked == true,
                SuppressPanelNextTime = SuppressPanelBox.IsChecked == true
            };
        }

        /// <summary>
        /// 显示面板并拿结果（**唯一的入口**）。任何"弹不出来 / 问不到"的情况都返回
        /// <see cref="OneClickOptionsOutcome.NotShown"/>，由调用方按设置继续。
        /// </summary>
        internal static OneClickOptionsPrompt Show(OneClickRunOptions seed, AppSettings? settings = null)
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
                    return ShowModal(seed, settings);
                }

                DispatcherOperation<OneClickOptionsPrompt> operation =
                    dispatcher.InvokeAsync(() => ShowModal(seed, settings));

                if (operation.Task.Wait(BackgroundWaitTimeout))
                {
                    return operation.Task.GetAwaiter().GetResult();
                }

                // 超时：按"没问到选择"处理，行为退回"按设置走"，绝不把整批卡在这里。
                return OneClickOptionsPrompt.NotShown();
            }
            catch
            {
                // 面板自己出问题绝不允许变成"一键处理失败"：退回按设置走。
                return OneClickOptionsPrompt.NotShown();
            }
        }

        private static OneClickOptionsPrompt ShowModal(OneClickRunOptions seed, AppSettings? settings)
        {
            var window = new OneClickOptionsWindow(settings, seed);

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
            switch (seed.PlacementMode)
            {
                case OutputPlacementMode.SourceDirectoryFlat:
                    PlacementSourceDirectoryOption.IsChecked = true;
                    break;

                case OutputPlacementMode.CustomRootPerArchive:
                    PlacementCustomPerArchiveOption.IsChecked = true;
                    break;

                case OutputPlacementMode.CustomRootFlat:
                    PlacementCustomFlatOption.IsChecked = true;
                    break;

                default:
                    PlacementPerArchiveOption.IsChecked = true;
                    break;
            }

            CustomRootBox.Text = seed.CustomRoot ?? string.Empty;

            TerminalUseArchiveNameOption.IsChecked = seed.TerminalLayout == TerminalLayoutMode.UseArchiveName;
            TerminalKeepLastFolderOption.IsChecked = seed.TerminalLayout != TerminalLayoutMode.UseArchiveName;

            SourceKeepInPlaceOption.IsChecked = seed.SourceHandling == SourceHandlingMode.KeepInPlace;
            SourceDeleteAfterVerifyOption.IsChecked = seed.SourceHandling == SourceHandlingMode.DeleteAfterVerify;
            SourceMoveToRestOption.IsChecked =
                seed.SourceHandling != SourceHandlingMode.KeepInPlace &&
                seed.SourceHandling != SourceHandlingMode.DeleteAfterVerify;

            /*
             * ⚠ 这两个勾选框**刻意不预置**：
             * · 「存为默认」不预置 = 不写设置文件（默认档行为与加面板之前一致）；
             * · 「以后不再询问」不预置 = 下次仍然问（面板是本功能的主体，默认不该把自己关掉）。
             * 预置任何一个都会让"默认档"偏离既有行为（§9.2 硬要求⑤）。
             */
            SaveAsDefaultBox.IsChecked = false;
            SuppressPanelBox.IsChecked = false;
        }

        private OutputPlacementMode CurrentPlacementMode
        {
            get
            {
                if (PlacementSourceDirectoryOption.IsChecked == true)
                {
                    return OutputPlacementMode.SourceDirectoryFlat;
                }

                if (PlacementCustomPerArchiveOption.IsChecked == true)
                {
                    return OutputPlacementMode.CustomRootPerArchive;
                }

                if (PlacementCustomFlatOption.IsChecked == true)
                {
                    return OutputPlacementMode.CustomRootFlat;
                }

                return OutputPlacementMode.PerArchiveSubfolder;
            }
        }

        private SourceHandlingMode ResolveSourceHandling()
        {
            if (SourceKeepInPlaceOption.IsChecked == true)
            {
                return SourceHandlingMode.KeepInPlace;
            }

            if (SourceDeleteAfterVerifyOption.IsChecked == true)
            {
                return SourceHandlingMode.DeleteAfterVerify;
            }

            return SourceHandlingMode.MoveToRest;
        }

        /// <summary>
        /// 界面随选择刷新（摘要 / 「指定位置」可用性 / 能不能点「开始处理」）。
        ///
        /// "选了指定位置却没填路径"必须在**点按钮之前**就看出来：
        /// 空根会被落点实现解释成"解压到压缩包所在目录"，静默换个地方落盘是最不该有的形态
        /// （见 <see cref="OneClickRunOptions.IsPlacementValid"/>）。
        /// </summary>
        private void RefreshUi()
        {
            OneClickRunOptions current = ReadResult();
            bool customMode = OutputPlacement.UsesCustomRoot(current.PlacementMode);
            bool placementValid = current.IsPlacementValid;

            CustomRootBox.IsEnabled = customMode;
            BrowseButton.IsEnabled = customMode;

            CustomRootHint.Text = customMode
                ? "这两个模式会把内容物解到你选的目录里；目录不存在会自动创建。"
                : "只对上面两种「指定位置」模式生效；当前这一档用不到它。";

            StartButton.IsEnabled = placementValid;

            ValidationText.Text = placementValid
                ? string.Empty
                : "选了「指定位置」但还没填路径：点「浏览…」选一个目录，或改用上面两种模式。";

            Summary = current.Describe();
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
