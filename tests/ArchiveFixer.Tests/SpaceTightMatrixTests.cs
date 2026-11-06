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
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Sdk;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 空间不足 20 批矩阵：15 批普通 + 5 批特定解压，每批开始时 5/6 个顶层包。
    /// 只验证空间算法与逐包独立性，不固定断言处理次序。
    /// 环境变量 ARCHIVEFIXER_SPACE_TIGHT_MATRIX=1 时运行。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class SpaceTightMatrixTests
    {
        private const string FixtureRoot = @"H:\BaiduNetdiskDownload\困难大测试\大测试";
        private const string NormalRoot = FixtureRoot + @"\913000000";
        private const string SpecificRoot = FixtureRoot + @"\913100000";
        private const string ResultRoot = @"H:\BaiduNetdiskDownload\困难大测试\913-space-matrix-results";
        private const long Reserve = 64 * 1024;

        [Fact]
        public async Task 空间不足二十批_普通与特定解压_按矩阵跑完()
        {
            if (Environment.GetEnvironmentVariable("ARCHIVEFIXER_SPACE_TIGHT_MATRIX") != "1")
            {
                return;
            }

            Directory.CreateDirectory(ResultRoot);
            var results = new List<object>();
            for (int i = 1; i <= 15; i++)
            {
                results.Add(await RunCaseAsync("C" + i.ToString("00"), NormalRoot, useSpecific: false));
            }
            for (int i = 1; i <= 5; i++)
            {
                results.Add(await RunCaseAsync("B" + i, SpecificRoot, useSpecific: true));
            }

            File.WriteAllText(
                Path.Combine(ResultRoot, "space-matrix-results.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Assert.Equal(20, results.Count);
        }

        private static async Task<object> RunCaseAsync(string caseId, string root, bool useSpecific)
        {
            string caseRoot = Path.Combine(root, caseId);
            string recipePath = Path.Combine(caseRoot, "recipe.json");
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(recipePath));
            JsonElement recipe = doc.RootElement;
            string spaceType = recipe.GetProperty("spaceType").GetString() ?? string.Empty;
            long freeBytes = recipe.GetProperty("space").GetProperty("freeBytes").GetInt64();

            string sourceRoot = Path.Combine(ResultRoot, "src", caseId);
            string targetRoot = Path.Combine(ResultRoot, "out", caseId);
            string dataRoot = Path.Combine(ResultRoot, "data", caseId);
            ResetDirectory(sourceRoot);
            ResetDirectory(targetRoot);
            ResetDirectory(dataRoot);
            CopyDirectory(Path.Combine(caseRoot, "原包"), sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.ExtractToOriginalDirectory = false;
            settings.CustomOutputDirectory = targetRoot;
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

            await vm.AddPathsAsync(new[] { sourceRoot }, ImportMode.Replace);

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
            string logPath = Path.Combine(ResultRoot, "logs", caseId + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllLines(logPath, logs, new UTF8Encoding(false));

            if (!string.IsNullOrWhiteSpace(runError))
            {
                throw new XunitException($"{caseId} 运行异常：{runError}");
            }

            Validate(caseId, spaceType, recipe, rows, useSpecific);

            return new
            {
                caseId,
                useSpecific,
                spaceType,
                freeBytes,
                runError,
                rows,
                logPath
            };
        }

        private static void Validate(
            string caseId,
            string spaceType,
            JsonElement recipe,
            object[] rows,
            bool useSpecific)
        {
            JsonElement expected = recipe.GetProperty("expected");
            var blocked = expected.GetProperty("blocked").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = expected.GetProperty("failed").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 普通批次：任务文件就是顶层包名。特定批次：按 recipe 的 sourceFiles 映射任务。
            var packages = recipe.GetProperty("packages").EnumerateArray().ToList();

            foreach (JsonElement package in packages)
            {
                string packageId = useSpecific
                    ? package.GetProperty("packageId").GetString() ?? string.Empty
                    : package.GetProperty("fileName").GetString() ?? string.Empty;

                var sourceFiles = useSpecific
                    ? package.GetProperty("sourceFiles").EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty)
                        .Where(item => item.Length > 0)
                        .ToArray()
                    : new[] { package.GetProperty("fileName").GetString() ?? string.Empty };

                object[] packageRows = rows
                    .Where(row => sourceFiles.Contains(GetString(row, "FileName"), StringComparer.OrdinalIgnoreCase)
                                  || sourceFiles.Contains(Path.GetFileName(GetString(row, "CurrentPath")), StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                Assert.False(packageRows.Length == 0, $"{caseId}/{packageId} 没有对应任务；源文件：{string.Join(',', sourceFiles)}");

                bool expectBlocked = blocked.Contains(packageId);
                bool expectFailed = failed.Contains(packageId)
                                    || package.TryGetProperty("corrupt", out JsonElement corrupt)
                                    && corrupt.GetBoolean();

                object[] activeRows = packageRows.Where(row => !IsFollower(row)).ToArray();
                Assert.NotEmpty(activeRows);

                if (expectBlocked)
                {
                    Assert.Contains(activeRows, row =>
                        GetString(row, "Status") == StatusText.DiskSpaceInsufficient
                        || GetString(row, "ErrorMessage").Contains("空间不足", StringComparison.Ordinal));
                    Assert.All(sourceFiles, file => Assert.True(
                        File.Exists(Path.Combine(ResultRoot, "src", caseId, file)),
                        $"{caseId}/{packageId} 被空间挡住时源包必须保留：{file}"));
                }
                else if (expectFailed)
                {
                    Assert.Contains(activeRows, row =>
                        GetString(row, "Outcome") == nameof(TaskOutcome.Failed));
                    Assert.All(sourceFiles, file => Assert.True(
                        File.Exists(Path.Combine(ResultRoot, "src", caseId, file)),
                        $"{caseId}/{packageId} 失败时源包必须保留：{file}"));
                }
                else if (spaceType == "largest")
                {
                    /*
                     * “最大包挡住”这一档不要求所有小包都成功：空间到底够几个由实际需求决定。
                     * 只要求：最大包确实被空间挡住；小包若成功则删源、有产物，若也被挡则源包不动。
                     * 分卷跟班卷是 Skipped，不参与“成功/空间不足”判据。
                     */
                    Assert.All(activeRows, row =>
                    {
                        string outcome = GetString(row, "Outcome");
                        if (outcome == nameof(TaskOutcome.Succeeded))
                        {
                            Assert.All(sourceFiles, file => Assert.False(
                                File.Exists(Path.Combine(ResultRoot, "src", caseId, file)),
                                $"{caseId}/{packageId} 成功后源包应删除：{file}"));
                            Assert.True(GetInt(row, "OutputFiles") > 0);
                        }
                        else
                        {
                            Assert.True(
                                GetString(row, "Status") == StatusText.DiskSpaceInsufficient
                                || GetString(row, "ErrorMessage").Contains("空间不足", StringComparison.Ordinal),
                                $"{caseId}/{packageId} 非成功/非空间不足状态：{GetString(row, "Status")}");
                            Assert.All(sourceFiles, file => Assert.True(
                                File.Exists(Path.Combine(ResultRoot, "src", caseId, file)),
                                $"{caseId}/{packageId} 空间不足时源包必须保留：{file}"));
                        }
                    });
                }
                else
                {
                    Assert.All(activeRows, row => Assert.Equal(nameof(TaskOutcome.Succeeded), GetString(row, "Outcome")));
                    Assert.All(sourceFiles, file => Assert.False(
                        File.Exists(Path.Combine(ResultRoot, "src", caseId, file)),
                        $"{caseId}/{packageId} 成功后源包应删除：{file}"));
                    Assert.Contains(activeRows, row => GetInt(row, "OutputFiles") > 0);
                    if (useSpecific)
                    {
                        Assert.Contains(activeRows, row => GetInt(row, "OutputFiles") >= 5);
                    }
                }
            }

            Assert.All(rows, row => Assert.NotEqual(nameof(TaskOutcome.Pending), GetString(row, "Outcome")));
        }

        private static string GetString(object row, string name) =>
            row.GetType().GetProperty(name)?.GetValue(row)?.ToString() ?? string.Empty;

        private static int GetInt(object row, string name) =>
            row.GetType().GetProperty(name)?.GetValue(row) is int value ? value : 0;

        private static bool IsFollower(object row) =>
            GetString(row, "Outcome") == nameof(TaskOutcome.Skipped)
            && GetString(row, "ErrorMessage").Contains("跟班卷", StringComparison.Ordinal);

        private static void ResetDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            Directory.CreateDirectory(path);
        }

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
    }
}
