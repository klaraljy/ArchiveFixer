using ArchiveFixer.ViewModels;
using System.Windows.Controls;

namespace ArchiveFixer.Views.Tabs
{
    /// <summary>
    /// ② 解压方式页的 code-behind。并发数由用户手动选择（1–4），下拉框直接绑定设置，
    /// 不需要在这里写联动逻辑；这里只剩 InitializeComponent。
    /// </summary>
    public partial class ExtractionTab : UserControl
    {
        public ExtractionTab()
        {
            InitializeComponent();
        }
    }
}
