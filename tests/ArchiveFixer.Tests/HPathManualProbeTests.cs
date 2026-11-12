using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 手动真机探针：需要 ARCHIVEFIXER_H_PATH_TEST=1。
    /// 两份用户给定的原包目录，源包不删，指定位置输出；只用于本次验收，不进入常规全量。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class HPathManualProbeTests
    {
        [Fact]
        public async Task 真机两份原包_指定位置_源包保留_计时()
        {
            if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_H_PATH_TEST") != "1")
            {
                return;
            }

            string[] sources = Environment.GetEnvironmentVariable("ARCHIVEFIXER_H_PATH_ROOTS") is { Length: > 0 } roots
                ? roots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[]
                {
                    @"H:\BaiduNetdiskDownload\2222\M-M-91",
                    @"H:\BaiduNetdiskDownload\2222\M-M-92"
                };

            string outputRoot = Environment.GetEnvironmentVariable("ARCHIVEFIXER_H_PATH_OUTPUT") is { Length: > 0 } outputOverride
                ? outputOverride
                : @"H:\BaiduNetdiskDownload\222\测试指定位置";
            Directory.CreateDirectory(outputRoot);

            string dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerHPath", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            try
            {
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
                        $"[H路径] {source} 用时 {one.Elapsed}；任务 {vm.Tasks.Count}；"
                        + $"成功 {vm.Tasks.Count(t => t.Outcome == TaskOutcome.Succeeded)}；"
                        + $"失败 {vm.Tasks.Count(t => t.Outcome == TaskOutcome.Failed)}；"
                        + $"跳过 {vm.Tasks.Count(t => t.Outcome == TaskOutcome.Skipped)}");

                    foreach (ArchiveTask task in vm.Tasks.Take(30))
                    {
                        Console.WriteLine(
                            $"  {task.FileName} | {task.Outcome} | {task.Status} | {task.OutputPath} | {task.ErrorMessage}");
                    }
                }

                total.Stop();
                Console.WriteLine($"[H路径] 总用时 {total.Elapsed}");
            }
            finally
            {
                if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_H_PATH_KEEP") == "1")
                {
                    Console.WriteLine("[H路径] 数据根保留：" + dataRoot);
                }
                else
                {
                    try
                    {
                        if (Directory.Exists(dataRoot))
                        {
                            Directory.Delete(dataRoot, recursive: true);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
