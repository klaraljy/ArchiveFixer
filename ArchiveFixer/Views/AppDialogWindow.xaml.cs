using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 对话框图标。
    /// </summary>
    public enum AppDialogIcon
    {
        /// <summary>信息。</summary>
        Info,

        /// <summary>警告。</summary>
        Warning,

        /// <summary>错误。</summary>
        Error,

        /// <summary>确认（询问）。</summary>
        Question
    }

    /// <summary>
    /// 对话框按钮组合。
    /// </summary>
    public enum AppDialogButtons
    {
        /// <summary>只有"确定"。</summary>
        Ok,

        /// <summary>"确定 / 取消"。</summary>
        OkCancel,

        /// <summary>"是 / 否"。</summary>
        YesNo,

        /// <summary>"是 / 否 / 取消"。</summary>
        YesNoCancel
    }

    /// <summary>
    /// 一次对话框请求。给 <see cref="AppDialogWindow"/> 用，同时也是
    /// <see cref="ArchiveFixer.Services.DialogService"/> 与窗口之间的唯一契约。
    /// </summary>
    public sealed class AppDialogRequest
    {
        public string Title { get; set; } = "提示";

        public string Message { get; set; } = string.Empty;

        /// <summary>标题下面那行小字（例如"此操作不可撤销"）。</summary>
        public string Subtitle { get; set; } = string.Empty;

        /// <summary>附加细节（异常类型等），等宽小字 + 警示底色，默认不显示。</summary>
        public string Detail { get; set; } = string.Empty;

        public AppDialogIcon Icon { get; set; } = AppDialogIcon.Info;

        public AppDialogButtons Buttons { get; set; } = AppDialogButtons.Ok;

        /// <summary>
        /// 主操作（是/确定）是不是危险操作。
        /// true 时主按钮用实心红 —— 例如"清空密码列表""删除源压缩包"。
        /// </summary>
        public bool Destructive { get; set; }

        /// <summary>可选项位的文案（如"本次运行内不再询问"）。空 = 不显示该勾选框。</summary>
        public string OptionText { get; set; } = string.Empty;

        /// <summary>可选项位的初始勾选状态。</summary>
        public bool OptionChecked { get; set; }

        public string OkText { get; set; } = "确定";

        public string YesText { get; set; } = "是";

        public string NoText { get; set; } = "否";

        public string CancelText { get; set; } = "取消";
    }

    /// <summary>
    /// 统一自绘对话框。
    ///
    /// 只由 <see cref="ArchiveFixer.Services.DialogService"/> 创建：窗口本身不做线程判断，
    /// marshal 与降级都在 DialogService 里（那里才知道有没有 UI 宿主）。
    /// </summary>
    public partial class AppDialogWindow : Window
    {
        private readonly AppDialogRequest _request;

        /// <summary>用户的选择。窗口被直接关掉（标题栏 ×）时保持 <see cref="MessageBoxResult.Cancel"/>。</summary>
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

        /// <summary>可选项位最终是否被勾选。</summary>
        public bool IsOptionChecked => OptionCheckBox.IsChecked == true;

        public AppDialogWindow(AppDialogRequest request)
        {
            _request = request ?? new AppDialogRequest();

            InitializeComponent();

            Apply(_request);

            Loaded += (_, _) =>
            {
                // 焦点给正文：一进来就能 Ctrl+A / Ctrl+C 复制（MessageBox 做不到这一点）。
                MessageTextBox.Focus();
                MessageTextBox.CaretIndex = 0;
            };
        }

        private void Apply(AppDialogRequest request)
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? "提示" : request.Title;
            TitleTextBlock.Text = Title;

            MessageTextBox.Text = request.Message ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(request.Subtitle))
            {
                SubtitleTextBlock.Text = request.Subtitle;
                SubtitleTextBlock.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrWhiteSpace(request.Detail))
            {
                DetailTextBox.Text = request.Detail;
                DetailPanel.Visibility = Visibility.Visible;
            }

            ApplyIcon(request.Icon);
            ApplyButtons(request);

            if (!string.IsNullOrWhiteSpace(request.OptionText))
            {
                OptionCheckBox.Content = request.OptionText;
                OptionCheckBox.IsChecked = request.OptionChecked;
                OptionCheckBox.Visibility = Visibility.Visible;
            }
        }

        private void ApplyIcon(AppDialogIcon icon)
        {
            switch (icon)
            {
                case AppDialogIcon.Warning:
                    IconGlyph.Text = "!";
                    IconBadge.Background = Brush("WarningBrush", Color.FromRgb(0xB4, 0x53, 0x09));
                    break;

                case AppDialogIcon.Error:
                    IconGlyph.Text = "×";
                    IconBadge.Background = Brush("DangerBrush", Color.FromRgb(0xDC, 0x26, 0x26));
                    break;

                case AppDialogIcon.Question:
                    IconGlyph.Text = "?";
                    IconBadge.Background = Brush("PrimaryBrush", Color.FromRgb(0x25, 0x63, 0xEB));
                    break;

                default:
                    IconGlyph.Text = "i";
                    IconBadge.Background = Brush("PrimaryBrush", Color.FromRgb(0x25, 0x63, 0xEB));
                    break;
            }
        }

        private void ApplyButtons(AppDialogRequest request)
        {
            Style primaryStyle = TryStyle(request.Destructive ? "DangerButtonStyle" : "PrimaryButtonStyle", YesButton);
            Style secondaryStyle = TryStyle("SecondaryButtonStyle", CancelButton);

            OkButton.Content = request.OkText;
            YesButton.Content = request.YesText;
            NoButton.Content = request.NoText;
            CancelButton.Content = request.CancelText;

            OkButton.Style = primaryStyle;
            YesButton.Style = primaryStyle;
            NoButton.Style = secondaryStyle;
            CancelButton.Style = secondaryStyle;

            switch (request.Buttons)
            {
                case AppDialogButtons.OkCancel:
                    OkButton.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                    OkButton.IsDefault = true;
                    CancelButton.IsCancel = true;
                    break;

                case AppDialogButtons.YesNo:
                    YesButton.Visibility = Visibility.Visible;
                    NoButton.Visibility = Visibility.Visible;
                    YesButton.IsDefault = true;
                    // Esc = 否：对"要不要删/要不要继续"这类询问，不确认才是安全默认
                    NoButton.IsCancel = true;
                    break;

                case AppDialogButtons.YesNoCancel:
                    YesButton.Visibility = Visibility.Visible;
                    NoButton.Visibility = Visibility.Visible;
                    CancelButton.Visibility = Visibility.Visible;
                    YesButton.IsDefault = true;
                    CancelButton.IsCancel = true;
                    break;

                default:
                    OkButton.Visibility = Visibility.Visible;
                    OkButton.IsDefault = true;
                    OkButton.MinWidth = 104;
                    break;
            }
        }

        private Style TryStyle(string key, FrameworkElement fallback)
        {
            if (TryFindResource(key) is Style style)
            {
                return style;
            }

            return fallback.Style;
        }

        private Brush Brush(string resourceKey, Color fallback)
        {
            if (TryFindResource(resourceKey) is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(fallback);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Close(MessageBoxResult.OK, dialogResult: true);
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            Close(MessageBoxResult.Yes, dialogResult: true);
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Close(MessageBoxResult.No, dialogResult: false);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close(MessageBoxResult.Cancel, dialogResult: false);
        }

        private void Close(MessageBoxResult result, bool dialogResult)
        {
            Result = result;

            try
            {
                // ShowDialog 之外调用时设置 DialogResult 会抛异常，所以这里只尝试关闭窗口。
                DialogResult = dialogResult;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }
    }
}
