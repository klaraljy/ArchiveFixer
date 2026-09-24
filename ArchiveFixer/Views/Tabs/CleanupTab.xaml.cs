using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ③ 清理与删除页（用户 2026-09-24 第 11 条）。
    ///
    /// <para>
    /// 这一页没有自己的交互逻辑：所有按钮都绑 <c>MainViewModel</c> 的既有命令
    /// （删除其余物 / 删除本目录全部其余物 / 清理空文件夹），
    /// 确认框、预览、二次确认全在命令那一侧 —— 搬页面不改行为。
    /// </para>
    /// </summary>
    public partial class CleanupTab : UserControl
    {
        public CleanupTab()
        {
            InitializeComponent();
        }
    }
}
