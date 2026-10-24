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
    // 递归工作区的根是**进程级静态**（<c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>）：
    // 本文件只读它，别的用例类会改写它 —— 全量并发跑时被抢走会让工作区落到别的用例的临时目录里、
    // 那个目录又被对方 Dispose 掉（现场：`工作区删不掉` 断言 "第 0 层的产物目录不存在"）。
    // 所以与改写它的那些类一起**串行**跑，见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
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

            RecursionResult result = await ExtractAsync(archive, output);

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
        // ────────────── 单链档的判据：只看**有几个真归档**（用户 2026-10-04 当场推翻旧口径） ──────────────

        /// <summary>
        /// <b>用例③（用户点名的那一格）</b>：单链档 + 这一层里**只有 1 个**内层归档 + 旁边还有
        /// **非说明类**文件（`.mp4` / `.pdf`）⇒ **必须继续解**。
        ///
        /// <para>用户原话：「第6集.7z 那层内层包只有 1 个，但旁边还有不属于"说明类"的文件 ⇒ 被算成"多分支"
        /// —— <b>这是压缩包吗，不是那你停什么</b>」。</para>
        ///
        /// <para><b>红检</b>：把判据改回旧的"同层除该包以外全是说明类文件"
        /// （<c>toProcess.Count == 1 &amp;&amp; HasOnlyInformationalSiblings(…)</c>）⇒ 本用例当场红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 单链档_一层里只有1个内层包_旁边有非归档文件也照旧继续解()
        {
            RequireSevenZip();

            (string outer, _) = BuildSingleInnerWithNonInformationalSiblings();

            string output = Path.Combine(_root, "out", "single-inner-noninfo");
            var logLines = new List<(string Level, string Message)>();

            RecursionResult result = await ExtractAsync(outer, output, logLines);

            // ① 真的往下解了：内层包的内容落到"内层包自己那一层"里。
            Assert.True(result.Completed, result.Summary);
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);
            Assert.Empty(result.UnexpandedNames);
            Assert.True(
                File.Exists(Path.Combine(output, "sibling-inner", "data.txt")),
                "单链档遇到「1 个内层包 + 旁边有 .mp4/.pdf」必须继续解，实际产物树："
                + string.Join(" | ", Directory.GetFileSystemEntries(output, "*", SearchOption.AllDirectories))
                + $"\n{result.Summary}");

            // ② 旁边那两个非归档文件照旧留在这一层（它们不是内层归档，也不该被吃掉）。
            Assert.True(File.Exists(Path.Combine(output, "movie.mp4")), result.Summary);

            /*
             * ③ 行为不许静默：日志里必须留下"第 1 层真的开工"那一行。
             * ⚠ 2026-10-21：老断言钉的是 `只看内层归档的数量` 那句 —— 它属于**已删的档位**
             * （单层/单链两档随迁移删除，现在一律展开所有分支），这句话不再写。
             * 本条要证明的事实没变（1 个内层包 + 旁边有非归档文件 ⇒ 照样往下解），
             * 所以改成钉"内层包真的被当成下一层开工了"。
             */
            Assert.Contains(
                logLines,
                line => line.Message.Contains("第 1 层", StringComparison.Ordinal)
                        && line.Message.Contains("sibling-inner.7z", StringComparison.Ordinal));
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
            RecursionResult result = await ExtractAsync(outer, output);

            Assert.True(result.Completed, result.Summary);
            Assert.True(result.Layers.Count >= 2, $"应至少解出 2 层，实际 {result.Layers.Count}；{result.Summary}");

            // 最内层的文件必须出现在最终目录里，而且在**内层包自己那一层**里面
            // （用户 2026-09-30 中午：就地替换 —— 被解开的包在原位置留下一个以它命名的文件夹）；
            // 中间层那个 inner.7z 本身不该再留在产物里。
            Assert.True(File.Exists(Path.Combine(output, "inner", "data.txt")), result.Summary);
            Assert.False(File.Exists(Path.Combine(output, "inner.7z")), "内层归档已被展开，不该再出现在最终产物里");

            // 外层自己的文件原地不动。
            Assert.True(File.Exists(Path.Combine(output, "说明.txt")), result.Summary);

            Assert.Equal(new[] { 0, 1 }, result.Layers.Select(layer => layer.Depth).ToList());

            // 每一层都必须成功（失败会在 Report 里带上原因，这里顺带把它当失败信息用）。
            foreach (RecursionLayerReport layer in result.Layers)
            {
                Assert.True(layer.Success, layer.Message);
            }
        }

        // ─────────────────── 就地替换（用户 2026-09-30 中午的整棵树例子） ───────────────────
        //
        // 用户给的那棵树（这里用**真 7z 造的真归档**跑整条递归链，逐字对照产物）：
        //   AAA/  ├── 1.mp4 2.mp4 3.mp4                        # 真文件，原地不动
        //         ├── DDDD.mp4 EEEE.mp4 FFFF.mp4                # 是压缩包 ⇒ DDDD\内容物、EEEE\内容物、FFFF\内容物
        //         ├── BBBB/  ├── CCCCC.mp4 → BBBB\CCCCC\内容物
        //         │          └── DDDDD.mp4 → BBBB\DDDDD\内容物
        //         └── CCCC/  ├── EEEEE.mp4 → CCCC\EEEEE\内容物
        //                    └── DDDDD/ └── EEEEEE.mp4 → CCCC\DDDDD\EEEEEE\内容物
        //
        // ⛔ 判据是"**就地**"两个字：每一个包都留在**它原来那个位置**换成一个同名文件夹，
        // 而不是被搬到顶层再各建一层。

        [SevenZipFact]
        public async Task 就地替换_用户极端例子的整棵树逐字成立()
        {
            RequireSevenZip();

            string source = Path.Combine(_root, "_extreme");
            Directory.CreateDirectory(source);

            foreach (string name in new[] { "1.mp4", "2.mp4", "3.mp4" })
            {
                File.WriteAllText(Path.Combine(source, name), "真文件，一个字节都不许动\n", Utf8NoBom);
            }

            // 七个"后缀是假的、内容是真的"的压缩包：`DDDD.mp4` 装的是 `内容物\DDDD.bin`。
            foreach ((string archive, string relativePath) in new[]
                     {
                         ("DDDD", "DDDD.mp4"),
                         ("EEEE", "EEEE.mp4"),
                         ("FFFF", "FFFF.mp4"),
                         ("CCCCC", @"BBBB\CCCCC.mp4"),
                         ("DDDDD", @"BBBB\DDDDD.mp4"),
                         ("EEEEE", @"CCCC\EEEEE.mp4"),
                         ("EEEEEE", @"CCCC\DDDDD\EEEEEE.mp4")
                     })
            {
                string target = Path.Combine(source, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(BuildInnerPackage(archive), target);
            }

            string outer = Path.Combine(_root, "AAA.zip");
            Run7z("a", "-tzip", outer, Path.Combine(source, "*"));

            string output = Path.Combine(_root, "out", "AAA");
            RecursionResult result = await ExtractAsync(outer, output);

            Assert.True(result.Completed, result.Summary);
            Assert.Equal(8, result.Layers.Count);

            Assert.True(
                new[]
                {
                    "1.mp4",
                    "2.mp4",
                    "3.mp4",
                    "BBBB",
                    @"BBBB\CCCCC",
                    @"BBBB\CCCCC\内容物",
                    @"BBBB\CCCCC\内容物\CCCCC.bin",
                    @"BBBB\DDDDD",
                    @"BBBB\DDDDD\内容物",
                    @"BBBB\DDDDD\内容物\DDDDD.bin",
                    "CCCC",
                    @"CCCC\DDDDD",
                    @"CCCC\DDDDD\EEEEEE",
                    @"CCCC\DDDDD\EEEEEE\内容物",
                    @"CCCC\DDDDD\EEEEEE\内容物\EEEEEE.bin",
                    @"CCCC\EEEEE",
                    @"CCCC\EEEEE\内容物",
                    @"CCCC\EEEEE\内容物\EEEEE.bin",
                    "DDDD",
                    @"DDDD\内容物",
                    @"DDDD\内容物\DDDD.bin",
                    "EEEE",
                    @"EEEE\内容物",
                    @"EEEE\内容物\EEEE.bin",
                    "FFFF",
                    @"FFFF\内容物",
                    @"FFFF\内容物\FFFF.bin"
                }.SequenceEqual(Tree(output)),
                "产物树与用户给的形状不一致。实际：" + string.Join(" | ", Tree(output))
                + "　结论：" + result.Summary);

            // 被解开的包一个都不许留在产物里（原地让位给同名目录）。
            foreach (string consumed in new[]
                     {
                         "DDDD.mp4", "EEEE.mp4", "FFFF.mp4",
                         @"BBBB\CCCCC.mp4", @"BBBB\DDDDD.mp4",
                         @"CCCC\EEEEE.mp4", @"CCCC\DDDDD\EEEEEE.mp4"
                     })
            {
                Assert.False(File.Exists(Path.Combine(output, consumed)), consumed + " 应该已经被就地替换成目录");
            }
        }

        [SevenZipFact]
        public async Task 普通文件夹_1111里面包着真内容物_真7z下不许摊平()
        {
            RequireSevenZip();

            /*
             * 用户原话："如果是一个文件夹 1111 里面包裹真正的内容物，这个时候你就会把 1111 省略，
             * 这是非常大忌。1111 只是一个文件夹名字，我们不能去假设原打包人的逻辑。"
             *
             * 老口径（发布侧摊"无意义外壳" + 判定表 ④ 单链塌缩）都会把 `1111\真内容` 一路摊到一层，
             * 于是 `1111` 这个名字直接消失。这里跑真 7z，断言的是**磁盘上的产物**。
             */
            string source = Path.Combine(_root, "_plain-folder");
            Directory.CreateDirectory(Path.Combine(source, "1111", "真内容"));
            File.WriteAllText(Path.Combine(source, "1111", "真内容", "a.txt"), "1111 里面的真内容\n", Utf8NoBom);
            File.Copy(BuildInnerPackage("inner"), Path.Combine(source, "inner.7z"));

            string outer = Path.Combine(_root, "plain-folder.zip");
            Run7z("a", "-tzip", outer, Path.Combine(source, "*"));

            string output = Path.Combine(_root, "out", "plain");
            RecursionResult result = await ExtractAsync(outer, output);

            Assert.True(result.Completed, result.Summary);

            // `1111` 与它下面那层 `真内容` 都在（一个都没被当成"无意义外壳"摊掉）。
            Assert.True(
                File.Exists(Path.Combine(output, "1111", "真内容", "a.txt")),
                "`1111\\真内容` 被摊平了。实际产物：" + string.Join(" | ", Tree(output)));

            Assert.False(Directory.Exists(Path.Combine(output, "真内容")), "`真内容` 不许被提上来当壳");
            Assert.False(File.Exists(Path.Combine(output, "a.txt")), "`1111\\真内容` 两层都不许被摊平");

            // 内层包就地换成了同名文件夹。
            Assert.True(File.Exists(Path.Combine(output, "inner", "内容物", "inner.bin")), result.Summary);
        }

        /// <summary>
        /// **单链档的判据只看"这一层有几个真归档"** —— 同层出现一个**非说明类**文件（`.bin`）
        /// **不再**是停下来的理由。
        ///
        /// <para><b>⚠ 2026-10-04 改结论（用户当场推翻旧口径）</b>：老用例名与断言是
        /// "只有说明类文件时才自动继续 / 其它文件存在时改为询问" —— 用户原话（真机 `第6集.7z`）：
        /// 「那层内层包只有 1 个，但旁边还有不属于"说明类"的文件 ⇒ 被算成"多分支" ——
        /// **这是压缩包吗，不是那你停什么**」。
        /// 现在 1 个内层归档一律继续解（旁边是什么文件都不影响），≥2 个才停。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 单链档_旁边有非说明类文件也照旧继续解()
        {
            RequireSevenZip();

            // 同层除内层归档外出现一个"非说明类"文件（.bin）：旧口径会拿它当"多分支"停下。
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
            RecursionResult result = await ExtractAsync(outer, output);

            Assert.Equal(RecursionStopReason.Completed, result.StopReason);
            Assert.Empty(result.UnexpandedNames);

            // 内层包真的被解开了（旁边那个 .bin 没有拦住它）。
            Assert.True(
                FindFileUnder(output, "data.txt") != null,
                $"内层包的内容必须解出来，实际产物树："
                + string.Join(" | ", Directory.GetFileSystemEntries(output, "*", SearchOption.AllDirectories))
                + $"\n{result.Summary}");

            // 旁边那个非说明类文件照旧留着。
            Assert.True(FindFileUnder(output, "payload.bin") != null, result.Summary);
        }

        // ────────────────────────────── 多分支 ──────────────────────────────
        [SevenZipFact]
        public async Task AllBranches模式_两个分支都展开且不再询问()
        {
            RequireSevenZip();

            (string outer, _) = BuildTwoBranchPackage();

            string output = Path.Combine(_root, "out", "allbranches");

            // AllBranches 是"用户已经明确要求展开所有分支"，所以不该再出现询问。
            RecursionResult result = await ExtractAsync(outer, output);

            Assert.True(result.Completed, result.Summary);
            Assert.Null(result.Decision);
            Assert.True(result.Layers.Count >= 3, $"外层 + 两个分支至少 3 层，实际 {result.Layers.Count}");

            // 每个分支**各自一层自己的包名**（用户 2026-09-30 中午的就地替换：
            // `a.7z` 在原位置留下 `a\`、`b.7z` 留下 `b\`，⛔ 不许"多个分支共用一个名字"）。
            Assert.True(File.Exists(Path.Combine(output, "a", "a.txt")), result.Summary);
            Assert.True(File.Exists(Path.Combine(output, "b", "b.txt")), result.Summary);

            // 外层的说明文件原地不动。
            Assert.True(File.Exists(Path.Combine(output, "note.txt")), result.Summary);
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
                null,
                CancellationToken.None);

            Assert.True(result.Completed, result.Summary);

            // 内层包 `secret.7z` 在原位置留下 `secret\`（就地替换），内容物放进去。
            Assert.True(File.Exists(Path.Combine(output, "secret", "secret.txt")), result.Summary);

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

            /*
             * ⚠ 内层包刻意**不用** `-mhe`（头不加密 ⇒ 清单列得出来）：`-mhe` 的包连清单都读不出来，
             * 2026-10-05 起收尾落的是「文件名已加密」那一档（与单层路径对齐，见
             * `RecursionGateAndHintFixesTests` 那三条），轮不到这里要验的"密码错误 / 达到上限"。
             *
             * 本用例要钉的是：**候选全试完了都不对 ⇒ 密码错误**（不是「达到尝试上限」）。
             */
            string outer = BuildEncryptedInnerPackage(encryptFileNames: false);

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
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
            Assert.True(result.PartiallyCompleted, result.Summary);
            Assert.False(result.Completed);
        }

        /// <summary>
        /// **报错取"走得最远的那一次"**（用户 2026-09-30 真机）：7z `-mhe` 四卷包里，正确密码那一次
        /// 真解出 17.7 GiB 才失败，紧跟其后的错密码候选 50 毫秒就被顶回来 —— 老口径收尾用
        /// `lastFailure`，报出来的就是**最后那个错候选**的原话，真原因（数据校验不过）被吃掉。
        ///
        /// <para>判据只看事实：这一次尝试在产物目录里留下过多少字节。</para>
        ///
        /// <para><b>红检</b>：把 `informativeFailure` 那两行撤掉（收尾改回 `lastFailure`）→
        /// 本用例立刻变红（Message 变成 <c>Cannot open encrypted archive. Wrong password?</c>）。</para>
        /// </summary>
        [Fact]
        public async Task 候选循环_报错取走得最远的那一次_不是最后一次()
        {
            string source = Path.Combine(_root, "widest-failure-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();

            engine.OnExtractAsync = (request, options) =>
            {
                if (string.Equals(request.Password, "first", StringComparison.Ordinal))
                {
                    // 这一次"走得很远"：真的留下了产物，然后才失败（数据校验不过）。
                    // 输出路径由本测试自己发请求时给全，`!` 只为消掉 CS8604（`ExtractRequest.OutputPath` 是可空字符串），不改语义。
                    Directory.CreateDirectory(request.OutputPath!);
                    File.WriteAllBytes(Path.Combine(request.OutputPath!, "partial.bin"), new byte[4096]);

                    return Task.FromResult(new ArchiveOperationResult
                    {
                        Success = false,
                        Status = StatusText.ExtractFailed,
                        Message = "CRC Failed in encrypted file. Wrong password? : partial.bin",
                        DetectedErrorType = "WrongPassword",
                        EngineId = engine.Id
                    });
                }

                // 后面几个候选连门都没进去，一个字节都没留下。
                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.WrongPassword,
                    Message = "Cannot open encrypted archive. Wrong password?",
                    DetectedErrorType = "WrongPassword",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "first", "second", "third" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-widest-failure"),
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
            Assert.Contains("CRC Failed", result.Layers[0].Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Cannot open encrypted archive", result.Layers[0].Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// **递归那条路也要认「密码已被这一趟解压证实」**（用户 2026-10-01 真机 `giu910` 重跑一次又白等 13 分钟）。
        ///
        /// <para>现场：那一档修复先只接在**单层**候选循环上（`ExtractionCoordinator`），
        /// 而真机 `giu910` 走的是**递归**那条（`SingleChain`）—— 于是候选 3 把 17.7 GiB 解到 98%、
        /// 只死在一个 mp4 的 CRC 上，程序照样把它当成"这个密码候选不对"，回去把候选 4..10 又试一遍，
        /// 13 分钟的产物全扔、结论写「密码错误」。用户原话：
        /// 「**为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候**」。</para>
        ///
        /// <para><b>红检</b>：把 <c>RecursiveExtractor</c> 里那一档（<see cref="ProducedContentGate"/> 那个
        /// <c>if</c>）撤掉 → 本用例立刻变红（只试了 1 个候选变成 3 个候选全试完、结论从"压缩包损坏"
        /// 变回"密码错误"）。</para>
        /// </summary>
        [Fact]
        public async Task 递归候选循环_解出了大半个包之后失败_不再试下一个候选()
        {
            string source = Path.Combine(_root, "crc-proven-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();
            int extractCalls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                extractCalls++;

                // 第一趟（正确密码）：真解出两个非空文件才在一个文件上 CRC 失败 —— 真机那一单的形状。
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "good-1.mp4"), new byte[8192]);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "good-2.mp4"), new byte[4096]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.ExtractFailed,
                    Message = "Sub items Errors: 1 | ERROR: CRC Failed in encrypted file. Wrong password? : bad.mp4",
                    DetectedErrorType = "WrongPassword",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "right-password", "wrong-1", "wrong-2" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-crc-proven"),
                null,
                CancellationToken.None);

            // ⛔ 一个候选就够：密码已经被这一趟解压本身证明了，再试下去纯粹是浪费用户的时间。
            Assert.Equal(1, extractCalls);

            // 结论必须落在"数据坏了"，⛔ 不是「密码错误」（否则用户会拿着密码本白核对半天）。
            Assert.Equal(RecursionStopReason.Corrupted, result.StopReason);
            Assert.Contains("密码已经证实是对的", result.Layers[0].Message, StringComparison.Ordinal);
            Assert.Contains("CRC Failed", result.Layers[0].Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 对照：**只解出一个文件**时照老口径继续试下一个候选（保守边界，⛔ 不许放宽）。
        ///
        /// <para>「错密码」与「密码对、文件坏」在这一档上分不开 —— 真机那 10 个错候选里就有留下一个文件的。
        /// 所以门槛是"至少两个非空文件"，一个文件必须继续试。</para>
        ///
        /// <para>⚠ 2026-10-21 订正：**「结果相同 ⇒ 提前停」只对"引擎已经接受密码"那一支成立**
        /// （引擎说成功、产物没通过校验）。错密码这一支**不许**用它停：加密包的错密码也会在产物目录里
        /// 留下一模一样的半截数据，而正确密码排在后面时产物完全不同 —— 实测
        /// `Item45LogAndPasswordTests.真7z_先试错两个候选再成功_成功档照样只留一行` 因此在全量里变红
        /// （候选 3 才是对的，循环在候选 2 就停了）。⇒ 本条恢复"三个候选都试"。</para>
        /// </summary>
        [Fact]
        public async Task 递归候选循环_只解出一个文件时_仍按老口径继续试下一个候选()
        {
            string source = Path.Combine(_root, "one-file-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();
            int extractCalls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                extractCalls++;

                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "only-one.bin"), new byte[4096]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.WrongPassword,
                    Message = "Cannot open encrypted archive. Wrong password?",
                    DetectedErrorType = "WrongPassword",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "c1", "c2", "c3" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-one-file"),
                null,
                CancellationToken.None);

            // 三个候选全试完（错密码这一支不许因为"结果一样"提前停）。
            Assert.Equal(3, extractCalls);
            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
        }

        /// <summary>
        /// 两义那一档（引擎说"密码可能不对、也可能数据坏了"）在**真解出了内容**之后当场收敛成数据损坏 ——
        /// 密码已经不需要再猜了（用户 2026-10-01 真机 `giu910` 走的就是 7-Zip 那句
        /// `CRC Failed in encrypted file. Wrong password?`）。
        /// </summary>
        [Fact]
        public async Task 递归候选循环_两义那一档解出了内容之后_当场收敛成数据损坏()
        {
            string source = Path.Combine(_root, "ambiguous-proven-source.rar");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();
            int extractCalls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                extractCalls++;

                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "a.bin"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "b.bin"), new byte[2048]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.PasswordOrCorrupted,
                    Message = "在加密文件 X 里校验和错误。文件已损坏或密码错误。",
                    DetectedErrorType = "PasswordOrCorrupted",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "p1", "p2", "p3" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-ambiguous-proven"),
                null,
                CancellationToken.None);

            Assert.Equal(1, extractCalls);
            Assert.Equal(RecursionStopReason.Corrupted, result.StopReason);
            Assert.Contains("密码已经证实是对的", result.Layers[0].Message, StringComparison.Ordinal);
        }

        [SevenZipFact]
        public async Task 密码_候选还有剩余时报PasswordAttemptsExceeded()
        {
            RequireSevenZip();

            /*
             * ⚠ 这个包**保持** `-mhe`（连文件名一起加密）：正因为它连清单都读不出来，
             * 第 1 层才会"一个候选都没成功"就撞上"每层只试 1 个"的上限 —— 收尾必须是
             * 「达到密码尝试上限」（候选还有剩、再试就能开），⛔ **不许**被「文件名已加密」那一档盖掉
             * （判据只覆盖"密码错误"那一档，见 RecursiveExtractor 里那段说明）。
             */
            string outer = BuildEncryptedInnerPackage();

            /*
             * 三个候选（空、错、对），但每层只允许试 1 个。
             * 第 0 层用空密码就成功了，所以"试 1 个"的限制落在第 1 层：它先是跳过空密码
             * （`-mhe` 那一档：列目录报加密头 ⇒ 空密码必然白跑），再试了第一个非空候选就停手，
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

        /// <summary>
        /// ⛔ **发布这一步一个外壳都不摊**（2026-10-02 真机修）：归档自带的文件夹原样保留。
        ///
        /// <para>老口径把"只有一个子目录、没有同级文件"的那一层当"解压器自动加的壳"摊掉 ——
        /// 真机现场 <c>风景01.7z.001</c>（只解一层、内容全在一个 <c>风景\</c> 文件夹里）就这样变成了
        /// <c>&lt;包名&gt;\1.mp4</c>，用户的文件夹被吃过一层、日志里一个字都没提。用户 2026-09-30 的红线：
        /// 「如果是一个文件夹 1111 里面包裹真正的内容物，这个时候你就会把 1111 省略，这是非常大忌」。</para>
        ///
        /// <para>摊不摊"哪一层留"由**唯一出口** <c>ResultFinalizer</c> 决定（同名套娃 <c>pack\pack\</c>
        /// 由它自己的"同名不套层"那一条收掉）。</para>
        /// </summary>
        [Fact]
        public void 工作区_Publish不摊任何外壳_归档自带的文件夹原样保留()
        {
            var workspace = new ExtractionWorkspace(Path.Combine(_root, "pub-ws2"), "task-shell");
            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(_root, "pack.zip"));

            // out\pack\文件：这一层 `pack` 是打包人自己的结构，发布时**原样保留**。
            string shell = Path.Combine(layer.OutputPath, "pack");
            Directory.CreateDirectory(shell);
            File.WriteAllText(Path.Combine(shell, "a.txt"), "内容", Utf8NoBom);

            string target = Path.Combine(_root, "pub-target2");
            WorkspacePublishResult published = workspace.Publish(target);

            Assert.True(published.Success, published.Message);

            Assert.True(
                File.Exists(Path.Combine(target, "pack", "a.txt")),
                "归档自带的文件夹必须原样保留（摊平它是用户的红线）：" + published.Message);

            Assert.False(
                File.Exists(Path.Combine(target, "a.txt")),
                "⛔ 不许把归档自带的文件夹摊平到发布目标根上");
        }

        /// <summary>
        /// ⛔ **展开了内层包时，发布不许摊掉"内层包自己产出的那层文件夹"**（用户 2026-09-30 红线：
        /// 落点最少两层，最里层 = 最后一个压缩包那一层）。
        ///
        /// <para>
        /// 真机现场 <c>26081118.7z</c>：里面只有一个内层包，内层包解出
        /// <c>T 小小绘 推特大合集 330P+454V-9.31G\P|V</c>。丢 <c>T …</c> 的**真凶就是发布这一步** ——
        /// 它把"只有一个子目录、没有同级文件"的那一层当"解压器自动加的壳"摊掉了，
        /// 于是暂存区里只剩 <c>P</c>、<c>V</c>，定稿侧那两条分支只是补刀。
        /// </para>
        ///
        /// <para>
        /// 本用例走**整条递归**（真实 7z 造两个包），因此同时钉住
        /// <c>RecursiveExtractor.BuildResult</c> 有没有把"展开了内层包"这一个事实传给发布 ——
        /// 只测 <see cref="ExtractionWorkspace.Publish"/> 自己的用例（<c>TwoLayerLayoutTests</c>）
        /// 钉不住这根线。
        /// </para>
        /// </summary>
        [SevenZipFact]
        public async Task 递归展开内层包_发布时保留内层包自己产出的那层文件夹()
        {
            RequireSevenZip();

            // 内层包：解出来是一个文件夹 T 内层包产出\P\a.jpg（现场那个形状）。
            string innerSource = Path.Combine(_root, "inner-src");
            string innerFolder = Path.Combine(innerSource, "T 内层包产出", "P");
            Directory.CreateDirectory(innerFolder);
            File.WriteAllText(Path.Combine(innerFolder, "a.jpg"), "内容物", Utf8NoBom);

            string innerArchive = Path.Combine(_root, "inner.7z");
            Run7z("a", "-t7z", innerArchive, Path.Combine(innerSource, "*"));

            // 外层包：里面只有那个内层包。
            string outerSource = Path.Combine(_root, "outer-src");
            Directory.CreateDirectory(outerSource);
            File.Copy(innerArchive, Path.Combine(outerSource, "inner.7z"));

            string outerArchive = Path.Combine(_root, "two-layer-outer.7z");
            Run7z("a", "-t7z", outerArchive, Path.Combine(outerSource, "*"));

            string output = Path.Combine(_root, "out", "two-layer");

            RecursionResult result = await ExtractAsync(outerArchive, output);

            Assert.True(result.Completed, result.Summary);
            Assert.Equal(2, result.Layers.Count);

            // `T 内层包产出` 那一层必须还在（它是内层包自己产出的结构），P 待在它里面；
            // 而它外面还套着**内层包自己那一层**（`inner.7z` → `inner\`，用户 2026-09-30 的就地替换）。
            Assert.True(
                File.Exists(Path.Combine(output, "inner", "T 内层包产出", "P", "a.jpg")),
                "内层包自己产出的那层文件夹被摊掉了。实际目录树："
                + string.Join(" | ", Directory.GetFileSystemEntries(output, "*", SearchOption.AllDirectories))
                + $"\n结论：{result.Summary}");

            // ⛔ 老形状（P 直接躺在发布目标根上，或者 `T 内层包产出` 直接躺在发布目标根上）不许再出现。
            Assert.False(
                Directory.Exists(Path.Combine(output, "P")),
                "P 不许被摊到发布目标根上（那正是真机上 …\\26081118\\P 的形状）");
            Assert.False(
                Directory.Exists(Path.Combine(output, "T 内层包产出")),
                "内层包自己那一层（`inner\\`）不许被摊掉");
        }

        // ─────────────────── 工作区清理（成功后不留 data\work\recursive 垃圾） ───────────────────
        //
        // 这一组盯的是**磁盘泄漏**：递归工作区动辄几百 MB，成功后必须清掉；失败 / 取消 / 部分完成
        // 必须原样保留（那时的中间产物是用户唯一的线索）。清理只许针对**本实例自己持有的那个**工作区，
        // 绝不能按目录名或"最新目录"去扫 —— 那会删掉并发任务正在用的工作区。

        [SevenZipFact]
        public async Task 递归成功_工作区被清理且产物仍在()
        {
            RequireSevenZip();

            string source = BuildSourceDir(("readme.txt", "成功之后不该留工作区\n"));
            string archive = Path.Combine(_root, "cleanup-ok.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "*"));

            string output = Path.Combine(_root, "out", "cleanup-ok");
            var log = new List<(string Level, string Message)>();

            var task = new ArchiveTask(archive);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                task,
                output,
                null,
                CancellationToken.None);

            Assert.True(result.Completed, result.Summary);

            // 产物必须真的在最终目录里：清工作区不能把产物一起清掉。
            Assert.True(File.Exists(Path.Combine(output, "readme.txt")), result.Summary);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            Assert.False(Directory.Exists(workspaceDirectory), $"成功之后工作区不该还在：{workspaceDirectory}");

            // 删什么、为什么：删前一条 INFO（带路径）、删后一条 INFO；本次不该有 WARN。
            Assert.Contains(
                log,
                x => x.Level == "INFO" &&
                     x.Message.Contains("清理本次任务的工作区", StringComparison.Ordinal) &&
                     x.Message.Contains(workspaceDirectory, StringComparison.Ordinal));
            Assert.Contains(
                log,
                x => x.Level == "INFO" &&
                     x.Message.Contains("工作区已清理", StringComparison.Ordinal) &&
                     x.Message.Contains(workspaceDirectory, StringComparison.Ordinal));
            Assert.DoesNotContain(log, x => x.Level == "WARN");
        }

        [SevenZipFact]
        public async Task 递归失败_工作区保留()
        {
            RequireSevenZip();

            // 外层没加密能解开、内层加密（-mhe）只给错密码 → 停在第 1 层，这一份工作区必须留着。
            string outer = BuildEncryptedInnerPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "keep-on-failure"),
                null,
                CancellationToken.None);

            /*
             * ⚠ 2026-10-05：这个内层包是 `-mhe`（连文件名一起加密）⇒ 每个候选都列不出清单、
             * 引擎给的结构化错误类型是"加密头" ⇒ 收尾落「文件名已加密」那一档
             * （与单层路径对齐；以前这里落的是「密码错误」）。
             * 本用例钉的是**工作区保留**，与结论落在哪一档无关 —— 所以只跟着改这一个期望值。
             */
            Assert.Equal(RecursionStopReason.EncryptedHeaders, result.StopReason);
            Assert.False(result.Completed);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            Assert.True(Directory.Exists(workspaceDirectory), "失败之后工作区必须保留：中间产物是用户唯一的线索");
            Assert.True(
                File.Exists(Path.Combine(workspaceDirectory, "report.json")),
                "失败之后崩溃恢复报告也该留着");
        }

        [Fact]
        public async Task 递归取消_工作区保留()
        {
            string archive = Path.Combine(_root, "cancel-mid.zip");
            File.WriteAllBytes(archive, new byte[200]);

            using var cts = new CancellationTokenSource();

            var engine = new FakeEngine();
            engine.OnExtractAsync = (request, _) =>
            {
                // 解压途中用户按下取消：引擎这一单以"已取消"收场（真实 7z 被杀掉也是这样）。
                cts.Cancel();

                WritePayload(request.OutputPath);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.Cancelled,
                    Message = "已取消",
                    DetectedErrorType = "Cancelled"
                });
            };

            var task = new ArchiveTask(archive);
            var extractor = new RecursiveExtractor(engine, new MagicAwareProber(), _ => new[] { string.Empty });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "cancel-mid"),
                null,
                cts.Token);

            Assert.Equal(RecursionStopReason.UserCancelled, result.StopReason);
            Assert.False(result.Completed);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            Assert.True(Directory.Exists(workspaceDirectory), "取消之后工作区必须保留（半截产物是用户唯一的线索）");
        }

        /// <summary>
        /// 工作区删不掉（被占用 / 权限）时：只写一条 WARN，**绝不让已经成功的任务变成失败**
        /// （与单层路径的 CleanupTaskWorkspaceDirectory 同一口径）。产物该发布的照样发布。
        /// </summary>
        [Fact]
        public async Task 工作区删不掉_只写WARN且任务仍然成功()
        {
            string archivePath = Path.Combine(_root, "locked-ws.zip");
            File.WriteAllBytes(archivePath, new byte[200]);

            string output = Path.Combine(_root, "out", "locked-ws");
            var log = new List<(string Level, string Message)>();

            /*
             * 一定要让"解压"先停住：假引擎返回的是**已完成**的 Task，整条递归会同步跑到底，
             * 那时工作区已经被删掉了，测试根本没机会往里面放东西（第一次写这条用例就是这么红的）。
             */
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var engine = new FakeEngine();
            engine.OnExtractAsync = async (request, _) =>
            {
                await gate.Task.ConfigureAwait(false);

                WritePayload(request.OutputPath);

                return Succeeded();
            };

            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            Task<RecursionResult> run = extractor.ExtractAsync(
                new ArchiveTask(archivePath),
                output,
                null,
                CancellationToken.None);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            /*
             * 在本次任务自己的工作区根上放一个**被独占打开**的文件（发布只搬各层产物，不碰它），
             * 于是收尾那次 Directory.Delete(recursive: true) 必然失败。
             */
            string lockedPath = Path.Combine(workspaceDirectory, "locked.bin");
            File.WriteAllBytes(lockedPath, new byte[16]);

            RecursionResult result;

            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 放好之后才放行：收尾那次删除必然撞上这个句柄。
                gate.TrySetResult(true);

                result = await run;
            }

            Assert.True(result.Completed, result.Summary);
            Assert.True(Directory.Exists(workspaceDirectory), "删不掉时目录应当原样保留，不能假装删了");
            Assert.Contains(
                log,
                x => x.Level == "WARN" && x.Message.Contains("清理工作区失败", StringComparison.Ordinal));

            // 删不掉工作区不影响任务结论与产物发布。
            Assert.True(File.Exists(Path.Combine(output, "payload.bin")), result.Summary);

            // 断言做完了，顺手把这条用例自己占出来的目录收掉（句柄已释放，现在删得掉）。
            try
            {
                Directory.Delete(workspaceDirectory, recursive: true);
            }
            catch
            {
                // 收不掉就留给临时目录，不影响结论。
            }
        }

        /// <summary>
        /// 并发这条是**本组的重点**：清理只认"本实例自己建的那个目录"，
        /// 不许按目录名 / "最新的那个"去扫工作区根 —— 那种实现会把正在跑的那个任务的工作区端掉。
        /// </summary>
        [Fact]
        public async Task 并发两个递归任务_完成的那一个不会删掉另一个的工作区()
        {
            string slowArchive = Path.Combine(_root, "concurrent-slow.zip");
            string fastArchive = Path.Combine(_root, "concurrent-fast.zip");
            File.WriteAllBytes(slowArchive, new byte[200]);
            File.WriteAllBytes(fastArchive, new byte[200]);

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var slowEngine = new FakeEngine();
            slowEngine.OnExtractAsync = async (request, _) =>
            {
                started.TrySetResult(true);

                // 卡在"解压中"：此刻它的工作区正在被写，另一个任务收尾时的清理绝不能碰它。
                await release.Task.ConfigureAwait(false);

                WritePayload(request.OutputPath);

                return Succeeded();
            };

            var slowExtractor = new RecursiveExtractor(slowEngine, new MagicAwareProber(), _ => new[] { string.Empty });
            var fastExtractor = new RecursiveExtractor(new FakeEngine(), new MagicAwareProber(), _ => new[] { string.Empty });

            string slowOutput = Path.Combine(_root, "out", "concurrent-slow");
            string fastOutput = Path.Combine(_root, "out", "concurrent-fast");

            Task<RecursionResult> slowRun = slowExtractor.ExtractAsync(
                new ArchiveTask(slowArchive),
                slowOutput,
                null,
                CancellationToken.None);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            string slowWorkspace = Assert.IsType<ExtractionWorkspace>(slowExtractor.CurrentWorkspace).TaskDirectory;
            File.WriteAllText(Path.Combine(slowWorkspace, "still-mine.txt"), "另一个任务不许删我", Utf8NoBom);

            // 第二个任务（同一个工作区根目录、另一个 taskId）完整跑完 → 它会清掉**自己**的工作区。
            RecursionResult fastResult = await fastExtractor.ExtractAsync(
                new ArchiveTask(fastArchive),
                fastOutput,
                null,
                CancellationToken.None);

            Assert.True(fastResult.Completed, fastResult.Summary);
            Assert.False(
                Directory.Exists(Assert.IsType<ExtractionWorkspace>(fastExtractor.CurrentWorkspace).TaskDirectory),
                "完成的任务应当清掉自己的工作区");

            // 关键断言：清理只动自己那一个目录 —— 正在跑的那个任务的工作区（含刚写进去的文件）必须原封不动。
            Assert.True(Directory.Exists(slowWorkspace), "并发任务的清理把对方正在用的工作区删掉了");
            Assert.True(File.Exists(Path.Combine(slowWorkspace, "still-mine.txt")), "对方工作区里的文件被删了");

            release.TrySetResult(true);

            RecursionResult slowResult = await slowRun;

            Assert.True(slowResult.Completed, slowResult.Summary);
            Assert.True(File.Exists(Path.Combine(slowOutput, "payload.bin")), slowResult.Summary);
            Assert.False(Directory.Exists(slowWorkspace), "自己成功之后也没清掉工作区");
        }
        /// <summary>
        /// 同一个实例被拿去跑**另一个**归档时（不是续跑），上一个任务的工作区不能被连带清掉 ——
        /// 那个任务没跑成、工作区按规矩留着，它的中间产物是用户唯一的线索。
        /// 判据是归档路径，不是"实例上有没有记着东西"。
        ///
        /// <para>⚠ 2026-10-21：第一趟的收尾原因从「多分支询问」换成「文件名已加密」——
        /// 多分支不再问用户（档位已删，`NeedsDecision` 现在不可达），而"没跑成 ⇒ 工作区留着"
        /// 这条没变（见 <c>递归失败_工作区保留</c>）。本条钉的是**换归档**那一半，与停在哪一档无关。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 同一个实例换归档_不清理上一个任务的工作区()
        {
            RequireSevenZip();

            string firstOuter = BuildEncryptedInnerPackage();

            string otherSource = BuildSourceDir(new[] { ("data.txt", "另一个包\n") }, "_other");
            string secondArchive = Path.Combine(_root, "other.zip");
            Run7z("a", "-tzip", secondArchive, Path.Combine(otherSource, "*"));

            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass" });

            RecursionResult first = await extractor.ExtractAsync(
                new ArchiveTask(firstOuter),
                Path.Combine(_root, "out", "swap-first"),
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.EncryptedHeaders, first.StopReason);

            string firstWorkspace = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            // 换成另一个包（不是续跑）：这一次真的成功了，也只许清它自己的那一份。
            RecursionResult second = await extractor.ExtractAsync(
                new ArchiveTask(secondArchive),
                Path.Combine(_root, "out", "swap-second"),
                null,
                CancellationToken.None);

            Assert.True(second.Completed, second.Summary);
            Assert.False(
                Directory.Exists(Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory),
                "本次运行的工作区应当被清理");
            Assert.True(
                Directory.Exists(firstWorkspace),
                "上一个任务（没跑成、工作区留着）的工作区被别的任务连带清掉了");
        }

        // ────────────────────────────── 测试基础设施 ──────────────────────────────

        private async Task<RecursionResult> ExtractAsync(string archivePath, string outputDirectory)
        {
            return await ExtractAsync(archivePath, outputDirectory, logLines: null);
        }

        /// <summary>
        /// 同上，另收一份日志（判据要说清"为什么这么走"时用得上；<paramref name="logLines"/> 传 null = 不记日志，
        /// 与只传两个参数的那个重载**逐字相同**）。
        /// </summary>
        private async Task<RecursionResult> ExtractAsync(
            string archivePath,
            string outputDirectory,
            List<(string Level, string Message)>? logLines)
        {
            var task = new ArchiveTask(archivePath);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: logLines == null ? null : (level, message) => logLines.Add((level, message)));

            return await extractor.ExtractAsync(task, outputDirectory, null, CancellationToken.None);
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

        /// <summary>
        /// 造"1 个内层归档 + 旁边两个**非说明类**文件"的包：<c>siblings.zip</c> 里是
        /// <c>sibling-inner.7z</c>（里面有 data.txt）+ <c>movie.mp4</c> + <c>手册.pdf</c>。
        ///
        /// <para>这正是用户 2026-10-04 点名的那一格（真机 `第6集.7z`）：旧判据把"
        /// 旁边有非说明类文件"当成"多分支"停下，而这一层其实**只有一个**压缩包。</para>
        /// </summary>
        private (string Outer, string Inner) BuildSingleInnerWithNonInformationalSiblings()
        {
            string source = BuildSourceDir(new[] { ("data.txt", "内层包里的内容\n") }, "_sibling_src");

            string inner = Path.Combine(_root, "sibling-inner.7z");
            Run7z("a", "-t7z", inner, Path.Combine(source, "*"));

            string outerSource = Path.Combine(_root, "_sibling_outer");
            Directory.CreateDirectory(outerSource);
            File.Copy(inner, Path.Combine(outerSource, "sibling-inner.7z"));
            File.WriteAllBytes(Path.Combine(outerSource, "movie.mp4"), new byte[4096]);
            File.WriteAllText(Path.Combine(outerSource, "手册.pdf"), "假装是 PDF\n", Utf8NoBom);

            string outer = Path.Combine(_root, "siblings.zip");
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

        /// <summary>
        /// **每一层开头都要把进度基准拉回本层**（用户 2026-10-02：「解压完成到真正的解释有个 8 分钟的差距」）。
        ///
        /// <para>现场：`P.7z.001` 第 0 层 09:47:18 到 100%，09:47:23 才开始解内层 `P.ra`，
        /// 09:51:57 才结束 —— 中间 **4 分 34 秒**界面上停在 100%、日志里一条进度都没有。
        /// 成因：进度条记的是"引擎最后一次报的百分比"，第 1 层从头开始时没有任何一帧把基准拉回来。</para>
        ///
        /// <para><b>红检</b>：把 `RecursiveExtractor.ExtractLayerAsync` 里那一帧（`progress?.Report(...)`）
        /// 撤掉 ⇒ 本用例立刻变红（找不到"第 1 层 + 0%"那一帧）。</para>
        /// </summary>
        [Fact]
        public async Task 每一层开头_进度基准都要拉回本层()
        {
            string source = Path.Combine(_root, "layer-progress-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            /*
             * 内层候选现在过的是「内容证据」闸门（FileNature.BodyKind == SingleComplete），
             * 不再看后缀/测试 prober ⇒ 这里必须放一份**真 7z**，否则第 1 层根本不会入队，
             * 用例测到的就不是"进度基准"，而是"假包没通过内容闸门"。
             */
            string innerSource = Path.Combine(_root, "_inner-progress");
            Directory.CreateDirectory(innerSource);
            File.WriteAllText(Path.Combine(innerSource, "leaf.txt"), "leaf");
            string realInner = Path.Combine(_root, "real-inner-progress.7z");
            Run7z("a", "-t7z", realInner, Path.Combine(innerSource, "*"));
            byte[] realInnerBytes = File.ReadAllBytes(realInner);

            var engine = new FakeEngine();
            var recorded = new List<ArchiveProgress>();
            int calls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                calls++;
                Directory.CreateDirectory(request.OutputPath!);

                if (calls == 1)
                {
                    // 第 0 层：解出一个真 7z（内容闸门才会把它收进第 1 层），
                    // 并把进度推到 100% —— 真机上第 0 层结束时就是这样。
                    File.WriteAllBytes(Path.Combine(request.OutputPath!, "inner.7z"), realInnerBytes);
                    request.Progress?.Report(new ArchiveProgress { Percent = 100, CurrentEntry = "第 0 层的最后一个文件" });
                }
                else
                {
                    File.WriteAllText(Path.Combine(request.OutputPath!, "leaf.bin"), "叶子内容");
                }

                return Task.FromResult(Succeeded());
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { string.Empty });

            await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-layer-progress"),
                null,
                CancellationToken.None,
                new RecordingProgress(recorded));

            Assert.Equal(2, calls);   // 内外两层都真的解了

            int layerZeroDone = recorded.FindIndex(p => p.Percent == 100);
            int layerOneStart = recorded.FindIndex(
                p => p.Percent == 0 && p.CurrentEntry.Contains("第 1 层", StringComparison.Ordinal));

            Assert.True(layerZeroDone >= 0, "第 0 层的引擎进度必须透传出来");
            Assert.True(
                layerOneStart > layerZeroDone,
                "第 1 层开头必须报一帧 0%（当前条目点名第 1 层），而且必须排在第 0 层的 100% 之后 —— "
                + "少了它，界面就会停在 100% 一动不动。实际收到的帧："
                + string.Join("；", recorded.Select(p => $"{p.Percent}%/{p.CurrentEntry}")));
        }

        /// <summary>同步记账的进度接收器（不用 <see cref="Progress{T}"/>：它会把回调甩到线程池，顺序就不好断言了）。</summary>
        private sealed class RecordingProgress : IProgress<ArchiveProgress>
        {
            private readonly List<ArchiveProgress> _sink;

            public RecordingProgress(List<ArchiveProgress> sink) => _sink = sink;

            public void Report(ArchiveProgress? value)
            {
                if (value != null)
                {
                    _sink.Add(value);
                }
            }
        }

        /// <summary>
        /// 造一个"外层不加密 zip + 内层加密 7z"的包，用于验证密码重试链路。
        /// </summary>
        /// <param name="encryptFileNames">
        /// 内层包是否**连文件名一起加密**（<c>-mhe=on</c>，默认 true）。
        ///
        /// <para>默认 true 的理由：这样"密码错"与"密码对"的差异没有别的旁路可看。
        /// ⚠ 2026-10-05 起它还决定**结论落在哪一档**：`-mhe` 的包连内容清单都列不出来 ⇒
        /// 收尾是「文件名已加密」（与单层路径对齐）；要验"密码错误 / 达到密码尝试上限"那几档
        /// 必须传 false（头不加密 ⇒ 清单列得出来，失败发生在解压阶段）。
        /// 这也是 `密码_候选全试完仍失败报WrongPassword` 显式传 false 的原因。</para>
        /// </param>
        private string BuildEncryptedInnerPackage(bool encryptFileNames = true)
        {
            string source = BuildSourceDir(("secret.txt", "加密内容\n"));

            string inner = Path.Combine(_root, "secret.7z");

            if (encryptFileNames)
            {
                Run7z("a", "-t7z", inner, "-p" + TestPass123, "-mhe=on", Path.Combine(source, "*"));
            }
            else
            {
                Run7z("a", "-t7z", inner, "-p" + TestPass123, Path.Combine(source, "*"));
            }

            string outerSource = Path.Combine(_root, "_enc");
            Directory.CreateDirectory(outerSource);
            File.Copy(inner, Path.Combine(outerSource, "secret.7z"));

            string outer = Path.Combine(_root, "enc-outer.zip");
            Run7z("a", "-tzip", outer, Path.Combine(outerSource, "*"));

            return outer;
        }

        /// <summary>
        /// 造一个"内容是 <c>内容物\&lt;名字&gt;.bin</c>"的真 7z 包，返回归档路径。
        /// 就地替换那一组用例拿它当内层包（用户例子里每个包解出来都是一层 <c>内容物\</c>）。
        /// </summary>
        private string BuildInnerPackage(string name)
        {
            string payload = Path.Combine(_root, "_payload-" + name, "内容物");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, name + ".bin"), name + " 的内容\n", Utf8NoBom);

            string archive = Path.Combine(_root, name + ".7z");
            Run7z("a", "-t7z", archive, Path.Combine(Path.GetDirectoryName(payload)!, "*"));

            return archive;
        }

        /// <summary>目录树里的全部条目（目录 + 文件），相对路径、序号排序 —— 用来逐字对照整棵树。</summary>
        private static IReadOnlyList<string> Tree(string root)
        {
            return Directory
                .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
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
        ///
        /// 另外也用于"并发 / 取消"这两条：它们要控制"解压到一半"的时机，
        /// 真 7z 跑得太快、卡不住，用假引擎才能把竞态摆成确定的局面。
        /// </summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            private readonly long _uncompressedSize;

            public FakeEngine(long uncompressedSize = 0)
            {
                _uncompressedSize = uncompressedSize;
            }

            /// <summary>非 null 时接管解压：并发 / 取消用例靠它决定"什么时候返回、返回什么"。</summary>
            public Func<ArchiveRequest, ExtractOptions, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

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
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                if (OnExtractAsync != null)
                {
                    return OnExtractAsync(request, options);
                }

                // 假引擎也要产出真实文件：否则后面"量产物"的代码路径根本没跑到。
                WritePayload(request.OutputPath);

                return Task.FromResult(Succeeded());
            }
        }

        /// <summary>假引擎的产物：一个普通文件（不是归档，所以不会被探测成"内层包"）。</summary>
        private static void WritePayload(string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            Directory.CreateDirectory(outputPath);
            File.WriteAllText(Path.Combine(outputPath, "payload.bin"), "假引擎产物", Utf8NoBom);
        }

        private static ArchiveOperationResult Succeeded()
        {
            return ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero);
        }
    }
}
