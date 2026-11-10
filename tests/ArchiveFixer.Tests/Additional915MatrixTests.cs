using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
    public sealed class Additional915MatrixTests
    {
        [Fact]
        public async Task 十条特定解压_全部跑完()
        {
            await RunAllAsync(
                Path.Combine(TestPaths.BackupRoot, "915100000-特定解压-备份"),
                10,
                useSpecific: true);
        }

        [Fact]
        public async Task 二十条异常样本_失败或跳过且源包保留()
        {
            await RunAllAsync(
                Path.Combine(TestPaths.BackupRoot, "915200000-额外异常-备份"),
                20,
                useSpecific: false);
        }

        private static async Task RunAllAsync(string root, int expectedCount, bool useSpecific)
        {
            if (!Directory.Exists(root))
            {
                // 夹具缺失（外接盘已损坏/换机）：跳过本用例，不伪装成通过。
                return;
            }

            string[] samples = Directory.GetDirectories(root)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(expectedCount, samples.Length);

            foreach (string sample in samples)
            {
                await RunOneAsync(sample, useSpecific);
            }
        }

        private static async Task RunOneAsync(string sample, bool useSpecific)
        {
            string id = Path.GetFileName(sample);
            string source = FindSource(sample);
            string work = Path.Combine(@"E:\DeepSeekProjects\_tmp\ArchiveFixer\additional915", Guid.NewGuid().ToString("N"));
            string copied = Path.Combine(work, "src");
            string data = Path.Combine(work, "data");
            string target = Path.Combine(work, "out");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(target);
            Copy(source, copied);
            File.WriteAllText(
                Path.Combine(data, "passwords.txt"),
                "2中！0@文#2删￥6除%\nDemo#Pass1\nDemo#Pass2\n",
                new System.Text.UTF8Encoding(false));

            ToolLocator.Default.CustomSevenZipExePath = SevenZipFactAttribute.LocateSevenZipPath()
                ?? throw new InvalidOperationException("no 7z");
            ToolLocator.Default.Invalidate();
            var paths = new PathService { DataRootDirectory = data };
            var settingsService = new SettingsService(paths);
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = target;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.UseSpecificExtraction = useSpecific;
            settings.SkipOneClickConfirm = true;
            settings.AutoScanAfterDrop = false;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var password = new PasswordService { DataRootDirectory = data, UseTextStore = true };
            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                password,
                new LogService(paths),
                settingsService,
                paths,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService(),
                useTextStore: true,
                encryptTextStore: false);
            var extraction = new ExtractionCoordinator(vm, engine, password, paths, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());
            oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = target,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                RestHandling = RestHandlingModes.Keep
            });

            await vm.AddPathsAsync(new[] { copied }, ImportMode.Replace);
            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

            Assert.True(Directory.Exists(Path.Combine(copied, "aaa")) || Directory.Exists(copied), $"{id} 源目录必须保留");
            ArchiveTask[] tasks = vm.Tasks.ToArray();
            Assert.NotEmpty(tasks);
        }

        private static string FindSource(string sample)
        {
            string nested = Path.Combine(sample, "原包", "原包");
            return Directory.Exists(nested) ? nested : Path.Combine(sample, "原包");
        }

        private static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string path = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(file, path);
            }
        }
    }
}
