using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 性能测试专用无头夹具：只由 performance runner 通过环境变量驱动。
    /// 普通全量测试没有这些变量，直接返回，不产生样本、不写用户目录。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class PerformanceHarnessTests
    {
        [Fact(Timeout = 30 * 60 * 1000)]
        public async Task 性能夹具_按环境变量跑一轮()
        {
            string? sample = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_SAMPLE");
            string? output = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_OUTPUT");
            string? data = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_DATA");
            string? modeText = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_MODE");
            string? resultPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_RESULT");
            string? logPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_LOG");

            if (string.IsNullOrWhiteSpace(sample)
                || string.IsNullOrWhiteSpace(output)
                || string.IsNullOrWhiteSpace(data)
                || string.IsNullOrWhiteSpace(resultPath))
            {
                return;
            }

            int mode = 0;
            if (!string.IsNullOrWhiteSpace(modeText)
                && !string.Equals(modeText, "auto", StringComparison.OrdinalIgnoreCase))
            {
                mode = int.Parse(modeText, System.Globalization.CultureInfo.InvariantCulture);
            }

            Directory.CreateDirectory(output);
            Directory.CreateDirectory(data);

            var pathService = new PathService { DataRootDirectory = data };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = output;
            settings.ExtractToOriginalDirectory = false;
            settings.SourceHandling = "KeepInPlace";
            settings.RestHandlingAfterVerify = "Keep";
            settings.ConcurrencyOverride = mode;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService { DataRootDirectory = data };
            var logService = new LogService(pathService);
            var vm = new MainViewModel(
                new FileScanService(), new ArchiveDetectService(), new RenameService(), engine,
                passwordService, logService, settingsService, pathService,
                new TaskSummaryService(), new ClipboardService(), new DialogService());

            ToolLocator.Default.CustomSevenZipExePath = SevenZipFactAttribute.LocateSevenZipPath() ?? string.Empty;
            ToolLocator.Default.Invalidate();

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            var stopwatch = Stopwatch.StartNew();
            await vm.AddPathsAsync(new[] { sample }, ImportMode.Replace);
            foreach (ArchiveTask task in vm.Tasks)
            {
                task.IsSelected = true;
            }

            oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = output,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                RestHandling = RestHandlingModes.Keep
            });

            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(25));
            stopwatch.Stop();

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllLines(logPath, logService.Logs.Select(x => x.DisplayText));
            }

            string[] outputs = Directory.GetFiles(output, "*.bin", SearchOption.AllDirectories);
            var result = new
            {
                mode = string.IsNullOrWhiteSpace(modeText) ? "auto" : modeText,
                elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                tasks = vm.Tasks.Count,
                pending = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Pending),
                failed = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Failed),
                outputFiles = outputs.Length,
                outputBytes = outputs.Sum(f => new FileInfo(f).Length),
                log = logPath ?? string.Empty
            };

            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
