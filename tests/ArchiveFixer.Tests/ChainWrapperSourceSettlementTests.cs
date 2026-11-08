using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Collection("ArchiveFixerGlobalState")]
    public sealed class ChainWrapperSourceSettlementTests : IDisposable
    {
        private readonly string _root;

        public ChainWrapperSourceSettlementTests()
        {
            _root = Path.Combine(@"E:\DeepSeekProjects\_tmp\ArchiveFixer\chain-fix-test", Guid.NewGuid().ToString("N"));
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
            }
        }

        [Fact]
        public async Task Wrapper_sources_are_settled_after_group_content_is_verified()
        {
            const string backup =
                @"H:\BaiduNetdiskDownload\困难大测试\大测试备份位置\914000000-备份\914000014\原包";
            if (!Directory.Exists(backup))
            {
                return;
            }

            string sourceRoot = Path.Combine(_root, "src");
            CopyDirectory(backup, sourceRoot);

            string dataRoot = Path.Combine(_root, "data");
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(
                Path.Combine(dataRoot, "passwords.txt"),
                "2中！0@文#2删￥6除%\nDemo#Pass1\nDemo#Pass2\n",
                new UTF8Encoding(false));

            string outputRoot = Path.Combine(_root, "out");
            Directory.CreateDirectory(outputRoot);

            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath()
                              ?? throw new InvalidOperationException("没有 7z");
            ToolLocator.Default.CustomSevenZipExePath = sevenZip;
            ToolLocator.Default.Invalidate();

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ConcurrencyOverride = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwords = new PasswordService { DataRootDirectory = dataRoot, UseTextStore = true };
            var log = new LogService(pathService);
            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwords,
                log,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService(),
                useTextStore: true,
                encryptTextStore: false);

            var coordinator = new ExtractionCoordinator(vm, engine, passwords, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(
                new OneClickRunOptions
                {
                    PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                    CustomRoot = outputRoot,
                    SourceHandling = SourceHandlingMode.DeleteInPlace,
                    RestHandling = RestHandlingModes.Keep
                });

            await vm.AddPathsAsync(new[] { sourceRoot }, ImportMode.Replace);
            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

            string w1 = Path.Combine(sourceRoot, "aaa", "L4", "片-002", "aaa-W1-1.mp4");
            string w2 = Path.Combine(sourceRoot, "aaa", "L4", "片-003", "aaa-W2-2.mp4");

            Assert.True(File.Exists(Path.Combine(outputRoot, "914000014", "aaa", "L4", "片-001", "aaa-L4", "aaa-L3", "aaa-L0", "content-14.bin"))
                        || Directory.EnumerateFiles(outputRoot, "content-14.bin", SearchOption.AllDirectories).Any(),
                "最终内容物必须出来");
            Assert.False(File.Exists(w1), "上游包装 111-1 的源包应在组内容校验后被结算删除");
            Assert.False(File.Exists(w2), "上游包装 111-2 的源包应在组内容校验后被结算删除");
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }
}

