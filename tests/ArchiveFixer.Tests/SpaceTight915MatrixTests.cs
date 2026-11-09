using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Collection("ArchiveFixerGlobalState")]
    public sealed class SpaceTight915MatrixTests
    {
        private const string Root = @"H:\BaiduNetdiskDownload\困难大测试\大测试备份位置\915300000-空间不足-矩阵-备份";
        private const string SpecificRoot = @"H:\BaiduNetdiskDownload\困难大测试\大测试备份位置\915400000-特定解压-矩阵-备份";

        [Fact]
        public async Task 二十条空间不足_按recipe注入可用空间并验证结果()
        {
            string[] cases = Directory.GetDirectories(Root)
                .Concat(Directory.GetDirectories(SpecificRoot))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(20, cases.Length);

            foreach (string sample in cases)
            {
                await RunOneAsync(sample);
            }
        }

        private static async Task RunOneAsync(string sample)
        {
            string id = Path.GetFileName(sample);
            string recipePath = Path.Combine(sample, "recipe.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(recipePath));
            JsonElement root = document.RootElement;
            long freeBytes = root.GetProperty("space").GetProperty("freeBytes").GetInt64();
            long reserveBytes = root.GetProperty("space").GetProperty("reserveBytes").GetInt64();
            bool useSpecific = root.TryGetProperty("kind", out JsonElement kind)
                && string.Equals(kind.GetString(), "SpecificBatch", StringComparison.OrdinalIgnoreCase);
            string[] blocked = ReadNames(root, "blocked");
            string[] failed = ReadNames(root, "failed");

            string work = Path.Combine(@"E:\DeepSeekProjects\_tmp\ArchiveFixer\space915", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(work, "src");
            string target = Path.Combine(work, "out");
            string data = Path.Combine(work, "data");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(target);
            Copy(Path.Combine(sample, "原包"), source);

            var paths = new PathService { DataRootDirectory = data };
            var settingsService = new SettingsService(paths);
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = target;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.UseSpecificExtraction = useSpecific;
            settings.SkipOneClickConfirm = true;
            settings.AutoScanAfterDrop = false;
            settings.ConcurrencyOverride = 2;
            settingsService.Save(settings);

            ToolLocator.Default.CustomSevenZipExePath = SevenZipFactAttribute.LocateSevenZipPath()
                ?? throw new InvalidOperationException("no 7z");
            ToolLocator.Default.Invalidate();
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

            extraction.SpaceProbeOverride = _ => freeBytes;
            extraction.SpaceReserveOverride = reserveBytes;
            vm.SpaceTightMode = true;
            vm.SpaceTightKeepSource = false;

            oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = target,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                RestHandling = RestHandlingModes.Keep
            });

            await vm.AddPathsAsync(new[] { source }, ImportMode.Replace);
            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

            ArchiveTask[] tasks = vm.Tasks
                .Where(task => !task.IsContentGroupProcessTask)
                .ToArray();

            if (useSpecific)
            {
                Assert.NotEmpty(tasks);
                return;
            }

            foreach (string name in blocked)
            {
                ArchiveTask task = Find(tasks, name, id);
                Assert.True(
                    task.Status == StatusText.DiskSpaceInsufficient,
                    $"{id}/{name} 应为空间不足，实际 {task.Outcome}/{task.Status}");
            }

            foreach (string name in failed)
            {
                ArchiveTask task = Find(tasks, name, id);
                Assert.True(
                    task.Outcome is TaskOutcome.Failed or TaskOutcome.Skipped
                    || task.Status == StatusText.DiskSpaceInsufficient,
                    $"{id}/{name} 应失败/跳过，实际 {task.Outcome}/{task.Status}");
            }

            foreach (string name in blocked.Concat(failed))
            {
                Assert.True(
                    Directory.GetFiles(source, name, SearchOption.AllDirectories).Length > 0,
                    $"{id}/{name} 失败或空间不足时必须保留源包");
            }
        }

        private static ArchiveTask Find(ArchiveTask[] tasks, string name, string id)
        {
            ArchiveTask? task = tasks.FirstOrDefault(item =>
                string.Equals(item.FileName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(item.CurrentPath), name, StringComparison.OrdinalIgnoreCase));

            Assert.True(task != null, $"{id} 找不到任务 {name}");
            return task!;
        }

        private static string[] ReadNames(JsonElement root, string property)
        {
            if (!root.TryGetProperty("expected", out JsonElement expected)
                || !expected.TryGetProperty(property, out JsonElement value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return value.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToArray();
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

