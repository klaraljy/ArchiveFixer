using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    /// 自造失败样本的真实管线：产物日志目录 + 一键诊断包。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class LogBundleRealRunTests
    {
        [Fact]
        public async Task 自造失败样本_生成操作文件夹和可发送诊断包()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-log-real-" + Guid.NewGuid().ToString("N"));
            string input = Path.Combine(root, "input");
            string output = Path.Combine(root, "out");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(output);

            try
            {
                string seven = @"E:\ArchiveFixer\tools\7zip\7z.exe";

                if (!File.Exists(seven))
                {
                    return;
                }

                string stage = Path.Combine(root, "stage");
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "good.txt"), "ok");
                string good = Path.Combine(input, "good.7z");
                ToolLocator.Default.CustomSevenZipExePath = seven;
                ToolLocator.Default.Invalidate();

                var start = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = seven,
                    Arguments = $"a -t7z \"{good}\" \"{Path.Combine(stage, "good.txt")}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                start?.WaitForExit();
                Assert.True(File.Exists(good), "造包失败");

                string badStage = Path.Combine(stage, "bad.txt");
                File.WriteAllText(badStage, "bad");
                string bad = Path.Combine(input, "bad.7z");
                var badStart = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = seven,
                    Arguments = $"a -t7z -pwrong -mhe=on \"{bad}\" \"{badStage}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                badStart?.WaitForExit();
                Assert.True(File.Exists(bad), "失败样本造包失败");

                string dataRoot = Path.Combine(root, "data");
                Directory.CreateDirectory(dataRoot);
                File.WriteAllText(Path.Combine(dataRoot, "passwords.txt"), "", new System.Text.UTF8Encoding(false));

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

                await vm.AddPathsAsync(new[] { input }, ImportMode.Replace);
                vm.Logger.MarkOperationStart("真实诊断包测试");
                await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

                Assert.Contains(vm.Tasks, t => t.Outcome == TaskOutcome.Failed);
                Assert.Contains(vm.Tasks, t => t.Outcome == TaskOutcome.Succeeded);

                string op = vm.Logger.CurrentOperationDirectory;
                Assert.True(Directory.Exists(op));
                Assert.True(File.Exists(Path.Combine(op, "manifest.json")));
                Assert.True(File.Exists(Path.Combine(op, "summary.json")));
                Assert.True(File.Exists(Path.Combine(op, "operation.log")));
                Assert.True(File.Exists(Path.Combine(op, "operation.jsonl")));
                Assert.True(File.Exists(Path.Combine(op, "operation.trace.jsonl")));

                string zip = Path.Combine(root, "diag.zip");
                DiagnosticBundleService.Export(vm.Logger, zip, vm.Tasks.ToList(), settings);

                using ZipArchive archive = ZipFile.OpenRead(zip);
                string[] names = archive.Entries.Select(e => e.FullName).ToArray();
                Assert.Contains("operation/operation.log", names);
                Assert.Contains("operation/operation.jsonl", names);
                Assert.Contains("operation/operation.trace.jsonl", names);
                Assert.Contains("extra/summary.json", names);
                Assert.Contains("extra/failed-tasks.json", names);

                ZipArchiveEntry summaryEntry = archive.GetEntry("extra/summary.json")!;
                using Stream summaryStream = summaryEntry.Open();
                using var reader = new StreamReader(summaryStream);
                string summary = reader.ReadToEnd();
                Assert.Contains("AF-TASK-001", summary, StringComparison.Ordinal);

                ZipArchiveEntry logEntry = archive.GetEntry("operation/operation.log")!;
                using Stream logStream = logEntry.Open();
                using var logReader = new StreamReader(logStream);
                Assert.Contains("错误码", logReader.ReadToEnd(), StringComparison.Ordinal);
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
