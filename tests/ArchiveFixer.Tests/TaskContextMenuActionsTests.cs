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

        /// <summary>把模态框挡在测试里的假对话框（真弹窗会让用例卡死）。</summary>
        private sealed class RecordingDialogService : DialogService
        {
            public string LastInfo { get; private set; } = string.Empty;

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
