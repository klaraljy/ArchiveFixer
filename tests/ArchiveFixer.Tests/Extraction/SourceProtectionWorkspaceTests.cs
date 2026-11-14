using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// KeepInPlace 源文件保护快照只能落在本批工作区：
    /// 旧实现会拿源盘盘根拼 `<盘>\.ArchiveFixerSrcProtect`，C/E/H 根目录因此留下隐藏目录。
    /// 环境变量 ARCHIVEFIXER_SOURCE_PROTECT_TEST=1 时运行。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class SourceProtectionWorkspaceTests : IDisposable
    {
        private readonly string _sourceRoot;
        private readonly string _targetRoot;
        private readonly string _dataRoot;

        public SourceProtectionWorkspaceTests()
        {
            string id = Guid.NewGuid().ToString("N");
            _sourceRoot = Path.Combine(@"E:\DeepSeekProjects\_tmp\af-source-protect", id);
            _targetRoot = Path.Combine(TestPaths.TempRoot, "_tmp-source-protect", id);
            _dataRoot = Path.Combine(_targetRoot, "data");
            Directory.CreateDirectory(_sourceRoot);
            Directory.CreateDirectory(_targetRoot);
        }

        public void Dispose()
        {
            TryDelete(_sourceRoot);
            TryDelete(_targetRoot);
        }

        [Fact]
        public async Task 源文件保护_不创建盘根快照_只使用批工作区()
        {
            if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_SOURCE_PROTECT_TEST") != "1"
                || !Directory.Exists(@"H:\"))
            {
                return;
            }

            RecursionFixHarness.RequireSevenZip();

            string build = Path.Combine(_sourceRoot, "build");
            string source = Path.Combine(_sourceRoot, "原包");
            Directory.CreateDirectory(build);
            Directory.CreateDirectory(source);
            string payload = Path.Combine(build, "payload.bin");
            File.WriteAllBytes(payload, Enumerable.Repeat((byte)0x5A, 1300000).ToArray());
            RecursionFixHarness.Run7z(build, "a", "-t7z", "-mx=0", "-v1m", Path.Combine(build, "parts.7z"), "payload.bin");
            foreach (string piece in Directory.GetFiles(build, "parts.7z.*"))
            {
                File.Copy(piece, Path.Combine(source, Path.GetFileName(piece)));
            }

            string[] beforeE = SnapshotRoot(@"E:\.ArchiveFixerSrcProtect");
            string[] beforeH = SnapshotRoot(@"H:\.ArchiveFixerSrcProtect");

            var pathService = new PathService { DataRootDirectory = _dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.ExtractToOriginalDirectory = false;
            settings.CustomOutputDirectory = _targetRoot;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.SkipOneClickConfirm = true;
            settings.ConcurrencyOverride = 1;
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

            await vm.AddPathsAsync(new[] { source }, ImportMode.Replace);
            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(2));

            Assert.Equal(beforeE, SnapshotRoot(@"E:\.ArchiveFixerSrcProtect"));
            Assert.Equal(beforeH, SnapshotRoot(@"H:\.ArchiveFixerSrcProtect"));
        }

        private static string[] SnapshotRoot(string root) =>
            Directory.Exists(root)
                ? Directory.GetDirectories(root)
                    .Select(path => Path.GetFileName(path) ?? string.Empty)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();

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
