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
using System.Runtime.InteropServices;
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
        [Fact(Timeout = 60 * 60 * 1000)]
        public async Task 性能夹具_按环境变量跑一轮()
        {
            string? sample = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_SAMPLE");
            string? output = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_OUTPUT");
            string? data = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_DATA");
            string? modeText = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_MODE");
            string? resultPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_RESULT");
            string? logPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_LOG");
            string? password = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_PASSWORD");
            string? pristineSource = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_SOURCE");

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
            // 性能样本故意做高压缩比（compressible/encrypted）；这是测试夹具，不是产品默认安全值。
            settings.MaxExtractionRatio = 100_000d;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService { DataRootDirectory = data };
            if (!string.IsNullOrEmpty(password))
            {
                passwordService.Passwords.Add(new PasswordItem { Value = password, Source = "Performance" });
            }
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
            oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = output,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                RestHandling = RestHandlingModes.Keep
            });

            int soakSeconds = 0;
            string? soakText = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_SOAK_SECONDS");
            if (!string.IsNullOrWhiteSpace(soakText))
            {
                soakSeconds = int.Parse(soakText, System.Globalization.CultureInfo.InvariantCulture);
            }

            int iterations = 0;
            long peakWorkingSet = 0;
            do
            {
                if (iterations > 0 && Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }
                Directory.CreateDirectory(output);

                string batchInput = sample;
                if (soakSeconds > 0
                    && !string.IsNullOrWhiteSpace(pristineSource)
                    && Directory.Exists(pristineSource))
                {
                    batchInput = Path.Combine(Path.GetDirectoryName(sample)!, "input-" + iterations);
                    LinkArchives(pristineSource, batchInput);
                }

                await vm.AddPathsAsync(new[] { batchInput }, ImportMode.Replace);
                foreach (ArchiveTask task in vm.Tasks)
                {
                    task.IsSelected = true;
                }

                await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(25));
                iterations++;
                peakWorkingSet = Math.Max(peakWorkingSet, Process.GetCurrentProcess().WorkingSet64);

                if (!string.Equals(batchInput, sample, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(batchInput, recursive: true); } catch { }
                }
            }
            while (soakSeconds > 0 && stopwatch.Elapsed.TotalSeconds < soakSeconds);

            stopwatch.Stop();

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllLines(logPath, logService.Logs.Select(x => x.DisplayText).TakeLast(20_000));
            }

            string[] outputs = Directory.GetFiles(output, "*.bin", SearchOption.AllDirectories);
            var result = new
            {
                mode = string.IsNullOrWhiteSpace(modeText) ? "auto" : modeText,
                elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                tasks = vm.Tasks.Count,
                iterations,
                peakWorkingSetMb = Math.Round(peakWorkingSet / 1048576d, 1),
                pending = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Pending),
                failed = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Failed),
                outputFiles = outputs.Length,
                outputBytes = outputs.Sum(f => new FileInfo(f).Length),
                log = logPath ?? string.Empty
            };

            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void LinkArchives(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source, "*.7z", SearchOption.TopDirectoryOnly))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target))
                {
                    continue;
                }

                try
                {
                    if (!CreateHardLink(target, file, IntPtr.Zero))
                    {
                        File.Copy(file, target, overwrite: false);
                    }
                }
                catch
                {
                    File.Copy(file, target, overwrite: false);
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
