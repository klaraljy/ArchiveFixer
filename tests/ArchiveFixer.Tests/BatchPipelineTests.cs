using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// M3 验收：「一批包一键搞定」的端到端逻辑链路（AGENTS.md §10 M3）。
    ///
    /// 一批包 → 识别 → 按密码本解压 → **校验落盘结果** → **归集到统一目录** → **按开关清理源包**。
    ///
    /// 重点验证两条不可逆的安全线：
    /// 1. 校验没通过时**一个源文件都不许删**；
    /// 2. 开关关闭时同样一个都不许删。
    /// </summary>
    public class BatchPipelineTests : IDisposable
    {
        private const string BookPasswordA = "BatchPassA1";
        private const string BookPasswordB = "BatchPassB2";

        private readonly string _root;
        private readonly string _sevenZip;

        public BatchPipelineTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerBatch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private void Run7z(params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}");
            }
        }

        private void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>造一批包：两个加密 7z（各用不同密码）+ 一个伪装后缀的加密包。</summary>
        private void BuildBatch()
        {
            string source = Path.Combine(_root, "_src");

            WriteText(Path.Combine(source, "packA", "a.txt"), "包 A 的内容\n");
            WriteText(Path.Combine(source, "packB", "b.txt"), "包 B 的内容\n");
            WriteText(Path.Combine(source, "packC", "c.txt"), "包 C 的内容\n");

            string archiveDir = Path.Combine(_root, "packages");
            Directory.CreateDirectory(archiveDir);

            Run7z("a", "-t7z", Path.Combine(archiveDir, "packA.7z"), "-p" + BookPasswordA, "-mhe=on", Path.Combine(source, "packA"));
            Run7z("a", "-t7z", Path.Combine(archiveDir, "packB.7z"), "-p" + BookPasswordB, "-mhe=on", Path.Combine(source, "packB"));

            // 第三个：内容一样是加密 7z，但文件名被改成 .jpg（伪装后缀）
            string packC = Path.Combine(archiveDir, "packC.7z");
            Run7z("a", "-t7z", packC, "-p" + BookPasswordA, "-mhe=on", Path.Combine(source, "packC"));
            File.Move(packC, Path.Combine(archiveDir, "packC.jpg"));

            Directory.Delete(source, recursive: true);
        }

        /// <summary>
        /// 跑一遍 M3 链路：识别 → 修正伪装后缀 → 密码本命中 → 解压 → 校验 → 归集 → 清理。
        /// </summary>
        private async Task<(List<ArchiveTask> Tasks, string TargetRoot)> RunBatchAsync(bool deleteSource)
        {
            BuildBatch();

            string bookPath = Path.Combine(_root, "密码本.txt");
            WriteText(bookPath,
                "packA:" + BookPasswordA + "\n" +
                "packB:" + BookPasswordB + "\n" +
                "packC：" + BookPasswordA + "\n");

            var passwordService = new PasswordService();
            passwordService.ImportPasswordList(bookPath);

            var scan = new FileScanService();
            var detect = new ArchiveDetectService();
            var rename = new RenameService();
            var engine = new SevenZipEngine();
            var collector = new ResultCollector();
            var cleanup = new SourceCleanupService();

            string packageDir = Path.Combine(_root, "packages");
            string targetRoot = Path.Combine(_root, "collected");

            List<ArchiveTask> tasks = await scan.ScanPathsAsync(
                new[] { packageDir },
                new ScanOptions { ScanMode = "ScanAllFiles" },
                CancellationToken.None);

            Assert.Equal(3, tasks.Count);

            foreach (ArchiveTask task in tasks)
            {
                await detect.ApplyDetectResultAsync(task, CancellationToken.None);

                // 修正伪装后缀（GUI 上会先出预览）
                if (task.ExtensionStatus == StatusText.ExtensionMismatch ||
                    task.ExtensionStatus == StatusText.ExtensionMissing ||
                    task.ExtensionStatus == StatusText.ExtensionMultiFake)
                {
                    List<RenamePreviewItem> preview = rename.BuildPreview(
                        new[] { task },
                        RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

                    await rename.ExecuteRenameAsync(preview, new[] { task });
                }

                // 密码本命中
                // 输出目录用真正的 PathService 算（每个包一个独立目录），别在测试里手搓规则。
                task.OutputPath = new PathService().BuildOutputPath(
                    task,
                    new ExtractOptions
                    {
                        ExtractToOriginalDirectory = false,
                        CustomOutputDirectory = Path.Combine(_root, "out"),
                        KeepArchiveNameFolder = true
                    });

                List<PasswordItem> candidates = passwordService.GetPasswordCandidates(
                    task,
                    globalPassword: string.Empty,
                    passwordList: passwordService.Passwords,
                    tryEmptyFirst: true);

                string winningPassword = string.Empty;
                ArchiveOperationResult extractResult = ArchiveOperationResult.CreateFailure(-1, "", "", StatusText.ExtractFailed, "未执行", "UnknownError", TimeSpan.Zero);

                foreach (PasswordItem candidate in candidates)
                {
                    extractResult = await engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = task.CurrentPath,
                            OutputPath = task.OutputPath,
                            Password = candidate.Value
                        },
                        new ExtractOptions(),
                        CancellationToken.None);

                    if (extractResult.Success)
                    {
                        winningPassword = candidate.Value ?? string.Empty;
                        break;
                    }
                }

                Assert.True(extractResult.Success, $"{task.FileName} 应能用密码本里的密码解开：{extractResult.Message}");

                // 校验：引擎给预期，落盘结果对一遍
                ArchiveListResult expected = await engine.ListAsync(
                    ArchiveRequest.For(task.CurrentPath, winningPassword),
                    CancellationToken.None);

                OutputVerificationResult verification = OutputVerifier.Verify(task.OutputPath, expected.Success ? expected : null);

                Assert.True(verification.Verified, $"{task.FileName} 校验应通过：{verification.Message}");

                // 归集
                CollectResult collected = collector.Collect(task, targetRoot);
                Assert.True(collected.Success, $"{task.FileName} 归集应成功：{collected.Message}");

                // 清理（按开关）
                SourceCleanupResult cleanupResult = cleanup.Cleanup(task, verification, deleteSource);
                Assert.Equal(deleteSource, cleanupResult.Attempted);
            }

            return (tasks, targetRoot);
        }

        [Fact]
        public async Task 一批包跑完_结果全部归集到统一目录()
        {
            (List<ArchiveTask> tasks, string targetRoot) = await RunBatchAsync(deleteSource: false);

            Assert.True(Directory.Exists(targetRoot), "归集目标目录应存在");

            // 三个包的产物都要在（A/B 是 .7z，C 从 .jpg 改名成 .7z）
            var collectedFiles = Directory.GetFiles(targetRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();

            Assert.Contains("a.txt", collectedFiles);
            Assert.Contains("b.txt", collectedFiles);
            Assert.Contains("c.txt", collectedFiles);

            // 归集是"移动"，原输出目录里不该还留着产物
            foreach (ArchiveTask task in tasks)
            {
                if (Directory.Exists(task.OutputPath))
                {
                    Assert.Empty(Directory.GetFiles(task.OutputPath, "*", SearchOption.AllDirectories));
                }
            }
        }

        [Fact]
        public async Task 开关关闭时_一个源包都不许删()
        {
            (List<ArchiveTask> tasks, _) = await RunBatchAsync(deleteSource: false);

            foreach (ArchiveTask task in tasks)
            {
                Assert.True(File.Exists(task.CurrentPath), $"开关关闭时源包必须保留：{task.CurrentPath}");
            }
        }

        [Fact]
        public async Task 开关开启且校验通过时_源包被删除()
        {
            (List<ArchiveTask> tasks, _) = await RunBatchAsync(deleteSource: true);

            foreach (ArchiveTask task in tasks)
            {
                Assert.False(File.Exists(task.CurrentPath), $"校验通过且开关开启时源包应被删除：{task.CurrentPath}");
            }
        }

        [Fact]
        public void 校验未通过时_清理服务拒绝执行()
        {
            string source = Path.Combine(_root, "keep-me.7z");
            WriteText(source, "占位内容");

            var task = new ArchiveTask(source);
            var failed = new OutputVerificationResult
            {
                Verified = false,
                Message = "预期 3 个文件，实际 0 个"
            };

            SourceCleanupResult result = new SourceCleanupService().Cleanup(task, failed, enabled: true);

            Assert.False(result.Attempted);
            Assert.True(File.Exists(source), "校验没过就绝不能删源文件");
            Assert.Contains("校验", result.Message, StringComparison.Ordinal);
        }
    }
}
