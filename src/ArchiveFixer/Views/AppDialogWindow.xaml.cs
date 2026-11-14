using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArchiveFixer.Services;

namespace ArchiveFixer.Views
{

    /// <summary>
    /// 统一自绘对话框。
    ///
    /// 只由 <see cref="ArchiveFixer.Services.DialogService"/> 创建：窗口本身不做线程判断，
    /// marshal 与降级都在 DialogService 里（那里才知道有没有 UI 宿主）。
    /// </summary>
    public partial class AppDialogWindow : Window, IAppDialogWindow
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

            // 模态子窗：不许最小化（A2），但保留拖边框改大小。
            WindowMinimizePolicy.Apply(this);

            Apply(_request);

            /*
             * 让它真的被看见（用户 2026-09-22 反馈："弹出来之后躲到主窗口后面了，只有声音、没有闪烁"）。
             * 强度按图标/是否危险操作定：警告 / 错误 / 询问 / 危险操作 = 强提醒（一直闪到被点 + 连响三声）。
             */
            ArchiveFixer.Helpers.WindowAttention.Attach(this, ResolveAttention(_request));

            Loaded += (_, _) =>
            {
                // 焦点给正文：一进来就能 Ctrl+A / Ctrl+C 复制（MessageBox 做不到这一点）。
                MessageTextBox.Focus();
                MessageTextBox.CaretIndex = 0;
            };
        }

        /// <summary>
        /// 这个对话框该用多强的提醒。
        ///
        /// <para>判据是"它需不需要有人来处理"：信息提示（"完成了"这类）响一声就够；
        /// 警告 / 错误 / 询问 / 危险操作必须把人叫过来 —— 那几种没人看着就会出事或卡住整批。</para>
        /// </summary>
        internal static ArchiveFixer.Helpers.AttentionStrength ResolveAttention(AppDialogRequest? request)
        {
            if (request == null)
            {
                return ArchiveFixer.Helpers.AttentionStrength.Normal;
            }

            if (request.Destructive)
            {
                return ArchiveFixer.Helpers.AttentionStrength.Strong;
            }

            switch (request.Icon)
            {
                case AppDialogIcon.Warning:
                case AppDialogIcon.Error:
                case AppDialogIcon.Question:
                    return ArchiveFixer.Helpers.AttentionStrength.Strong;

                default:
                    return ArchiveFixer.Helpers.AttentionStrength.Normal;
            }
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

            ApplySummarySeverity(request.SummarySeverity);
        }

        /// <summary>色带（批末汇总那一档）的底色画刷键 —— 判据（哪一档）不在这里，这里只管"哪一档长什么样"。</summary>
        internal static string ResolveSummaryBannerBrushKey(ArchiveFixer.Models.BatchSummarySeverity severity) =>
            severity switch
            {
                ArchiveFixer.Models.BatchSummarySeverity.Failed => "SummaryFailedBrush",
                ArchiveFixer.Models.BatchSummarySeverity.Partial => "SummaryPartialBrush",
                _ => "SummarySuccessBrush"
            };

        /// <summary>色带底色的兜底值（App.xaml 里的资源取不到时用它，例如没有 Application 的测试宿主）。</summary>
        internal static Color ResolveSummaryBannerFallbackColor(ArchiveFixer.Models.BatchSummarySeverity severity) =>
            severity switch
            {
                ArchiveFixer.Models.BatchSummarySeverity.Failed => Color.FromRgb(0xDC, 0x26, 0x26),
                ArchiveFixer.Models.BatchSummarySeverity.Partial => Color.FromRgb(0xEA, 0x58, 0x0C),
                _ => Color.FromRgb(0x1D, 0x4E, 0xD8)
            };

        /// <summary>色带里那几个字/图标怎么画（三档共用一个键：白字 + 半透明白底徽标）。</summary>
        internal const string SummaryBannerForegroundBrushKey = "TextOnAccentBrush";

        /// <summary>正文区的底色 / 字色（用户 2026-09-29："下面是白底，黑字" —— 三档完全一样）。</summary>
        internal const string SummaryBodyBackgroundBrushKey = "PanelBrush";

        internal const string SummaryBodyForegroundBrushKey = "TextPrimaryBrush";

        /// <summary>
        /// 批末汇总框：顶部放出一条**色带**（用户 2026-09-29 原话："我记得你是上30%的位置是蓝色，
        /// 然后下面是白底，黑字，我什么时候说过白字的"）。
        ///
        /// <para>三档的差别**只在色带**：蓝 = 全成功 / 橙 = 有部分完成或跳过 / 红 = 有失败；
        /// 正文一律白底黑字 —— 所以这里**不碰** <c>MessageTextBox</c> 的颜色，
        /// 只把色带拉出来，并把普通布局里那块标题（图标 + 标题 + 副标题）收起来，免得同一句话出现两遍。</para>
        ///
        /// <para>色带的字号与配色刻意与普通布局的标题区一致（15.5 / SemiBold / 白字），
        /// 这样"同一个框、多了一条色带"而不是"换了一个框"。</para>
        /// </summary>
        private void ApplySummarySeverity(ArchiveFixer.Models.BatchSummarySeverity? severity)
        {
            if (severity == null)
            {
                return;
            }

            SummaryBanner.Background = Brush(
                ResolveSummaryBannerBrushKey(severity.Value),
                ResolveSummaryBannerFallbackColor(severity.Value));

            SummaryBannerTitle.Text = Title;
            SummaryBannerTitle.Foreground = Brush(SummaryBannerForegroundBrushKey, Colors.White);
            SummaryBannerGlyph.Foreground = Brush(SummaryBannerForegroundBrushKey, Colors.White);

            /*
             * 正文字色按"白底黑字"钉住（不随图标/严重度变）：色带档下用户要的是"上面一条颜色、
             * 下面照旧读得清"，把正文也染成白字正是他明确否掉的那一版。
             */
            MessageTextBox.Foreground = Brush(SummaryBodyForegroundBrushKey, Color.FromRgb(0x1F, 0x24, 0x30));
            MessageTextBox.Background = Brush(SummaryBodyBackgroundBrushKey, Colors.White);

            // 标题已经搬到色带里了：普通布局那块整块收起（包括图标与副标题）。
            TitlePanel.Visibility = Visibility.Collapsed;
            MessageTextBox.Margin = new Thickness(0);

            // 一个按钮的纯提示框：白底描边按钮，三种底色上都看得清（主色实心按钮压在红底上会糊）。
            if (OkButton.Visibility == Visibility.Visible)
            {
                OkButton.Style = TryStyle("SecondaryButtonStyle", OkButton);
            }

            SummaryBanner.Visibility = Visibility.Visible;
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
        /// <summary>按原逻辑挑宿主窗口并挂 Owner（从 DialogService 搬到窗口实现，行为不变）。</summary>
        public void AttachOwner()
        {
            Window? owner = ResolveOwner();

            if (owner != null && owner.IsVisible)
            {
                Owner = owner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>同名冲突询问的默认按钮：主按钮「覆盖」不作默认，避免回车即覆盖。</summary>
        public void ApplyConflictDefaults()
        {
            YesButton.IsDefault = false;
            NoButton.IsDefault = true;
        }

        /// <summary>先当前激活窗口、再主窗口；不这样做的话，从模态子窗口弹出的提示会跑到主窗口后面。</summary>
        private static Window? ResolveOwner()
        {
            Application? app = Application.Current;

            if (app == null)
            {
                return null;
            }

            Window? active = app.Windows
                .OfType<Window>()
                .FirstOrDefault(window => window.IsActive && window.IsVisible);

            if (active != null)
            {
                return active;
            }

            Window? main = app.MainWindow;

            return main != null && main.IsVisible ? main : null;
        }
    }
}
