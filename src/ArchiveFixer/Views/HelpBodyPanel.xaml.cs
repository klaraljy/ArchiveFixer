using System.Windows.Controls;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 说明正文的唯一视图（「说明」选项卡与说明窗共用）。
    ///
    /// <para>⚠ 2026-10-25 定位：本控件原来只有 XAML、没有 code-behind 构造函数，
    /// 于是 <c>InitializeComponent()</c> 一次都没调用 —— 视觉树里只有空 UserControl，
    /// 正文一个字都不渲染；「复制全部说明」仍能工作，所以看起来像"页面空白但按钮正常"。
    /// 这里补上唯一必需的构造，正文与 XAML 仍是同一份。</para>
    /// </summary>
    public partial class HelpBodyPanel : UserControl
    {
        public HelpBodyPanel()
        {
            InitializeComponent();
        }
    }
}
