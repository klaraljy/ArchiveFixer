using System;
using System.IO;
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
            var vm = new MainViewModel(
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
                vm,
                new ArchiveFixer.Engines.SevenZip.SevenZipEngine(),
                new PasswordService(),
                pathService,
                new ConfirmingDialogService());
        }
    }
}
