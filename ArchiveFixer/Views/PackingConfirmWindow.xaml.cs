using System.Windows;
using ArchiveFixer.Packing;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 打包的小确认弹窗（用户 2026-09-26 第 46 条 + 当天追加）。
    ///
    /// <para>⚠ **2026-09-26 追加改口径**（用户原话："**选择还是得放在页面**"）：
    /// 这里只剩**两档操作**（原包 / 其余物）可选；"最终压缩包放在哪"变成**只显示**
    /// （显示的是⑤页上选好的那一个，含"默认跟①页输出位置"解析出来的实际目录）。
    /// </para>
    ///
    /// <para>它不做任何判断：所有选项与那句红字都来自 <see cref="PackingRunOptions"/> 与
    /// <see cref="PackingConfirmRequest"/>（纯逻辑，可测）。窗口只负责把它们画出来并回传用户的选择。</para>
    /// </summary>
    public partial class PackingConfirmWindow : Window
    {
        private readonly PackingConfirmRequest _request;
        private bool _loading = true;

        public PackingConfirmWindow(PackingConfirmRequest request)
        {
            _request = request ?? new PackingConfirmRequest();

            InitializeComponent();

            HeadList.ItemsSource = _request.BuildHeadLines();
            SourceExplanationText.Text = _request.SourceExplanation;
            RestExplanationText.Text = _request.RestExplanation;

            PackingRunOptions initial = _request.Initial ?? new PackingRunOptions();

            PlacementText.Text = _request.PlacementText;

            SourceKeep.IsChecked = initial.SourceHandling == PackingSourceHandling.KeepInPlace;
            SourceMove.IsChecked = initial.SourceHandling == PackingSourceHandling.MoveToRest;

            RestKeep.IsChecked = initial.RestHandling == PackingRestHandling.Keep;
            RestRecycle.IsChecked = initial.RestHandling == PackingRestHandling.RecycleBin;
            RestDelete.IsChecked = initial.RestHandling == PackingRestHandling.Delete;

            _loading = false;

            UpdateEnabledState();
        }

        /// <summary>用户确认后的那一套选择（取消时是 null）。</summary>
        /// <remarks>
        /// ⛔ 落点那三档**原样带回去**（窗口没动过它）—— 页面上选的落点不许在这里被改掉。
        /// </remarks>
        public PackingRunOptions? Result { get; private set; }

        private PackingRunOptions ReadOptions()
        {
            PackingRunOptions initial = _request.Initial ?? new PackingRunOptions();

            return new PackingRunOptions
            {
                TargetMode = initial.TargetMode,
                CustomOutputDirectory = initial.CustomOutputDirectory,
                DefaultOutputDirectory = initial.DefaultOutputDirectory,
                SourceHandling = SourceMove.IsChecked == true
                    ? PackingSourceHandling.MoveToRest
                    : PackingSourceHandling.KeepInPlace,
                RestHandling = RestRecycle.IsChecked == true
                    ? PackingRestHandling.RecycleBin
                    : RestKeep.IsChecked == true
                        ? PackingRestHandling.Keep
                        : PackingRestHandling.Delete
            };
        }

        private void UpdateEnabledState()
        {
            PackingRunOptions options = ReadOptions();

            // 红字警告：只在"原包移入其余物 + 其余物要删"时出现（用户见过这条说明）。
            string? warning = options.SourceLossWarning;

            DangerBox.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
            DangerText.Text = warning ?? string.Empty;
        }

        private void OnOptionsChanged(object sender, RoutedEventArgs e) => UpdateIfLoaded();

        private void UpdateIfLoaded()
        {
            if (!_loading)
            {
                UpdateEnabledState();
            }
        }

        private void OnStart(object sender, RoutedEventArgs e)
        {
            Result = ReadOptions();
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
        }
    }
}
