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
/// 手动 UI 线程探针：ARCHIVEFIXER_H_PATH_UI=1 才跑。
/// 用真实 Dispatcher + UiResponsivenessMonitor 跑两份 H 原包，专门定位 UI 停顿。
/// </summary>
[Collection("DetectionState")]
public sealed class HPathUiThreadProbeTests
{
    [Fact]
    public void 真机UI线程_两份原包_记录停顿与阶段()
    {
        if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_H_PATH_UI") != "1")
        {
            return;
        }

        var completed = new ManualResetEventSlim(false);
        Exception? failure = null;
        string heartbeatPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_H_PATH_HEARTBEAT") ?? string.Empty;

        if (heartbeatPath.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(heartbeatPath)!);
            File.WriteAllText(heartbeatPath + ".pid", Environment.ProcessId.ToString());
        }

        var stageSamples = new List<(DateTime At, string Stage)>();
        using var samplerStop = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            while (!samplerStop.IsCancellationRequested)
            {
                try
                {
                    stageSamples.Add((DateTime.Now, UiActivity.Snapshot()));
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(100, samplerStop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            UiResponsivenessMonitor? monitor = null;

            monitor = UiResponsivenessMonitor.Start(
                dispatcher,
                message => Console.WriteLine("[UI停顿] " + message));

            DispatcherTimer? heartbeat = null;

            if (heartbeatPath.Length > 0)
            {
                heartbeat = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(250),
                    DispatcherPriority.Send,
                    (_, _) => File.WriteAllText(heartbeatPath, Environment.TickCount64.ToString()),
                    dispatcher);
                heartbeat.Start();
            }

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await RunAsync();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    heartbeat?.Stop();
                    monitor.Dispose();

                    if (heartbeatPath.Length > 0)
                    {
                        File.WriteAllText(heartbeatPath + ".done", Environment.TickCount64.ToString());
                    }

                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    completed.Set();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!completed.Wait(TimeSpan.FromMinutes(20)))
        {
            throw new TimeoutException("UI 线程探针 20 分钟未完成");
        }

        thread.Join(TimeSpan.FromSeconds(10));
        samplerStop.Cancel();

        foreach ((DateTime at, string stage) in stageSamples.TakeLast(80))
        {
            Console.WriteLine($"[阶段样本] {at:HH:mm:ss.fff} {stage}");
        }

        if (failure != null)
        {
            throw new InvalidOperationException("UI 线程探针失败：" + failure);
        }
    }

    private static async Task RunAsync()
    {
        string[] sources =
        {
            @"H:\BaiduNetdiskDownload\2222\M-M-91",
            @"H:\BaiduNetdiskDownload\2222\M-M-92"
        };

        string outputRoot = @"H:\BaiduNetdiskDownload\222\测试指定位置\ui-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(outputRoot);

        string dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerHPathUi", Guid.NewGuid().ToString("N"));
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
        settings.CustomOutputDirectory = outputRoot;
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

        var total = Stopwatch.StartNew();

        foreach (string source in sources)
        {
            var one = Stopwatch.StartNew();
            await vm.AddPathsAsync(new[] { source }, ImportMode.Replace);
            await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(15));
            one.Stop();

            Console.WriteLine(
                $"[UI路径] {source} 用时 {one.Elapsed}；任务 {vm.Tasks.Count}；"
                + $"成功 {vm.Tasks.Count(t => t.Outcome == TaskOutcome.Succeeded)}；"
                + $"失败 {vm.Tasks.Count(t => t.Outcome == TaskOutcome.Failed)}；"
                + $"跳过 {vm.Tasks.Count(t => t.Outcome == TaskOutcome.Skipped)}");
        }

        total.Stop();
        Console.WriteLine("[UI路径] 总用时 " + total.Elapsed);
        Console.WriteLine("[UI路径] 输出根 " + outputRoot);
        Console.WriteLine("[UI路径] 数据根 " + dataRoot);
    }
}
