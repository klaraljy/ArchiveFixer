using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 缺陷 1（2026-09-22 真机验收）的回归：**"勾选"与"当前行"两种选中含义必须一致**。
    ///
    /// <para><b>现场</b>：任务列表上方那条蓝字写着「一键处理 / 智能修正 / 移除选中 / 清空列表」
    /// 都只作用于**已勾选**的任务，而三个清理入口（删除其余物 / 删除本目录全部其余物 / 清理空文件夹）
    /// 实际只看 DataGrid 的**当前行**：勾选框明明勾着（汇总区也写着"选中 1"），
    /// 点「工具 → 删除其余物…」弹出的却是"请先在列表里选中一个任务"，
    /// 日志还写着"没有选中任务，已取消" —— 把人往"是不是没勾上"的方向带。手动点中那一行它才工作。</para>
    ///
    /// <para><b>现在的语义</b>（<c>MainViewModel.ResolveCleanupTargets</c> 是唯一判定处）：
    /// 有勾选 → 作用于勾选的那些；一个都没勾 → 退化为当前点中的那一行；
    /// 两者都没有 → 提示"请先勾选或点中一个任务"（与蓝字同一套词，且**用了哪一种必须写进日志**）。</para>
    ///
    /// <para><b>多任务合并确认（用户 2026-09-22 追加拍板）</b>：勾了 N 个任务时
    /// **只弹一次确认**（以前每个任务各弹一次预览 + 一次确认，勾 5 个就是 5 次点击 ——
    /// 用户原话"要合并成 1 次确认，不要有冗余操作"）。所以下面的等待条件从
    /// "N 条『用户取消』"改成了"1 条『用户取消』+ 一个都没动"。</para>
    ///
    /// <para><b>安全</b>：删除执行器换成只记账、绝不碰磁盘的假件；而且本进程没有 WPF 宿主，
    /// 确认框一律降级成"未确认" → 流程停在删除之前。所以这一组用例**不会删掉任何东西**
    /// （连临时目录里的也不会），断言全部落在"作用于谁"和"说了什么"上。</para>
    ///
    /// <para>与 <c>ViewStateGuardTests</c> 同属"不与其他集合并行"的集合：构造 MainViewModel
    /// 会写两个进程级静态（7z 路径、递归工作区根目录），装配后立刻还原。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class CleanupSelectionScopeTests : IDisposable
    {
        private readonly string _root;

        public CleanupSelectionScopeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCleanupScope", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ ① 删除其余物

        [Fact]
        public async Task 删除其余物_以勾选为准_勾选两个只弹一次确认且两个都在范围里()
        {
            Harness harness = CreateHarness();

            AddOwnOutputTask(harness, "222", isSelected: true);
            AddOwnOutputTask(harness, "333", isSelected: true);
            ArchiveTask notChecked = AddOwnOutputTask(harness, "444", isSelected: false);

            // 当前行**故意**停在没勾选的那个上：必须以勾选为准，而不是被当前行带跑。
            harness.Vm.SelectedTask = notChecked;

            // ⚠ 相对计数：DialogService.FallbackLog 是**进程级**的，跨用例从不清空 ——
            // 用绝对值断言"只弹了一次"会被前面任何一条用例污染（全量跑才红）。
            int confirmDialogsBefore = CountFallbackEntries("ShowDestructiveConfirmWithOption");

            await RunCleanupAsync(harness, harness.Vm.CleanProcessArtifactsCommand);

            List<string> logs = Snapshot(harness);

            Assert.Contains(logs, line => line.Contains("以勾选为准", StringComparison.Ordinal)
                                          && line.Contains("2 个任务", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("（222.7z）", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("（333.7z）", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("444.7z", StringComparison.Ordinal));

            /*
             * 合并确认（用户 2026-09-22 的口径）：整批**只弹一次**确认框，
             * 而且那一次里要同时看得到两个任务各自的作用范围。
             */
            Assert.Equal(confirmDialogsBefore + 1, CountFallbackEntries("ShowDestructiveConfirmWithOption"));

            string confirm = LastFallbackEntry("ShowDestructiveConfirmWithOption");

            Assert.Contains("2 个任务", confirm, StringComparison.Ordinal);
            Assert.Contains("222.7z", confirm, StringComparison.Ordinal);
            Assert.Contains("333.7z", confirm, StringComparison.Ordinal);
            Assert.DoesNotContain("444.7z", confirm, StringComparison.Ordinal);

            // 没通过确认 → 一个字节都没动（假执行器连调用都不该收到）。
            Assert.Empty(harness.Executor.RecycleCalls);
            Assert.Empty(harness.Executor.PermanentCalls);
        }

        [Fact]
        public async Task 删除其余物_一个都没勾时退化为当前点中的那一行()
        {
            Harness harness = CreateHarness();

            ArchiveTask currentRow = AddOwnOutputTask(harness, "222", isSelected: false);
            AddOwnOutputTask(harness, "333", isSelected: false);

            harness.Vm.SelectedTask = currentRow;

            int confirmDialogsBefore = CountFallbackEntries("ShowDestructiveConfirmWithOption");

            await RunCleanupAsync(harness, harness.Vm.CleanProcessArtifactsCommand);

            List<string> logs = Snapshot(harness);

            Assert.Contains(logs, line => line.Contains("没有勾选任何任务，按当前点中的那一行处理", StringComparison.Ordinal)
                                          && line.Contains("222.7z", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("333.7z", StringComparison.Ordinal));

            // 单个任务时确认框仍然只有一次，而且格式与合并之前逐字节一致
            //（同样是相对计数：FallbackLog 是进程级共享的）。
            Assert.Equal(confirmDialogsBefore + 1, CountFallbackEntries("ShowDestructiveConfirmWithOption"));
            Assert.DoesNotContain(
                "本次只确认这一次",
                LastFallbackEntry("ShowDestructiveConfirmWithOption"),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task 删除其余物_两者都没有_提示文案与界面蓝字同一套词()
        {
            Harness harness = CreateHarness();

            AddOwnOutputTask(harness, "222", isSelected: false);
            harness.Vm.SelectedTask = null;

            DialogService.ClearFallbackLog();

            int confirmDialogsBefore = CountFallbackEntries("ShowDestructiveConfirmWithOption");

            await RunCleanupAsync(harness, harness.Vm.CleanProcessArtifactsCommand, expectConfirmDialog: false);

            List<string> logs = Snapshot(harness);

            Assert.Contains(logs, line => line.Contains("既没有勾选任务，也没有点中任何一行", StringComparison.Ordinal));

            string warning = LastFallbackEntry("ShowWarning");

            Assert.Contains("请先勾选或点中一个任务", warning, StringComparison.Ordinal);
            Assert.Contains("删除其余物", warning, StringComparison.Ordinal);

            // 旧文案"请先在列表里选中一个任务"会让人以为勾选没用 —— 它必须消失。
            Assert.DoesNotContain("请在列表里选中", warning, StringComparison.Ordinal);

            // 根本不该走到确认框（没有目标）。
            Assert.Equal(confirmDialogsBefore, CountFallbackEntries("ShowDestructiveConfirmWithOption"));

            Assert.Empty(harness.Executor.RecycleCalls);
        }

        // ================================================================ ② 删除本目录全部其余物

        [Fact]
        public async Task 删除本目录全部其余物_以勾选为准_同目录只算一次且点名影响几个包()
        {
            Harness harness = CreateHarness();

            ArchiveTask first = AddSharedOutputTask(harness, "222", isSelected: true);
            AddSharedOutputTask(harness, "333", isSelected: true);
            AddSharedOutputTask(harness, "444", isSelected: false);

            DialogService.ClearFallbackLog();

            await RunCleanupAsync(harness, harness.Vm.CleanAllProcessArtifactsCommand);

            List<string> logs = Snapshot(harness);

            // 勾了两个、但它们的输出目录是同一个 → "整个目录"这件事只问一次。
            Assert.Contains(logs, line => line.Contains("以勾选为准", StringComparison.Ordinal)
                                          && line.Contains("2 个任务", StringComparison.Ordinal));

            /*
             * 点名的必须是**目录里实际有几个包**（这里是 3 个，含没勾选的 444），
             * 不是"勾了几个"（2 个）—— 整目录语义会把 444 的那一份一起删掉，
             * 这正是用户点之前必须看见的那句话。
             */
            Assert.Contains(logs, line => line.Contains("会影响该目录下 3 个包", StringComparison.Ordinal));

            // 确认框正文里必须点名"影响几个包"（不是一句"会影响所有包"的形容词）。
            string confirm = LastFallbackEntry("ShowDestructiveConfirmWithOption");
            Assert.Contains("删除本目录全部其余物", confirm, StringComparison.Ordinal);
            Assert.Contains("3 个包", confirm, StringComparison.Ordinal);
            Assert.Contains("222", confirm, StringComparison.Ordinal);
            Assert.Contains("333", confirm, StringComparison.Ordinal);
            Assert.Contains("444", confirm, StringComparison.Ordinal);
        }

        [Fact]
        public void 删除本目录全部其余物_预览自己数得出影响几个包()
        {
            Harness harness = CreateHarness();

            ArchiveTask task = AddSharedOutputTask(harness, "222", isSelected: true);
            AddSharedOutputTask(harness, "333", isSelected: false);

            CleanupPreview preview = new MaintenanceCleanupService(harness.Executor)
                .PreviewProcessArtifacts(task, ArtifactDeleteScope.EverythingInDirectory);

            Assert.True(preview.HasTarget);
            Assert.True(preview.ResolvedScope!.DeletesEverythingInDirectory);
            Assert.Equal(2, preview.AffectedPackageCount);
            Assert.Equal(new[] { "222", "333" }, preview.AffectedPackageNames);

            // 只删自己那一份时不需要"影响几个包" —— 恒为 0，免得确认框里出现无意义的数字。
            CleanupPreview ownOnly = new MaintenanceCleanupService(harness.Executor)
                .PreviewProcessArtifacts(task, ArtifactDeleteScope.SelectedTask);

            Assert.Equal(0, ownOnly.AffectedPackageCount);
        }

        // ================================================================ ③ 清理空文件夹

        [Fact]
        public async Task 清理空文件夹_以勾选为准()
        {
            Harness harness = CreateHarness();

            ArchiveTask first = AddOwnOutputTask(harness, "222", isSelected: true);
            ArchiveTask second = AddOwnOutputTask(harness, "333", isSelected: true);
            ArchiveTask third = AddOwnOutputTask(harness, "444", isSelected: false);

            MakeEmptyShell(first);
            MakeEmptyShell(second);
            MakeEmptyShell(third);

            harness.Vm.SelectedTask = third;

            await RunCleanupAsync(harness, harness.Vm.CleanEmptyFoldersCommand);

            List<string> logs = Snapshot(harness);

            Assert.Contains(logs, line => line.Contains("清理空文件夹", StringComparison.Ordinal)
                                          && line.Contains("222.7z", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("清理空文件夹", StringComparison.Ordinal)
                                          && line.Contains("333.7z", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("444.7z", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 清理空文件夹_一个都没勾时退化为当前点中的那一行()
        {
            Harness harness = CreateHarness();

            ArchiveTask first = AddOwnOutputTask(harness, "222", isSelected: false);
            ArchiveTask second = AddOwnOutputTask(harness, "333", isSelected: false);

            MakeEmptyShell(first);
            MakeEmptyShell(second);

            harness.Vm.SelectedTask = second;

            await RunCleanupAsync(harness, harness.Vm.CleanEmptyFoldersCommand);

            List<string> logs = Snapshot(harness);

            Assert.Contains(logs, line => line.Contains("按当前点中的那一行处理", StringComparison.Ordinal)
                                          && line.Contains("333.7z", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("（222.7z）", StringComparison.Ordinal));
        }

        // ================================================================ ④ 移除选中 / 清空列表

        [Fact]
        public void 移除选中_勾选生效_没勾选退化为当前行_都没有才提示()
        {
            Harness harness = CreateHarness();

            ArchiveTask first = AddOwnOutputTask(harness, "222", isSelected: true);
            AddOwnOutputTask(harness, "333", isSelected: true);
            ArchiveTask third = AddOwnOutputTask(harness, "444", isSelected: false);

            // ① 勾选生效：当前行是没勾的那个，也只该移除勾选的两个。
            harness.Vm.SelectedTask = third;
            harness.Vm.RemoveSelectedCommand.Execute(null);

            Assert.Single(harness.Vm.Tasks);
            Assert.Same(third, harness.Vm.Tasks[0]);
            Assert.Contains(
                Snapshot(harness),
                line => line.Contains("移除选中", StringComparison.Ordinal)
                        && line.Contains("以勾选为准", StringComparison.Ordinal)
                        && line.Contains("2 个任务", StringComparison.Ordinal));

            // ② 一个都没勾 → 按当前点中的那一行办（以前是静默什么都不做）。
            harness.Vm.SelectedTask = third;
            harness.Vm.RemoveSelectedCommand.Execute(null);

            Assert.Empty(harness.Vm.Tasks);
            Assert.Contains(
                Snapshot(harness),
                line => line.Contains("按当前点中的那一行处理", StringComparison.Ordinal)
                        && line.Contains("444.7z", StringComparison.Ordinal));

            // ③ 都没有 → 提示（与清理类命令同一句话）。
            AddOwnOutputTask(harness, "555", isSelected: false);
            harness.Vm.SelectedTask = null;

            DialogService.ClearFallbackLog();
            harness.Vm.RemoveSelectedCommand.Execute(null);

            Assert.Single(harness.Vm.Tasks);

            string warning = LastFallbackEntry("ShowWarning");
            Assert.Contains("请先勾选或点中一个任务", warning, StringComparison.Ordinal);
            Assert.Contains("移除选中", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void 清空列表_是整表操作_确认框写明与勾选无关()
        {
            Harness harness = CreateHarness();

            AddOwnOutputTask(harness, "222", isSelected: true);
            AddOwnOutputTask(harness, "333", isSelected: false);

            DialogService.ClearFallbackLog();

            harness.Vm.ClearCommand.Execute(null);

            string confirm = LastFallbackEntry("ShowConfirm");

            Assert.Contains("整表操作", confirm, StringComparison.Ordinal);
            Assert.Contains("与勾选无关", confirm, StringComparison.Ordinal);
            Assert.Contains("2", confirm, StringComparison.Ordinal);
            Assert.Contains("移除选中", confirm, StringComparison.Ordinal);

            // 无 UI 宿主 → 确认降级为"没确认" → 一个任务都没被清掉（同一条红线）。
            Assert.Equal(2, harness.Vm.Tasks.Count);
        }

        [Fact]
        public void 清空列表在界面上不再被说成只作用于勾选()
        {
            // 蓝字是唯一的口径声明：它必须把"清空列表 = 整表操作"与勾选作用域分开说。
            Assert.Contains("清空列表", StatusText.SelectionScopeHint, StringComparison.Ordinal);
            Assert.Contains("整表操作", StatusText.SelectionScopeHint, StringComparison.Ordinal);
            Assert.Contains("勾选", StatusText.SelectionScopeHint, StringComparison.Ordinal);
            Assert.Contains("点中", StatusText.SelectionScopeHint, StringComparison.Ordinal);

            // 删除其余物 / 清理空文件夹 也在蓝字的勾选名单里（它们以前不在这句话的覆盖下）。
            Assert.Contains("删除其余物", StatusText.SelectionScopeHint, StringComparison.Ordinal);
            Assert.Contains("清理空文件夹", StatusText.SelectionScopeHint, StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 判定本身（纯函数）

        [Fact]
        public void 选中口径判定_勾选优先于当前行()
        {
            var checkedTask = new ArchiveTask(@"C:\t\222.7z", 1) { IsSelected = true };
            var notChecked = new ArchiveTask(@"C:\t\333.7z", 2) { IsSelected = false };

            MainViewModel.CleanupTargetSelection byCheckbox =
                MainViewModel.ResolveCleanupTargets(new[] { checkedTask, notChecked }, notChecked);

            Assert.Equal(MainViewModel.CleanupTargetSource.Checked, byCheckbox.Source);
            Assert.Equal(new[] { checkedTask }, byCheckbox.Tasks);

            MainViewModel.CleanupTargetSelection byRow =
                MainViewModel.ResolveCleanupTargets(new[] { notChecked }, notChecked);

            Assert.Equal(MainViewModel.CleanupTargetSource.CurrentRow, byRow.Source);
            Assert.Equal(new[] { notChecked }, byRow.Tasks);

            MainViewModel.CleanupTargetSelection none =
                MainViewModel.ResolveCleanupTargets(new[] { notChecked }, null);

            Assert.Equal(MainViewModel.CleanupTargetSource.None, none.Source);
            Assert.False(none.HasTarget);
        }

        [Fact]
        public void 当前行已经不在列表里时_不算数()
        {
            // 任务被移除之后 SelectedTask 可能还指着那个对象（界面会清成 null，命令层不能依赖它）。
            var removedTask = new ArchiveTask(@"C:\t\999.7z", 9) { IsSelected = false };
            var inList = new ArchiveTask(@"C:\t\222.7z", 1) { IsSelected = false };

            MainViewModel.CleanupTargetSelection selection =
                MainViewModel.ResolveCleanupTargets(new[] { inList }, removedTask);

            Assert.Equal(MainViewModel.CleanupTargetSource.None, selection.Source);
            Assert.False(selection.HasTarget);
        }

        [Fact]
        public void 每次命令都要在日志里写清用的是哪一种选中含义()
        {
            var task = new ArchiveTask(@"C:\t\222.7z", 1) { IsSelected = true };

            string byChecked = MainViewModel.DescribeCleanupTargets(
                "删除其余物",
                MainViewModel.ResolveCleanupTargets(new[] { task }, null));

            Assert.Contains("以勾选为准", byChecked, StringComparison.Ordinal);
            Assert.Contains("222.7z", byChecked, StringComparison.Ordinal);

            var rowTask = new ArchiveTask(@"C:\t\333.7z", 2) { IsSelected = false };

            string byRow = MainViewModel.DescribeCleanupTargets(
                "删除其余物",
                MainViewModel.ResolveCleanupTargets(new[] { rowTask }, rowTask));

            Assert.Contains("没有勾选任何任务", byRow, StringComparison.Ordinal);
            Assert.Contains("按当前点中的那一行处理", byRow, StringComparison.Ordinal);

            string nothing = MainViewModel.DescribeCleanupTargets(
                "删除其余物",
                MainViewModel.ResolveCleanupTargets(new[] { rowTask }, null));

            Assert.Contains("既没有勾选任务", nothing, StringComparison.Ordinal);
            Assert.Contains("已取消", nothing, StringComparison.Ordinal);
        }

        [Fact]
        public void 整目录那一档_同一个输出目录只处理一次()
        {
            var first = new ArchiveTask(@"C:\t\222.7z", 1) { IsSelected = true, OutputPath = @"C:\t\out" };
            var second = new ArchiveTask(@"C:\t\333.7z", 2) { IsSelected = true, OutputPath = @"C:\t\out" };
            var third = new ArchiveTask(@"C:\t\444.7z", 3) { IsSelected = true, OutputPath = @"C:\t\other" };

            MainViewModel.CleanupTargetSelection selection =
                MainViewModel.ResolveCleanupTargets(new[] { first, second, third }, null);

            // 整目录语义：同目录只算一次（其余两个包本来也会被这一次删掉）。
            Assert.Equal(2, MainViewModel.ExpandCleanupTargets(selection, everythingInDirectory: true).Count);

            // 其余两档是"每个任务各删自己那一份"，一个都不能少。
            Assert.Equal(3, MainViewModel.ExpandCleanupTargets(selection, everythingInDirectory: false).Count);
        }

        // ================================================================ ⑥ 合并确认（用户 2026-09-22 拍板）

        [Fact]
        public void 多任务合并成一次确认_正文逐个列出作用范围与合计()
        {
            var first = new ArchiveTask(@"C:\t\222.7z", 1) { OutputPath = @"C:\t\out\222" };
            var second = new ArchiveTask(@"C:\t\333.7z", 2) { OutputPath = @"C:\t\out\333" };

            var plans = new List<MainViewModel.CleanupTaskPlan>
            {
                new()
                {
                    Task = first,
                    Preview = new CleanupPreview
                    {
                        Scope = CleanupScope.Artifacts,
                        ScopePath = @"C:\t\out\222\其余物",
                        HasTarget = true,
                        ItemCount = 2,
                        EntryCount = 5,
                        TotalBytes = 1000,
                        Determined = true,
                        SourcePackageCount = 1,
                        SourcePackageNames = new[] { "222.7z" }
                    }
                },
                new()
                {
                    Task = second,
                    Preview = new CleanupPreview
                    {
                        Scope = CleanupScope.Artifacts,
                        ScopePath = @"C:\t\out\333\其余物",
                        HasTarget = true,
                        ItemCount = 3,
                        EntryCount = 7,
                        TotalBytes = 2000,
                        Determined = true
                    }
                }
            };

            CleanupPreview merged = MainViewModel.MergeCleanupPreviews(CleanupScope.Artifacts, plans);

            // 合计：条目数与字节数相加；作用范围不止一个目录时不再假装是一个。
            Assert.Equal(5, merged.ItemCount);
            Assert.Equal(12, merged.EntryCount);
            Assert.Equal(3000, merged.TotalBytes);
            Assert.Equal(1, merged.SourcePackageCount);

            string text = MainViewModel.BuildCleanupConfirmText(
                "删除其余物",
                plans,
                merged,
                DeleteMode.RecycleBin);

            // 一次性说清：哪几个任务、各自删哪个目录、合计多少、谁含源包（删了要重新下载）。
            Assert.Contains("只确认这一次", text, StringComparison.Ordinal);
            Assert.Contains("2 个任务", text, StringComparison.Ordinal);
            Assert.Contains("222.7z", text, StringComparison.Ordinal);
            Assert.Contains(@"C:\t\out\222\其余物", text, StringComparison.Ordinal);
            Assert.Contains("333.7z", text, StringComparison.Ordinal);
            Assert.Contains(@"C:\t\out\333\其余物", text, StringComparison.Ordinal);
            Assert.Contains("顶层 5 项", text, StringComparison.Ordinal);
            Assert.Contains("12 个条目", text, StringComparison.Ordinal);
            Assert.Contains("3000 字节", text, StringComparison.Ordinal);
            Assert.Contains("需要重新下载", text, StringComparison.Ordinal);

            // 单个任务时保持旧格式（不多出一段清单）—— 老用户看到的字不该因为批处理而变。
            string single = MainViewModel.BuildCleanupConfirmText(
                "删除其余物",
                new[] { plans[0] },
                plans[0].Preview!,
                DeleteMode.RecycleBin);

            Assert.DoesNotContain("只确认这一次", single, StringComparison.Ordinal);
            Assert.Contains("作用范围：", single, StringComparison.Ordinal);
        }

        [Fact]
        public void 整批里单个任务失败_汇总如实分列成功与失败且不说全部成功()
        {
            var first = new ArchiveTask(@"C:\t\222.7z", 1);
            var second = new ArchiveTask(@"C:\t\333.7z", 2);

            var tally = new MainViewModel.BatchCleanupTally();

            tally.Record(first, new CleanupOutcome
            {
                Attempted = true,
                SuccessCount = 3,
                FailureCount = 0,
                FreedBytes = 0,
                RecycledBytes = 4096,
                Message = "移入回收站成功 3 项"
            });

            tally.Record(second, new CleanupOutcome
            {
                Attempted = true,
                SuccessCount = 1,
                FailureCount = 2,
                FailureReasons = new[] { "文件被占用", "权限不足" },
                Message = "移入回收站成功 1 项，失败 2 项"
            });

            // 有一项没删掉 → 整批不得显示成"全部成功"。
            Assert.True(tally.HasAnyFailure);
            Assert.Equal(1, tally.SucceededTaskCount);
            Assert.Equal(1, tally.FailedTaskCount);
            Assert.Equal(2, tally.FailedItemCount);

            string summary = MainViewModel.BuildBatchCleanupSummary("删除其余物", DeleteMode.RecycleBin, 2, tally);

            Assert.Contains("成功 1 个", summary, StringComparison.Ordinal);
            Assert.Contains("失败 1 个", summary, StringComparison.Ordinal);
            Assert.Contains("文件被占用", summary, StringComparison.Ordinal);
            Assert.Contains("333.7z", summary, StringComparison.Ordinal);

            // 只跑一个任务成功时：没有失败就不列"没删掉的原因"。
            var clean = new MainViewModel.BatchCleanupTally();
            clean.Record(first, new CleanupOutcome { Attempted = true, SuccessCount = 3, Message = "OK" });

            Assert.False(clean.HasAnyFailure);
            Assert.DoesNotContain(
                "没删掉的原因",
                MainViewModel.BuildBatchCleanupSummary("删除其余物", DeleteMode.RecycleBin, 1, clean),
                StringComparison.Ordinal);
        }

        [Fact]
        public void 没有可删的目标时_合并成一条说明而不是每个任务弹一次()
        {
            var first = new ArchiveTask(@"C:\t\222.7z", 1);
            var second = new ArchiveTask(@"C:\t\333.7z", 2);

            var plans = new List<MainViewModel.CleanupTaskPlan>
            {
                new() { Task = first, SkipReason = "还没有输出目录（先解压一次）" },
                new() { Task = second, SkipReason = "其余物目录里没有可删的东西" }
            };

            string text = MainViewModel.BuildNothingToCleanText("删除其余物", plans);

            Assert.Contains("2 个任务都没有可删的东西", text, StringComparison.Ordinal);
            Assert.Contains("222.7z", text, StringComparison.Ordinal);
            Assert.Contains("333.7z", text, StringComparison.Ordinal);
            Assert.Contains("先解压一次", text, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public required MainViewModel Vm { get; init; }

            public required List<string> Logs { get; init; }

            public required RecordingDeleteExecutor Executor { get; init; }

            public required string Root { get; init; }
        }

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            var logService = new LogService(pathService);
            var logs = new List<string>();

            logService.LogAdded += (_, item) =>
            {
                lock (logs)
                {
                    logs.Add($"[{item.Level}] {item.Message}");
                }
            };

            var executor = new RecordingDeleteExecutor();

            // MainViewModel 的构造会写这两个进程级静态 —— 装配后立刻还原（同 ViewStateGuardTests）。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new SevenZipEngine(),
                new PasswordService { DataRootDirectory = dataRoot },
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService(),
                new MaintenanceCleanupService(executor));

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            return new Harness { Vm = vm, Logs = logs, Executor = executor, Root = _root };
        }

        /// <summary>普通布局：任务有自己的输出目录，其余物在它自己的目录里。</summary>
        private ArchiveTask AddOwnOutputTask(Harness harness, string baseName, bool isSelected)
        {
            string sourceDirectory = Path.Combine(harness.Root, "src");
            string outputDirectory = Path.Combine(harness.Root, "out", baseName);

            ArchiveTask task = CreateTask(harness, sourceDirectory, baseName, outputDirectory, isSelected);

            string artifactDirectory = Path.Combine(outputDirectory, MaintenanceCleanupService.ArtifactDirectoryName);
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(Path.Combine(artifactDirectory, "inner.7z"), "中间产物（测试用，不会真的被删）");

            return task;
        }

        /// <summary>
        /// 共享输出布局（模式 B / 指定位置直接放）：多个包的输出目录就是同一个源目录，
        /// 其余物按包基名再分一层（<c>&lt;共享根&gt;\其余物\&lt;包基名&gt;\</c>）。
        /// </summary>
        private ArchiveTask AddSharedOutputTask(Harness harness, string baseName, bool isSelected)
        {
            string sourceDirectory = Path.Combine(harness.Root, "src");

            ArchiveTask task = CreateTask(harness, sourceDirectory, baseName, sourceDirectory, isSelected);

            string artifactDirectory = Path.Combine(
                sourceDirectory,
                MaintenanceCleanupService.ArtifactDirectoryName,
                baseName);

            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllText(Path.Combine(artifactDirectory, "inner.7z"), "中间产物（测试用，不会真的被删）");

            // 源包本身也在其余物里（用户 2026-09-22 的布局），确认框要能认出来。
            File.WriteAllText(Path.Combine(sourceDirectory, baseName + ".7z"), "源包（测试用）");

            return task;
        }

        private ArchiveTask CreateTask(
            Harness harness,
            string sourceDirectory,
            string baseName,
            string outputDirectory,
            bool isSelected)
        {
            Directory.CreateDirectory(sourceDirectory);

            string sourcePath = Path.Combine(sourceDirectory, baseName + ".7z");

            if (!File.Exists(sourcePath))
            {
                File.WriteAllText(sourcePath, "源包（测试用）");
            }

            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsSelected = isSelected,
                OutputPath = outputDirectory,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        /// <summary>在任务的输出目录里造一棵"任意层级都没有文件"的空目录树（清理空文件夹的候选）。</summary>
        private static void MakeEmptyShell(ArchiveTask task)
        {
            string shell = Path.Combine(task.OutputPath, "空壳", "里面还是空的");
            Directory.CreateDirectory(shell);
        }

        /// <summary>
        /// 跑一次清理命令并等它结束。
        ///
        /// <c>AsyncRelayCommand.Execute</c> 是 <c>async void</c>，拿不到 Task，所以按日志等：
        /// 走到（唯一那一次）确认框时就会留一条"用户取消"（本进程没有 WPF 宿主，确认一律降级为未确认）。
        ///
        /// <para>
        /// ⚠ 多任务现在是**一次**确认，所以这里不再按"处理了几个任务"计数 ——
        /// 改成等"出现了取消日志 + 不再忙"。<paramref name="expectConfirmDialog"/> 为 false 时
        /// 表示这次根本不该走到确认框（没有可删的目标），只等"不忙 + 有一条结果日志"。
        /// </para>
        /// </summary>
        private static async Task RunCleanupAsync(
            Harness harness,
            System.Windows.Input.ICommand command,
            bool expectConfirmDialog = true)
        {
            Assert.True(command.CanExecute(null), "命令在空闲状态下必须可点");

            command.Execute(null);

            await WaitUntilAsync(
                () =>
                {
                    if (harness.Vm.IsBusy)
                    {
                        return false;
                    }

                    if (!expectConfirmDialog)
                    {
                        // 没有目标时：应当出现"请先勾选或点中"或"都没有可删的"这一类结论。
                        return CountFallbackEntries("ShowWarning") + CountFallbackEntries("ShowInfo") > 0;
                    }

                    List<string> logs = Snapshot(harness);

                    return logs.Any(line => line.Contains("用户取消", StringComparison.Ordinal));
                },
                TimeSpan.FromSeconds(10));
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();

            while (watch.Elapsed < timeout)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"等待条件超时（{timeout.TotalSeconds:F0} 秒）");
        }

        private static List<string> Snapshot(Harness harness)
        {
            lock (harness.Logs)
            {
                return harness.Logs.ToList();
            }
        }

        /// <summary>某个对话框类型的降级记录条数（"只弹了一次"这类断言就靠它）。</summary>
        private static int CountFallbackEntries(string context)
        {
            return DialogService.FallbackLog.Count(item => item.Contains("[" + context + "]", StringComparison.Ordinal));
        }

        /// <summary>取最近一条某个对话框的降级记录（无 UI 宿主时，提示正文就落在那里）。</summary>
        private static string LastFallbackEntry(string context)
        {
            string? entry = DialogService.FallbackLog
                .LastOrDefault(item => item.Contains("[" + context + "]", StringComparison.Ordinal));

            Assert.False(entry == null, $"没有找到 {context} 的降级记录（无 UI 宿主时提示应当落在那里）");

            return entry!;
        }

        /// <summary>只记账、**绝不删任何东西**的执行器（本组用例的安全性就靠它）。</summary>
        private sealed class RecordingDeleteExecutor : IDeleteExecutor
        {
            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = "测试执行器：只记账，不删东西";
                return RecycleAttemptResult.Failed;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);
            }
        }
    }
}
