using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 小型跨盘分卷组真机形状：
    /// 源片在 E，目标在 H；空间不足模式成功 → 内容写 H、源片删除、
    /// 标准名入口落在 E 且同盘硬链接，H 上绝不出现源包副本。
    /// 环境变量 ARCHIVEFIXER_SPACE_TIGHT_CROSS=1 时才运行。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class SpaceTightCrossVolumeGroupTests : IDisposable
    {
        private readonly string _sourceRoot;
        private readonly string _targetRoot;
        private readonly string _dataRoot;

        public SpaceTightCrossVolumeGroupTests()
        {
            string id = Guid.NewGuid().ToString("N");
            _sourceRoot = Path.Combine(@"E:\DeepSeekProjects\_tmp\af-space-cross-group", id);
            _targetRoot = Path.Combine(@"H:\BaiduNetdiskDownload\困难大测试\_tmp-space-cross-group", id);
            _dataRoot = Path.Combine(@"H:\BaiduNetdiskDownload\困难大测试\_tmp-space-cross-data", id);
            Directory.CreateDirectory(_sourceRoot);
            Directory.CreateDirectory(_targetRoot);
            Directory.CreateDirectory(_dataRoot);
        }

        public void Dispose()
        {
            TryDelete(_sourceRoot);
            TryDelete(_targetRoot);
            TryDelete(_dataRoot);
        }

        [Fact]
        public async Task 跨盘分卷组_空间不足_内容进目标盘且不复制源包()
        {
            if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_SPACE_TIGHT_CROSS") != "1"
                || !Directory.Exists(@"H:\"))
            {
                return;
            }

            RecursionFixHarness.RequireSevenZip();

            string work = Path.Combine(_sourceRoot, "build");
            string sourceDir = Path.Combine(_sourceRoot, "原包");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(sourceDir);

            string content = Path.Combine(work, "content.bin");
            byte[] bytes = new byte[2 * 1024 * 1024];
            new Random(911).NextBytes(bytes);
            File.WriteAllBytes(content, bytes);

            string archive = Path.Combine(work, "cross.7z");
            RecursionFixHarness.Run7z(work, "a", "-t7z", "-mx=0", "-v1m", archive, "content.bin");

            string[] pieces = Directory.GetFiles(work, "cross.7z.*")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.True(pieces.Length >= 2, "夹具必须切出至少两卷");
            foreach (string piece in pieces)
            {
                File.Copy(piece, Path.Combine(sourceDir, Path.GetFileName(piece)));
            }

            string[] sourcePieces = Directory.GetFiles(sourceDir, "cross.7z.*");
            Assert.Equal(pieces.Length, sourcePieces.Length);

            var pathService = new PathService { DataRootDirectory = _dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.ExtractToOriginalDirectory = false;
            settings.CustomOutputDirectory = _targetRoot;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.ConcurrencyOverride = 2;
            settings.SkipOneClickConfirm = true;
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());
            oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown();

            extraction.SpaceProbeOverride = _ => 20L * 1024 * 1024;
            extraction.SpaceReserveOverride = 64 * 1024;
            vm.SpaceTightMode = true;
            vm.SpaceTightKeepSource = false;

            await vm.AddPathsAsync(new[] { sourceDir }, ImportMode.Replace);
            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(2));

            // 源片必须被回收（空间不足模式成功层）。
            string relevantLog = string.Join(
                Environment.NewLine,
                logService.Logs
                    .Select(item => item.DisplayText)
                    .Where(line => line.Contains("第二大步", StringComparison.Ordinal)
                                   || line.Contains("空间不足模式", StringComparison.Ordinal)
                                   || line.Contains("组分析", StringComparison.Ordinal)
                                   || line.Contains("源片", StringComparison.Ordinal)
                                   || line.Contains("源清单", StringComparison.Ordinal)
                                   || line.Contains("源包", StringComparison.Ordinal)
                                   || line.Contains("已立刻永久删除", StringComparison.Ordinal)
                                   || line.Contains("文件损坏", StringComparison.Ordinal))
                    .TakeLast(80));

            string sourceState = string.Join(
                "、",
                Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                    .Select(Path.GetFileName));

            string taskDump = string.Join(
                Environment.NewLine,
                vm.Tasks.Select(task =>
                    $"{task.FileName}｜Outcome={task.Outcome}｜Move={task.SourcePackageMove}｜"
                    + $"Group={task.IsVolumeGroup}/{task.IsVolumeGroupFollower}｜"
                    + $"Current={task.CurrentPath}｜Original={task.OriginalPath}｜"
                    + $"Volumes={task.VolumePaths.Count}｜Output={task.OutputPath}"));

            Assert.All(
                sourcePieces,
                piece => Assert.False(
                    File.Exists(piece),
                    $"源片未删除：{piece}；当前源目录：{sourceState}{Environment.NewLine}{taskDump}{Environment.NewLine}{relevantLog}"));

            // 目标盘只有内容物，没有跨盘复制出来的源包/group-entry。
            string[] targetFiles = Directory.GetFiles(_targetRoot, "*", SearchOption.AllDirectories);
            Assert.Contains(targetFiles, file => new FileInfo(file).Length == bytes.Length);
            Assert.DoesNotContain(targetFiles, file => file.EndsWith(".7z.001", StringComparison.OrdinalIgnoreCase)
                                                        || file.EndsWith(".7z.002", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(_targetRoot, ".ArchiveFixer.work", "group-entry")),
                "目标盘不应出现跨盘复制的 group-entry");

            // 源盘 group-entry 临时壳不能在结束后残留。
            Assert.False(Directory.Exists(Path.Combine(sourceDir, ".ArchiveFixer.work", "group-entry")),
                "源盘 group-entry 临时壳必须清理");
            Assert.Contains(
                logService.Logs.Select(item => item.DisplayText),
                line => line.Contains("空间不足模式", StringComparison.Ordinal));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
