using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    /// 这一组测试要构造真的 <see cref="MainViewModel"/>，而它的构造过程会写两个**进程级静态**：
    /// 7z.exe 路径（<c>ToolLocator.Default</c>）与递归工作区根目录（<c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>）。
    /// 和别的测试集并行跑时，别的测试会拿着"我这边马上要被删掉的临时目录"当工作区去解压
    /// （实测：RecursiveExtractorTests 因此在解压到一半时目录被删，报"引擎操作失败"）。
    ///
    /// 所以这一组声明成"不与其他集合并行"，并且每次装配后**立刻把两个静态还原**（见 CreateHarness）。
    /// </summary>
    [CollectionDefinition("ArchiveFixerGlobalState", DisableParallelization = true)]
    public class ArchiveFixerGlobalStateCollection
    {
    }

    /// <summary>
    /// 一键处理**自动续解内层包**（第 2 层起）的行为测试。
    ///
    /// 真实现场：用户的文件是"双面文件"（<c>xxx.mp4</c> = 真 MP4 头 + 尾部一个完整 ZIP），
    /// 解出来的第一层不是最终数据，而是 <c>&lt;id&gt;.7z.001</c> + <c>.002</c>（加密分卷），
    /// 要再解一层才是结果。以前一键处理只做第一层就停手，用户看到的现象就是"只有第一层能解出来"。
    ///
    /// 这里跑的是**真链路**：真 7z.exe、真 MainViewModel + 各 Coordinator、真的单层解压管线
    /// （刻意不碰 <c>RecursiveExtractor</c> —— 它在用户的真实文件上会卡死，续解不走它）。
    /// 引擎被包一层只是为了**数调用次数**：要证明"第 2 轮没有再解一遍已经处理过的包"，
    /// 只能看引擎实际被调了几次、拿的是哪个文件。
    ///
    /// 样本全部自己造（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class InnerLayerContinuationTests : IDisposable
    {
        private const string OuterPassword = "OuterPass1";
        private const string InnerPassword = "InnerPass2";

        /// <summary>续解链测试用的统一密码（列表式密码本，一行一个）。</summary>
        private const string ChainPassword = "ChainPass9";

        private const string InnerPayloadText = "第二层才有的最终数据\n";

        /// <summary>假视频头长度：与 EmbeddedArchiveTests 一致（34 KB 的文件头读不到尾部的 ZIP）。</summary>
        private const int FakeVideoPrefixLength = 32768;

        private readonly string _root;
        private readonly string _sevenZip;

        public InnerLayerContinuationTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerInnerLayer", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还被 7z 释放中）。
            }
        }

        // ---------------------------------------------------------------- 第一步：产物里有 .7z.001 → 续解

        [Fact]
        public async Task 第一层产物里有7z分卷时_自动续解到第二层()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            ArchiveTask outerTask = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.ExtensionNormal, outerTask.ExtensionStatus);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);
            Assert.False(outcome.HitRoundLimit);
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            // 第 2 层真的解出来了。
            // 输出目录名沿用既有的 BuildOutputPath 规则（分卷名 inner.7z.001 → 目录名 inner.7z），
            // 这里不去猜目录名，直接在产物根下找那份只有第 2 层才有的文件。
            Assert.Equal(2, harness.Vm.Tasks.Count);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"第 2 层应该产出一份 payload.txt，实际 {payloads.Length} 份（目录：{harness.OutputRoot}）");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // 分项之和必须等于本次处理的任务数（含续解加进来的任务）
            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);

            // 临时关掉的"导入后自动扫描"必须还原（不能把用户的设置改坏了）
            Assert.True(harness.Vm.Settings.AutoScanAfterDrop);

            // 源文件一个都不许动（不变量 1）
            Assert.True(File.Exists(outer));
        }

        // ---------------------------------------------------------------- 第二步：不重复解压已处理的任务

        [Fact]
        public async Task 第二轮不会把已经处理过的任务再解一遍()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            ArchiveTask outerTask = Assert.Single(harness.Vm.Tasks);
            string outerPath = outerTask.CurrentPath;

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // 先确认真的续解了，否则"没重复解压"是句废话
            Assert.Equal(2, outcome.Rounds);

            /*
             * 一个包被"完整解一轮"最多只会提交几次（每个密码候选一次：空密码 + 密码本命中）。
             * 如果第 2 轮把第 1 轮的任务又解了一遍，这里会变成 4 次以上。
             */
            int calls = harness.Engine.ExtractCalls.Count(p => string.Equals(p, outerPath, StringComparison.OrdinalIgnoreCase));
            Assert.True(calls <= 2, $"第 1 轮的任务被重复解压了：ExtractAsync 一共被调了 {calls} 次");

            Assert.False(outerTask.IsSelected, "处理过的任务必须取消勾选，否则下一轮会重新解压、产出 (1) 垃圾副本");

            /*
             * 内层分卷落进**其余物**（契约 §3.2）：用户看到的是"一个源包 = 一个目录"，
             * 中间件集中在 其余物 里，不再和内容物混在同一层。
             */
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "outer", "其余物", "inner.7z.001")));

            // 没有多出"(1)"这种自动改名副本目录
            Assert.False(Directory.Exists(Path.Combine(harness.OutputRoot, "outer (1)")), "不该出现重复解压留下的副本目录");
        }

        // ---------------------------------------------------------------- 第三步：轮数硬上限

        [Fact]
        public async Task 一直产出新包时_最多只续解到三轮()
        {
            string outer = BuildFourLevelChain();

            Harness harness = CreateHarness(ChainPassword + "\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(OneClickCoordinator.MaxRounds, outcome.Rounds);
            Assert.Equal(OneClickCoordinator.MaxRounds - 1, outcome.ContinuationLayers);
            Assert.True(outcome.HitRoundLimit, "第 4 层还有包，应该报到上限");
            Assert.Contains("轮上限", outcome.Summary, StringComparison.Ordinal);
            Assert.False(outcome.Stopped);

            // 第 4 层的包没有被解压：既没有引擎调用，也没有产物目录
            Assert.DoesNotContain(harness.Engine.ExtractCalls, p => p.EndsWith("level4.7z", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(harness.OutputRoot, "level4")), "到上限就不该再往下解");
            Assert.DoesNotContain(harness.Vm.Tasks, t => t.CurrentPath.EndsWith("level4.7z", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(3, harness.Vm.Tasks.Count);

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(3, scope);
        }

        // ---------------------------------------------------------------- 用户的验收判据：一个源包 = 一个目录

        /// <summary>
        /// 用户的原始抱怨（必须被这条测试钉死）：
        /// <i>"这次的一键处理给我弄的相当糟糕，你给我多弄了四个文件夹，分卷文件你居然又解压到外面来了，
        /// 文件一多根本就分不清"</i>。
        ///
        /// 期望：处理完一个源包之后，**最终只有 <c>&lt;输出根&gt;\&lt;包名&gt;\</c> 这一个目录**，
        /// 里面是内容物 + 一个集中的 <c>其余物</c>；中间件（内层分卷、抠出的 ZIP）一个都不许漏到外面。
        ///
        /// 这里用的是最像真实现场的那条链：`outer.7z` →（第一层就是内层加密分卷）→ 分卷 → 内容物。
        /// 旧实现会额外产出 <c>inner.7z\</c> 这类平级目录，而且分卷本身还和内容物躺在同一层。
        /// </summary>
        [Fact]
        public async Task 一个源包处理完_输出根下只有一个目录()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);

            // ① 输出根下只有那**一个**目录（源包名）
            string[] directories = Directory.GetDirectories(harness.OutputRoot);
            Assert.True(
                directories.Length == 1,
                $"输出根下应该只有一个目录，实际 {directories.Length} 个：{string.Join("、", directories.Select(Path.GetFileName))}");
            Assert.Equal(Path.Combine(harness.OutputRoot, "outer"), directories[0]);

            string outputDirectory = directories[0];

            // ② 内容物在这个目录里，而且只有一份
            string[] payloads = Directory.GetFiles(outputDirectory, "payload.txt", SearchOption.AllDirectories);
            Assert.Single(payloads);
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // ③ 中间件全部集中在 其余物 里，一个都不许漏在外面
            string processDirectory = Path.Combine(outputDirectory, "其余物");
            Assert.True(Directory.Exists(processDirectory), $"其余物目录不存在：{processDirectory}");
            Assert.True(File.Exists(Path.Combine(processDirectory, "inner.7z.001")));

            // 除 其余物 之外的顶层条目只能有内容物（这里就是 payload.txt / big.bin）
            string[] topLevel = Directory.GetFileSystemEntries(outputDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.Equals(name, "其余物", StringComparison.Ordinal))
                .Select(name => name ?? string.Empty)
                .ToArray();

            Assert.All(
                topLevel,
                name => Assert.False(
                    OneClickCoordinator.IsArchiveStartPoint(name) || name.EndsWith(".002", StringComparison.OrdinalIgnoreCase),
                    $"中间件漏到内容物一层了：{name}"));

            // ④ 源文件一个字节都不许动（不变量 1）
            Assert.True(File.Exists(outer));
        }

        // ---------------------------------------------------------------- 第四步：没有内层包 = 回归

        [Fact]
        public async Task 第一层就是最后一层时_只跑一轮()
        {
            string stage = Path.Combine(_root, "plain-stage");
            WriteText(Path.Combine(stage, "readme.txt"), "就一层\n");

            string outer = BuildPackage("plain.7z", stage, "readme.txt");

            Harness harness = CreateHarness($"plain:{OuterPassword}\n");
            await harness.AddPathsAsync(outer);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(0, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);
            Assert.False(outcome.HitRoundLimit);
            Assert.DoesNotContain("自动续解", outcome.Summary);

            Assert.Single(harness.Vm.Tasks);
            Assert.True(File.Exists(Path.Combine(harness.OutputRoot, "plain", "readme.txt")));

            /*
             * "一个内层包都没找到"**不许静默**：以前这里一声不吭地 break，
             * 用户分不清"本来就没有内层包"和"续解坏了 / 内层包落在了没被看到的目录里"。
             */
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("没有发现可继续解压的内层包", StringComparison.Ordinal));

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(1, scope);
            Assert.Equal(scope, sum);
        }

        // ---------------------------------------------------------------- 第四步之二：输出目录被自动改名

        [Fact]
        public async Task 输出目录已存在被改名成括号1时_第二层仍然会被续解()
        {
            BuildInnerVolumeGroup();
            string outer = BuildPackageFromInnerStage("outer.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");

            /*
             * 预置一个"已存在且非空"的目录，占住原定的输出目录名 <out>\outer。
             * 解压管线遇到这种目录会为避免混入旧文件而自动改名成 <out>\outer(1)，
             * 并把**实际落点**回写进 task.OutputPath（不是"打算输出到哪"）。
             *
             * 旧实现的续解只认"解压前快照过的目录集合"，而快照里只有原定的 <out>\outer，
             * 于是内层包所在的 outer(1) 被整段跳过 → 第二层静默不跑，
             * 汇总却照样打印"一键处理完成：成功 N"（用户看到的就是"只有第一层能解出来"）。
             */
            string plannedOutput = Path.Combine(harness.OutputRoot, "outer");
            Directory.CreateDirectory(plannedOutput);
            string staleFile = Path.Combine(plannedOutput, "用户原有文件.txt");
            WriteText(staleFile, "这一份不能被覆盖，也不该被当成产物\n");

            await harness.AddPathsAsync(outer);

            ArchiveTask outerTask = Assert.Single(harness.Vm.Tasks);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            // 前提：解压真的落到改名后的目录（否则这条测试什么都没验证到）
            Assert.Equal(Path.Combine(harness.OutputRoot, "outer(1)"), outerTask.OutputPath);
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("自动改用新目录", StringComparison.Ordinal));

            // 核心判据：第二层照样续解
            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);
            Assert.False(outcome.Stopped);
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"第 2 层应该产出一份 payload.txt，实际 {payloads.Length} 份（目录：{harness.OutputRoot}）");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // 内层包确实是落在"实际落点"里的那一批（其余物 是它的集中处，契约 §3.2）
            Assert.True(
                File.Exists(Path.Combine(outerTask.OutputPath, "其余物", "inner.7z.001")),
                $"内层分卷应该落在实际输出目录的其余物里：{outerTask.OutputPath}");

            // 用户原来放在同名目录里的文件一个字节都不许动（不变量 1）
            Assert.True(File.Exists(staleFile));
            Assert.Equal("这一份不能被覆盖，也不该被当成产物\n", File.ReadAllText(staleFile));

            // 汇总仍然自洽
            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);
        }

        // ---------------------------------------------------------------- 第四步之三：达到密码尝试上限

        [Fact]
        public async Task 达到密码尝试上限的任务_算失败而不是未处理或已停止()
        {
            string stage = Path.Combine(_root, "locked-stage");
            WriteText(Path.Combine(stage, "secret.txt"), "密码不在密码本里\n");
            string locked = BuildPackage("locked.7z", stage, "secret.txt");

            // 40 条候选（都不是真密码）：超过单层上限（ExtractionCoordinator 里是 10），
            // 于是任务终态是"达到密码尝试上限"，而不是"密码错误"。
            var book = new StringBuilder();

            for (int i = 1; i <= 40; i++)
            {
                book.AppendLine("候选密码" + i);
            }

            Harness harness = CreateHarness(book.ToString());
            await harness.AddPathsAsync(locked);

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.PasswordAttemptLimitReached, task.Status);

            /*
             * 先看**用户能看到的症状**（汇总），再看底层判定。
             * 顺序是有意的：这条测试要防的正是"跑完了却被说成被停止 / 未处理"，
             * 先断言症状，失败信息里就是那句错话，一眼能看懂坏在哪。
             */
            Assert.False(outcome.Stopped, outcome.Summary);
            Assert.StartsWith("一键处理完成", outcome.Summary, StringComparison.Ordinal);
            Assert.Contains("失败 1", outcome.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("未处理", outcome.Summary);
            Assert.DoesNotContain("已停止", outcome.Summary);

            /*
             * 核对移交清单里的第 2 条：这个新状态必须被 IsFailureStatus / IsHandled 认账。
             * 漏掉它的后果不是编译失败，而是上面那句汇总直接说反。
             */
            Assert.True(OneClickCoordinator.IsFailureStatus(task));
            Assert.True(OneClickCoordinator.IsHandled(task));

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(1, scope);
            Assert.Equal(scope, sum);
        }

        // ---------------------------------------------------------------- 第五步：停止后续

        [Fact]
        public async Task 点了停止后续_不再续解_且汇总不显示成成功()
        {
            BuildInnerVolumeGroup();
            string first = BuildPackageFromInnerStage("pack-a.7z");
            string second = BuildPackageFromInnerStage("pack-b.7z");

            Harness harness = CreateHarness($"outer:{OuterPassword}\ninner:{InnerPassword}\n");
            await harness.AddPathsAsync(first, second);

            Assert.Equal(2, harness.Vm.Tasks.Count);

            // 模拟用户在第 1 个包解压期间点了「停止后续」
            harness.Engine.OnExtract = _ => harness.Vm.IsStopping = true;

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.True(outcome.Stopped, "按过停止后续就必须记成已停止");
            Assert.Equal(1, outcome.Rounds);
            Assert.Equal(0, outcome.ContinuationLayers);
            Assert.StartsWith("一键处理已停止", outcome.Summary, StringComparison.Ordinal);
            Assert.Contains("停止后续", outcome.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("自动续解", outcome.Summary);

            // 内层包一个都没加进任务列表 —— 停止之后不许开新活
            Assert.Equal(2, harness.Vm.Tasks.Count);

            (int sum, int scope) = ParseSummaryCounts(outcome.Summary);
            Assert.Equal(scope, sum);
            Assert.Equal(2, scope);
        }

        // ---------------------------------------------------------------- 第六步：用户的真实现场

        [Fact]
        public async Task 双面文件真实现场_抠出尾部ZIP后继续解内层加密分卷()
        {
            string userFile = BuildPolyglotUserFile();

            Harness harness = CreateHarness($"inner:{InnerPassword}\n");
            await harness.AddPathsAsync(userFile);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);
            Assert.True(task.EmbeddedArchiveOffset > 0, "应该识别出尾部有内嵌归档");
            Assert.False(OneClickCoordinator.IsArchiveStartPoint(userFile), "用户给的 mp4 不是归档起点，不该被自己当成内层包");

            OneClickOutcome outcome = await harness.RunOneClickAsync();

            Assert.Equal(2, outcome.Rounds);
            Assert.Equal(1, outcome.ContinuationLayers);
            Assert.Contains("自动续解 1 层", outcome.Summary, StringComparison.Ordinal);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, "payload.txt", SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"应该只有第 2 层产出一份 payload.txt，实际 {payloads.Length} 份");
            Assert.Equal(InnerPayloadText, File.ReadAllText(payloads[0]));

            // 源文件（双面文件）一个字节都不该动
            Assert.True(File.Exists(userFile));
        }

        // ---------------------------------------------------------------- 归档起点判定（纯函数）

        [Theory]
        [InlineData("a.7z.001", true)]
        [InlineData("a.zip.001", true)]
        [InlineData("a.rar.001", true)]
        [InlineData("a.7z.002", false)]
        [InlineData("a.7z.010", false)]
        [InlineData("a.zip.z01", false)]
        [InlineData("a.zip.z02", false)]
        [InlineData("a.rar.r00", false)]
        [InlineData("a.rar.r01", false)]
        [InlineData("a.part1.rar", true)]
        [InlineData("a.part01.rar", true)]
        [InlineData("a.part2.rar", false)]
        [InlineData("a.part002.rar", false)]
        [InlineData("a.7z", true)]
        [InlineData("a.zip", true)]
        [InlineData("a.tar.gz", true)]
        [InlineData("a.mp4", false)]
        [InlineData("a.txt", false)]
        [InlineData("noextension", false)]
        public void 只有归档起点才算内层包(string fileName, bool expected)
        {
            Assert.Equal(expected, OneClickCoordinator.IsArchiveStartPoint(Path.Combine("C:\\somewhere", fileName)));
        }

        // ---------------------------------------------------------------- 样本构造

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                if (a is IEnumerable<string> many)
                {
                    foreach (string s in many)
                    {
                        psi.ArgumentList.Add(s);
                    }
                }
                else
                {
                    psi.ArgumentList.Add(a.ToString() ?? string.Empty);
                }
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// 造一组加密分卷 <c>inner.7z.001/.002…</c>（40 KB 一卷，保证切得开），落在
        /// <c>&lt;root&gt;/inner-stage</c>，并返回这些分卷的路径。
        /// </summary>
        private List<string> BuildInnerVolumeGroup()
        {
            string stage = InnerStageDirectory;
            Directory.CreateDirectory(stage);

            WriteText(Path.Combine(stage, "payload.txt"), InnerPayloadText);

            byte[] filler = new byte[150 * 1024];
            new Random(20260921).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(stage, "big.bin"), filler);

            // 文件名用相对形式传给 7z（工作目录就是 stage），归档里的条目名才稳定。
            Run7z(stage, "a", "-t7z", "inner.7z", "-v40k", "-p" + InnerPassword, "-mhe=on", "payload.txt", "big.bin");

            List<string> volumes = Directory.GetFiles(stage, "inner.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(volumes.Count >= 2, $"应该切出多个分卷，实际 {volumes.Count} 个");

            return volumes;
        }

        /// <summary>把内层分卷打成一个加密包，返回包的路径。</summary>
        private string BuildPackageFromInnerStage(string packageFileName)
        {
            List<string> volumes = Directory.GetFiles(InnerStageDirectory, "inner.7z.*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.GetFileName(p) ?? string.Empty)
                .ToList();

            Assert.NotEmpty(volumes);

            return BuildPackage(packageFileName, InnerStageDirectory, volumes.Cast<object>().ToArray());
        }

        /// <summary>在 <c>&lt;root&gt;/packages</c> 下造一个加密 7z（条目名相对 <paramref name="workingDirectory"/>）。</summary>
        private string BuildPackage(string packageFileName, string workingDirectory, params object[] entryNames)
        {
            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string path = Path.Combine(packageDirectory, packageFileName);

            var args = new List<object> { "a", "-t7z", path, "-p" + OuterPassword, "-mhe=on" };
            args.AddRange(entryNames);

            Run7z(workingDirectory, args.ToArray());

            return path;
        }

        /// <summary>
        /// 造一条四层的嵌套链：<c>outer.7z → level2.7z → level3.7z → level4.7z → final.txt</c>。
        /// 每一层的产物里都有一个**新包**，所以它会一直想往下解 —— 正好用来验证轮数硬上限。
        /// </summary>
        private string BuildFourLevelChain()
        {
            string build = Path.Combine(_root, "chain");
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, "final.txt"), "最深一层的内容\n");

            Run7z(build, "a", "-t7z", "level4.7z", "-p" + ChainPassword, "-mhe=on", "final.txt");
            Run7z(build, "a", "-t7z", "level3.7z", "-p" + ChainPassword, "-mhe=on", "level4.7z");
            Run7z(build, "a", "-t7z", "level2.7z", "-p" + ChainPassword, "-mhe=on", "level3.7z");

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string outer = Path.Combine(packageDirectory, "outer.7z");
            Run7z(build, "a", "-t7z", outer, "-p" + ChainPassword, "-mhe=on", "level2.7z");

            return outer;
        }

        /// <summary>
        /// 造用户的真实现场：<c>user.mp4</c> = 假 MP4 头 + 尾部一个完整 ZIP，ZIP 里是加密的 7z 分卷。
        ///
        /// 造法必须是"先把 ZIP 整个建好、再在前面拼数据"：这样 ZIP 的内部偏移才保持相对它自己，
        /// 才是那个"7z 打不开、必须按偏移抠出来"的形态（见 EmbeddedArchiveTests 的说明）。
        /// </summary>
        private string BuildPolyglotUserFile()
        {
            List<string> volumes = BuildInnerVolumeGroup();

            string stage = Path.Combine(_root, "zip-stage");
            Directory.CreateDirectory(stage);

            foreach (string volume in volumes)
            {
                File.Copy(volume, Path.Combine(stage, Path.GetFileName(volume)), overwrite: true);
            }

            string plainZip = Path.Combine(_root, "tail.zip");
            ZipFile.CreateFromDirectory(stage, plainZip);

            string packageDirectory = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packageDirectory);

            string userFile = Path.Combine(packageDirectory, "user.mp4");

            using (var output = new FileStream(userFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(BuildFakeVideoPrefix());
                output.Write(File.ReadAllBytes(plainZip));
            }

            return userFile;
        }

        /// <summary>假 MP4 头：前 12 字节是真格式的 ftyp box，后面填随机字节（固定种子，样本可复现）。</summary>
        private static byte[] BuildFakeVideoPrefix()
        {
            byte[] prefix = new byte[FakeVideoPrefixLength];
            new Random(20260921).NextBytes(prefix);

            byte[] boxSize = { 0x00, 0x00, 0x00, 0x20 };
            byte[] ftyp = Encoding.ASCII.GetBytes("ftypisom");

            Array.Copy(boxSize, 0, prefix, 0, boxSize.Length);
            Array.Copy(ftyp, 0, prefix, 4, ftyp.Length);

            return prefix;
        }

        private string InnerStageDirectory => Path.Combine(_root, "inner-stage");

        // ---------------------------------------------------------------- 装配

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎（外面包一层计数）。
        /// 缓存根 / 输出目录 / 密码本全部落在临时目录里，绝不碰用户的目录（AGENTS.md §8）。
        /// </summary>
        private Harness CreateHarness(string passwordBookText, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            string bookPath = Path.Combine(_root, "密码本.txt");
            WriteText(bookPath, passwordBookText);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;

            // 7z 路径留空 = 用 ToolLocator 解析出的内置路径。测试目录里也有一份 tools\7zip
            // （由 ArchiveFixer.csproj 的 CopyToOutputDirectory 带过来），所以真引擎在测试里是可用的。
            settings.CustomSevenZipExePath = string.Empty;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new CountingEngine(new SevenZipEngine());
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            /*
             * MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）。
             * 这里先存下来、构造完立刻还原：不然别的测试会拿着"我这边马上要删掉的临时目录"当工作区去解压。
             * （本测试类同时声明成不与其他集合并行，见文件顶部的 CollectionDefinition。）
             */
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

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

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, engine, oneClick, outputRoot, logService);
        }

        /// <summary>把汇总里的分项数字抠出来求和，用来验证"分项之和 = 本次任务数"这条硬要求。</summary>
        private static (int Sum, int Scope) ParseSummaryCounts(string summary)
        {
            int sum = 0;

            foreach (Match match in Regex.Matches(summary, @"(成功|失败|跳过|部分完成|取消|未处理) (\d+)"))
            {
                sum += int.Parse(match.Groups[2].Value);
            }

            Match scope = Regex.Match(summary, @"本次 (\d+) 个任务");

            Assert.True(scope.Success, $"汇总里没有本次 N 个任务：{summary}");

            return (sum, int.Parse(scope.Groups[1].Value));
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                CountingEngine engine,
                OneClickCoordinator oneClick,
                string outputRoot,
                LogService log)
            {
                Vm = vm;
                Engine = engine;
                _oneClick = oneClick;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public CountingEngine Engine { get; }

            public string OutputRoot { get; }

            /// <summary>
            /// 真 LogService：屏幕日志集合（<c>Logs</c>）在无 WPF 应用的测试进程里照样会被填充
            /// （MainViewModel.AppendLog 走的就是它），所以"有没有写这条日志"可以直接断言。
            /// </summary>
            public LogService Log { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>
            /// 跑一键处理的**流程部分**（不弹任何对话框 —— 测试里没人在那儿点确定）。
            /// 走的是 RunAsync 内部同一个 RunPipelineAsync。
            /// </summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }

        /// <summary>
        /// 真引擎外面包一层，只为**数调用**：测试要证明"第 2 轮没有再解一遍已经处理过的包"，
        /// 只能看引擎实际被调了几次、拿的是哪个文件。
        /// </summary>
        private sealed class CountingEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;

            public CountingEngine(IArchiveEngine inner)
            {
                _inner = inner;
            }

            public List<string> ExtractCalls { get; } = new();

            /// <summary>每次解压前回调（用来模拟"用户在批次中途点了停止后续"）。</summary>
            public Action<ArchiveRequest>? OnExtract { get; set; }

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ProbeAsync(request, cancellationToken);
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ListAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.TestAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCalls.Add(request.ArchivePath);
                OnExtract?.Invoke(request);

                return _inner.ExtractAsync(request, options, cancellationToken);
            }
        }
    }
}
