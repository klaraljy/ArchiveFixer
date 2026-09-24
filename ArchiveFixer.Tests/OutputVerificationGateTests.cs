using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「**产物校验未通过 = 不是成功**」这一条红线的回归测试（用户 2026-09-24 真机铁证，不变量 6）。
    ///
    /// <para>
    /// 现场（用户机器上的日志 <c>ArchiveFixer_20260924_153637.log</c> 15:37–15:38）：
    /// 内嵌归档直读不支持 → 回落抠取 → 候选 1（空密码）密码错误 → 候选 2 的 7z **退出码 0**
    /// 却只写出 2 个 **0 字节**文件 → 输出校验当场判否 → 程序仍然：
    /// ① 落「解压成功」② 把 0 字节的 <c>*.7z.001</c> 当内层包继续解下一层
    /// ③ 把那 2 项搬进 <c>其余物</c> ④ **候选 3–10 一个都没再试**。
    /// </para>
    /// <para>
    /// 这一组就是按那四条逐个钉死：假引擎（真管线）+ 一条真 7z 的端到端。
    /// </para>
    /// </summary>
    public class OutputVerificationGateTests : IDisposable
    {
        private readonly string _root;

        public OutputVerificationGateTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVerifyGate", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ================================================================ ① 0 字节产物 = 什么都没解出来

        /// <summary>
        /// <b>用户铁证的第 ① 条</b>：引擎报成功、清单列了 2 个条目，但落盘的是 2 个 **0 字节**文件
        /// （真实形状：<c>2部轻熟1.7z.001/.002</c>）。
        ///
        /// <para>必须同时成立四条：任务**不是**成功、**不**继续下一层、**不**搬其余物、源包一个字节都不动。</para>
        /// </summary>
        [Fact]
        public async Task 假引擎成功但产物全是0字节_不是成功_不续解_不搬其余物_源包不动()
        {
            Harness harness = CreateHarness();

            string source = harness.CreateSourceFile("pack.7z");
            ArchiveTask task = harness.AddTask(source);

            // 引擎"成功"，清单说 2 个文件 / 1000 字节，实际写出 2 个 0 字节的桩文件。
            harness.Engine.Products = new[]
            {
                new FakeProduct("2部轻熟1.7z.001", 0),
                new FakeProduct("2部轻熟1.7z.002", 0)
            };
            harness.Engine.ExpectedFileCount = 2;
            harness.Engine.ExpectedTotalSize = 1000;

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // ① 不是成功，而且原因里必须有那三个数字（用户要求：预期 N 个 / X 字节，实际 M 个 / Y 字节）。
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Contains("产物校验未通过", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("预期 2 个文件 / 1000 字节", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("实际 2 个 / 0 字节", task.ErrorMessage, StringComparison.Ordinal);
            Assert.False(task.IsOutputVerified, "校验判否时不许显示「输出校验通过」");
            Assert.Equal(OutputVerificationOutcome.Failed, task.OutputVerification);
            Assert.True(
                task.Outcome == TaskOutcome.Failed,
                $"机器终态必须是失败（删源 / 搬源 / 续解都读它），实际：{task.Outcome}");
            Assert.Equal(0, outcome.ContinuationLayers);

            // 绝不出现"解压成功"那句话（有界面宿主时日志是用户唯一能对照的证据）。
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains($"解压成功：{task.FileName}", StringComparison.Ordinal));

            // ② 不继续下一层：0 字节的 *.7z.001 不是包。
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("没有发现可继续解压的内层包", StringComparison.Ordinal));

            // ③ 不搬其余物（连目录都不该建）。
            Assert.False(
                Directory.Exists(Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName)),
                "0 字节产物不许进其余物");

            // ④ 源包原地不动（不变量 1 的红线）。
            Assert.True(File.Exists(source), "校验未通过时源包必须原地不动");
        }

        /// <summary>
        /// 混合形状：一个真的 1 字节产物 + 一个 0 字节的 <c>*.7z.001</c>（校验因此**能**通过）。
        /// 这时 0 字节的那份仍然不许当内层包（用户要求："0 字节文件绝不许进入续解链"）。
        /// </summary>
        [Fact]
        public async Task 零字节的7z001不是内层包_不触发续解()
        {
            Harness harness = CreateHarness();

            string source = harness.CreateSourceFile("pack.7z");
            ArchiveTask task = harness.AddTask(source);

            harness.Engine.Products = new[]
            {
                new FakeProduct("payload.bin", 1),
                new FakeProduct("inner.7z.001", 0)
            };
            harness.Engine.ExpectedFileCount = 2;
            harness.Engine.ExpectedTotalSize = 1;

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 0 字节的那份既不算内层包、也不进其余物（它就留在暂存目录里）。
            Assert.Equal(0, outcome.ContinuationLayers);
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("0 字节的归档 / 分卷文件不算其余物", StringComparison.Ordinal));

            string restDirectory = Path.Combine(task.OutputPath, ProcessArtifactLayout.ArtifactDirectoryName);

            Assert.False(
                File.Exists(Path.Combine(restDirectory, "inner.7z.001")),
                "0 字节的 *.7z.001 不许被搬进其余物");
        }

        /// <summary>
        /// <b>用户铁证的第 ② 条（本次修复的核心）</b>：第一个候选产出 0 字节、第二个候选产出正确字节
        /// → **最终成功**，而且日志里两个候选都试过（老代码在候选 1 就当成成功退出了）。
        /// </summary>
        [Fact]
        public async Task 第一个候选产出0字节_第二个候选正确_最终成功且两个候选都试过()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.MaxPasswordAttemptsPerLayer = 10;

                // 关掉"空密码优先"，候选顺序就正好是密码列表的顺序（这条用例要钉的是"接着试下一个"）。
                settings.TryEmptyPasswordFirst = false;
            });

            string source = harness.CreateSourceFile("pack.7z");
            ArchiveTask task = harness.AddTask(source);

            // 密码本给两个候选：第一个产出 0 字节（真机现场），第二个才对。
            harness.SetPasswordCandidates("wrong-one", "right-one");

            harness.Engine.ExtractHandler = request =>
                string.Equals(request.Password, "right-one", StringComparison.Ordinal)
                    ? new[] { new FakeProduct("payload-00000.bin", 10), new FakeProduct("payload-00001.bin", 10) }
                    : new[] { new FakeProduct("2部轻熟1.7z.001", 0), new FakeProduct("2部轻熟1.7z.002", 0) };

            harness.Engine.ExpectedFileCount = 2;
            harness.Engine.ExpectedTotalSize = 20;

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.IsOutputVerified);
            Assert.Equal(OutputVerificationOutcome.Passed, task.OutputVerification);
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            // 两个候选**都试过**，而且顺序是"先错的、后对的"（这就是"校验没过要继续试下一个候选"的直接证据）。
            Assert.Equal(new[] { "wrong-one", "right-one" }, harness.Engine.ExtractPasswords.ToArray());
            Assert.Contains(
                harness.Log.Logs,
                x => x.Message.Contains("这个候选不算数，继续尝试下一个候选密码", StringComparison.Ordinal));

            // 内容物必须是真的那一份（第二个候选的产物），不是第一个候选留下的 0 字节桩。
            Assert.True(File.Exists(Path.Combine(task.OutputPath, "payload-00000.bin")));
            Assert.False(File.Exists(Path.Combine(task.OutputPath, "2部轻熟1.7z.001")));
        }

        /// <summary>
        /// 候选全试完仍然不完整 → 如实失败，且状态取更准确的那一个（试过密码的用「密码错误」语义），
        /// 原因里必须写明"已试 N 个候选，产物始终不完整"。
        /// </summary>
        [Fact]
        public async Task 候选全试完仍不完整_如实失败_说明试了几个候选()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.MaxPasswordAttemptsPerLayer = 10;
                settings.TryEmptyPasswordFirst = false;
            });

            string source = harness.CreateSourceFile("pack.7z");
            ArchiveTask task = harness.AddTask(source);

            harness.SetPasswordCandidates("a-one", "b-two");
            harness.Engine.ExpectedFileCount = 2;
            harness.Engine.ExpectedTotalSize = 500;

            // 两个候选都"成功"但都只写 0 字节。
            harness.Engine.ExtractHandler = _ => new[]
            {
                new FakeProduct("2部轻熟1.7z.001", 0),
                new FakeProduct("2部轻熟1.7z.002", 0)
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.Contains("产物校验未通过", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("已试 2 个候选，产物始终不完整", task.ErrorMessage, StringComparison.Ordinal);

            Assert.Equal(2, harness.Engine.ExtractPasswords.Count);
            Assert.True(File.Exists(source), "源包必须原地不动");
        }

        // ================================================================ ② 校验口径：0/0 不许算通过

        [Fact]
        public void 校验_预期0个0字节且实际0字节_必须判失败而不是通过()
        {
            string output = Path.Combine(_root, "empty-products");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "stub.7z.001"), Array.Empty<byte>());

            var expected = new ArchiveListResult
            {
                Success = true,
                FileCount = 0,
                TotalUncompressedSize = 0,
                Entries = new List<ArchiveEntry>()
            };

            OutputVerificationResult result = OutputVerifier.Verify(output, expected);

            Assert.False(result.Verified, "产物为空（0 字节）不许算校验通过");
            Assert.Equal(OutputVerificationOutcome.Failed, result.Outcome);
            Assert.Contains("产物为空", result.Message, StringComparison.Ordinal);
            Assert.Equal("产物校验未通过：产物为空", result.FailureMessage.Substring(0, "产物校验未通过：产物为空".Length));
        }

        [Fact]
        public void 校验_拿不到清单时0字节产物照样判失败()
        {
            string output = Path.Combine(_root, "empty-products-2");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "stub.7z.001"), Array.Empty<byte>());

            // 列目录失败（`-mhe` 读不出清单）时老口径只做"目录非空"的底线校验 —— 0 字节垃圾照样能混过去。
            OutputVerificationResult result = OutputVerifier.Verify(
                output,
                new ArchiveListResult { Success = false, Message = "测试用：列目录失败" });

            Assert.False(result.Verified);
            Assert.Contains("产物为空", result.Message, StringComparison.Ordinal);
        }

        // ================================================================ ③ 删除裁决读事实：校验没过一次都不许删

        /// <summary>
        /// <b>用户要求的那条断言</b>：就算状态被误置成"解压成功"，只要**校验事实**是判否，
        /// 「校验通过后删除」档就**一次都不许调用删除**（用记账的假执行器断言 0 次）。
        /// </summary>
        [Fact]
        public void 删除档_状态写着成功但校验判否_删除执行器0次调用()
        {
            string source = Path.Combine(_root, "misjudged", "pack.7z");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "源包");

            var task = new ArchiveTask(source, 1)
            {
                FileName = "pack.7z",

                // 故意把状态"误置"成成功（真机日志里出现过的那一帧）。
                Status = StatusText.ExtractSuccess,
                OutputVerification = OutputVerificationOutcome.Failed,
                Outcome = TaskOutcome.Failed
            };

            var deleteFileSystem = new CountingDeleteFileSystem();
            var service = new SourceCleanupService(deleteFileSystem);

            var failedVerification = new OutputVerificationResult
            {
                Verified = false,
                Message = "校验未通过：预期 2 个文件 / 1000 字节，实际 2 个 / 0 字节"
            };

            SourceCleanupResult cleanup = service.Cleanup(task, failedVerification, enabled: true);

            Assert.False(cleanup.Attempted);
            Assert.Equal(0, deleteFileSystem.DeleteCalls);
            Assert.True(File.Exists(source), "源包必须一个字节都没动");
            Assert.Contains("校验未通过", cleanup.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 同一条红线的**管线版**：DeleteAfterVerify 档 + 引擎写出 0 字节产物 →
        /// 整个任务流程里删除执行器一次都没被调用。
        /// </summary>
        [Fact]
        public async Task 删除档_管线里校验判否_删除执行器0次调用且源包还在()
        {
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.DeleteAfterVerify);
                settings.MaxPasswordAttemptsPerLayer = 10;
                settings.TryEmptyPasswordFirst = false;
            });

            var deleteFileSystem = new CountingDeleteFileSystem();
            harness.Coordinator.SourceDeleteFileSystemOverride = deleteFileSystem;

            string source = harness.CreateSourceFile("pack.7z");
            ArchiveTask task = harness.AddTask(source);

            harness.SetPasswordCandidates("a-one", "b-two");
            harness.Engine.ExpectedFileCount = 2;
            harness.Engine.ExpectedTotalSize = 1000;
            harness.Engine.ExtractHandler = _ => new[]
            {
                new FakeProduct("2部轻熟1.7z.001", 0),
                new FakeProduct("2部轻熟1.7z.002", 0)
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(0, deleteFileSystem.DeleteCalls);
            Assert.True(File.Exists(source), "校验未通过时删除档一个字节都不许删");
        }

        // ================================================================ ④ 汇总口径：不许算成成功

        [Fact]
        public void 汇总_状态写着成功但校验判否_不算成功()
        {
            var service = new TaskSummaryService();

            var task = new ArchiveTask(Path.Combine(_root, "t", "pack.7z"), 1)
            {
                Status = StatusText.ExtractSuccess,
                OutputVerification = OutputVerificationOutcome.Failed,
                VerifyMessage = "校验未通过：预期 2 个文件 / 1000 字节，实际 2 个 / 0 字节"
            };

            Assert.Equal(SummaryBucket.ExtractFailed, TaskSummaryService.ClassifyOutcome(task));

            TaskSummary summary = service.BuildSummary(new[] { task });

            Assert.Equal(0, summary.ExtractSuccessCount);
            Assert.Equal(1, summary.FailedTotalCount);
            Assert.True(service.IsFailedOrUnknownTask(task));
            Assert.Contains("pack.7z", service.BuildFailedListText(new[] { task }), StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 真 7z：错密码绝不出现「解压成功」

        /// <summary>
        /// 真 7z、真加密包、真密码候选循环：给一个**错误的密码**去解，绝不能出现「解压成功」。
        ///
        /// <para>
        /// 这条与①互补：①钉的是"引擎说成功但产物是 0 字节"，这条钉的是"真引擎在错密码下的那一整条路"
        /// （7z 可能报错、也可能半成功写出垃圾 —— 两种都不许落成功）。
        /// </para>
        /// </summary>
        [SevenZipFact]
        public async Task 真实7z_错密码解加密包_绝不出现解压成功()
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            string sampleDirectory = Path.Combine(_root, "real-7z");
            Directory.CreateDirectory(sampleDirectory);

            string payload = Path.Combine(sampleDirectory, "payload.txt");
            File.WriteAllText(payload, "这不是真数据，只是让 7z 有东西可压");

            string archive = Path.Combine(sampleDirectory, "secret.7z");

            RunSevenZip(sevenZip, new[] { "a", "-t7z", "-pDemo#Pass1", archive, payload });

            Assert.True(File.Exists(archive), "样本没有造出来");

            Harness harness = CreateHarness(
                settings =>
                {
                    settings.MaxPasswordAttemptsPerLayer = 3;
                    settings.TryEmptyPasswordFirst = false;
                },
                engine: new SevenZipEngine());

            ArchiveTask task = harness.AddTask(archive);
            harness.SetPasswordCandidates("definitely-not-the-password");

            /*
             * 7z 路径在**构造 MainViewModel 之后**再指过去：构造函数会顺手把设置里的路径写进
             * 进程级静态（`ToolLocator.Default`），本测试的装配又会立刻把它还原 —— 顺序反了就白指了。
             */
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;
            ToolLocator.Default.CustomSevenZipExePath = sevenZip;
            ToolLocator.Default.Invalidate();

            try
            {
                await harness.Coordinator.StartExtractAsync();
            }
            finally
            {
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;
                ToolLocator.Default.Invalidate();
            }

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.False(task.IsOutputVerified);
            Assert.NotEqual(TaskOutcome.Succeeded, task.Outcome);

            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains($"解压成功：{task.FileName}", StringComparison.Ordinal));

            Assert.True(File.Exists(archive), "源包必须原地不动");
        }

        /// <summary>调真实 7z.exe（用 ArgumentList 安全传参，不拼命令行字符串 —— 不变量 10）。</summary>
        private static void RunSevenZip(string sevenZipPath, string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(60_000), "7z.exe 超时未退出");
            Assert.Equal(0, process.ExitCode);
        }

        // ================================================================ 装配

        private Harness CreateHarness(Action<AppSettings>? configure = null, IArchiveEngine? engine = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var fake = engine as FakeEngine ?? new FakeEngine();
            IArchiveEngine effectiveEngine = engine ?? fake;

            var passwordService = new PasswordService();
            var logService = new LogService(pathService);
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                effectiveEngine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(vm, effectiveEngine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());

            return new Harness(vm, fake, passwordService, coordinator, oneClick, logService, outputRoot, dataRoot);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                PasswordService passwordService,
                ExtractionCoordinator coordinator,
                OneClickCoordinator oneClick,
                LogService log,
                string outputRoot,
                string dataRoot)
            {
                Vm = vm;
                Engine = engine;
                PasswordService = passwordService;
                Coordinator = coordinator;
                OneClick = oneClick;
                Log = log;
                OutputRoot = outputRoot;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public PasswordService PasswordService { get; }

            public ExtractionCoordinator Coordinator { get; }

            public OneClickCoordinator OneClick { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public string DataRoot { get; }

            public string CreateSourceFile(string fileName)
            {
                string directory = Path.Combine(DataRoot, "..", "src");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, fileName);
                File.WriteAllText(path, "不是真的压缩包（这一组用的是假引擎 / 真 7z，见每条用例）");
                return path;
            }

            public ArchiveTask AddTask(string sourcePath)
            {
                var task = new ArchiveTask(sourcePath, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                Vm.Tasks.Add(task);
                return task;
            }

            /// <summary>给密码列表喂候选（走真的 PasswordService，候选顺序与真实运行一致）。</summary>
            public void SetPasswordCandidates(params string[] values)
            {
                PasswordService.ClearPasswords();

                foreach (string value in values)
                {
                    PasswordService.AddPassword(value);
                }
            }

            /// <summary>跑一键处理的流程部分（不弹任何对话框 —— 测试里没人在那儿点确定）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => OneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>一个"解压产物"：名字 + 字节数（0 就是真机现场那种桩文件）。</summary>
        private readonly record struct FakeProduct(string Name, int Size);

        /// <summary>
        /// 可控的假引擎：产物清单可以按密码变化（这一组要验的正是"候选 1 产出垃圾、候选 2 才对"）。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public FakeEngine()
            {
                ExtractHandler = _ => Products;
            }

            public IReadOnlyList<FakeProduct> Products { get; set; } = new[] { new FakeProduct("payload-00000.bin", 4) };

            /// <summary>按请求决定产物（不给就退回 <see cref="Products"/>）。</summary>
            public Func<ArchiveRequest, IReadOnlyList<FakeProduct>> ExtractHandler { get; set; }

            public int ExpectedFileCount { get; set; } = 1;

            public long ExpectedTotalSize { get; set; } = 4;

            /// <summary>每次解压用的密码（断言"两个候选都试过"靠它）。</summary>
            public List<string> ExtractPasswords { get; } = new();

            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = ExpectedFileCount,
                    TotalUncompressedSize = ExpectedTotalSize,
                    Entries = Enumerable.Range(0, ExpectedFileCount)
                        .Select(i => new ArchiveEntry { Path = $"entry-{i:D5}.bin", Size = 1 })
                        .ToList(),
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractPasswords.Add(request.Password ?? string.Empty);

                string output = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);

                    foreach (FakeProduct product in ExtractHandler(request))
                    {
                        string path = Path.Combine(output, product.Name);

                        if (product.Size <= 0)
                        {
                            // 真机现场：0 字节的桩文件（File.WriteAllBytes 空数组 = 真的 0 字节文件）。
                            File.WriteAllBytes(path, Array.Empty<byte>());
                        }
                        else
                        {
                            File.WriteAllBytes(path, new byte[product.Size]);
                        }
                    }
                }

                return Task.FromResult(Succeeded());
            }

            private static ArchiveOperationResult Succeeded() => new()
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }

        /// <summary>记账的假删除执行器：只数调用次数，绝不真的删任何东西。</summary>
        private sealed class CountingDeleteFileSystem : ISourceDeleteFileSystem
        {
            public int DeleteCalls { get; private set; }

            public bool FileExists(string path) => File.Exists(path);

            public void DeleteFile(string path)
            {
                DeleteCalls++;
                throw new InvalidOperationException("这条用例里一次都不该走到删除（校验未通过）。");
            }
        }
    }
}
