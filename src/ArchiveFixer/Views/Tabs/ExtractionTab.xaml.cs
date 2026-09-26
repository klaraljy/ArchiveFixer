using ArchiveFixer.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ② 解压方式页的 code-behind。
    ///
    /// <para>
    /// 只有一件事必须在这里做：并发档一动，危险模式那句"凭证盖不盖得住当前并发档"就得跟着重算
    /// （<see cref="MainViewModel.RefreshSpaceModeText"/>）。它是**展示状态**的重算，
    /// 所以由视图在控件事件里触发，而不是让 ViewModel 去监听一个普通对象的属性。
    /// </para>
    /// <para>
    /// ⚠ 这不是"顺手改行为"：真正决定并行度与删除资格的仍是
    /// <c>DangerModeSelfTestStamp.Covers</c>（在批处理那一刻判），这里只是把同一句话重刷一遍 ——
    /// 不刷的后果是"用户以为凭证还盖得住"，那正是"不许静默降级"要防的事。
    /// </para>
    /// </summary>
    public partial class ExtractionTab : UserControl
    {
        public ExtractionTab()
        {
            InitializeComponent();
        }

        private void ParallelCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshDangerModeCoverage();
        }

        private void ParallelCount_LostFocus(object sender, RoutedEventArgs e)
        {
            // 手填 5~7 时只有 LostFocus 才知道用户填完了（可编辑下拉的 Text 绑定也是这个时机写回）。
            RefreshDangerModeCoverage();
        }

        private void RefreshDangerModeCoverage()
        {
            (DataContext as MainViewModel)?.RefreshSpaceModeText();
        }
    }
}
