using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ⑥ 设置页（用户 2026-09-24 第 11 条）。
    ///
    /// <para>
    /// 这一页没有自己的交互逻辑：每一项都绑 <c>SettingsEditor</c>（工作副本）或
    /// <c>MainViewModel</c> 的既有命令，落盘由自动保存负责（2026-09-26 起底栏没有保存按钮了）——
    /// 三页设置（②③④⑥）共用同一个保存动作，不会出现"这一页改了、那一页没保存"。
    /// </para>
    /// </summary>
    public partial class SettingsTab : UserControl
    {
        public SettingsTab()
        {
            InitializeComponent();
        }
    }
}
