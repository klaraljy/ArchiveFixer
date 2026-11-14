using System.Windows.Controls;

namespace ArchiveFixer.Views;

/// <summary>
/// 说明正文的唯一视图（「说明」选项卡与说明窗共用）。
/// 正文全部来自 <see cref="ArchiveFixer.Models.HelpContent"/>；这里只负责加载 XAML。
/// </summary>
public partial class HelpBodyPanel : UserControl
{
    public HelpBodyPanel()
    {
        InitializeComponent();
    }
}
