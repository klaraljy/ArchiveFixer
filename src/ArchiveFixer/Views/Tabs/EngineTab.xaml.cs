using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ⑦ 引擎页（用户 2026-10-14 最终设计：「引擎：从解压方式那边搬过来的」）。
    ///
    /// <para>
    /// 这一页没有自己的交互逻辑：全部绑 <c>MainViewModel.SettingsEditor</c> 的既有属性与命令
    /// （引擎列表 / 各引擎 exe 路径 / 打开官方下载页）—— **只搬位置，内容一个字不改**。
    /// ⛔ 说明文字里不许写「从主界面搬来」这类废话（用户 2026-10-14 点名）。
    /// </para>
    /// </summary>
    public partial class EngineTab : UserControl
    {
        public EngineTab()
        {
            InitializeComponent();
        }
    }
}
