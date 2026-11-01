using ArchiveFixer.ViewModels;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ② 解压方式页的 code-behind。
    ///
    /// <para>用户 2026-11-01：并发数不再给用户选（自动算法决定），原来"并发档一变就重算
    /// 空间档位那句话"的事件处理已随控件一起删除。这里只剩 InitializeComponent。</para>
    /// </summary>
    public partial class ExtractionTab : UserControl
    {
        public ExtractionTab()
        {
            InitializeComponent();
        }
    }
}
