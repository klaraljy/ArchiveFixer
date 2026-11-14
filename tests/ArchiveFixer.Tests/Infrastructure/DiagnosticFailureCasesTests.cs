using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 失败样本 → 诊断包的真实管线验收：错误包、源码位置、引擎/退出码、task.end
/// 必须能从 zip 里直接读出来，而不是只能靠猜。
/// </summary>
[Collection("ArchiveFixerGlobalState")]
public sealed class DiagnosticFailureCasesTests
{
    [Fact]
    public async Task 三类错误包_诊断包都能给出码和源码位置()
    {
        string root = Path.Combine(Path.GetTempPath(), "af-diag-cases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string seven = @"E:\ArchiveFixer\tools\7zip\7z.exe";

            if (!File.Exists(seven))
            {
                return;
            }

            await RunCaseAsync(root, seven, "password", input =>
            {
                string stage = Path.Combine(root, "password-stage");
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "secret.txt"), "secret");
                string archive = Path.Combine(input, "bad-password.7z");
                Run7z(seven, $"a -t7z -pwrong -mhe=on \"{archive}\" \"{Path.Combine(stage, "secret.txt")}\"");
            });

            await RunCaseAsync(root, seven, "corrupt", input =>
            {
                string stage = Path.Combine(root, "corrupt-stage");
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, "data.txt"), new string('x', 4096));
                string valid = Path.Combine(input, "valid.7z");
                Run7z(seven, $"a -t7z \"{valid}\" \"{Path.Combine(stage, "data.txt")}\"");
                byte[] bytes = File.ReadAllBytes(valid);
                int offset = bytes.Length / 2;

                for (int i = 0; i < 16 && offset + i < bytes.Length - 16; i++)
                {
                    bytes[offset + i] ^= 0xFF;
                }

                File.WriteAllBytes(Path.Combine(input, "corrupt.7z"), bytes);
                File.Delete(valid);
            });

            await RunCaseAsync(root, seven, "space-budget", input =>
            {
                string stage = Path.Combine(root, "budget-stage");
                Directory.CreateDirectory(stage);

                // 2 MiB 全零压缩到几百字节，展开比必然超过安全上限；这不是坏包，是安全门拒绝。
                File.WriteAllBytes(Path.Combine(stage, "zeros.bin"), new byte[2 * 1024 * 1024]);
                string archive = Path.Combine(input, "space-budget.7z");
                Run7z(seven, $"a -t7z \"{archive}\" \"{Path.Combine(stage, "zeros.bin")}\"");
            });
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

    private static async Task RunCaseAsync(string root, string seven, string name, Action<string> createInput)
    {
        string caseRoot = Path.Combine(root, name);
        string input = Path.Combine(caseRoot, "input");
        string output = Path.Combine(caseRoot, "out");
        string dataRoot = Path.Combine(caseRoot, "data");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(dataRoot);

        createInput(input);

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
        vm.Logger.MarkOperationStart("诊断失败用例-" + name);
        await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

        Assert.True(
            vm.Tasks.Any(task => task.Outcome is TaskOutcome.Failed or TaskOutcome.PartiallyCompleted or TaskOutcome.Cancelled),
            name + " 没有产生失败/部分完成，错误包没有触发管线");

        string zip = Path.Combine(caseRoot, "diag-" + name + ".zip");
        DiagnosticBundleService.Export(vm.Logger, zip, vm.Tasks.ToList(), settings);

        string keepDirectory = Environment.GetEnvironmentVariable("ARCHIVEFIXER_KEEP_DIAG_CASES") ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(keepDirectory))
        {
            Directory.CreateDirectory(keepDirectory);
            File.Copy(zip, Path.Combine(keepDirectory, name + ".zip"), overwrite: true);
        }

        using ZipArchive archive = ZipFile.OpenRead(zip);

        string failedText = ReadEntry(archive, "extra/failed-tasks.json");
        using JsonDocument failedDocument = JsonDocument.Parse(failedText);
        JsonElement failedTasks = failedDocument.RootElement;
        Assert.True(failedTasks.GetArrayLength() > 0, name + " 的 failed-tasks.json 是空的");

        JsonElement failed = failedTasks[0];
        string code = failed.GetProperty("error_code").GetString() ?? string.Empty;
        string sourceFile = failed.GetProperty("source_file").GetString() ?? string.Empty;
        int sourceLine = failed.GetProperty("source_line").GetInt32();

        Assert.False(string.IsNullOrWhiteSpace(code), name + " 缺 error_code");
        Assert.False(string.IsNullOrWhiteSpace(sourceFile), name + " 缺 source_file");
        Assert.True(sourceLine > 0, name + " 缺 source_line");

        string summaryText = ReadEntry(archive, "extra/summary.json");
        using JsonDocument summaryDocument = JsonDocument.Parse(summaryText);
        JsonElement summaryCodes = summaryDocument.RootElement.GetProperty("error_codes");
        Assert.Contains(code, summaryCodes.EnumerateArray().Select(item => item.GetString()));

        string trace = ReadEntry(archive, "operation/operation.trace.jsonl");
        Assert.Contains("\"event\":\"task.end\"", trace, StringComparison.Ordinal);

        if (name == "password")
        {
            Assert.Equal("AF-PASSWORD-010", code);
            Assert.Equal("sevenzip", failed.GetProperty("engine").GetString());
            Assert.Equal(2, failed.GetProperty("exit_code").GetInt32());
        }

        if (name == "space-budget")
        {
            Assert.Equal("AF-LIMIT-015", code);
            Assert.Equal("ResourceBudget", failed.GetProperty("component").GetString());
        }
    }

    private static void Run7z(string seven, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = seven,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process? process = Process.Start(startInfo);
        process?.WaitForExit();
        Assert.True(process?.ExitCode == 0, "7z 造包失败：" + arguments);
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidOperationException("诊断包缺 " + name);
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
