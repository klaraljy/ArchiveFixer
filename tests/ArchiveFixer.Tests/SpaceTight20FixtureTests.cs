using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
using Xunit.Sdk;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 20 份空间不足夹具的真实管线验收（环境变量开关，默认不跑）。
    ///
    /// <para>数据由 `E:\AF\大测试-造空间不足包.ps1` 生成：
    /// 15 普通（N1–N5 各 3 份）+ 5 特定（S1–S5）。
    /// 空间通过 SpaceProbeOverride / SpaceReserveOverride 注入，不填真实 H 盘。</para>
    ///
    /// <para>运行：ARCHIVEFIXER_SPACE_TIGHT_20=1 dotnet test --filter ...</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class SpaceTight20FixtureTests
    {
        private static readonly string FixtureRoot = TestPaths.TestAreaRoot;
        private static readonly string ResultRoot = Path.Combine(TestPaths.FixtureRoot, "space-tight-20-results");
        private const long Reserve = 64 * 1024;

        [Fact]
        public async Task 空间不足二十份_按组跑完并检查源包与失败内容物()
        {
            if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_SPACE_TIGHT_20") != "1")
            {
                return;
            }

            Directory.CreateDirectory(ResultRoot);

            var groups = new[]
            {
                new Group("N1", "910300000", 2_200_000, new[] { "910300001", "910300002", "910300003" }),
                new Group("N2", "910300000", 3_800_000, new[] { "910300004", "910300005", "910300006" }),
                new Group("N3", "910300000", 1_200_000, new[] { "910300007", "910300008", "910300009" }),
                new Group("N4", "910300000", 200_000, new[] { "910300010", "910300011", "910300012" }),
                new Group("N5", "910300000", 1_600_000, new[] { "910300013", "910300014", "910300015" })
            };

            var results = new List<object>();

            foreach (Group group in groups)
            {
                results.Add(await RunGroupAsync(group, useSpecific: false));
            }

            results.Add(await RunSpecificGroupAsync());

            File.WriteAllText(
                Path.Combine(ResultRoot, "space-tight-20-results.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Assert.Equal(6, results.Count);
        }

        private static async Task<object> RunGroupAsync(Group group, bool useSpecific)
        {
            string sourceRoot = Path.Combine(ResultRoot, "src", group.Id);
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, recursive: true);
            }

            foreach (string sample in group.Samples)
            {
                string source = Path.Combine(FixtureRoot, group.BaseSeed, sample, "原包");
                string target = Path.Combine(sourceRoot, sample, "原包");
                CopyDirectory(source, target);
            }

            return await RunAsync(group.Id, group.FreeBytes, useSpecific, new[] { sourceRoot }, group.Samples);
        }

        private static async Task<object> RunSpecificGroupAsync()
        {
            string sourceRoot = Path.Combine(ResultRoot, "src", "specific");
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, recursive: true);
            }

            string[] samples = { "910400001", "910400002", "910400003", "910400004", "910400005" };
            foreach (string sample in samples)
            {
                string source = Path.Combine(FixtureRoot, "910400000", sample, "原包");
                string target = Path.Combine(sourceRoot, sample, "原包");
                CopyDirectory(source, target);
            }

            return await RunAsync("specific", 2_000_000, useSpecific: true, new[] { sourceRoot }, samples);
        }

        private static async Task<object> RunAsync(
            string kind,
            long freeBytes,
            bool useSpecific,
            string[] sourcePaths,
            string[] samples)
        {
            string dataRoot = Path.Combine(ResultRoot, "data", kind + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.ExtractToOriginalDirectory = true;
            settings.CustomOutputDirectory = string.Empty;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.UseSpecificExtraction = useSpecific;
            settings.SkipOneClickConfirm = true;
            settings.AutoScanAfterDrop = false;
            settings.ConcurrencyOverride = 2;
            settings.VerboseLog = true;
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

            extraction.SpaceProbeOverride = _ => freeBytes;
            extraction.SpaceReserveOverride = Reserve;
            vm.SpaceTightMode = true;
            vm.SpaceTightKeepSource = false;

            await vm.AddPathsAsync(sourcePaths, ImportMode.Replace);

            string runError = string.Empty;
            try
            {
                await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(10));
            }
            catch (Exception ex)
            {
                runError = ex.GetType().Name + ": " + ex.Message;
            }

            var rows = vm.Tasks.Select(task => new
            {
                task.FileName,
                task.CurrentPath,
                task.OutputPath,
                Outcome = task.Outcome.ToString(),
                task.Status,
                task.ErrorMessage,
                SourceExists = File.Exists(task.CurrentPath),
                OutputFiles = Directory.Exists(task.OutputPath)
                    ? Directory.GetFiles(task.OutputPath, "*", SearchOption.AllDirectories).Length
                    : 0
            }).ToArray();

            string[] logs = logService.Logs.Select(item => item.DisplayText).ToArray();
            string logPath = Path.Combine(ResultRoot, "logs", kind + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, logs, new UTF8Encoding(false));

            object result = new
            {
                kind,
                freeBytes,
                samples,
                runError,
                rows,
                logPath
            };

            Validate(kind, rows, runError);
            return result;
        }

        private static void Validate(string kind, object[] rows, string runError)
        {
            if (!string.IsNullOrWhiteSpace(runError))
            {
                throw new Xunit.Sdk.XunitException($"{kind} 运行异常：{runError}");
            }

            switch (kind)
            {
                case "N1":
                case "N2":
                    Assert.Equal(3, rows.Length);
                    Assert.All(rows, row =>
                    {
                        Assert.Equal(nameof(TaskOutcome.Succeeded), GetString(row, "Outcome"));
                        Assert.False(GetBool(row, "SourceExists"), "成功层必须回收源包");
                        Assert.True(GetInt(row, "OutputFiles") > 0, "成功层必须有内容物");
                    });
                    break;

                case "N3":
                    Assert.Equal(3, rows.Length);

                    object largest = Assert.Single(rows, row => GetString(row, "FileName").Contains("910300007", StringComparison.Ordinal));
                    Assert.Equal(nameof(TaskOutcome.Failed), GetString(largest, "Outcome"));
                    Assert.Equal(StatusText.DiskSpaceInsufficient, GetString(largest, "Status"));
                    Assert.True(GetBool(largest, "SourceExists"), "一开始放不下的最大包源包必须保留");

                    Assert.All(
                        rows.Where(row => !ReferenceEquals(row, largest)),
                        row =>
                        {
                            Assert.Equal(nameof(TaskOutcome.Succeeded), GetString(row, "Outcome"));
                            Assert.False(GetBool(row, "SourceExists"));
                        });
                    break;

                case "N4":
                    Assert.Equal(3, rows.Length);
                    Assert.All(rows, row =>
                    {
                        Assert.Equal(nameof(TaskOutcome.Failed), GetString(row, "Outcome"));
                        Assert.Equal(StatusText.DiskSpaceInsufficient, GetString(row, "Status"));
                        Assert.True(GetBool(row, "SourceExists"), "空间不足任务源包必须不动");
                    });
                    break;

                case "N5":
                    Assert.Equal(3, rows.Length);

                    object corrupt = Assert.Single(rows, row => GetString(row, "FileName").Contains("910300015", StringComparison.Ordinal));
                    Assert.Equal(nameof(TaskOutcome.Failed), GetString(corrupt, "Outcome"));
                    Assert.True(GetBool(corrupt, "SourceExists"), "失败任务源包必须留作最后过程物");
                    Assert.Equal(0, GetInt(corrupt, "OutputFiles"));

                    Assert.All(
                        rows.Where(row => !ReferenceEquals(row, corrupt)),
                        row =>
                        {
                            Assert.Equal(nameof(TaskOutcome.Succeeded), GetString(row, "Outcome"));
                            Assert.False(GetBool(row, "SourceExists"));
                        });
                    break;

                case "specific":
                    Assert.Equal(5, rows.Length);
                    Assert.All(rows, row =>
                    {
                        Assert.Equal(nameof(TaskOutcome.Succeeded), GetString(row, "Outcome"));
                        Assert.False(GetBool(row, "SourceExists"));
                        Assert.True(GetInt(row, "OutputFiles") >= 3, "特定包应解出 3 个相同文件");
                    });
                    break;
            }
        }

        private static string GetString(object row, string name) =>
            row.GetType().GetProperty(name)?.GetValue(row)?.ToString() ?? string.Empty;

        private static bool GetBool(object row, string name) =>
            row.GetType().GetProperty(name)?.GetValue(row) is bool value && value;

        private static int GetInt(object row, string name) =>
            row.GetType().GetProperty(name)?.GetValue(row) is int value ? value : 0;

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

        private sealed record Group(string Id, string BaseSeed, long FreeBytes, string[] Samples);
    }
}
