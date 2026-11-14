using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 缺陷 2（2026-09-22 真机验收）：**改名预览的日志把"目标后缀"写错了**。
    ///
    /// <para><b>现场</b>：日志里是
    /// <c>生成改名预览：操作=FixByDetectedFormat，目标后缀=.7z，删除数量=1</c>，
    /// 而改名预览窗口里那一行明明是 <c>整包.mp4 → 整包.zip</c>（检测格式 ZIP、建议后缀 .zip）。
    /// 原因：日志打的是 <c>RenameOptions.TargetExtension</c>，也就是设置里的 <c>DefaultExtension</c>（.7z）——
    /// 它只是 <c>FixByDetectedFormat</c> 在"格式未知"时的兜底，正常路径根本不看它。
    /// 结果是排查的人一路去怀疑设置项，而真正的目标后缀就在预览里摆着。</para>
    ///
    /// <para><b>现在的口径</b>：日志打**真实目标**（从新文件名里取后缀），一条时顺手写出
    /// "谁改成谁"，多条不同目标就按项数汇总成一句。</para>
    ///
    /// <para>本组用例走的是**真链路**：真 <see cref="RenameService"/> 生成预览 +
    /// <see cref="RenameCoordinator.BuildPreviewAsync"/> 写日志，只是不开预览窗口
    /// （本进程没有 WPF 宿主）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RenamePreviewLogTests : IDisposable
    {
        private readonly string _root;

        public RenamePreviewLogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRenameLog", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 真链路：伪装 .mp4 → .zip

        [Fact]
        public async Task 智能修正的预览日志打的是识别出来的真实后缀_不是设置里的默认后缀()
        {
            Harness harness = CreateHarness(defaultExtension: ".7z");

            ArchiveTask task = AddMaskedZipTask(harness, "整包.mp4");

            RenameOptions options = SmartRenameOptions(harness);

            // 陷阱本身要先钉住：日志原来打的就是这个值（设置里的 .7z），而不是真实目标。
            Assert.Equal(".7z", options.TargetExtension);

            List<RenamePreviewItem>? items = await harness.Coordinator.BuildPreviewAsync(options);

            Assert.NotNull(items);
            Assert.Single(items!);
            Assert.Equal("整包.zip", items![0].NewFileName);

            string log = SinglePreviewLog(harness);

            Assert.Contains("操作=FixByDetectedFormat", log, StringComparison.Ordinal);

            // 真实目标：检测到 ZIP → .zip。
            Assert.Contains("目标后缀=.zip", log, StringComparison.Ordinal);

            // 一条时顺手写出"谁改成谁"：这正是真机上想看的那句话。
            Assert.Contains("整包.mp4 → 整包.zip", log, StringComparison.Ordinal);

            // 旧日志那一句必须消失（否则排查又会被带回设置项）。
            Assert.DoesNotContain(".7z", log, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 后缀本来就正常的任务_日志里也不会冒出设置里的默认后缀()
        {
            Harness harness = CreateHarness(defaultExtension: ".7z");

            ArchiveTask task = AddMaskedZipTask(harness, "已经是好的.zip");
            task.ExtensionStatus = StatusText.ExtensionNormal;

            List<RenamePreviewItem>? items = await harness.Coordinator.BuildPreviewAsync(SmartRenameOptions(harness));

            Assert.NotNull(items);
            Assert.Single(items!);

            // 后缀正常 → 预览里是"跳过"这一条，新文件名与原文件同名。
            Assert.Equal("已经是好的.zip", items![0].NewFileName);

            string log = SinglePreviewLog(harness);

            Assert.Contains("目标后缀=.zip", log, StringComparison.Ordinal);
            Assert.DoesNotContain(".7z", log, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 没有勾选任务时_预览日志不会写出来()
        {
            Harness harness = CreateHarness(defaultExtension: ".7z");

            ArchiveTask task = AddMaskedZipTask(harness, "整包.mp4");
            task.IsSelected = false;

            List<RenamePreviewItem>? items = await harness.Coordinator.BuildPreviewAsync(SmartRenameOptions(harness));

            Assert.Null(items);
            Assert.DoesNotContain(
                Snapshot(harness),
                line => line.Contains("生成改名预览", StringComparison.Ordinal));
        }

        // ================================================================ ② 纯函数：日志正文的四种形态

        [Fact]
        public void 多条不同目标后缀_按项数汇总成一句()
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".7z",
                DeleteExtensionCount = 1
            };

            var items = new List<RenamePreviewItem>
            {
                Item("整包.mp4", "整包.zip"),
                Item("另一个包.jpg", "另一个包.zip"),
                Item("旧包.bin", "旧包.rar")
            };

            string log = RenameCoordinator.BuildRenamePreviewLog(options, items);

            Assert.Equal(
                "生成改名预览：操作=FixByDetectedFormat，目标后缀=.zip（2 项）、.rar（1 项），删除数量=1",
                log);
        }

        [Fact]
        public void 删除后缀的操作_目标后缀写成无后缀而不是设置里的默认值()
        {
            var options = new RenameOptions
            {
                OperationType = "DeleteLastExtension",
                TargetExtension = string.Empty,
                DeleteExtensionCount = 1
            };

            var items = new List<RenamePreviewItem>
            {
                Item("test.rar.jpg", "test.rar"),
                Item("abc.7z.jpg", "abc")
            };

            string log = RenameCoordinator.BuildRenamePreviewLog(options, items);

            Assert.Equal(
                "生成改名预览：操作=DeleteLastExtension，目标后缀=.rar（1 项）、（无后缀）（1 项），删除数量=1",
                log);
        }

        [Fact]
        public void 一条预览项都没有时_日志如实说没有()
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".7z",
                DeleteExtensionCount = 1
            };

            string log = RenameCoordinator.BuildRenamePreviewLog(options, new List<RenamePreviewItem>());

            Assert.Equal("生成改名预览：操作=FixByDetectedFormat，没有生成任何预览项（删除数量=1）", log);
            Assert.DoesNotContain(".7z", log, StringComparison.Ordinal);
        }

        [Fact]
        public void 生不出新名字的那一条_写未知而不是拿原后缀冒充()
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".7z",
                DeleteExtensionCount = 1
            };

            var items = new List<RenamePreviewItem>
            {
                new RenamePreviewItem
                {
                    OriginalPath = @"C:\t\坏掉的.mp4",
                    OriginalFileName = "坏掉的.mp4",
                    NewFileName = string.Empty,
                    NewPath = string.Empty,
                    Status = StatusText.RenameCannot
                },
                Item("好的.mp4", "好的.zip")
            };

            string log = RenameCoordinator.BuildRenamePreviewLog(options, items);

            Assert.Contains("（未知）（1 项）", log, StringComparison.Ordinal);
            Assert.Contains(".zip（1 项）", log, StringComparison.Ordinal);
            Assert.DoesNotContain(".7z", log, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public required MainViewModel Vm { get; init; }

            public required RenameCoordinator Coordinator { get; init; }

            public required List<string> Logs { get; init; }

            public required string Root { get; init; }
        }

        private Harness CreateHarness(string defaultExtension)
        {
            string dataRoot = Path.Combine(_root, "data");
            string sourceRoot = Path.Combine(_root, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.AutoScanAfterDrop = false;
            settings.DefaultExtension = defaultExtension;
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
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var coordinator = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());

            return new Harness { Vm = vm, Coordinator = coordinator, Logs = logs, Root = _root };
        }

        /// <summary>
        /// 一个"后缀被改坏"的包：内容检测成 ZIP、建议后缀 .zip，但当前后缀是 .mp4
        /// （用户真机上的 <c>整包.mp4</c> 就是这个形态）。
        /// </summary>
        private ArchiveTask AddMaskedZipTask(Harness harness, string fileName)
        {
            string path = Path.Combine(harness.Root, "src", fileName);

            File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 });

            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1)
            {
                IsSelected = true,
                IsArchive = true,
                DetectedFormat = "ZIP",
                SuggestedExtension = ".zip",
                ExtensionStatus = StatusText.ExtensionMismatch,
                Status = StatusText.Recognized
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        private static RenameOptions SmartRenameOptions(Harness harness)
        {
            return new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = harness.Vm.Settings.DefaultExtension,
                DeleteExtensionCount = 1,
                ConflictAction = harness.Vm.Settings.ConflictAction,
                PreviewBeforeRename = true,
                UnknownFormatAction = harness.Vm.Settings.UnknownFormatAction
            };
        }

        private static RenamePreviewItem Item(string originalName, string newName)
        {
            return new RenamePreviewItem
            {
                OriginalPath = @"C:\t\" + originalName,
                OriginalFileName = originalName,
                NewFileName = newName,
                NewPath = @"C:\t\" + newName
            };
        }

        private static string SinglePreviewLog(Harness harness)
        {
            List<string> matches = Snapshot(harness)
                .Where(line => line.Contains("生成改名预览", StringComparison.Ordinal))
                .ToList();

            Assert.True(matches.Count == 1, $"期望恰好一条预览日志，实际 {matches.Count} 条");

            return matches[0];
        }

        private static List<string> Snapshot(Harness harness)
        {
            lock (harness.Logs)
            {
                return harness.Logs.ToList();
            }
        }
    }
}
