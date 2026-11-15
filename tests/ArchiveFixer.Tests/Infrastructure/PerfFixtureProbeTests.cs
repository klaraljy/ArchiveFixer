using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 手动性能夹具探针：ARCHIVEFIXER_PERF_FIXTURE 指向源包目录，
/// ARCHIVEFIXER_PERF_OUTPUT 指定本次唯一输出根；A/B 构建共用同一测试。
/// </summary>
[Collection("ArchiveFixerGlobalState")]
public sealed class PerfFixtureProbeTests
{
    [Fact]
    public void 性能夹具_一键处理_记录总耗时与扫描计数()
    {
        string? fixture = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_FIXTURE");

        if (string.IsNullOrWhiteSpace(fixture))
        {
            return;
        }

        string output = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_OUTPUT") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "ArchiveFixerPerf-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(output);

        var completed = new ManualResetEventSlim(false);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await RunAsync(fixture, output).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    completed.Set();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!completed.Wait(TimeSpan.FromMinutes(30)))
        {
            throw new TimeoutException("性能夹具 30 分钟未完成");
        }

        thread.Join(TimeSpan.FromSeconds(10));

        if (failure != null)
        {
            throw new InvalidOperationException("性能夹具失败：" + failure);
        }
    }

    private static async Task RunAsync(string fixture, string output)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerPerfData", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        string productionPasswords = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArchiveFixer",
            "passwords.txt");

        if (File.Exists(productionPasswords))
        {
            File.Copy(productionPasswords, Path.Combine(dataRoot, "passwords.txt"), overwrite: true);
        }

        ToolLocator.Default.CustomSevenZipExePath = @"E:\ArchiveFixer\tools\7zip\7z.exe";
        ToolLocator.Default.Invalidate();

        var pathService = new PathService { DataRootDirectory = dataRoot };
        var settingsService = new SettingsService(pathService);
        AppSettings settings = AppSettings.CreateDefault();
        settings.ExtractToOriginalDirectory = false;
        settings.CustomOutputDirectory = output;
        settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
        settings.RestHandlingAfterVerify = "Keep";
        settings.SkipOneClickConfirm = true;
        settings.UseSpecificExtraction = false;
        settingsService.Save(settings);

        var engine = new SevenZipEngine();
        var password = new PasswordService { DataRootDirectory = dataRoot, UseTextStore = true };
        var vm = new MainViewModel(
            new FileScanService(),
            new ArchiveDetectService(),
            new RenameService(),
            engine,
            password,
            new LogService(pathService),
            settingsService,
            pathService,
            new TaskSummaryService(),
            new ClipboardService(),
            new DialogService(),
            useTextStore: true,
            encryptTextStore: false);

        var extraction = new ExtractionCoordinator(vm, engine, password, pathService, new DialogService());
        var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
        var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
        var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());
        oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown();

        ExtractionProducedVolumeGate.ResetProducedCardDiagnostics();

        var total = Stopwatch.StartNew();
        await vm.AddPathsAsync(new[] { fixture }, ImportMode.Replace);
        await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(30));
        total.Stop();

        string[] logs = Directory.GetFiles(Path.Combine(dataRoot, "logs"), "*.log", SearchOption.TopDirectoryOnly);
        string logPath = logs.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? string.Empty;
        string logText = logPath.Length == 0 ? string.Empty : File.ReadAllText(logPath);

        int succeeded = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Succeeded);
        int failed = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Failed);
        int skipped = vm.Tasks.Count(t => t.Outcome == TaskOutcome.Skipped);

        (long lookups, long hits, long misses, long fileInfoCalls, long remembered) =
            ExtractionProducedVolumeGate.ProducedCardDiagnostics;
        string resultPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PERF_RESULT") ?? string.Empty;

        if (resultPath.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllLines(resultPath, new[]
            {
                $"fixture={fixture}",
                $"output={output}",
                $"log={logPath}",
                $"elapsed_ms={total.ElapsedMilliseconds}",
                $"tasks={vm.Tasks.Count}",
                $"succeeded={succeeded}",
                $"failed={failed}",
                $"skipped={skipped}",
                $"scan4160={logText.Contains("新文件 4160 个；按内容认出归档 8 个；跳过 4152 个", StringComparison.Ordinal)}",
                $"scan267={logText.Contains("新文件 267 个；按内容认出归档 0 个；跳过 267 个", StringComparison.Ordinal)}",
                $"cache_lookups={lookups}",
                $"cache_hits={hits}",
                $"cache_misses={misses}",
                $"fileinfo_calls={fileInfoCalls}",
                $"cache_remembered={remembered}"
            });
        }

        Console.WriteLine($"PERF elapsed_ms={total.ElapsedMilliseconds} tasks={vm.Tasks.Count} ok={succeeded} fail={failed} skip={skipped}");
        Console.WriteLine($"PERF log={logPath}");
        Console.WriteLine($"PERF output={output}");

        Assert.Equal(0, failed);
        Assert.True(succeeded > 0, "没有任何任务成功");
        Assert.Contains("新文件 4160 个；按内容认出归档 8 个；跳过 4152 个", logText, StringComparison.Ordinal);
        Assert.Contains("新文件 267 个；按内容认出归档 0 个；跳过 267 个", logText, StringComparison.Ordinal);
    }
}
