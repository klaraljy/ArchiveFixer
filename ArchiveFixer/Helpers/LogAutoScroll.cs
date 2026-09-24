using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 「日志区跟随最新一行」这件小事的**唯一实现**（主界面①页的日志区与独立日志窗口共用）。
    ///
    /// <para>
    /// 为什么要抽出来：独立日志窗口（第 18 条）与①页的日志区显示的是同一个集合，
    /// 两处各写一遍滚动逻辑，迟早有一处忘了合并滚动或忘了"贴底才跟随"。
    /// </para>
    /// <para>
    /// 两条规则（都是踩过的坑，不要"优化"掉）：
    /// </para>
    /// <list type="number">
    /// <item><description><b>贴底才跟随</b>：用户往上翻历史时不能把他一直拽回底部 ——
    /// 所以用"当前是否贴底"当开关，只认 <c>VerticalChange != 0</c> 的那种"用户真的滚了"
    /// （内容变多也会触发 ScrollChanged，那时 VerticalChange 为 0）。</description></item>
    /// <item><description><b>合并滚动</b>：一次排空周期只滚一次。<c>ScrollIntoView</c> 会强制布局，
    /// 而导入 / 一键处理会连着打几十上百行 —— 逐行滚会把界面线程拖住。</description></item>
    /// </list>
    /// </summary>
    internal sealed class LogAutoScroll
    {
        private readonly ListBox _listBox;

        private INotifyCollectionChanged? _hooked;
        private ScrollViewer? _scrollViewer;
        private bool _pinnedToBottom = true;
        private bool _scrollPending;

        public LogAutoScroll(ListBox listBox)
        {
            _listBox = listBox ?? throw new ArgumentNullException(nameof(listBox));
        }

        /// <summary>
        /// 接到一个日志集合上（传 null 表示只解绑）。
        /// 换宿主时可以直接再调一次：旧集合与旧 ScrollViewer 会先解绑。
        /// </summary>
        public void Attach(object? itemsSource)
        {
            Detach();

            if (itemsSource is INotifyCollectionChanged notifier)
            {
                _hooked = notifier;
                _hooked.CollectionChanged += OnCollectionChanged;
            }

            // ListBox 的 ScrollViewer 要等模板应用后才在可视树里。
            _scrollViewer = FindDescendant<ScrollViewer>(_listBox);

            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged += OnScrollChanged;
            }

            _pinnedToBottom = true;
        }

        public void Detach()
        {
            if (_hooked != null)
            {
                _hooked.CollectionChanged -= OnCollectionChanged;
                _hooked = null;
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer = null;
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.VerticalChange) < 0.1)
            {
                return;
            }

            // 8px 容差：滚动偏移是浮点，正好到底时未必严格相等。
            _pinnedToBottom = e.ExtentHeight <= e.ViewportHeight ||
                              e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 8;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add || _listBox.Items.Count == 0)
            {
                return;
            }

            if (!_pinnedToBottom || _scrollPending)
            {
                return;
            }

            _scrollPending = true;

            _listBox.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _scrollPending = false;

                    if (!_pinnedToBottom || _listBox.Items.Count == 0)
                    {
                        return;
                    }

                    _listBox.ScrollIntoView(_listBox.Items[_listBox.Items.Count - 1]);
                }));
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                if (child is T hit)
                {
                    return hit;
                }

                T? deeper = FindDescendant<T>(child);

                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }
    }
}
