using ArchiveFixer.Helpers;
using ArchiveFixer.ViewModels;
using System.Windows;

namespace ArchiveFixer.Views
{
    /// <summary>
    /// 独立日志窗口（用户 2026-09-24 第 18 条「日志区太小」）。
    ///
    /// <para>
    /// 与主界面①页的日志区读的是**同一个** <see cref="MainViewModel.Logs"/>，
    /// 所以两边内容永远一致；自动滚动走同一份实现（<see cref="LogAutoScroll"/>）。
    /// </para>
    /// <para>
    /// 它是用户自己点开的窗口，所以**不**做 <c>WindowAttention</c> 的闪烁 + 提示音
    /// （那套只用于"程序需要用户输入、必须叫得住人"的场合）。
    /// </para>
    /// </summary>
    public partial class LogWindow : Window
    {
        private readonly LogAutoScroll _logAutoScroll;

        public LogWindow()
        {
            InitializeComponent();

            _logAutoScroll = new LogAutoScroll(LogList);

            Loaded += LogWindow_Loaded;
            DataContextChanged += LogWindow_DataContextChanged;
        }

        private void LogWindow_Loaded(object sender, RoutedEventArgs e)
        {
            HookLogAutoScroll();
        }

        private void LogWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            HookLogAutoScroll();
        }

        private void HookLogAutoScroll()
        {
            _logAutoScroll.Attach((DataContext as MainViewModel)?.Logs);
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _logAutoScroll.Detach();
            base.OnClosed(e);
        }
    }
}
