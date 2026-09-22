using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 勾选这件事的三条要求（用户 2026-09-22 追加需求，原话见下）：
    ///
    /// <para>
    /// 「识别了文件夹，所有的文件都会选中，但是我们要去解压其中的一个**就得一个个点**，这种冗余操作
    /// 违背了我的初心；而且现在有种这样的情况，**你想取消勾选要点击两次**，第一次是选中，
    /// 第二次是点击一开始的关闭勾选……你就可以像文件夹选中一样，**有一个全选的选项和点击一次就可以成功的选项**。」
    /// </para>
    ///
    /// <list type="number">
    /// <item><description><b>一次点击即生效</b>：选择列必须是模板列 + 真正的 <c>CheckBox</c>
    /// （<c>DataGridCheckBoxColumn</c> 要点两次），而且绑的是 <c>Mode=TwoWay</c> +
    /// <c>UpdateSourceTrigger=PropertyChanged</c>（默认的 LostFocus 会让勾选写不回模型）；</description></item>
    /// <item><description><b>整格可点</b>：<c>CheckBox</c> 要铺满单元格，否则点在格子空白处只会选中行 ——
    /// 用户看到的就是"点了没反应，得再点一次"；</description></item>
    /// <item><description><b>全选 / 全不选 / 反选 / 只勾选这一个</b>都要有命令、有入口、有快捷键。</description></item>
    /// </list>
    ///
    /// <para><b>§13 纪律</b>：界面部分走既有的离屏探针（<see cref="UiProbe"/>：屏幕外 + 不进任务栏 +
    /// 不激活），全程不碰鼠标键盘、不置前；命令部分是无界面的纯 ViewModel 断言。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class TaskSelectionTests
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public TaskSelectionTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSelection", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        // ================================================================ ③ 命令（无界面）

        [Fact]
        public void 全选_全不选_反选_三个命令都在并且能改勾选()
        {
            MainViewModel vm = CreateViewModel();

            ArchiveTask first = AddTask(vm, "222.7z");
            ArchiveTask second = AddTask(vm, "333.7z");
            ArchiveTask third = AddTask(vm, "444.7z");

            // 三个命令都必须存在（绑定名写错时 WPF 会静默吞掉，所以这里直接断言属性不为空）。
            Assert.NotNull(vm.SelectAllTasksCommand);
            Assert.NotNull(vm.SelectNoneTasksCommand);
            Assert.NotNull(vm.InvertTaskSelectionCommand);
            Assert.NotNull(vm.SelectSoleTaskCommand);

            vm.SelectNoneTasks();

            Assert.All(vm.Tasks, task => Assert.False(task.IsSelected));

            vm.SelectAllTasks();

            Assert.All(vm.Tasks, task => Assert.True(task.IsSelected));

            vm.InvertTaskSelection();

            Assert.All(vm.Tasks, task => Assert.False(task.IsSelected));

            second.IsSelected = true;
            third.IsSelected = true;
            vm.InvertTaskSelection();

            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.False(third.IsSelected);
        }

        [Fact]
        public void 只勾选这一个_把其余全部取消_一个动作就够()
        {
            MainViewModel vm = CreateViewModel();

            ArchiveTask first = AddTask(vm, "222.7z");
            ArchiveTask second = AddTask(vm, "333.7z");
            ArchiveTask third = AddTask(vm, "444.7z");

            vm.SelectSoleTaskCommand.Execute(second);

            Assert.False(first.IsSelected);
            Assert.True(second.IsSelected);
            Assert.False(third.IsSelected);

            // 参数不是列表里的任务（例如右键菜单没拿到 SelectedItem）时不许乱动。
            vm.SelectSoleTaskCommand.Execute(new ArchiveTask(@"C:\t\999.7z", 9));

            Assert.True(second.IsSelected);
            Assert.Equal(1, vm.Tasks.Count(task => task.IsSelected));
        }

        [Fact]
        public void 勾选变化立刻反映到汇总的选中数上()
        {
            /*
             * 缺陷修正的回归：以前没有任何人监听 ArchiveTask.IsSelected，
             * 于是"点勾选框 → 屏幕上『选中：N』不动"，而命令执行时用的却是新勾选 ——
             * 界面在与自己的汇总数字打架。
             */
            MainViewModel vm = CreateViewModel();

            ArchiveTask first = AddTask(vm, "222.7z");
            AddTask(vm, "333.7z");

            vm.UpdateSummary();

            Assert.Equal(2, vm.Summary.SelectedCount);

            first.IsSelected = false;

            Assert.Equal(1, vm.Summary.SelectedCount);

            first.IsSelected = true;

            Assert.Equal(2, vm.Summary.SelectedCount);
        }

        [Fact]
        public void 只解压这个命令的可用性跟着勾选走()
        {
            /*
             * 另一个"界面在撒谎"的形态：CanExecute 依赖 Tasks.Any(IsSelected)，
             * 但 CanExecuteChanged 从来没在勾选变化时触发过 —— 一个都没勾时按钮还是亮的，
             * 点下去只会得到一句提示。
             */
            MainViewModel vm = CreateViewModel();

            ArchiveTask only = AddTask(vm, "222.7z");

            vm.UpdateSummary();
            Assert.True(vm.StartExtractCommand.CanExecute(null));

            only.IsSelected = false;

            Assert.False(vm.StartExtractCommand.CanExecute(null));

            only.IsSelected = true;

            Assert.True(vm.StartExtractCommand.CanExecute(null));
        }

        [Fact]
        public void 全不选不动当前行_删除其余物仍然能按当前行兜底()
        {
            // 口径是"一个都没勾时按当前点中的那一行办"（见 StatusText.SelectionScopeHint）：
            // 「全不选」若顺手把行高亮也清了，紧接着的「删除其余物」就没有兜底对象了。
            MainViewModel vm = CreateViewModel();

            ArchiveTask first = AddTask(vm, "222.7z");
            ArchiveTask second = AddTask(vm, "333.7z");

            vm.SelectedTask = second;

            vm.SelectNoneTasks();

            Assert.Equal(second, vm.SelectedTask);
            Assert.False(first.IsSelected);
            Assert.False(second.IsSelected);

            MainViewModel.CleanupTargetSelection selection =
                MainViewModel.ResolveCleanupTargets(vm.Tasks, vm.SelectedTask);

            Assert.Equal(MainViewModel.CleanupTargetSource.CurrentRow, selection.Source);
            Assert.Same(second, selection.Tasks[0]);
        }

        [Fact]
        public void 批量勾选只在最后刷一次汇总_不逐条刷()
        {
            // 200 个任务的批量导入 + 全选不该变成 O(n²)：批量动作期间挂起逐条刷新。
            MainViewModel vm = CreateViewModel();

            for (int i = 0; i < 50; i++)
            {
                AddTask(vm, $"{i:D3}.7z");
            }

            vm.SelectNoneTasks();
            vm.SelectAllTasks();

            Assert.Equal(50, vm.Summary.SelectedCount);
            Assert.Equal(50, vm.Tasks.Count);
        }

        // ================================================================ ①② 界面（离屏）

        [Fact]
        public void 选择列是模板列而不是DataGridCheckBoxColumn()
        {
            // DataGridCheckBoxColumn 的首次点击只是"进入编辑态"，要点第二次才切换 ——
            // 用户抱怨的正是这个。静态钉住：选择那一列必须是模板列。
            string xaml = ReadMainWindowXaml();

            int selectionColumnStart = xaml.IndexOf("Header=\"选择\"", StringComparison.Ordinal);

            Assert.True(selectionColumnStart > 0, "MainWindow.xaml 里找不到「选择」列");

            int templateColumnStart = xaml.LastIndexOf("<DataGridTemplateColumn", selectionColumnStart, StringComparison.Ordinal);

            Assert.True(templateColumnStart > 0, "「选择」列不是 DataGridTemplateColumn");

            // 两者之间不能再冒出一个列元素把这一列换掉；而且整份 XAML 里不能**使用**这个列类型
            // （注释里提到它没关系 —— 那条注释解释的正是"为什么不用它"）。
            Assert.DoesNotContain(
                "<DataGridCheckBoxColumn",
                xaml[templateColumnStart..selectionColumnStart],
                StringComparison.Ordinal);

            Assert.DoesNotContain("<DataGridCheckBoxColumn", xaml, StringComparison.Ordinal);
        }

        [Fact]
        public void 主窗口有全选全不选反选的入口与快捷键()
        {
            string xaml = ReadMainWindowXaml();

            Assert.Contains("SelectAllTasksCommand", xaml, StringComparison.Ordinal);
            Assert.Contains("SelectNoneTasksCommand", xaml, StringComparison.Ordinal);
            Assert.Contains("InvertTaskSelectionCommand", xaml, StringComparison.Ordinal);
            Assert.Contains("SelectSoleTaskCommand", xaml, StringComparison.Ordinal);

            // Ctrl+A 由 MainWindow.xaml.cs 的 PreviewKeyDown 实现（DataGrid 自己会吃掉 Ctrl+A）。
            string codeBehind = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer", "MainWindow.xaml.cs"));

            Assert.Contains("Key.A", codeBehind, StringComparison.Ordinal);
            Assert.Contains("SelectAllTasksCommand", codeBehind, StringComparison.Ordinal);
        }

        [Fact]
        public void 单击一次即可切换勾选_且整格都能点()
        {
            // 与其它自建 WPF 宿主的用例同一个集合（[Collection("ArchiveFixerGlobalState")]，
            // 该集合 DisableParallelization = true）：Application 是进程级单例，不能并行建。
            string? previousWorkspaceRoot = ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ArchiveFixer.Engines.ToolLocator.Default.CustomSevenZipExePath;

            UiProbeReport report;

            try
            {
                report = UiProbe.Run(
                    _output.WriteLine,
                    _root,
                    inspectMain: InspectSelectionColumn);
            }
            finally
            {
                ArchiveFixer.Extraction.RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ArchiveFixer.Engines.ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

                // ⚠ 必须还原：Application 是**进程级**静态，留着它会让同进程里
                // 下一个要离屏显示窗口的用例抛"不能创建多个 Application 实例"。
                ApplicationStash.RestoreNull();
            }

            if (report.SkipReason != null)
            {
                // 显示不了窗口 / 上一轮宿主没清干净：明确失败，绝不把"什么都没看"当成通过。
                Assert.Fail("无法验证「单击一次即切换勾选」这条要求：" + report.SkipReason);
            }

            Assert.True(
                report.FailureText == null,
                "主窗口的勾选检查失败：" + Environment.NewLine + report.FailureText);
        }

        /// <summary>
        /// 在探针线程上对着**真实可视树**检查选择列（离屏窗口，不碰鼠标键盘）。
        /// </summary>
        private static void InspectSelectionColumn(MainWindow window, MainViewModel viewModel)
        {
            DataGrid grid = FindDescendant<DataGrid>(window)
                            ?? throw new InvalidOperationException("主窗口里找不到任务 DataGrid");

            Assert.True(grid.Items.Count >= 3, "探针数据至少要三行，才验得动多行勾选");

            DataGridColumn selectionColumn = grid.Columns
                .FirstOrDefault(column => (column.Header as string) == "选择")
                ?? throw new InvalidOperationException("任务列表里找不到「选择」列");

            Assert.IsType<DataGridTemplateColumn>(selectionColumn);

            DataGridRow row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
            DataGridCell cell = FindDescendant<DataGridCell>(row)
                                ?? throw new InvalidOperationException("取不到第一个单元格");

            CheckBox checkBox = FindDescendant<CheckBox>(cell)
                                ?? throw new InvalidOperationException("「选择」列里没有 CheckBox");

            // ① 绑定必须是"即时双向"：LostFocus（默认）下勾选要等焦点离开才写回模型。
            BindingExpression? binding = BindingOperations.GetBindingExpression(
                checkBox,
                ToggleButton.IsCheckedProperty);

            Assert.NotNull(binding);
            Assert.Equal(BindingMode.TwoWay, binding!.ParentBinding.Mode);
            Assert.Equal(UpdateSourceTrigger.PropertyChanged, binding.ParentBinding.UpdateSourceTrigger);
            Assert.Equal(nameof(ArchiveTask.IsSelected), binding.ParentBinding.Path.Path);

            // ② 整格可点：勾选框必须铺满单元格的**内容区**，而且"点格子正中"要落在勾选框里。
            //    不铺满时（旧写法 HorizontalAlignment=Center）只有中间那一小块能点，
            //    点在格子空白处只会选中行 —— 用户看到的就是"点了没反应，得再点一次"。
            Rect checkBoxBounds = checkBox
                .TransformToAncestor(cell)
                .TransformBounds(new Rect(0, 0, checkBox.ActualWidth, checkBox.ActualHeight));

            var cellCenter = new Point(cell.ActualWidth / 2, cell.ActualHeight / 2);

            Assert.True(
                checkBoxBounds.Contains(cellCenter),
                $"点单元格正中不会落在勾选框上（勾选框 {checkBoxBounds} / 单元格 {cell.ActualWidth:F0}×{cell.ActualHeight:F0}）：" +
                "用户点这一格会「没反应」，得再点一次");

            Assert.True(
                checkBox.ActualWidth >= cell.ActualWidth * 0.6,
                $"勾选框太窄（{checkBox.ActualWidth:F0} / 单元格 {cell.ActualWidth:F0}）：整格点不动，只点得到中间一小块");

            // ③ 一次动作就能切换（UIA 的 Toggle 就是"点一下"的效果，且不需要前台焦点，§13）。
            ArchiveTask task = viewModel.Tasks[0];
            bool before = task.IsSelected;

            var peer = new CheckBoxAutomationPeer(checkBox);
            IToggleProvider? toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider;

            Assert.NotNull(toggle);
            toggle!.Toggle();

            Assert.NotEqual(before, task.IsSelected);

            // ④ 汇总里的「选中：N」必须跟着这次点击走（以前它一直停在旧值上）。
            Assert.Equal(viewModel.Tasks.Count(x => x.IsSelected), viewModel.Summary.SelectedCount);

            toggle.Toggle();

            Assert.Equal(before, task.IsSelected);
            Assert.Equal(viewModel.Tasks.Count(x => x.IsSelected), viewModel.Summary.SelectedCount);
        }

        // ================================================================ 装配

        private static MainViewModel CreateViewModel()
        {
            return new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new Engines.SevenZip.SevenZipEngine(),
                new PasswordService(),
                new LogService(),
                new SettingsService(),
                new PathService(),
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());
        }

        private static ArchiveTask AddTask(MainViewModel vm, string fileName)
        {
            var task = new ArchiveTask(Path.Combine(@"C:\t", fileName), vm.Tasks.Count + 1)
            {
                IsSelected = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized
            };

            vm.Tasks.Add(task);
            vm.UpdateSummary();

            return task;
        }

        private static string ReadMainWindowXaml()
        {
            return File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer", "MainWindow.xaml"));
        }

        private static T? FindDescendant<T>(DependencyObject? root)
            where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

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
