using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **批首"缺卷"不许判死**（用户 2026-10-06 原话：
    /// 「我说了不要显示红色的分卷缺失，你还没有压倒最后就跳过，你是听不懂吗」）。
    ///
    /// <para>批首只记缺口 ⇒ 状态必须是**中性档**（`等待解压`），⛔ 不许是 `分卷缺失`
    /// （那个在红色映射与失败名单里，批中间亮出来就是"判死"）；"还缺哪几个"写在错误信息里，
    /// 信息一个字不少；最终结论留到批末那一站（仍在缺 ⇒ 跳过）。</para>
    ///
    /// <para><b>红检</b>：把 <c>RecordDeferredVolumeDeficit</c> 里那行
    /// <c>task.Status = StatusText.WaitingExtract;</c> 注掉 ⇒ 本条变红
    /// （状态会退回上一次的结论，在真机那一格就是红色的「分卷缺失」）。</para>
    /// </summary>
    public class DeferredVolumeStatusTests : IDisposable
    {
        private readonly string _root;

        /// <summary>最近一次 <see cref="CreateCoordinator"/> 建出来的 VM（要用它的任务表时读这个）。</summary>
        private MainViewModel _vm = null!;

        public DeferredVolumeStatusTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-deferred-status-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>批首记缺口 ⇒ 中性状态 + 缺口写在错误信息里 + ⛔ 不算失败。</summary>
        [Fact]
        public void 批首记缺口_状态必须是中性档_不许是红色的分卷缺失()
        {
            string path = Path.Combine(_root, "111(4)", "111.z03");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[2048]);

            var task = new ArchiveTask(path, 1);

            // 摆成"上一次的结论还挂在那一行上"（真机那一格：批中间那一行顶着红色的分卷缺失）。
            task.Status = StatusText.VolumeMissing;

            var coordinator = CreateCoordinator();

            coordinator.RecordDeferredVolumeDeficitForTests(task);

            Assert.Equal(StatusText.WaitingExtract, task.Status);
            Assert.False(TaskOutcomeClassifier.IsFailureStatus(task.Status), "批首记缺口不许算失败");
            Assert.Contains("不判死", task.ErrorMessage, StringComparison.Ordinal);

            // ⚠ 2026-10-08：进了名单就带上"待批末判"这个**显示/统计事实位**（⛔ 不是状态）。
            Assert.True(task.IsVolumeDeficitDeferred);
        }

        /// <summary>
        /// **递归中途撞上缺卷 ⇒ 批中间只许说"跳过"，批末才落真结论**（用户 2026-10-08 口径：
        /// 「没有到最后一步都是先跳过」「要留在最后检查才是真正的分卷缺失」）。
        ///
        /// <para>真机现场：两个 `111.zip`（跨盘 ZIP 末片）在递归里停在缺卷、一个字节都没产出 ⇒
        /// 老写法在**批中间**就把红色的「分卷缺失」摆在①页上、批末汇总还把它们算成「失败 2」。
        /// 而缺的那几片完全可能被同批别的包补上（真机 `111.z01` 就是这么补上的）。</para>
        ///
        /// <para>钉三件事：① 状态格说「跳过（缺卷，等批末再判）」；② 汇总按**跳过**数、不算失败；
        /// ③ ⛔ <c>Status</c> / <c>Outcome</c> 一个字不动（发布链 / 其余物 / 删除闸门读的就是它们），
        /// 批末清掉事实位之后立刻回到真结论。</para>
        ///
        /// <para><b>红检</b>：把 <c>ArchiveTask.StatusDisplayText</c> 里
        /// <c>IsVolumeDeficitDeferred &amp;&amp; Status == StatusText.VolumeMissing</c> 那一档撤掉
        /// ⇒ 本条变红（那一格又变回「分卷缺失」）。</para>
        /// </summary>
        [Fact]
        public void 递归中途撞上缺卷_批中间只许说跳过_批末才落真结论()
        {
            string path = Path.Combine(_root, "111", "111.zip");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[2048]);

            var task = new ArchiveTask(path, 1)
            {
                // 递归停在缺卷、一个字节都没产出 ⇒ 机器那一档就是"分卷缺失 / 失败"。
                Status = StatusText.VolumeMissing,
                Outcome = TaskOutcome.Failed
            };

            // 还没进名单：如实显示、算失败。
            Assert.Equal(StatusText.VolumeMissing, task.StatusDisplayText);
            Assert.True(BatchOutcomeTally.IsCountedAsFailure(task));

            // 进名单（批中间）⇒ 显示"跳过"、汇总按跳过数；⛔ 机器状态一个字节不动。
            task.IsVolumeDeficitDeferred = true;

            Assert.Equal(StatusText.VolumeDeficitPendingText, task.StatusDisplayText);
            Assert.False(BatchOutcomeTally.IsCountedAsFailure(task));

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new[] { task });

            Assert.Equal(1, tally.Skipped);
            Assert.Equal(0, tally.Failed);
            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Equal(TaskOutcome.Failed, task.Outcome);

            // 批末定稿清掉事实位 ⇒ 回到真结论（这一批确实到最后一刻都缺）。
            task.IsVolumeDeficitDeferred = false;

            Assert.Equal(StatusText.VolumeMissing, task.StatusDisplayText);
            Assert.Equal(1, BatchOutcomeTally.Count(new[] { task }).Failed);
        }

        /// <summary>
        /// **链尾那一趟才凑齐的那几单必须落结论，⛔ 不许留在 `Pending` / 「等待解压」**
        /// （用户 2026-10-09：「链尾 toRun 落结论、Pending 收口」）。
        ///
        /// <para><b>老写法</b>：`FinalizeDeferredVolumeDeficits` 把 `RecheckDeferredVolumeDeficits(finalPass: true)`
        /// 返回的那几单**只打一行日志就丢掉** ⇒ 它们机器终态一直停在 `Pending`、状态停在「等待解压」
        /// ⇒ 批末汇总按终态把它们算进「未处理」，而事实是"整组在链尾这一趟才凑齐、这一批轮次已经跑完"。</para>
        ///
        /// <para><b>钉三件事</b>：① 链尾那一站**真的把它们交回来**（返回值非空）；
        /// ② 它们的终态**不再是 `Pending`**；③ 状态格读的是**新的那一句**（不许再说"缺卷 / 分卷缺失"，
        /// 也不许说成"跟班卷"—— 这一组的完整性刚在这一趟判成"齐了"、内容没有任何一单解过）。</para>
        ///
        /// <para><b>红检</b>：把 `FinalizeDeferredVolumeDeficits` 里那个循环体（`Outcome = Skipped` +
        /// `MarkSkipped(...)` 两句）撤掉 ⇒ 本条的 ② ③ 当场变红。</para>
        /// </summary>
        [Fact]
        public void 链尾才凑齐的那一单_必须落结论_不许留在等待解压()
        {
            string layer = Path.Combine(_root, "111");

            Directory.CreateDirectory(layer);

            // 一份**完整**的跨盘 ZIP 组：末片 `111.zip` + 第 1/2 片 `111.z01`/`111.z02`（等大）。
            string tail = Path.Combine(layer, "111.zip");
            string disk1 = Path.Combine(layer, "111.z01");
            string disk2 = Path.Combine(layer, "111.z02");

            File.WriteAllBytes(tail, new byte[4096]);
            File.WriteAllBytes(disk1, new byte[4096]);
            File.WriteAllBytes(disk2, new byte[4096]);

            var task = new ArchiveTask(disk1, 1)
            {
                Status = StatusText.WaitingExtract,
                Outcome = TaskOutcome.Pending
            };

            var coordinator = CreateCoordinator();

            coordinator.RecordDeferredVolumeDeficitForTests(task);

            Assert.True(task.IsVolumeDeficitDeferred, "这一单应当先挂在「缺卷留到最后再判」那份名单上");

            // 链尾那一站（与真机里链尾调的是同一个入口）。
            System.Collections.Generic.IReadOnlyList<ArchiveTask> chainEndResolved =
                coordinator.FinalizeDeferredVolumeDeficits();

            Assert.Contains(task, chainEndResolved);

            // ② 终态落定（⛔ 不再停在 Pending）。
            Assert.NotEqual(TaskOutcome.Pending, task.Outcome);

            /*
             * ③ 状态格 = 「已跳过」，**理由写进错误信息那一格**（`MarkSkipped` 的口径，与"跟班卷"那一档逐行同一套：
             * 状态只放一句短的、理由进 `ErrorMessage`）。
             */
            Assert.Equal(StatusText.Skipped, task.Status);
            Assert.Contains(StatusText.VolumeDeficitChainEndNotRunText, task.ErrorMessage, StringComparison.Ordinal);

            /*
             * ④ ⛔ **不许把结论写成"缺卷 / 跟班卷"** —— 判据走**结构化事实**，⛔ 不拿中文文案当判据
             * （那一句里本来就含"不是缺卷"这四个字，比字符串必然误判）：
             *   · 缺卷那一档的机器终态是 `PartiallyCompleted`、且 `Status` 落在 `IsFailureStatus` 里 ⇒ 两者都必须不成立；
             *   · 跟班卷那一档会置 `IsVolumeGroupFollower` ⇒ 必须为 false（内容没有任何一单解过）。
             */
            Assert.Equal(TaskOutcome.Skipped, task.Outcome);
            Assert.NotEqual(TaskOutcome.PartiallyCompleted, task.Outcome);
            Assert.False(TaskOutcomeClassifier.IsFailureStatus(task.Status), "链尾才凑齐的那一单不是缺卷，不许进失败名单");
            Assert.False(task.IsVolumeGroupFollower, "内容没有任何一单解过，不许按跟班卷收场");

            // ⑤ 文案里必须给出下一步动作（用户点名的那句「再点一次就会解它」）。
            Assert.Contains("只解压", task.ErrorMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// **"未识别的东西"只许跳过 + 记账，⛔ 不许当场终结、也⛔ 不许被算成"未处理"**
        /// （用户口径：「分卷和其他**未识别的东西统统跳过**到了最后再重新检验一步」）。
        ///
        /// <para><b>老写法的病</b>：魔数不认、引擎也列不出来时那一支只写状态（「已跳过」）就 `return` ——
        /// **不写 `EndTime`、不写 `Outcome`**；而外层只有取消 / 异常那条路才补 `EndTime` ⇒
        /// 终态收口 `FinalizeOutcomeIfPending` 的判据（要求 `EndTime` 有值）不成立 ⇒
        /// 这一位永远是 `Pending` ⇒ ①页写着「已跳过」、批末汇总却按 `Pending` 把它算进**「未处理」**
        /// （同一件事两处口径打架）。</para>
        ///
        /// <para><b>红检</b>：把 `ExtractSingleTaskAsync` 里那一支新加的
        /// <c>task.Outcome = TaskOutcome.Skipped;</c> 撤掉 ⇒ 本条的终态断言当场变红（回到 `Pending`）。
        /// ⚠ 顺带钉住"⛔ 不能只补 `EndTime`"：只补 `EndTime` 的话终态收口会接手，
        /// 而它的 else 支按"不是缺卷 ⇒ `Failed`"落结论 ⇒ 一个"跳过"会变成**失败**。</para>
        /// </summary>
        [Fact]
        public async Task 未识别的东西_只跳过_机器终态必须收口不许算未处理()
        {
            string path = Path.Combine(_root, "notarchive", "text.7z");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "这不是压缩包。\n", new System.Text.UTF8Encoding(false));

            var task = new ArchiveTask(path, 1) { IsSelected = true };

            // 走扫描那一趟的唯一出口 ⇒ DetectedFormat 落 "Unknown"、IsArchive=false（真机那一帧）。
            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            ExtractionCoordinator coordinator = CreateCoordinator();

            _vm.Tasks.Add(task);

            await coordinator.StartExtractAsync();

            // ① 状态如实说"跳过"（中性档，⛔ 不是失败）。
            Assert.Equal(StatusText.Skipped, task.Status);

            // ② ⛔ 机器终态必须收口 —— 停在 Pending 就会被批末汇总算进「未处理」。
            Assert.NotEqual(TaskOutcome.Pending, task.Outcome);
            Assert.Equal(TaskOutcome.Skipped, task.Outcome);

            /*
             * ③ ⛔ 不许被算成失败（"只补 EndTime"那条错路就会落这一档）。
             *    判据走**结构化事实**，⛔ 不比中文文案。
             */
            Assert.False(TaskOutcomeClassifier.IsFailureStatus(task.Status), "未识别的东西只是跳过，不许进失败档");

            BatchOutcomeTally tally = BatchOutcomeTally.Count(new[] { task });

            Assert.Equal(1, tally.Skipped);
            Assert.Equal(0, tally.Failed);
            Assert.Equal(0, tally.Untouched);
        }

        /// <summary>
        /// **这一层被内层包自己那个文件占着 ⇒ 定稿⛔ 不许再套那一层**（2026-10-09 补的守门用例）。
        ///
        /// <para><b>缺陷</b>：包名被改坏、剥不出后缀时（`user.jp删除g`）包基名就等于文件名本身 ⇒
        /// `<落点>\user.jp删除g` 既是那个文件、又要当这一层的目录。**落点侧早就挡住了**
        /// （`OneClickCoordinator.ResolveContinuationOutputDirectory` 里那一句
        /// `IsOwnLayerOccupiedByPackageFile`），**定稿侧一次都没调** ⇒ 两侧判据分家 ⇒
        /// 定稿把每条内容物都往 `<落点>\user.jp删除g\inner.7z.001` 搬 ⇒ `EnsureDirectoryExists`
        /// 撞同名文件必然失败 ⇒ 整单报「定稿搬运失败 ……（无法创建目标目录）」
        /// （真机排障原文见 `InnerLayerContinuationTests.续解_下一层名字和文件头都是假的`）。</para>
        ///
        /// <para><b>红检</b>：撤掉 `PlanFinalLayoutForTask` 里 <c>|| ownLayerOccupied</c> 那一项
        /// ⇒ 计划里会出现 `<落点>\user.jp删除g\inner.7z.001` 这种"父路径是个文件"的搬运目标 ⇒ 本条变红。</para>
        /// </summary>
        [Fact]
        public void 定稿_这一层被内层包自己那个文件占着_不许再套那一层()
        {
            string stage = Path.Combine(_root, "stage-occupied");

            Directory.CreateDirectory(stage);

            /*
             * ⚠ 暂存目录里**必须有两个以上文件**：只要一个文件，定稿走的是「判定表 1：终端是单个文件，
             * 直接放进目标目录」⇒ **根本不会套包名那一层** ⇒ 这条用例就测不到该测的东西
             * （本轮实测踩到过：单个文件时撤掉修复也照样绿）。
             * ⛔ 也不要用分卷名：`engineOutput` 为 null 时它们必然触发分卷组完整性闸门 ⇒ 计划作废、测不到布局。
             */
            File.WriteAllBytes(Path.Combine(stage, "a.mp4"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(stage, "b.mp4"), new byte[4096]);

            string dest = Path.Combine(_root, "out-occupied");

            Directory.CreateDirectory(dest);

            /*
             * 内层包自己躺在落点那一层里，而且名字剥不出已知后缀
             * （`ResolveArchiveBaseName` 对这种名字给的就是文件名本身）。
             */
            string innerPackage = Path.Combine(dest, "user.jp删除g");

            File.WriteAllText(innerPackage, "x");

            var task = new ArchiveTask(innerPackage, 1);

            ExtractionCoordinator coordinator = CreateCoordinator();

            ExtractionCoordinator.FinalLayoutPlan plan = coordinator.PlanFinalLayoutForTask(
                task,
                stage,
                dest,
                sharedOutputRoot: false,
                TerminalLayoutMode.KeepLastFolder);

            Assert.False(plan.Failed, plan.FailureReason);
            Assert.NotEmpty(plan.Moves);

            // ⛔ 落点里不许出现 `<落点>\user.jp删除g\…` —— 那是一条"父路径是个文件"的路径，物理上建不出来。
            string occupiedPrefix = innerPackage + Path.DirectorySeparatorChar;

            Assert.DoesNotContain(
                plan.Moves,
                move => move.To.StartsWith(occupiedPrefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>无界面宿主里默认"没人点过 = 不确认"；这里注入"用户点了确定"（与真机那一档一致）。</summary>
        private sealed class ConfirmingDialogService : DialogService
        {
            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = optionCheckedByDefault;
                return true;
            }
        }

        private ExtractionCoordinator CreateCoordinator()
        {
            string dataRoot = Path.Combine(_root, "data");

            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);
            var logService = new LogService(pathService);
            _vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new ArchiveFixer.Engines.SevenZip.SevenZipEngine(),
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new ConfirmingDialogService());

            return new ExtractionCoordinator(
                _vm,
                new ArchiveFixer.Engines.SevenZip.SevenZipEngine(),
                new PasswordService(),
                pathService,
                new ConfirmingDialogService());
        }
    }
}
