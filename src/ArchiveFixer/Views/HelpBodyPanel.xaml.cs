using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

        /// <summary>
        /// 流程图那一层只允许横向滚动；普通滚轮必须交给外层正文纵向滚动。
        /// ScrollViewer 自己会吃掉 MouseWheel（内层纵向禁用也不冒泡），所以在预览阶段转交。
        /// </summary>
        private void FlowDiagram_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return;
            }

            e.Handled = true;

            BodyScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent,
                Source = BodyScrollViewer
            });
        }
    }
}
