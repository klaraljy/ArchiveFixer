using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static ArchiveFixer.Tests.RecursionFixHarness;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-10-05 第三轮：两条解压路之间**说话与落点**的三处小口径。
    ///
    /// <list type="number">
    /// <item><b>递归路的"密码来源"日志印错</b>：递归层只拿得到候选的**值**，来源靠按值回查密码列表，
    /// 于是「本批已成功 / 密码本命中 / 密码本整行 / 说明文件旁路 / 手动输入」全被印成
    /// 「尝试密码列表第 N 项」（说话不实）。现在由"算候选时顺手记下的映射"回答，与单层路逐字同一份措辞。</item>
    /// <item><b>密码探针的临时目录落点不一致</b>：单层路建在暂存（产物）目录**里面**，而产物目录里的东西
    /// 要参与结果校验与发布。现在两条路统一走 <see cref="PasswordProbe.ResolveProbeDirectory"/>
    /// （产物目录的**兄弟位置**），且探针目录名进了单层那侧的工作区清理白名单。</item>
    /// <item>两处纯注释失真（无行为影响）。</item>
    /// </list>
    ///
    /// <para>能用假引擎的地方一律用假引擎（探针落点这种"看了哪两个路径"的判据，假引擎才看得见参数）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RecursionLogAndProbePathFixesTests : IDisposable
    {
        /// <summary>测试专用合成密码；只出现在测试数据里，不是任何真实凭据。</summary>
        private const string RightPassword = "Right-Pass-2026";

        /// <summary>说明文件里那条（故意写错的）密码 —— 它的**来源**才是本组要验的东西。</summary>
        private const string SidecarPassword = "Sidecar-Wrong-Pass-2026";

        private readonly string _root;

        public RecursionLogAndProbePathFixesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerLogProbeFix", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        // ================================================================ ① 候选来源说明

        /// <summary>
        /// **递归路的候选来源说明必须与单层路逐字同一份**（用户 2026-10-05 点名：递归那条日志说假话）。
        ///
        /// <para>场景选"同目录说明文件里的密码"（来源 = <c>Sidecar</c>）：它根本不在密码列表里，
        /// 所以按值回查必然落到兜底「尝试密码列表第 N 项」—— 一眼看得出修没修。
        /// 说明文件里的密码故意是错的：任务会失败 ⇒ 任务级 INFO 缓冲会吐出来，那一行才读得到。</para>
        ///
        /// <para><b>红检</b>：撤掉 <c>ResolveCandidateSourceForLog</c> 里那次映射查询 ⇒ 本用例变红
        /// （递归那条印成 <c>尝试密码列表第 1 项</c>）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 递归路的候选来源说明与单层路逐字同一份()
        {
            RecursionFixHarness.RequireSevenZip();

            string package = BuildEncryptedPackageWithSidecarFile();

            // ① 参照物：单层路（它手上有 PasswordItem，来源说明本来就是对的）。
            string singleLayerLine = await RunAndTakeSidecarCandidateLineAsync(package, "SingleLayer");

            // ② 要修的那条：递归路。
            string recursionLine = await RunAndTakeSidecarCandidateLineAsync(package, "AllBranches");

            Assert.Contains("尝试同目录说明文件里的密码", singleLayerLine, StringComparison.Ordinal);

            // ⛔ 递归路以前把这一档印成"尝试密码列表第 N 项"（说话不实，排障时看错方向）。
            // ⚠ 断言带自定义消息：红检时要能从失败里直接读到"实际印成了什么"。
            Assert.True(
                recursionLine.Contains("尝试同目录说明文件里的密码", StringComparison.Ordinal),
                $"递归路的候选来源说明必须与单层路同一份。实际那一行：{recursionLine}");

            Assert.DoesNotContain("尝试密码列表第", recursionLine, StringComparison.Ordinal);

            // 与单层路**逐字**同一份来源说明（层前缀之外的部分一模一样）。
            Assert.True(
                string.Equals(
                    DescribeCandidateTail(singleLayerLine),
                    DescribeCandidateTail(recursionLine),
                    StringComparison.Ordinal),
                "两条路的来源说明必须逐字相同。"
                + $"单层：{DescribeCandidateTail(singleLayerLine)}；递归：{DescribeCandidateTail(recursionLine)}");
        }

        /// <summary>
        /// 来源**先查"算候选时记下的那一份"**，查不到才退回按值回查密码列表 ——
        /// 判据只有这一处（<c>ResolveCandidateSourceForLog</c>），所以直接钉它。
        ///
        /// <para>为什么不能只按值回查：同一个值可能既是"密码本命中"又是"本批已成功"
        /// （密码列表里可能就是同一条），而"说明文件旁路 / 手动输入 / 密码本整行"压根不在密码列表里。</para>
        /// </summary>
        [Fact]
        public void 来源先查算候选时那一份_再退回按值回查()
        {
            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                new[] { "书里的密码" },
                attemptLimit: 10);

            var recorded = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["同一个值"] = "BatchSuccess"
            };

            // ① 映射里有 ⇒ 以映射为准（同一个值在密码列表里可能是另一档，按值猜就猜错了）。
            Assert.Equal("BatchSuccess", harness.Coordinator.ResolveCandidateSourceForLog("同一个值", recorded));

            // ② 映射里没有 ⇒ 退回按值回查（这里命中的是密码列表那一档）。
            Assert.Equal("ImportedList", harness.Coordinator.ResolveCandidateSourceForLog("书里的密码", recorded));

            // ③ 空密码照旧（映射里没有空串时）。
            Assert.Equal("Empty", harness.Coordinator.ResolveCandidateSourceForLog(string.Empty, recorded));
        }

        // ================================================================ ② 探针目录落点

        /// <summary>
        /// **探针目录的落点只有一处判据**（<see cref="PasswordProbe.ResolveProbeDirectory"/>）：
        /// 产物目录的**兄弟位置**，绝不放产物目录里面；单层路与递归路同解。
        /// </summary>
        [Fact]
        public void 探针目录落在产物目录外面_两条路同一处判据()
        {
            string singleStage = Path.Combine(@"C:\t\work", "包-1234", PathService.StageDirectoryName);
            string recursionOutput = Path.Combine(@"C:\t\work", "包-1234", "recursive", "layer-000", "output");

            string singleProbe = PasswordProbe.ResolveProbeDirectory(singleStage);
            string recursionProbe = PasswordProbe.ResolveProbeDirectory(recursionOutput);

            Assert.Equal(
                Path.Combine(@"C:\t\work", "包-1234", RecursiveExtractor.ProbeDirectoryName),
                singleProbe);

            Assert.Equal(
                Path.Combine(@"C:\t\work", "包-1234", "recursive", "layer-000", RecursiveExtractor.ProbeDirectoryName),
                recursionProbe);

            // ⛔ 产物目录**里面**一个字节都不许放（这正是要改的理由：产物目录要参与校验与发布）。
            Assert.False(
                singleProbe.StartsWith(singleStage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"探针目录不许建在产物目录里面：{singleProbe}");

            Assert.False(
                recursionProbe.StartsWith(recursionOutput + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"探针目录不许建在层产物目录里面：{recursionProbe}");

            // 拿不到父目录（畸形输入）时退回产物目录里面 —— 判不出就别把它建到说不清的地方。
            Assert.Equal(
                Path.Combine("stage", RecursiveExtractor.ProbeDirectoryName),
                PasswordProbe.ResolveProbeDirectory("stage"));
        }

        /// <summary>
        /// 单层路端到端（假引擎，看得见两次解压请求的落点）：
        /// 探针目录在**暂存（产物）目录外面**、用完就没了；成功与"失败 + 保留现场"两条收尾都验。
        ///
        /// <para><b>红检</b>：把单层那处落点改回 <c>Path.Combine(stageDirectory, …)</c> ⇒
        /// 本用例变红（<c>探针目录不许建在产物目录里面：…\stage\_密码预检</c>）。</para>
        /// </summary>
        [Fact]
        public async Task 单层路的探针在产物目录外面_用完就没了_成功与失败两档()
        {
            ProbeScenario success = await RunSingleLayerProbeScenarioAsync(expectSuccess: true);
            AssertProbeOutsideProductDirectory(success, expectWorkspaceKept: false);

            ProbeScenario failure = await RunSingleLayerProbeScenarioAsync(expectSuccess: false);
            AssertProbeOutsideProductDirectory(failure, expectWorkspaceKept: true);
        }

        /// <summary>
        /// 递归路同一个口径（假引擎）：探针目录在**层产物目录外面**、用完就没了 ——
        /// 这也是"产物的字节数不会被探针文件污染"的前提。
        /// </summary>
        [Fact]
        public async Task 递归路的探针在层产物目录外面_用完就没了()
        {
            var probePaths = new List<string>();
            var productPaths = new List<string>();

            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListingWithDeclaredTotal(("readme.txt", 12L), declaredTotalBytes: 65L * 1024 * 1024),
                OnExtractWithOptions = (request, options) =>
                {
                    if (options.IncludeEntries.Count > 0)
                    {
                        probePaths.Add(request.OutputPath!);
                        return ExtractSucceeded(request.OutputPath);
                    }

                    productPaths.Add(request.OutputPath!);
                    return ExtractSucceeded(request.OutputPath);
                }
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { RightPassword },
                // 展开比这一档与本题无关：源文件是几十字节的假样本，而声明总量是 65 MiB（探针门槛）。
                new RecursionLimits { MaxExpansionRatio = 10_000_000d },
                out _);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "recursive-probe.7z");

            Assert.True(result.Completed, result.Summary);

            // 前提：探针真的跑过（否则后面两条是废话）。
            string probePath = Assert.Single(probePaths);
            string productPath = Assert.Single(productPaths);

            Assert.Equal(Path.GetDirectoryName(productPath), Path.GetDirectoryName(probePath));
            Assert.False(
                probePath.StartsWith(productPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"探针目录不许建在层产物目录里面：{probePath}");

            Assert.False(Directory.Exists(probePath), "探针用完必须删掉");
        }

        /// <summary>
        /// 探针目录名必须能通过**单层那侧的工作区清理白名单** ——
        /// ⛔ 漏了它，进程被强杀留下的探针目录会把整份清理拦下（"目录里有非本任务造的子目录"）。
        /// </summary>
        [Fact]
        public void 单层工作区白名单要把探针目录算成我们自己的()
        {
            Assert.True(ExtractionCoordinator.IsOurWorkspaceSubdirectory(RecursiveExtractor.ProbeDirectoryName));
            Assert.True(ExtractionCoordinator.IsOurWorkspaceSubdirectory(PathService.StageDirectoryName));
            Assert.True(ExtractionCoordinator.IsOurWorkspaceSubdirectory(SplitVolumeAssembler.AssemblyDirectoryName));

            // 别人的目录照旧一律拦下。
            Assert.False(ExtractionCoordinator.IsOurWorkspaceSubdirectory("别人的目录"));
            Assert.False(ExtractionCoordinator.IsOurWorkspaceSubdirectory("recursive"));
        }

        // ================================================================ 夹具

        private sealed class ProbeScenario
        {
            public string ProbePath { get; init; } = string.Empty;

            public string StageDirectory { get; init; } = string.Empty;

            public ArchiveTask Task { get; init; } = null!;
        }

        /// <summary>跑一次单层路（假引擎），返回"探针落点 + 暂存目录"两个事实。</summary>
        private async Task<ProbeScenario> RunSingleLayerProbeScenarioAsync(bool expectSuccess)
        {
            var probePaths = new List<string>();
            var productPaths = new List<string>();

            var engine = new ScriptedEngine
            {
                // 声明总量 ≥ 64 MiB（PasswordProbe 的门槛）⇒ 值得做"只解一个条目"的探针。
                OnList = _ => PlainListingWithDeclaredTotal(("readme.txt", 12L), declaredTotalBytes: 65L * 1024 * 1024),
                OnExtractWithOptions = (request, options) =>
                {
                    if (options.IncludeEntries.Count > 0)
                    {
                        probePaths.Add(request.OutputPath!);
                        return ExtractSucceeded(request.OutputPath);
                    }

                    productPaths.Add(request.OutputPath!);

                    return expectSuccess
                        ? ExtractSucceededWithDeclaredSize(request.OutputPath, 65L * 1024 * 1024)
                        : WrongPasswordWithStub(request.OutputPath);
                }
            };

            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                new[] { RightPassword },
                attemptLimit: 10,
                recursionMode: "SingleLayer",
                engine: engine,
                // 失败那一档要把现场留住：探针目录真要是漏在暂存目录里，这一档才看得见。
                configure: settings => settings.KeepFailedWorkspace = !expectSuccess);

            string package = BuildTinyRealArchive();

            ArchiveTask task = await harness.AddTaskAsync(package);

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(expectSuccess, task.Outcome == TaskOutcome.Succeeded);

            return new ProbeScenario
            {
                ProbePath = Assert.Single(probePaths),
                StageDirectory = Assert.Single(productPaths),
                Task = task
            };
        }

        private static void AssertProbeOutsideProductDirectory(ProbeScenario scenario, bool expectWorkspaceKept)
        {
            // ① 落点在产物（暂存）目录**外面**的兄弟位置。
            Assert.Equal(Path.GetDirectoryName(scenario.StageDirectory), Path.GetDirectoryName(scenario.ProbePath));
            Assert.Equal(RecursiveExtractor.ProbeDirectoryName, Path.GetFileName(scenario.ProbePath));

            Assert.False(
                scenario.ProbePath.StartsWith(
                    scenario.StageDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                $"探针目录不许建在产物目录里面：{scenario.ProbePath}");

            // ② 用完就没了（成功 / 失败都一样）。
            Assert.False(Directory.Exists(scenario.ProbePath), "探针用完必须删掉");

            // ③ 产物目录里不许有探针那一格（保留现场那一档更要紧：它是唯一还看得见的现场）。
            if (expectWorkspaceKept)
            {
                Assert.True(Directory.Exists(scenario.StageDirectory), "这一档要求工作区保留现场");
            }

            if (Directory.Exists(scenario.StageDirectory))
            {
                Assert.DoesNotContain(
                    Directory.GetDirectories(scenario.StageDirectory),
                    directory => string.Equals(
                        Path.GetFileName(directory),
                        RecursiveExtractor.ProbeDirectoryName,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        private async Task<string> RunAndTakeSidecarCandidateLineAsync(string package, string recursionMode)
        {
            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                Array.Empty<string>(),
                attemptLimit: 10,
                recursionMode: recursionMode,
                configure: settings => settings.EnableSidecarPassword = true);

            await harness.AddTaskAsync(package);

            await harness.Coordinator.StartExtractAsync();

            /*
             * 取"候选日志"那一行：**不按来源文字找** —— 否则红检时只能看到"没找到一行"，
             * 看不到"实际印成了什么"，而那正是本用例要钉的东西。
             * 空密码那一行排除掉：它在两条路上的去留不同（单层路先把它从候选表里删了，
             * 递归路先记一行再跳过）。
             */
            return Assert.Single(
                harness.LogTexts,
                line => line.Contains("开始解压，密码候选", StringComparison.Ordinal)
                        && !line.Contains("尝试空密码", StringComparison.Ordinal));
        }

        /// <summary>取"来源说明"那半句（层前缀 / 任务名前缀之外的部分）——两条路要逐字比它。</summary>
        private static string DescribeCandidateTail(string line)
        {
            int cut = line.IndexOf("开始解压", StringComparison.Ordinal);

            return cut < 0 ? line : line[cut..];
        }

        // ================================================================ 样本

        /// <summary>
        /// 造一个真加密 7z（<c>-p</c>，文件名不加密 ⇒ 清单列得出来）+ 同目录一份说明文件，
        /// 说明文件里写的是一条**错的**密码 ⇒ 候选来源 = <c>Sidecar</c> 且必然失败（日志才吐得出来）。
        /// </summary>
        private string BuildEncryptedPackageWithSidecarFile()
        {
            string stage = Path.Combine(_root, "sidecar-stage-" + Guid.NewGuid().ToString("N"));
            string packages = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(packages);

            File.WriteAllText(Path.Combine(stage, "payload.bin"), "sidecar-payload");

            string archive = Path.Combine(packages, "sidecar.7z");

            Run7z(stage, "a", "-t7z", "-mx0", "-p" + RightPassword, archive, "payload.bin");

            File.WriteAllText(
                Path.Combine(packages, "说明.txt"),
                $"密码：{SidecarPassword}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));

            Assert.True(File.Exists(archive), "加密包没造出来：" + archive);

            return archive;
        }

        /// <summary>
        /// 一个**真的**小归档（只为让识别阶段认出"这是归档"）：
        /// 体积刻意 ≥ 200 KB —— 假引擎会声明"解压后 65 MiB"，而预算是拿源包大小算展开比的。
        /// </summary>
        private string BuildTinyRealArchive()
        {
            string stage = Path.Combine(_root, "tiny-stage-" + Guid.NewGuid().ToString("N"));
            string packages = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(packages);

            byte[] filler = new byte[200 * 1024];
            new Random(20261005).NextBytes(filler);
            File.WriteAllBytes(Path.Combine(stage, "filler.bin"), filler);

            string archive = Path.Combine(packages, "tiny.7z");

            Run7z(stage, "a", "-t7z", "-mx0", archive, "filler.bin");

            Assert.True(File.Exists(archive), "小归档没造出来：" + archive);

            return archive;
        }

        // ================================================================ 假件补充

        /// <summary>
        /// 一份"清单里条目很小、但**声明总量**很大"的成功清单：
        /// 探针的门槛看的是声明总量，而校验按同一份声明比 —— 两处用的都是引擎给的那两个数。
        /// </summary>
        private static ArchiveListResult PlainListingWithDeclaredTotal(
            (string Path, long Size) entry,
            long declaredTotalBytes)
        {
            return new ArchiveListResult
            {
                Success = true,
                Entries = new List<ArchiveEntry>
                {
                    new ArchiveEntry { Path = entry.Path, Size = entry.Size }
                },
                FileCount = 1,
                TotalUncompressedSize = declaredTotalBytes,
                IsEncrypted = true,
                EngineId = EngineIds.SevenZip,
                EngineVersion = "0.0"
            };
        }

        /// <summary>写一个"声明多大就多大"的占位产物（稀疏长度，瞬间完成；校验按字节数比对）。</summary>
        private static ArchiveOperationResult ExtractSucceededWithDeclaredSize(string? outputPath, long size)
        {
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                Directory.CreateDirectory(outputPath);

                using FileStream stream = new(
                    Path.Combine(outputPath, "readme.txt"),
                    FileMode.Create,
                    FileAccess.Write);

                stream.SetLength(size);
            }

            return ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero);
        }

        /// <summary>
        /// 密码不对的那一次：**留下一个 0 字节桩**再失败 —— 真 7-Zip 用错密码时就是这么干的
        /// （见候选循环里"每换一个候选先把产物目录清空"那段注释）。
        /// 留桩是为了让"失败 + 保留现场"那一档真的有东西可留（零文件空壳按设计一定会被删掉）。
        /// </summary>
        private static ArchiveOperationResult WrongPasswordWithStub(string? outputPath)
        {
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                Directory.CreateDirectory(outputPath);
                File.WriteAllBytes(Path.Combine(outputPath, "readme.txt"), Array.Empty<byte>());
            }

            return WrongPassword();
        }
    }
}
