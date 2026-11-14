using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 任务页右键菜单里「打开…」这一类命令：⛔ **点了必须有反应**
    /// （用户 2026-10-10 原话：「右键点打开输出目录没有，打开文件夹目录没用，什么东西都是没有用的」）。
    ///
    /// <para><b>现场（代码级，改动前）</b>：`MainViewModel.OpenTaskDirectory`（`:6022`）与
    /// `OpenTaskOutputDirectory`（`:6043`）把 `PathService.OpenDirectory` 的返回值**丢掉**
    /// —— 目录已经不在了（源包被搬进「其余物」、按删除档删掉、改名；输出目录被清理、被归集搬走）
    /// 时**什么都不发生、不弹窗、不写日志**，正是"界面在撒谎"的死点；而同一个菜单里的
    /// 「打开其余物目录」是正解（先查存在性再给一句提示），两处没对齐。</para>
    ///
    /// <para><b>红检</b>：把这两条命令改回"丢掉返回值"⇒ 前两条用例当场红（`ShowInfo` 与日志都空）；
    /// 把参数为空那一支改回 `return` ⇒ 第三条红。</para>
    /// </summary>
    public sealed class TaskContextMenuActionsTests : IDisposable
    {
        private readonly string _root;
        private readonly RecordingDialogService _dialogs = new();
        private readonly PathService _pathService;
        private readonly SettingsService _settingsService;
        private readonly MainViewModel _vm;

        public TaskContextMenuActionsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerMenuActions", Guid.NewGuid().ToString("N"));

            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);

            _pathService = new PathService { DataRootDirectory = dataRoot };
            _settingsService = new SettingsService(_pathService);
            _settingsService.Save(AppSettings.CreateDefault());

            var logService = new LogService(_pathService);

            _vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new SevenZipEngine(),
                new PasswordService { DataRootDirectory = dataRoot },
                logService,
                _settingsService,
                _pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                _dialogs);

            Logs = logService;
        }

        private LogService Logs { get; }

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
                // 临时目录删不掉不影响结论。
            }
        }

        /// <summary>源包所在的目录已经不在了（真机上就是"源包被搬进其余物 / 按档删掉"那一档）。</summary>
        [Fact]
        public void 打开源目录_目录已经不在_必须如实说一句而不是死点()
        {
            string missingDirectory = Path.Combine(_root, "已经被搬走的源目录");
            var task = new ArchiveTask(Path.Combine(missingDirectory, "111.rar"), 1) { FileName = "111.rar" };

            _vm.OpenTaskDirectoryCommand.Execute(task);

            Assert.False(string.IsNullOrWhiteSpace(_dialogs.LastInfo), "打不开目录时必须弹一句说明，⛔ 不许静默");
            Assert.Contains(missingDirectory, _dialogs.LastInfo, StringComparison.Ordinal);
            Assert.Contains(
                Logs.Logs,
                entry => entry.Level == "WARN" && entry.Message.Contains("打开源目录失败", StringComparison.Ordinal));
        }

        /// <summary>输出目录已经不在了（真机：定稿后整份清理 / 归集把产物搬走）。</summary>
        [Fact]
        public void 打开输出目录_目录已经不在_必须如实说一句而不是死点()
        {
            string missingOutput = Path.Combine(_root, "已经被清理的输出目录");
            var task = new ArchiveTask(Path.Combine(_root, "111.rar"), 1)
            {
                FileName = "111.rar",
                OutputPath = missingOutput
            };

            _vm.OpenTaskOutputDirectoryCommand.Execute(task);

            Assert.False(string.IsNullOrWhiteSpace(_dialogs.LastInfo), "打不开输出目录时必须弹一句说明，⛔ 不许静默");
            Assert.Contains(missingOutput, _dialogs.LastInfo, StringComparison.Ordinal);
            Assert.Contains(
                Logs.Logs,
                entry => entry.Level == "WARN" && entry.Message.Contains("打开输出目录失败", StringComparison.Ordinal));
        }

        /// <summary>还没有输出目录（没解压过）—— 那一支本来就有说明，这条钉住它别退化。</summary>
        [Fact]
        public void 打开输出目录_还没解压过_也要说一句()
        {
            var task = new ArchiveTask(Path.Combine(_root, "111.rar"), 1) { FileName = "111.rar" };

            _vm.OpenTaskOutputDirectoryCommand.Execute(task);

            Assert.False(string.IsNullOrWhiteSpace(_dialogs.LastInfo));
            Assert.Contains("还没有输出目录", _dialogs.LastInfo, StringComparison.Ordinal);
        }

        /// <summary>
        /// 右键落在**空白处 / 列头**时，菜单项拿到的参数是 null（`PlacementTarget.SelectedItem` 为空）
        /// —— ⛔ 这一档也**不许静默**（原来两条命令都是 `return` 了事）。
        /// </summary>
        [Theory]
        [InlineData("directory")]
        [InlineData("output")]
        public void 没有选中行时_点了也要说一句(string which)
        {
            if (which == "directory")
            {
                _vm.OpenTaskDirectoryCommand.Execute(null);
            }
            else
            {
                _vm.OpenTaskOutputDirectoryCommand.Execute(null);
            }

            Assert.False(string.IsNullOrWhiteSpace(_dialogs.LastInfo), "参数为空时必须说一句，⛔ 不许点了没反应");
            Assert.Contains("点中一行", _dialogs.LastInfo, StringComparison.Ordinal);
        }

        /// <summary>
        /// **行级命令一率补可用性判据 + 点了不许静默**（用户 2026-10-10：「右键点打开输出目录没有，
        /// 打开文件夹目录没用，什么东西都是没有用的」「所有的都要检查…要么就功能弄好来，要么就不要弄这个功能」）。
        ///
        /// <para>这几条原来是"永远可点"，实现在参数不是任务时直接 `return` ⇒ 右键落在空白处 / 列头时
        /// **什么都不发生**。现在两条腿都有：命令层 `IsRowInList` 判据（该灰就灰）+ 实现里一句说明。</para>
        /// </summary>
        [Fact]
        public async Task 行级命令_没选中行时是灰的_而且点了也有一句说明()
        {
            string file = Path.Combine(_root, "111.rar");
            File.WriteAllText(file, "桩");

            await _vm.AddPathsAsync(new[] { file });

            ArchiveTask task = Assert.Single(_vm.Tasks);

            // ① 有这一行 ⇒ 可点；参数为空 / 不是表里那一行 ⇒ 灰。
            Assert.True(_vm.RemoveTaskCommand.CanExecute(task));
            Assert.False(_vm.RemoveTaskCommand.CanExecute(null));
            Assert.False(_vm.RemoveTaskCommand.CanExecute(new ArchiveTask(file, 99)));

            System.Windows.Input.ICommand[] rowCommands =
            {
                _vm.RemoveTaskCommand,
                _vm.RescanTaskCommand,
                _vm.CopyTaskPathCommand,
                _vm.CopyTaskErrorCommand,
                _vm.CopyTaskInfoCommand
            };

            foreach (System.Windows.Input.ICommand command in rowCommands)
            {
                Assert.False(command.CanExecute(null), "没选中行时这些行级命令必须是灰的（原来是一律可点）");
            }

            // ② 万一还是被调到（别的入口 / 判据过期）：⛔ 不许静默，必须说一句。
            //
            // ⚠ 「重新扫描」那条**不放进这个循环**：`AsyncRelayCommand.Execute` 自己先看 `CanExecute`
            // （第 147-149 行）⇒ 灰了的命令根本不会被派到实现里，所以"点了有没有说明"对它不是可观测路径。
            // 它那第二道说明单独钉（下面直调协调器那一档）。
            foreach (System.Windows.Input.ICommand command in new[]
                     {
                         _vm.RemoveTaskCommand,
                         _vm.CopyTaskPathCommand,
                         _vm.CopyTaskErrorCommand,
                         _vm.CopyTaskInfoCommand
                     })
            {
                _dialogs.Clear();

                command.Execute(null);

                Assert.False(
                    string.IsNullOrWhiteSpace(_dialogs.LastInfo),
                    "没选中行时点了这些行级命令必须说一句，⛔ 不许静默");
            }

            // ②.5 「重新扫描」的第二道说明：直接调协调器那一层（跳过命令层的判据）。
            var scan = new ScanCoordinator(
                _vm,
                new FileScanService(),
                new ArchiveDetectService(),
                _dialogs);

            _dialogs.Clear();
            await scan.RescanTaskAsync(null);

            Assert.False(
                string.IsNullOrWhiteSpace(_dialogs.LastInfo),
                "「重新扫描此文件」在参数不是任务时也必须说一句，⛔ 不许静默");

            // ③ 列表里那一行还在（没有被那些空参数调用误删）。
            Assert.Single(_vm.Tasks);
        }

        /// <summary>把模态框挡在测试里的假对话框（真弹窗会让用例卡死）。</summary>
        private sealed class RecordingDialogService : DialogService
        {
            public string LastInfo { get; private set; } = string.Empty;

            public void Clear() => LastInfo = string.Empty;

            public override void ShowInfo(string message) => LastInfo = message;

            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = false;
                return false;
            }
        }
    }
}
