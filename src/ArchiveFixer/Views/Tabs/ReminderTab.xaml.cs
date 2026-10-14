using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ⑥ 提醒页（用户 2026-10-14 最终设计：「提醒：就是之前的一些弹窗操作全部移到这边来」）。
    ///
    /// <para>
    /// 这一页没有自己的交互逻辑：开关全部绑 <c>MainViewModel.SettingsEditor</c> 的既有属性，
    /// 弹与不弹仍由各自的执行路径读同一个设置决定 —— **搬页面不改行为**。
    /// </para>
    /// </summary>
    public partial class ReminderTab : UserControl
    {
        public ReminderTab()
        {
            InitializeComponent();
        }
    }
}
