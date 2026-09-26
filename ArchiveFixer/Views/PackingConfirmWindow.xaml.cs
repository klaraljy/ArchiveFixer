using System.Windows;
using ArchiveFixer.Packing;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 打包的小确认弹窗（用户 2026-09-26 第 46 条）：
    /// **只显示三件事** —— 最终压缩包放在哪 / 原包操作 / 其余物操作（外加危险组合的红字警告）。
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
            LocalPathText.Text = "放在：" + _request.LocalTargetText;
            SourceExplanationText.Text = _request.SourceExplanation;
            RestExplanationText.Text = _request.RestExplanation;

            PackingRunOptions initial = _request.Initial ?? new PackingRunOptions();

            TargetLocal.IsChecked = initial.TargetMode == PackingTargetMode.Local;
            TargetCustom.IsChecked = initial.TargetMode == PackingTargetMode.Custom;
            CustomTargetBox.Text = initial.CustomOutputDirectory ?? string.Empty;

            SourceKeep.IsChecked = initial.SourceHandling == PackingSourceHandling.KeepInPlace;
            SourceMove.IsChecked = initial.SourceHandling == PackingSourceHandling.MoveToRest;

            RestKeep.IsChecked = initial.RestHandling == PackingRestHandling.Keep;
            RestRecycle.IsChecked = initial.RestHandling == PackingRestHandling.RecycleBin;
            RestDelete.IsChecked = initial.RestHandling == PackingRestHandling.Delete;

            _loading = false;

            UpdateEnabledState();
        }

        /// <summary>用户确认后的那一套选择（取消时是 null）。</summary>
        public PackingRunOptions? Result { get; private set; }

        private PackingRunOptions ReadOptions() => new()
        {
            TargetMode = TargetCustom.IsChecked == true ? PackingTargetMode.Custom : PackingTargetMode.Local,
            CustomOutputDirectory = CustomTargetBox.Text?.Trim() ?? string.Empty,
            SourceHandling = SourceMove.IsChecked == true
                ? PackingSourceHandling.MoveToRest
                : PackingSourceHandling.KeepInPlace,
            RestHandling = RestRecycle.IsChecked == true
                ? PackingRestHandling.RecycleBin
                : RestKeep.IsChecked == true
                    ? PackingRestHandling.Keep
                    : PackingRestHandling.Delete
        };

        private void UpdateEnabledState()
        {
            bool custom = TargetCustom.IsChecked == true;

            CustomTargetBox.IsEnabled = custom;
            PickTargetButton.IsEnabled = custom;

            PackingRunOptions options = ReadOptions();

            // ⚠ 抬头那几行要跟着**当前**选择重算：用户改成"指定位置"之后，
            //    "最终产物：…"必须换成那个目录里的路径（否则弹窗说的是 A、跑出来落在 B）。
            HeadList.ItemsSource = _request.BuildHeadLines(options);

            // 红字警告：只在"原包移入其余物 + 其余物要删"时出现（用户见过这条说明）。
            string? warning = options.SourceLossWarning;

            DangerBox.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
            DangerText.Text = warning ?? string.Empty;
        }

        private void OnTargetChanged(object sender, RoutedEventArgs e) => UpdateIfLoaded();

        private void OnCustomTargetChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
            UpdateIfLoaded();

        private void OnOptionsChanged(object sender, RoutedEventArgs e) => UpdateIfLoaded();

        private void UpdateIfLoaded()
        {
            if (!_loading)
            {
                UpdateEnabledState();
            }
        }

        private void OnPickTarget(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择最终压缩包放在哪" };

            if (dialog.ShowDialog(this) == true)
            {
                CustomTargetBox.Text = dialog.FolderName;
            }
        }

        private void OnStart(object sender, RoutedEventArgs e)
        {
            PackingRunOptions options = ReadOptions();

            if (options.TargetMode == PackingTargetMode.Custom)
            {
                if (string.IsNullOrWhiteSpace(options.CustomOutputDirectory))
                {
                    MessageBox.Show(this, "选了「指定位置」就要挑一个目录。", "打包", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                /*
                 * ⛔ 这里**刻意不建目录**（原来那句话是 `Directory.CreateDirectory`）：
                 * 落点合法性由规划层判定（比如"落点不能在源文件夹里面"），确认之后才开始动盘 ——
                 * 先建一个空目录、再告诉他"这个位置不行"，等于在源目录里留了一个我们造的垃圾。
                 * 目标目录由 `PackingService` 在**校验通过之后**建（`CreateDirectory` 会连父级一起建）。
                 */
                string? placementProblem = CheckPlacement(options);

                if (placementProblem != null)
                {
                    MessageBox.Show(this, placementProblem, "打包", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            Result = options;
            DialogResult = true;
        }

        /// <summary>
        /// 确认之前先替用户挡一道"落点在源里面"这种明显错误 —— 免得他确认完才看到一句失败
        /// （那时候什么都没动，但白点了一次）。判据与规划层同一口径：`PackingPaths.IsInside`。
        /// </summary>
        private string? CheckPlacement(PackingRunOptions options)
        {
            if (_request.SourceKind != PackingSourceKind.Folder ||
                string.IsNullOrWhiteSpace(_request.SourcePath) ||
                string.IsNullOrWhiteSpace(options.CustomOutputDirectory))
            {
                return null;
            }

            if (PackingPaths.IsInside(_request.SourcePath, options.CustomOutputDirectory))
            {
                return "这个落点在源文件夹里面 —— 分卷会落进正在打包的目录，越打越多。请换一个源文件夹外面的目录。";
            }

            return null;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
        }
    }
}
