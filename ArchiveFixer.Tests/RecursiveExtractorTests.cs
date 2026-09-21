using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// "需要真实 7z.exe"的用例标记。
    ///
    /// 为什么要它：本文件大多数用例要求真实的 <c>tools/7zip/7z.exe</c>（递归链路只有跑真 7z 才测得出
    /// 密码重试、损坏判定这些行为）。但"测试机上没放 7z"是环境问题、不是代码缺陷，
    /// 环境不满足时应当**跳过并说明原因**，而不是把整条测试链染红。
    ///
    /// 做法是在**发现阶段**（构造特性时）就探测一次 7z 是否存在，不满足则设 <see cref="FactAttribute.Skip"/>。
    /// 刻意不用"运行时抛异常来跳过"：xunit 2.x 没有可靠的运行时跳过 API，
    /// 用它反而会引入一个"看起来是跳过、实际是失败"的陷阱。
    /// 探测结果按路径缓存，避免每个用例都去走一遍目录树。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SevenZipFactAttribute : FactAttribute
    {
        private static readonly Lazy<string> LocatedSevenZip = new(LocateSevenZipPath);

        public SevenZipFactAttribute()
        {
            if (string.IsNullOrEmpty(LocatedSevenZip.Value))
            {
                Skip = "测试机上没有可用的 7z.exe（ArchiveFixer/tools/7zip/7z.exe），跳过需要真实引擎的用例。";
            }
        }

        /// <summary>定位内置 7z.exe：从测试程序集目录向上找含 ArchiveFixer.slnx 的目录，再取 tools/7zip/7z.exe。</summary>
        internal static string LocateSevenZipPath()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            // 兜底：测试工程把 tools\7zip 也复制到了输出目录（与 OneClickPipelineTests 同一套写法）。
            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }
    }

    /// <summary>
    /// M4「递归解压」的验收测试（AGENTS.md §10 M4）。
    ///
    /// 测试策略（对应用户要求）：
    /// - 用**真实文件**跑：临时目录里先造真实归档（真实 7z.exe 生成 zip / 7z），再让
    ///   <see cref="RecursiveExtractor"/> 去解，断言的是磁盘上的真实产物；
    /// - <see cref="IArchiveProber"/> 用本文件里的假实现（魔数 + 后缀两路判断），
    ///   因为"是不是归档"不是本模块要验的东西，它只该提供一个可控的判据；
    /// - <see cref="IArchiveEngine"/> 用真实的 <see cref="SevenZipEngine"/>。
    ///   这样才测得出"引擎报密码错误 → 换下一个候选"这类真实链路。
    ///   只有"展开比超限"那条用假引擎 —— 真压缩包很难天然造出 500 倍展开比
    ///   （真造一个压缩炸弹既慢又没意义），而那条上限本身必须被验证。
    ///
    /// 临时目录全部建在 <see cref="Path.GetTempPath"/> 下，<see cref="Dispose"/> 里删干净。
    /// </summary>
    public sealed class RecursiveExtractorTests : IDisposable
    {
        /// <summary>测试专用合成密码；只出现在测试数据里，不是任何真实凭据。</summary>
        private const string TestPass123 = "TestPass123!";

        private readonly string _root;
        private readonly string _sevenZip;

        public RecursiveExtractorTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRecursive", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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
                // 临时目录删不掉不影响结论，下次跑测试换一个 GUID 目录。
            }
        }

        // ────────────────────────────── 单层 ──────────────────────────────

        [SevenZipFact]
        public async Task 单层包_SingleChain解完即完成且产物落在最终目录()
        {
            RequireSevenZip();

            string source = BuildSourceDir(("readme.txt", "没有内层归档的单层包\n"));
            string archive = Path.Combine(_root, "single.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "*"));

            string output = Path.Combine(_root, "out", "single");

            RecursionResult result = await ExtractAsync(archive, output, RecursionMode.SingleChain);

            Assert.True(result.Completed, result.Summary);
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);
            Assert.False(result.PartiallyCompleted);
            Assert.Single(result.Layers);
            Assert.Null(result.Decision);

            // 产物必须真的在最终目录里，而且工作区不该再占着这份数据。
            Assert.True(File.Exists(Path.Combine(output, "readme.txt")), result.Summary);
            Assert.Equal(
                Path.GetFullPath(output),
                Path.GetFullPath(result.FinalOutputPath));
        }

        [SevenZipFact]
        public async Task SingleLayer模式_即使里面还有归档也不继续且只解一层()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            string output = Path.Combine(_root, "out", "singlelayer");
            RecursionResult result = await ExtractAsync(outer, output, RecursionMode.SingleLayer);

            Assert.True(result.Completed, result.Summary);
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);

            // 只解一层：内层归档原样留在产物里，用户想展开再单独发起。
            Assert.Single(result.Layers);
            Assert.True(File.Exists(Path.Combine(output, "a.7z")));
            Assert.True(File.Exists(Path.Combine(output, "b.7z")));
        }

        // ────────────────────────────── 单链嵌套 ──────────────────────────────

        [SevenZipFact]
        public async Task 单链嵌套_SingleChain自动展开到最内层()
        {
            RequireSevenZip();

            /*
             * outer.zip → inner.7z → data.txt
             * 这是最典型的"下载的包里面还套着包"，也是 SingleChain 默认要自动处理的情形：
             * 内层恰好一个归档，同层其余文件（说明.txt）都是说明类文件。
             */
            string source = BuildSourceDir(("data.txt", "最内层的内容\n"));
            string innerSevenZip = Path.Combine(_root, "inner.7z");
            Run7z("a", "-t7z", innerSevenZip, Path.Combine(source, "*"));

            string inner = Path.Combine(_root, "inner.7z");
            string outerSource = Path.Combine(_root, "_outer");
            Directory.CreateDirectory(outerSource);
            File.Copy(inner, Path.Combine(outerSource, "inner.7z"));
            File.WriteAllText(Path.Combine(outerSource, "说明.txt"), "本包只有一个内层归档\n", Utf8NoBom);

            string outer = Path.Combine(_root, "outer.zip");
            Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

            string output = Path.Combine(_root, "out", "chain");
            RecursionResult result = await ExtractAsync(outer, output, RecursionMode.SingleChain);

            Assert.True(result.Completed, result.Summary);
            Assert.True(result.Layers.Count >= 2, $"应至少解出 2 层，实际 {result.Layers.Count}；{result.Summary}");

            // 最内层的文件必须出现在最终目录里，而且中间层不该再套着 inner.7z。
            Assert.True(File.Exists(Path.Combine(output, "data.txt")), result.Summary);
            Assert.False(File.Exists(Path.Combine(output, "inner.7z")), "内层归档已被展开，不该再出现在最终产物里");

            Assert.Equal(new[] { 0, 1 }, result.Layers.Select(layer => layer.Depth).ToList());

            // 每一层都必须成功（失败会在 Report 里带上原因，这里顺带把它当失败信息用）。
            foreach (RecursionLayerReport layer in result.Layers)
            {
                Assert.True(layer.Success, layer.Message);
            }
        }

        [SevenZipFact]
        public async Task 只有说明类文件时才自动继续_其它文件存在时改为询问()
        {
            RequireSevenZip();

            // 同层除内层归档外出现一个"非说明类"文件（.bin）：说明这层产物本身就是用户要的东西，
            // 里面那个归档未必是主角 —— 不能替用户决定，必须问。
            string source = BuildSourceDir(("data.txt", "内层内容\n"));
            string innerSevenZip = Path.Combine(_root, "inner.7z");
            Run7z("a", "-t7z", innerSevenZip, Path.Combine(source, "*"));

            string outerSource = Path.Combine(_root, "_outer2");
            Directory.CreateDirectory(outerSource);
            File.Copy(innerSevenZip, Path.Combine(outerSource, "inner.7z"));
            File.WriteAllText(Path.Combine(outerSource, "payload.bin"), "不是说明类文件\n", Utf8NoBom);

            string outer = Path.Combine(_root, "outer2.zip");
            Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

            string output = Path.Combine(_root, "out", "mixed");
            RecursionResult result = await ExtractAsync(outer, output, RecursionMode.SingleChain);

            Assert.Equal(RecursionStopReason.NeedsDecision, result.StopReason);
        }

        // ────────────────────────────── 多分支 ──────────────────────────────

        [SevenZipFact]
        public async Task 多分支_SingleChain停下来询问且候选数量与提示正确()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            string output = Path.Combine(_root, "out", "decision");
            RecursionResult result = await ExtractAsync(outer, output, RecursionMode.SingleChain);

            Assert.Equal(RecursionStopReason.NeedsDecision, result.StopReason);

            // 不算失败、也不算完成（规则 6）。
            Assert.False(result.Completed);
            Assert.True(result.PartiallyCompleted);

            RecursionDecisionRequest decision = Assert.IsType<RecursionDecisionRequest>(result.Decision);
            Assert.Equal(2, decision.CandidateArchives.Count);
            Assert.Contains(decision.CandidateArchives, p => p.EndsWith("a.7z", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(decision.CandidateArchives, p => p.EndsWith("b.7z", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, decision.Depth);

            Assert.False(string.IsNullOrWhiteSpace(decision.Prompt));
            Assert.Contains("2", decision.Prompt, StringComparison.Ordinal);

            // 工作区里必须能看到产物（用户要能自己去看一眼再决定）。
            // 用递归查找而不是写死层数：7z 是否自带"包名外套目录"属于引擎细节，
            // 本用例要验的是"产物确实解出来了、工作区里看得到"，不该被这个细节绑住。
            string workspacePath = ExtractWorkspacePath(result);

            Assert.True(Directory.Exists(workspacePath), result.Summary);
            Assert.True(
                FindFileUnder(workspacePath, "a.7z") != null,
                $"工作区里应当能看到内层归档 a.7z：{workspacePath}");
            Assert.True(
                FindFileUnder(workspacePath, "note.txt") != null,
                $"工作区里应当能看到说明文件 note.txt：{workspacePath}");
        }

        [SevenZipFact]
        public async Task AllBranches模式_两个分支都展开且不再询问()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            string output = Path.Combine(_root, "out", "allbranches");

            // AllBranches 是"用户已经明确要求展开所有分支"，所以不该再出现询问。
            RecursionResult result = await ExtractAsync(outer, output, RecursionMode.AllBranches);

            Assert.True(result.Completed, result.Summary);
            Assert.Null(result.Decision);
            Assert.True(result.Layers.Count >= 3, $"外层 + 两个分支至少 3 层，实际 {result.Layers.Count}");

            Assert.True(File.Exists(Path.Combine(output, "a.txt")), result.Summary);
            Assert.True(File.Exists(Path.Combine(output, "b.txt")), result.Summary);
        }

        [SevenZipFact]
        public async Task 用户确认继续后_previousDecision里的两个分支都被展开()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            string output = Path.Combine(_root, "out", "all");
            RecursionResult first = await ExtractAsync(outer, output, RecursionMode.SingleChain);

            Assert.Equal(RecursionStopReason.NeedsDecision, first.StopReason);
            RecursionDecisionRequest decision = Assert.IsType<RecursionDecisionRequest>(first.Decision);

            // 用户点"继续"：把上一次的询问原样传回来。
            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty });

            RecursionResult second = await extractor.ExtractAsync(
                task,
                output,
                RecursionMode.SingleChain,
                decision,
                CancellationToken.None);

            Assert.True(second.Completed, second.Summary);
            Assert.Equal(RecursionStopReason.Completed, second.StopReason);
            Assert.True(second.Layers.Count >= 3, $"两个分支都该被解开，实际层数 {second.Layers.Count}");

            // 两个分支的产物都要落到最终目录。
            Assert.True(File.Exists(Path.Combine(output, "a.txt")), second.Summary);
            Assert.True(File.Exists(Path.Combine(output, "b.txt")), second.Summary);
        }

        [SevenZipFact]
        public async Task previousDecision_只处理候选里的归档不重新全盘扫描()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            string output = Path.Combine(_root, "out", "partial-decision");

            // 先把候选清单真实地跑出来（用户看到的就是这一份）。
            RecursionResult askResult = await ExtractAsync(outer, output, RecursionMode.SingleChain);
            RecursionDecisionRequest ask = Assert.IsType<RecursionDecisionRequest>(askResult.Decision);
            Assert.Equal(2, ask.CandidateArchives.Count);

            /*
             * 候选只给一个分支，同时把"单层内层归档数上限"压到 1：
             * 这同时验证两件事 ——
             * ① 续跑只处理用户点名的那一个，未点名的 b.7z 不该被展开（不是重新全盘扫描）；
             * ② 条数上限作用在"用户点名的清单"上，而不是扫描结果上，否则用户选 1 个也会被拦。
             */
            var decision = new RecursionDecisionRequest
            {
                CurrentArchivePath = outer,
                Depth = ask.Depth,
                CandidateArchives = new[] { ask.CandidateArchives[0] },
                Prompt = ask.Prompt
            };

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                new RecursionLimits { MaxInnerArchivesPerLayer = 1 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                output,
                RecursionMode.SingleChain,
                decision,
                CancellationToken.None);

            Assert.NotEqual(RecursionStopReason.TooManyInnerArchives, result.StopReason);
            Assert.True(result.Completed, result.Summary);
            Assert.Null(result.Decision);

            // 被点名的那一个分支的产物必须解出来（a.7z → a.txt）。
            Assert.True(File.Exists(Path.Combine(output, "a.txt")), result.Summary);

            // 没被点名的分支不能出现在最终产物里。
            Assert.False(File.Exists(Path.Combine(output, "b.txt")), "未被点名的分支不该被展开");
        }

        [SevenZipFact]
        public async Task 内层归档数量超上限_TooManyInnerArchives()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                new RecursionLimits { MaxInnerArchivesPerLayer = 1 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "too-many"),
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.TooManyInnerArchives, result.StopReason);
            Assert.True(result.PartiallyCompleted);
            Assert.Contains("1", result.Summary, StringComparison.Ordinal);
        }

        // ────────────────────────────── 上限 ──────────────────────────────

        [SevenZipFact]
        public async Task MaxDepth为1_单链嵌套停下来并标记部分完成()
        {
            RequireSevenZip();

            (string outer, _) = BuildChainPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                new RecursionLimits { MaxDepth = 1 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "maxdepth"),
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.MaxDepthReached, result.StopReason);
            Assert.True(result.PartiallyCompleted);
            Assert.False(result.Completed);

            // 只解了第 0 层，产物留在工作区（规则 7：部分完成不发布）。
            Assert.Single(result.Layers);
            Assert.Equal(0, result.Layers[0].Depth);
            Assert.True(Directory.Exists(result.FinalOutputPath), result.Summary);
            Assert.Contains("工作区", result.Summary, StringComparison.Ordinal);

            /*
             * 崩溃恢复用的 report.json 必须真的写出来了，而且必须是**可解析的 JSON**
             * （"给崩溃后可恢复用"的前提就是它可读；写成半截文件等于没有）。
             */
            string taskDirectory = Path.GetDirectoryName(
                Path.GetDirectoryName(result.Layers[0].OutputPath)!)!;
            string reportPath = Path.Combine(taskDirectory, "report.json");

            Assert.True(File.Exists(reportPath), $"部分完成时必须留下恢复报告：{reportPath}");

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            JsonElement reportRoot = report.RootElement;

            Assert.Equal("MaxDepthReached", reportRoot.GetProperty("StopReason").GetString());
            Assert.False(reportRoot.GetProperty("Completed").GetBoolean());
            Assert.True(reportRoot.GetProperty("PartiallyCompleted").GetBoolean());
            Assert.Equal(1, reportRoot.GetProperty("Layers").GetArrayLength());

            JsonElement engine = reportRoot.GetProperty("Engine");
            Assert.False(string.IsNullOrWhiteSpace(engine.GetProperty("Id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(engine.GetProperty("Version").GetString()));

            // 报告里绝不能出现明文密码（规则 9）。本用例的密码候选只有空密码，所以断言的是"没有别的密码痕迹"。
            string reportJson = File.ReadAllText(reportPath);
            Assert.DoesNotContain(TestPass123, reportJson, StringComparison.Ordinal);
        }

        [SevenZipFact]
        public async Task MaxTotalFiles为1_停下来并给出对应停因()
        {
            RequireSevenZip();

            /*
             * 第 0 层产物 = inner.7z + 说明.txt = 2 个文件 > 1，
             * 于是第 1 层在动手之前就被累计文件数拦下（规则 5：上限检查在真正动手之前）。
             */
            (string outer, _) = BuildChainPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                new RecursionLimits { MaxTotalFiles = 1 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "maxfiles"),
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.MaxTotalFilesReached, result.StopReason);
            Assert.True(result.PartiallyCompleted);
            Assert.Single(result.Layers);
            Assert.Contains("文件数", result.Summary, StringComparison.Ordinal);
        }

        [SevenZipFact]
        public async Task 展开比超限_报疑似压缩炸弹()
        {
            RequireSevenZip();

            // 用假引擎把"解压后 200000 字节 / 归档 200 字节"造出来（1000 倍）。
            // 真压缩包很难天然超过 500 倍，而这条上限本身必须被验证 —— 所以这里刻意用假引擎。
            string archivePath = Path.Combine(_root, "bomb.7z");
            File.WriteAllBytes(archivePath, new byte[200]);

            var task = new ArchiveTask(archivePath);
            var extractor = new RecursiveExtractor(
                new FakeEngine(uncompressedSize: 200_000),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                new RecursionLimits { MaxExpansionRatio = 500d });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "bomb"),
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.ExpansionRatioExceeded, result.StopReason);
            Assert.False(result.Completed);
            Assert.Contains("疑似压缩炸弹", result.Summary, StringComparison.Ordinal);

            // 一次都没解 —— 展开比是在动手之前判的。
            Assert.Empty(result.Layers);
        }

        // ────────────────────────────── 密码 ──────────────────────────────

        [SevenZipFact]
        public async Task 密码_先给错再给对能解开且报告里没有明文密码()
        {
            RequireSevenZip();

            string outer = BuildEncryptedInnerPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass", TestPass123 });

            string output = Path.Combine(_root, "out", "pass-ok");
            RecursionResult result = await extractor.ExtractAsync(
                task,
                output,
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.True(result.Completed, result.Summary);
            Assert.True(File.Exists(Path.Combine(output, "secret.txt")), result.Summary);

            // 内层那一层的密码标记只能是脱敏值，且任何报告字段都不能出现明文（规则 9）。
            RecursionLayerReport innerLayer = result.Layers.Single(layer => layer.Depth == 1);
            Assert.Equal("******", innerLayer.UsedPasswordMasked);

            string everything = string.Join(
                "|",
                result.Layers.Select(layer => $"{layer.Status}|{layer.Message}|{layer.UsedPasswordMasked}"))
                + "|" + result.Summary;

            Assert.DoesNotContain(TestPass123, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-pass", everything, StringComparison.Ordinal);

            // 外层是没加密的 zip：第一层走的是空密码。
            Assert.Equal("空密码", result.Layers.Single(layer => layer.Depth == 0).UsedPasswordMasked);
        }

        [SevenZipFact]
        public async Task 密码_候选全试完仍失败报WrongPassword()
        {
            RequireSevenZip();

            string outer = BuildEncryptedInnerPackage();

            // 两个候选都错 + 上限 8 → 候选被试完，属于"密码错误"，不是"达到尝试上限"。
            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass" },
                new RecursionLimits { MaxPasswordAttemptsPerLayer = 8 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "pass-wrong"),
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
            Assert.True(result.PartiallyCompleted, result.Summary);
            Assert.False(result.Completed);
        }

        [SevenZipFact]
        public async Task 密码_候选还有剩余时报PasswordAttemptsExceeded()
        {
            RequireSevenZip();

            string outer = BuildEncryptedInnerPackage();

            /*
             * 三个候选（空、错、对），但每层只允许试 1 个。
             * 第 0 层用空密码就成功了，所以"试 1 个"的限制落在第 1 层：它只试了空密码就停手，
             * 而候选还有剩 —— 这正是"达到密码尝试上限"，与"密码错误"必须区分开（AGENTS.md §9.2）。
             */
            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass", TestPass123 },
                new RecursionLimits { MaxPasswordAttemptsPerLayer = 1 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "pass-limit"),
                RecursionMode.SingleChain,
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.PasswordAttemptsExceeded, result.StopReason);
            Assert.True(result.PartiallyCompleted);
            Assert.Contains("尝试次数上限", result.Summary, StringComparison.Ordinal);
        }

        // ────────────────────────────── 取消 ──────────────────────────────

        [SevenZipFact]
        public async Task 已取消的token_返回UserCancelled且不抛异常()
        {
            RequireSevenZip();

            string source = BuildSourceDir(("readme.txt", "取消用例\n"));
            string archive = Path.Combine(_root, "cancel.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "*"));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = new ArchiveTask(archive);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty });

            // 这里刻意**不包 try/catch**：一旦实现改成"取消就抛"，本用例会直接红掉。
            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "cancelled"),
                RecursionMode.SingleChain,
                null,
                cts.Token);

            Assert.Equal(RecursionStopReason.UserCancelled, result.StopReason);
            Assert.False(result.Completed);
            Assert.False(result.PartiallyCompleted);

            // 一层都没解出来，所以不算"部分完成"；但工作区仍然保留着，方便排查与重试（规则 8）。
            Assert.True(Directory.Exists(result.FinalOutputPath), result.Summary);
            Assert.Contains("工作区", result.Summary, StringComparison.Ordinal);
        }

        // ────────────────────────────── 工作区 ──────────────────────────────

        [Fact]
        public void 工作区_层目录按层号递进()
        {
            var workspace = new ExtractionWorkspace(_root, "task-a");

            WorkspaceLayer first = workspace.CreateNextLayer(Path.Combine(_root, "a.zip"));
            WorkspaceLayer second = workspace.CreateNextLayer(Path.Combine(_root, "b.7z"));

            Assert.Equal(0, first.Depth);
            Assert.Equal(1, second.Depth);
            Assert.EndsWith("layer-000", first.DirectoryPath, StringComparison.Ordinal);
            Assert.EndsWith("layer-001", second.DirectoryPath, StringComparison.Ordinal);
            Assert.EndsWith("output", first.OutputPath, StringComparison.Ordinal);
            Assert.True(Directory.Exists(first.OutputPath));
            Assert.True(Directory.Exists(second.OutputPath));
            Assert.Equal(2, workspace.Layers.Count);
        }

        [Fact]
        public void 工作区_taskId带上级目录时被清洗且删不到root之外的东西()
        {
            string root = Path.Combine(_root, "ws-root");
            Directory.CreateDirectory(root);

            // root 的兄弟目录：它就是"root 之外的任何东西"的代表。
            string sibling = Path.Combine(_root, "sibling");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "keep.txt"), "不能被删掉\n", Utf8NoBom);

            // taskId 写成 ".."：如果实现不做文件名清洗、直接 Path.Combine(root, "..")，
            // 工作区就会落在 root 之外，Cleanup 会去删用户的目录 —— 这是本用例要盯住的失效模式。
            var workspace = new ExtractionWorkspace(root, "..");

            // ① 工作区目录必须仍然是 root 的**直接子目录**（清洗后不可能含路径分隔符）。
            string taskDirectoryName = Path.GetFileName(
                workspace.TaskDirectory.TrimEnd(Path.DirectorySeparatorChar));
            Assert.DoesNotContain("..", taskDirectoryName, StringComparison.Ordinal);
            Assert.Equal(
                Path.GetFullPath(root),
                Path.GetFullPath(Path.GetDirectoryName(workspace.TaskDirectory)!));

            workspace.CreateNextLayer(Path.Combine(_root, "x.zip"));
            Assert.True(Directory.Exists(workspace.TaskDirectory));

            workspace.Cleanup();

            // ② root 之外的东西一个都不能少。
            Assert.True(Directory.Exists(sibling), "工作区之外的目录不该被删");
            Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")), "工作区之外的文件不该被删");
            Assert.True(Directory.Exists(root), "工作区根目录不该被删");
        }

        [Fact]
        public void 工作区_Publish同名文件不覆盖且把改名记进Message()
        {
            string source = Path.Combine(_root, "pub-src");
            Directory.CreateDirectory(Path.Combine(source, "inner"));
            File.WriteAllText(Path.Combine(source, "inner", "a.txt"), "新内容", Utf8NoBom);

            var workspace = new ExtractionWorkspace(Path.Combine(_root, "pub-ws"), "task-pub");
            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(_root, "pack.zip"));
            File.Copy(
                Path.Combine(source, "inner", "a.txt"),
                Path.Combine(layer.OutputPath, "a.txt"));

            string target = Path.Combine(_root, "pub-target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "a.txt"), "旧内容", Utf8NoBom);

            WorkspacePublishResult published = workspace.Publish(target);

            Assert.True(published.Success, published.Message);
            Assert.Equal(1, published.MovedFileCount);

            // 原有文件必须原样保留，新产物换成 a(1).txt，并且这件事要写在 Message 里。
            Assert.Equal("旧内容", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.True(File.Exists(Path.Combine(target, "a(1).txt")));
            Assert.Contains("a(1).txt", published.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 工作区_Publish去掉一层无意义外壳()
        {
            var workspace = new ExtractionWorkspace(Path.Combine(_root, "pub-ws2"), "task-shell");
            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(_root, "pack.zip"));

            // out\pack\文件：外面那层 pack 只是解压器自动加的壳，发布时应当去掉。
            string shell = Path.Combine(layer.OutputPath, "pack");
            Directory.CreateDirectory(shell);
            File.WriteAllText(Path.Combine(shell, "a.txt"), "内容", Utf8NoBom);

            string target = Path.Combine(_root, "pub-target2");
            WorkspacePublishResult published = workspace.Publish(target);

            Assert.True(published.Success, published.Message);
            Assert.True(File.Exists(Path.Combine(target, "a.txt")), published.Message);
            Assert.False(Directory.Exists(Path.Combine(target, "pack")), "无意义外壳应当被去掉");
        }

        // ────────────────────────────── 测试基础设施 ──────────────────────────────

        private async Task<RecursionResult> ExtractAsync(string archivePath, string outputDirectory, RecursionMode mode)
        {
            var task = new ArchiveTask(archivePath);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty });

            return await extractor.ExtractAsync(task, outputDirectory, mode, null, CancellationToken.None);
        }

        /// <summary>从结果里反推某一层产物的目录（第 0 层就是工作区里的 layer-000\output）。</summary>
        private static string ExtractWorkspacePath(RecursionResult result)
        {
            return result.Layers[0].OutputPath;
        }

        /// <summary>在目录下递归找指定文件名的文件；找不到返回 null。</summary>
        private static string? FindFileUnder(string root, string fileName)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory
                .GetFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        /// <summary>造一个单链嵌套包：outer.zip → inner.7z → data.txt（外加一个说明文件）。</summary>
        private (string Outer, string Inner) BuildChainPackage()
        {
            string source = BuildSourceDir(("data.txt", "最内层的内容\n"));

            string inner = Path.Combine(_root, "inner.7z");
            Run7z("a", "-t7z", inner, Path.Combine(source, "*"));

            string outerSource = Path.Combine(_root, "_chain");
            Directory.CreateDirectory(outerSource);
            File.WriteAllText(Path.Combine(outerSource, "说明.txt"), "只有一个内层归档\n", Utf8NoBom);
            File.Copy(inner, Path.Combine(outerSource, "inner.7z"));

            string outer = Path.Combine(_root, "chain.zip");
            Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

            return (outer, inner);
        }

        /// <summary>造一个双分支包：outer.zip 里同时有 a.7z 与 b.7z（外加一个说明文件）。</summary>
        private (string Outer, string BranchA) BuildTwoBranchPackage()
        {
            string sourceA = BuildSourceDir(("a.txt", "分支 A\n"));
            string sourceB = BuildSourceDir(new[] { ("b.txt", "分支 B\n") }, "_srcb");

            string branchA = Path.Combine(_root, "a.7z");
            string branchB = Path.Combine(_root, "b.7z");
            Run7z("a", "-t7z", branchA, Path.Combine(sourceA, "*"));
            Run7z("a", "-t7z", branchB, Path.Combine(sourceB, "*"));

            string outerSource = Path.Combine(_root, "_branches");
            Directory.CreateDirectory(outerSource);
            File.Copy(branchA, Path.Combine(outerSource, "a.7z"));
            File.Copy(branchB, Path.Combine(outerSource, "b.7z"));
            File.WriteAllText(Path.Combine(outerSource, "note.txt"), "两个内层归档\n", Utf8NoBom);

            string outer = Path.Combine(_root, "branches.zip");
            Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

            return (outer, branchA);
        }

        /// <summary>造一个"外层不加密 zip + 内层加密 7z"的包，用于验证密码重试链路。</summary>
        private string BuildEncryptedInnerPackage()
        {
            string source = BuildSourceDir(("secret.txt", "加密内容\n"));

            // -mhe=on：连文件头一起加密，这样"密码错"与"密码对"的差异没有别的旁路可看。
            string inner = Path.Combine(_root, "secret.7z");
            Run7z("a", "-t7z", inner, "-p" + TestPass123, "-mhe=on", Path.Combine(source, "*"));

            string outerSource = Path.Combine(_root, "_enc");
            Directory.CreateDirectory(outerSource);
            File.Copy(inner, Path.Combine(outerSource, "secret.7z"));

            string outer = Path.Combine(_root, "enc-outer.zip");
            Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

            return outer;
        }

        private string BuildSourceDir(params (string Name, string Content)[] files)
        {
            return BuildSourceDir(files, "_src");
        }

        private string BuildSourceDir((string Name, string Content)[] files, string directoryName)
        {
            string directory = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(directory);

            foreach ((string name, string content) in files)
            {
                File.WriteAllText(Path.Combine(directory, name), content, Utf8NoBom);
            }

            return directory;
        }

        private static UTF8Encoding Utf8NoBom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// 调真实 7z.exe 造测试数据。
        /// 这里允许直接起进程：它是**测试**，不是产品代码 —— 产品侧拼 7z 参数只允许存在于
        /// <c>Engines/SevenZip/</c> 内部（AGENTS.md §3.1 四条禁止项）。
        /// </summary>
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

            foreach (object arg in args)
            {
                if (arg is IEnumerable<string> many)
                {
                    foreach (string item in many)
                    {
                        psi.ArgumentList.Add(item);
                    }
                }
                else
                {
                    psi.ArgumentList.Add(arg?.ToString() ?? string.Empty);
                }
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退了就无所谓。
                }

                throw new InvalidOperationException($"7z 造样本超时：{string.Join(' ', psi.ArgumentList)}");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {process.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private void RequireSevenZip()
        {
            /*
             * 正常路径下不会走到这里：没 7z 时 [SevenZipFact] 已经在发现阶段把这批用例跳过了。
             * 留着是为了防"特性探测到了、fixture 却没拿到"这种不一致 ——
             * 那种情况下必须**响亮地失败**，绝不能悄悄跳过（跳过会让整批用例假绿）。
             */
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        /// <summary>
        /// 测试用探测器：先看文件头魔数，再看后缀。
        ///
        /// 为什么这一层要用假的：本模块要验的是"探测到内层归档之后怎么决策"，
        /// 不是"能不能识别格式"。把它做成可控的，才能构造出"恰好 1 个 / 恰好 2 个"的确定局面。
        /// 判断本身仍然按真实规矩来（魔数优先，避免只看后缀把说明文件误判成归档）。
        /// </summary>
        private sealed class MagicAwareProber : IArchiveProber
        {
            public Task<bool> IsArchiveAsync(string filePath, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(HasArchiveSignature(filePath) || HasArchiveExtension(filePath));
            }

            private static bool HasArchiveExtension(string filePath)
            {
                string extension = Path.GetExtension(filePath);

                return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".gz", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>按魔数判断常见归档（0 字节 / 太短的文件一律不是）。</summary>
        private static bool HasArchiveSignature(string filePath)
        {
            try
            {
                Span<byte> header = stackalloc byte[512];

                using FileStream stream = File.OpenRead(filePath);
                int read = stream.Read(header);

                if (read < 6)
                {
                    return false;
                }

                ReadOnlySpan<byte> bytes = header[..read];

                // 7z：37 7A BC AF 27 1C
                if (bytes[0] == 0x37 && bytes[1] == 0x7A && bytes[2] == 0xBC &&
                    bytes[3] == 0xAF && bytes[4] == 0x27 && bytes[5] == 0x1C)
                {
                    return true;
                }

                // zip（含空归档 50 4B 05 06、分卷 50 4B 07 08）
                if (bytes[0] == 0x50 && bytes[1] == 0x4B &&
                    (bytes[2] == 0x03 || bytes[2] == 0x05 || bytes[2] == 0x07))
                {
                    return true;
                }

                // rar4 / rar5：52 61 72 21 1A 07
                if (bytes[0] == 0x52 && bytes[1] == 0x61 && bytes[2] == 0x72 &&
                    bytes[3] == 0x21 && bytes[4] == 0x1A && bytes[5] == 0x07)
                {
                    return true;
                }

                // gzip：1F 8B
                if (bytes[0] == 0x1F && bytes[1] == 0x8B)
                {
                    return true;
                }

                // tar：偏移 257 处是 "ustar"
                if (bytes.Length >= 262 &&
                    bytes[257] == (byte)'u' && bytes[258] == (byte)'s' &&
                    bytes[259] == (byte)'t' && bytes[260] == (byte)'a' &&
                    bytes[261] == (byte)'r')
                {
                    return true;
                }

                return false;
            }
            catch
            {
                // 读不出来（占用 / 权限）时按"不是归档"处理，与 IArchiveProber 的约定一致。
                return false;
            }
        }

        /// <summary>
        /// 只用于"展开比超限"这条：真压缩包很难天然造出 500 倍展开比。
        /// 其余行为（成功判定、错误分类）都尽量走真引擎，不用这个。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            private readonly long _uncompressedSize;

            public FakeEngine(long uncompressedSize)
            {
                _uncompressedSize = uncompressedSize;
            }

            public string Id => "fake";

            public string DisplayName => "假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = _uncompressedSize,
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                // 假引擎也要产出真实文件：否则后面"量产物"的代码路径根本没跑到。
                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllText(Path.Combine(outputPath, "payload.bin"), "假引擎产物", Utf8NoBom);
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }
        }
    }
}
