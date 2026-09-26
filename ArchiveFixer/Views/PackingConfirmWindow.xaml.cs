using System;
using System.IO;
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

            if (options.TargetMode == PackingTargetMode.Custom &&
                string.IsNullOrWhiteSpace(options.CustomOutputDirectory))
            {
                MessageBox.Show(this, "选了「指定位置」就要挑一个目录。", "打包", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (options.TargetMode == PackingTargetMode.Custom)
            {
                try
                {
                    Directory.CreateDirectory(options.CustomOutputDirectory);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "这个位置用不了：" + ex.Message,
                        "打包",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return;
                }
            }

            Result = options;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
        }
    }
}
