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
    public partial class RecursiveExtractorTests
    {
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
    }
}
