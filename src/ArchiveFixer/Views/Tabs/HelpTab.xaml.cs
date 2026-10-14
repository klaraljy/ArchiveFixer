using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ⑨ 说明页（用户 2026-10-14 最终设计：说明独立成一页）。
    ///
    /// <para>
    /// 正文与「说明」窗共用同一份 <see cref="HelpBodyPanel"/>，文字唯一出处仍是
    /// <c>Models/HelpContent.cs</c>；这一页只有一颗「复制全部说明」按钮，
    /// 复用的就是窗口那一份拼装（<see cref="HelpWindow.BuildPlainText"/>）。
    /// </para>
    /// </summary>
    public partial class HelpTab : UserControl
    {
        public HelpTab()
        {
            InitializeComponent();
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(HelpWindow.BuildPlainText());
            }
            catch
            {
                // 剪贴板被别的进程占用是常事：复制失败不值得弹框。
            }
        }
    }
}
