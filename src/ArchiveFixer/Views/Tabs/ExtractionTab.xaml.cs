using ArchiveFixer.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ② 解压方式页的 code-behind。
    ///
    /// <para>
    /// 只有一件事必须在这里做：并发档一动，旁边那句「当前档位」（源包怎么处理 / 其余物怎么处理）
    /// 就得跟着重算（<see cref="MainViewModel.RefreshSpaceModeText"/>）。它是**展示状态**的重算，
    /// 所以由视图在控件事件里触发，而不是让 ViewModel 去监听一个普通对象的属性。
    /// </para>
    /// <para>
    /// ⚠ 它不是"顺手改行为"：这里只是把同一句话按当前设置重刷一遍，真正决定并行度与删除资格的
    /// 仍是批处理那一刻读设置的那几处 —— 不刷的后果是界面在说一件与事实不符的事。
    /// （2026-09-26 改口径：原来这段注释讲的是"危险模式自测凭证盖不盖得住"，那套东西已在
    /// 2026-09-25 第 32 条整块退役，注释跟着事实走。）
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
            RefreshSpaceModeSummary();
        }

        private void ParallelCount_LostFocus(object sender, RoutedEventArgs e)
        {
            // 手填 5~7 时只有 LostFocus 才知道用户填完了（可编辑下拉的 Text 绑定也是这个时机写回）。
            RefreshSpaceModeSummary();
        }

        private void RefreshSpaceModeSummary()
        {
            (DataContext as MainViewModel)?.RefreshSpaceModeText();
        }
    }
}
