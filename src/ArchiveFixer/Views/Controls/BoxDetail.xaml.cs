using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Controls
{
    /// <summary>
    /// 功能框最下面那一行「展示框」（用户 2026-10-14 最终设计）。
    ///
    /// <para>用户原话：「所有说明性文字，详细的说明文字全部写入选项卡说明里面和在每一个功能框最下面
    /// 有一个展示框，点击才能详细展示，平时的功能介绍只能简洁的文字介绍」。</para>
    ///
    /// <para>⛔ 默认收起（<c>BoxDetailTests</c> 钉住）；⛔ 只有两个入参：标题那一句 + 详细说明那一段，
    /// 文案一律来自 <c>Models/StatusText.cs</c>，这个控件自己不写任何一句中文。</para>
    /// </summary>
    public partial class BoxDetail : UserControl
    {
        /// <summary>展示框那一行标题（收起时看得见的那句）。</summary>
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header),
                typeof(string),
                typeof(BoxDetail),
                new PropertyMetadata(string.Empty));

        /// <summary>点开之后才显示的那段详细说明。</summary>
        public static readonly DependencyProperty DetailProperty =
            DependencyProperty.Register(
                nameof(Detail),
                typeof(string),
                typeof(BoxDetail),
                new PropertyMetadata(string.Empty));

        public BoxDetail()
        {
            InitializeComponent();
        }

        /// <summary>展示框那一行标题（收起时看得见的那句）。</summary>
        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        /// <summary>点开之后才显示的那段详细说明。</summary>
        public string Detail
        {
            get => (string)GetValue(DetailProperty);
            set => SetValue(DetailProperty, value);
        }
    }
}
