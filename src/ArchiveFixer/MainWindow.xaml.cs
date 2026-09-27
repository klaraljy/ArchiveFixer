using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using ArchiveFixer.Views.Tabs;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ArchiveFixer
{
    /// <summary>
    /// 主窗口的 code-behind（用户 2026-09-24 第 11 条之后）。
    ///
    /// <para>
    /// 界面已经拆成「菜单 + 六个选项卡 + 底栏」，所以这里只剩三类东西：
    /// 菜单项的处理（视图 / 帮助 / 退出）、拖放导入、以及 Ctrl+A 的抢占。
    /// 原来堆在这里的日志跟随滚动、密码框同步、右键菜单、详情窗口全部跟着控件
    /// 搬进了对应的选项卡（<c>Views\Tabs\</c>）。
    /// </para>
    /// <para>
    /// 菜单项刻意用 <c>Click</c> 而不是新造一堆命令：它们做的都是"操作界面本身"
    /// （开日志窗口、收起日志区、滚动到选中行、打开文档），没有业务语义，不该塞进 ViewModel。
    /// </para>
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DialogService _dialogService = new();

        /// <summary>说明文档的相对位置（开发态在仓库里，分发态在程序目录旁）。</summary>
        private const string UsageDocRelativePath = @"docs\使用说明.md";

        public MainWindow()
        {
            InitializeComponent();

            Loaded += MainWindow_Loaded;

            /*
             * Ctrl+A 的可靠入口（用户 2026-09-22 追加需求："有一个全选的选项"）。
             *
             * 为什么不用 Window.InputBindings 里的 KeyBinding：DataGrid **自己**注册了 Ctrl+A
             * （它选的是"行高亮"，跟勾选框是两套东西），焦点在表格里时那条键轮不到窗口级绑定。
             * PreviewKeyDown 是**隧道**事件，在 DataGrid 看到之前就到达窗口 —— 于是
             * "Ctrl+A = 勾选全部"这件事在本窗口里是确定的。
             */
            PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        /// <summary>
        /// 接上"跳到某一页"的请求（<see cref="MainViewModel.TabRequested"/>）。
        ///
        /// <para>
        /// 为什么由窗口来切页，而不是让 ViewModel 去碰 TabControl：ViewModel 不该知道
        /// 界面长什么样。设置与打包搬进选项卡之后，那两条旧命令的语义就只剩"切到那一页"。
        /// </para>
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.TabRequested -= ViewModel_TabRequested;
                viewModel.TabRequested += ViewModel_TabRequested;
            }
        }

        private void ViewModel_TabRequested(int index)
        {
            if (index >= 0 && index < MainTabControl.Items.Count)
            {
                MainTabControl.SelectedIndex = index;
            }
        }

        /// <summary>
        /// 切到②「解压方式」页 / ③「清理与删除」页时，让那一页上的档位控件**按最新设置重新求值**。
        ///
        /// <para><b>为什么要有这一道</b>（用户 2026-09-25 第 31 条，他报了两次"两处不同步、很意外"；
        /// 第 34 条又栽在同一类问题上一次）：落点有两处入口（①页「输出位置」与②页「落点（解压到哪）」），
        /// 真值只有一份（<c>ExtractToOriginalDirectory</c> + <c>CustomOutputDirectory</c>），
        /// 但②页那几个控件绑的是 <c>SettingsEditor</c> 算出来的属性 —— 只要有一条写设置的路径漏了通知，
        /// 用户切过去看到的就是旧状态。写设置的地方已经统一走
        /// <c>MainViewModel.NotifyOutputPlacementChangedEverywhere</c>；这里再补一道"进页面就对齐"，
        /// 让漏通知这种错误**不可能**再以"两处显示不一样"的形式露到界面上。</para>
        ///
        /// <para>③页同理（第 34 条）：一键处理的弹窗也会读/改"源包操作 / 删除操作"这两档
        /// （弹窗的 GroupName 现在已经与①③页隔开，见 <c>OneClickOptionsWindow.xaml</c>），
        /// 切进③页时按设置重新求值一次，黑点永远回到真值上。</para>
        ///
        /// <para>只做重新求值，不写任何设置，所以不可能与别的入口形成来回覆盖。</para>
        /// </summary>
        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, MainTabControl))
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (ReferenceEquals(MainTabControl.SelectedItem, ExtractionTabItem))
            {
                viewModel.SettingsEditor.NotifyOutputPlacementChanged();
            }
            else if (ReferenceEquals(MainTabControl.SelectedItem, CleanupTabItem))
            {
                viewModel.SettingsEditor.NotifyProcessingOptionsChanged();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.TabRequested -= ViewModel_TabRequested;

                /*
                 * 关窗前的最后一次落盘（用户 2026-09-26 要的"改了就自动存、重启还在"）：
                 * 自动保存是每 800 ms 一跳，用户完全可能"改完立刻关窗" —— 那一下没跳到的改动
                 * 必须在关窗这一刻补上，否则他会看到"我刚改的又没了"（那正是他报的那类"没保存"）。
                 */
                viewModel.FlushSettingsAutoSave();
            }

            base.OnClosed(e);
        }

        /// <summary>
        /// Ctrl+A / Ctrl+D / Ctrl+I：全选 / 全不选 / 反选（都作用在**勾选框**上）。
        ///
        /// <para>
        /// Ctrl+D 与 Ctrl+I 走 <c>Window.InputBindings</c> 就够了（DataGrid 没用这两个键），
        /// 只有 Ctrl+A 需要在这里抢下来。任务表格现在住在①任务页里，
        /// 所以"焦点在不在表格里"这件事由那一页回答（<see cref="TaskTab.IsKeyboardFocusWithinTaskGrid"/>）——
        /// 它顺带保证了密码框里的 Ctrl+A（全选文本）不会被吃掉。
        /// </para>
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.A || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            if (Keyboard.FocusedElement is not DependencyObject focused ||
                !TaskTabHost.IsKeyboardFocusWithinTaskGrid(focused))
            {
                return;
            }

            if (!viewModel.SelectAllTasksCommand.CanExecute(null))
            {
                return;
            }

            viewModel.SelectAllTasksCommand.Execute(null);
            e.Handled = true;
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void LogWindowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new LogWindow
                {
                    DataContext = DataContext,
                    Owner = this
                };

                window.Show();
            }
            catch (Exception ex)
            {
                _dialogService.ShowException(ex, "打开日志窗口失败");
            }
        }

        private void ToggleLogAreaMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // 日志区在①任务页里：菜单与页内那个「收起 / 展开」按钮走的是同一份状态。
            TaskTabHost.ToggleLogArea();
        }

        private void JumpToSelectedTaskMenuItem_Click(object sender, RoutedEventArgs e)
        {
            TaskTabHost.ScrollToSelectedTask();
        }

        /// <summary>
        /// 选项卡那一排最右端的「说明」（用户 2026-09-27）。
        /// ⛔ 只读程序里自带的内容（<see cref="Models.HelpContent"/>），不依赖任何文件 ——
        /// 与帮助菜单里那份「使用说明」的区别就在这里。
        /// </summary>
        private void HelpMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new Views.HelpWindow { Owner = this };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _dialogService.ShowException(ex, "打开说明失败");
            }
        }

        private void UsageMenuItem_Click(object sender, RoutedEventArgs e)
        {
            string? doc = LocateUsageDoc();

            if (doc == null || !SafePathHelper.OpenFileInExplorer(doc))
            {
                /*
                 * 用户 2026-09-27 真机："我选中最上端的帮助点击提示显示路径错误"。
                 *
                 * 现场：绿色目录 `E:\ArchiveFixer\` 里没有 `docs\`，而这个方法从程序目录往上爬 6 层
                 * 找 `docs\使用说明.md`（那是给开发态准备的），于是必然找不到 —— 而他看到的
                 * 只是一句"找不到…路径错误"，不知道该去哪儿找、也不知道还有别的入口。
                 *
                 * 现在三件事一起给：①**列出我真找过的位置**；②告诉他把 docs 放到程序旁边就能用；
                 * ③指路那颗**永远打得开**的「说明」（程序自带、不依赖文件）。
                 */
                _dialogService.ShowInfo(
                    "找不到《使用说明》文档。" + Environment.NewLine + Environment.NewLine
                    + "我找过这些位置：" + Environment.NewLine
                    + DescribeUsageDocSearch() + Environment.NewLine
                    + "解决办法：把仓库里的 docs 目录整个放到程序目录旁边（ArchiveFixer.exe 所在的那一层），"
                    + "或者直接点选项卡那一排最右端的「说明」—— 那份是程序自带的，不依赖任何文件。");
            }
        }

        /// <summary>把"我找过哪些位置"逐行写出来（找不到文档时给用户看，⛔ 不再只说一句路径错）。</summary>
        private static string DescribeUsageDocSearch()
        {
            var builder = new System.Text.StringBuilder();

            try
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);

                for (int depth = 0; directory != null && depth < 6; depth++)
                {
                    builder.AppendLine("　· " + Path.Combine(directory.FullName, UsageDocRelativePath));

                    directory = directory.Parent;
                }
            }
            catch
            {
                builder.AppendLine("　· （列目录时出错，位置读不出来）");
            }

            return builder.ToString().TrimEnd();
        }

        private void ShortcutsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _dialogService.ShowInfo(
                "快捷键" + Environment.NewLine + Environment.NewLine
                + "Ctrl+A　勾选全部任务" + Environment.NewLine
                + "Ctrl+D　取消全部勾选" + Environment.NewLine
                + "Ctrl+I　反选" + Environment.NewLine + Environment.NewLine
                + "勾选是一切的入口：一键处理 / 只解压 / 删除其余物 / 清理空文件夹都只作用于勾选中的任务，"
                + "一个都没勾就什么都不做。");
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                _dialogService.ShowInfo(viewModel.AboutText);
            }
        }

        /// <summary>
        /// 找《使用说明》：先看程序目录旁的 <c>docs\</c>（分发态），再从程序目录往上找仓库根（开发态）。
        /// 找不到就返回 null，由调用方给一句人能看懂的提示 —— 绝不静默无反应。
        /// </summary>
        private static string? LocateUsageDoc()
        {
            try
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);

                for (int depth = 0; directory != null && depth < 6; depth++)
                {
                    string candidate = Path.Combine(directory.FullName, UsageDocRelativePath);

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    directory = directory.Parent;
                }
            }
            catch
            {
                // 找文档失败不是功能故障：调用方会给出提示。
            }

            return null;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    return;
                }

                if (DataContext is not MainViewModel viewModel)
                {
                    return;
                }

                string[]? paths = e.Data.GetData(DataFormats.FileDrop) as string[];

                if (paths == null || paths.Length == 0)
                {
                    return;
                }

                e.Handled = true;

                await viewModel.AddPathsAsync(paths);
            }
            catch (Exception ex)
            {
                // 拖拽导入失败也要走统一对话框（系统 MessageBox 与本程序的观感不搭）
                _dialogService.ShowException(ex, "拖拽导入失败");
            }
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }
    }
}
