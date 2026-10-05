using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 核心层修复的回归用例（对应审查报告里的 P0/P1 条目）。
    ///
    /// 覆盖：
    /// - 中文路径/条目名乱码（P1）：必须带 <c>-sccUTF-8</c>，且真的能列出中文条目名；
    /// - 只给非首卷时的错误分类（P1）：必须是"分卷缺失"，并报出缺哪一卷；
    /// - 递归展开的内层包也要过 Security（P1）：危险条目名与越界落点都必须是失败结论；
    /// - 更深的层出现多分支（P2）：不能静默当成"已完成"。
    /// </summary>
    // 碰进程级静态（构造 RecursiveExtractor / 指定 ConfiguredWorkspaceRoot）：
    // 与同类用例串行跑，不与别的集合并行 —— 见 InnerLayerContinuationTests 顶部的 CollectionDefinition。
    [Collection("ArchiveFixerGlobalState")]
    public class CoreFixRegressionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _sevenZip;

        public CoreFixRegressionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCoreFix", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
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
                // 临时目录删不掉不影响结论，下次跑换一个 GUID 目录。
            }
        }

        // ────────────────────────── P1：控制台编码 ──────────────────────────

        [Fact]
        public void 解压参数必须带_sccUTF_8()
        {
            var runner = new SevenZipProcessRunner(new ToolLocator());

            List<string> extract = runner.BuildExtractArguments(
                @"C:\t\中文包.7z",
                @"C:\t\out",
                string.Empty,
                new ExtractOptions());

            List<string> test = runner.BuildTestArguments(@"C:\t\中文包.7z", string.Empty);

            Assert.Contains("-sccUTF-8", extract);
            Assert.Contains("-sccUTF-8", test);
        }

        [Fact]
        public async Task 中文条目名_列目录不乱码且能解出中文文件名()
        {
            RequireSevenZip();

            string source = Path.Combine(_root, "_中文源");
            Directory.CreateDirectory(Path.Combine(source, "子目录"));
            File.WriteAllText(Path.Combine(source, "普通.txt"), "普通\n", Utf8NoBom);
            File.WriteAllText(Path.Combine(source, "子目录", "中文条目.txt"), "中文\n", Utf8NoBom);

            string archive = Path.Combine(_root, "中文包.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "普通.txt"), Path.Combine(source, "子目录"));

            var engine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(archive), CancellationToken.None);

            Assert.True(list.Success, list.Message);
            Assert.Contains(list.Entries, entry => entry.Path.Contains("普通.txt", StringComparison.Ordinal));
            Assert.Contains(list.Entries, entry => entry.Path.Contains("中文条目.txt", StringComparison.Ordinal));

            // 乱码的典型形态：解码失败后的替换字符 U+FFFD。
            Assert.DoesNotContain(list.Entries, entry => entry.Path.Contains('\uFFFD'));

            string output = Path.Combine(_root, "out_中文");
            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = archive, OutputPath = output, Password = string.Empty },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);
            Assert.True(
                File.Exists(Path.Combine(output, "普通.txt")),
                "解压后没有中文名的文件：" + string.Join("、", Directory.EnumerateFileSystemEntries(output, "*", SearchOption.AllDirectories)));
        }

        // ────────────────────────── P1：缺卷分类 ──────────────────────────

        [Fact]
        public void 非首卷错误文案_分类为缺首卷()
        {
            const string error = "ERROR: C:\\t\\only\\volume.7z.002 : Cannot open the file as archive";

            string type = SevenZipOutputParser.DetectSevenZipErrorType(2, string.Empty, error, "C:\\t\\only\\volume.7z.002");

            Assert.Equal(SevenZipOutputParser.MissingFirstVolumeErrorType, type);
            Assert.Equal(StatusText.VolumeMissing, SevenZipOutputParser.ErrorTypeToTaskStatus(type));
        }

        /// <summary>
        /// **第 1 卷自己打不开时不许说"缺少首卷"**（2026-10-05 真机）。
        ///
        /// <para>现场：三卷 7z 的三片躺在三个层产物目录里，<c>HK.7z.001</c>（**它自己就是第 1 卷**）
        /// 单独交给 7-Zip 报 <c>Open ERROR: Cannot open the file as [7z] archive</c>，
        /// 老判据"名字像分卷 ⇒ MissingFirstVolume"于是说「这是分卷压缩包的**后续卷**，缺少首卷」——
        /// 两条都不成立（它既不是后续卷、也不缺首卷）⇒ 用户被指去找一个**就在手上**的东西。</para>
        ///
        /// <para>判据只读名字与既有出口：卷号 ≥ 2 才允许下"缺首卷"的断言；卷号 == 1 一律落既有的
        /// <c>"VolumeMissing"</c>（「分卷缺失」，不含任何错误断言，上层同一档处理）。</para>
        /// </summary>
        [Fact]
        public void 第一卷自己打不开_不许报缺少首卷()
        {
            const string error =
                "ERROR: C:\\t\\only\\HK.7z.001 ｜ Open ERROR: Cannot open the file as [7z] archive ｜ ERRORS:";

            string type = SevenZipOutputParser.DetectSevenZipErrorType(2, string.Empty, error, "C:\\t\\only\\HK.7z.001");

            Assert.Equal(SevenZipOutputParser.SevenZipVolumeMissingErrorType, type);
            Assert.Equal(StatusText.VolumeMissing, SevenZipOutputParser.ErrorTypeToTaskStatus(type));

            // ⛔ 文案里不许再出现"缺少首卷"那种反过来的断言。
            Assert.DoesNotContain("缺少首卷", SevenZipOutputParser.ErrorTypeToMessage(type), StringComparison.Ordinal);

            // 对照：卷号 ≥ 2 的那一档照旧（判据只按卷号分档，⛔ 不按中文）。
            Assert.True(SevenZipOutputParser.LooksLikeMissingVolumePart("volume.7z.002"));
            Assert.False(SevenZipOutputParser.LooksLikeMissingVolumePart("volume.7z.001"));
        }

        [Fact]
        public async Task 只给非首卷_分类为分卷缺失并报出缺哪一卷()
        {
            RequireSevenZip();

            string volumeDirectory = Path.Combine(_root, "vol");
            Directory.CreateDirectory(volumeDirectory);

            string payload = Path.Combine(_root, "big.bin");
            byte[] bytes = new byte[2 * 1024 * 1024 + 512 * 1024];
            new Random(20260921).NextBytes(bytes);
            File.WriteAllBytes(payload, bytes);

            string archive = Path.Combine(volumeDirectory, "volume.7z");
            Run7z("a", "-t7z", archive, "-v1m", payload);

            Assert.True(File.Exists(archive + ".001"), "分卷样本没有生成 .001");

            /*
             * 造出"用户的真实现场"：手上只有非首卷（两个 mp4 各藏一段分卷，
             * 解到两个目录里去了，于是谁都没有首卷）。
             */
            string misplaced = Path.Combine(_root, "only-second", "volume.7z.002");
            Directory.CreateDirectory(Path.GetDirectoryName(misplaced)!);
            File.Copy(archive + ".002", misplaced);

            var engine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));

            ArchiveOperationResult result = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = misplaced,
                    OutputPath = Path.Combine(_root, "out_vol"),
                    Password = string.Empty
                },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.False(result.Success);

            /*
             * 分类必须是"分卷缺失"这一族，而不是"不支持该格式"：
             * 引擎的文本分类给出 MissingFirstVolume（文件名本身就是分卷），
             * 运行器在有文件系统可查时会把它判成 VolumeMissing（能列出缺哪几卷）。
             * 两者对用户是同一个结论（同状态、同动作），测试接受任意一种，但必须落在这一族里。
             */
            Assert.Contains(
                result.DetectedErrorType,
                new[] { "VolumeMissing", SevenZipOutputParser.MissingFirstVolumeErrorType });
            Assert.Equal(StatusText.VolumeMissing, result.Status);

            // 不变量 7：必须报缺哪几个，而且名字要跟用户手上的文件同风格（真实名是 volume.7z.001）。
            Assert.Contains("volume.7z.001", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────── P1：递归也要过 Security ──────────────────────────

        [Fact]
        public async Task 递归解压_内层包条目名含上级目录_必须拒绝且不算成功()
        {
            RequireSevenZip();

            byte[] unsafeZip = BuildZipWithUnsafeEntryName("..\\..\\evil.txt");

            // 先确认这个手工样本本身是"7z 认得出、且条目名带 .."的（否则下面的断言测不到东西）。
            string probePath = Path.Combine(_root, "unsafe-inner.zip");
            File.WriteAllBytes(probePath, unsafeZip);

            var probeEngine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));
            ArchiveListResult probe = await probeEngine.ListAsync(
                ArchiveRequest.For(probePath),
                CancellationToken.None);

            Assert.True(
                probe.Success,
                "手工构造的 zip 7z 打不开，样本本身有问题：" + probe.Message);
            Assert.Contains(
                probe.Entries,
                entry => entry.Path.Contains("..", StringComparison.Ordinal));

            // 7z 不会自己生成带 `..` 的条目名，所以这里手工写一个 zip 供外层包转发。
            string outer = BuildArchiveContaining("inner.zip", unsafeZip);

            RecursionResult result = await RunRecursiveAsync(outer, "unsafe-entry");

            Assert.Equal(RecursionStopReason.UnsafeEntry, result.StopReason);
            Assert.False(result.Completed);

            /*
             * 外层的产物是有效的，所以这里必须是"部分完成"而不是"失败"（不变量 6）：
             * 用户拿到的外层内容没问题，坏的是内层那个包。
             */
            Assert.True(result.PartiallyCompleted, result.Summary);
            Assert.Contains(result.Layers, layer => layer.Message.Contains("不安全", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 递归解压_产物落点越界_必须是失败结论而不是解压成功()
        {
            string archivePath = Path.Combine(_root, "escape.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            // 造一个"产物目录里有个联接点指向目录外"的现场：目录遍历看得见它，
            // 但它的真实落点在产物目录之外 —— 这正是第二道防线该拦下的形态。
            string outside = Path.Combine(_root, "outside");
            Directory.CreateDirectory(outside);

            var engine = new JunctionEngine();

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "junction",
                engine,
                Path.Combine(_root, "out_junction"));

            Assert.Equal(RecursionStopReason.UnsafeEntry, result.StopReason);
            Assert.False(result.Completed);
            Assert.False(result.PartiallyCompleted);
            Assert.Contains(result.Layers, layer => layer.Message.Contains("越出本层产物目录", StringComparison.Ordinal));
        }

        // ────────────────────────── P2：更深的层多分支不得静默 ──────────────────────────

        /// <summary>
        /// 更深的层出现多分支 ⇒ 停下，而且**按真实数量说、还点名是哪几个没展开**
        /// （用户 2026-10-04 真机：过去只写"该层还有 N 个内层包未展开"，N 与"多个"还互相打架）。
        ///
        /// <para><b>红检</b>：把 <c>BuildSummary</c> 里的名单/数量换回写死的"多个"
        /// ⇒ 本用例当场红（`Assert.Contains() Failure: 还有 2 个内层归档未展开`）。</para>
        /// </summary>
        [Fact]
        public async Task 更深的层出现多分支_停下来按真实数量说并点名没展开的包()
        {
            string archivePath = Path.Combine(_root, "branches.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "branch",
                new BranchingEngine(),
                Path.Combine(_root, "out_branch"));

            Assert.Equal(RecursionStopReason.BranchNotExpanded, result.StopReason);
            Assert.False(result.Completed);

            // 真实数量（不是写死的"多个"）。
            Assert.Contains("还有 2 个内层归档未展开", result.Summary, StringComparison.Ordinal);

            // 点名：用户要能拿这两个名字去目录里对上号。
            Assert.Equal(2, result.UnexpandedNames.Count);
            Assert.Contains("a.7z", result.Summary, StringComparison.Ordinal);
            Assert.Contains("b.7z", result.Summary, StringComparison.Ordinal);
        }

        /// <summary>
        /// **只有 1 个**内层归档、可它旁边还有个别的东西（非说明类文件）⇒ **照旧往下解**
        /// （不再停在这一档）。
        ///
        /// <para><b>⚠ 2026-10-04 改结论（用户当场推翻旧口径）</b>：老用例断言的是"停在 <c>BranchNotExpanded</c>、
        /// 理由 = 它旁边还有别的文件" —— 用户原话（真机 `第6集.7z`）：「那层内层包只有 1 个，
        /// 但旁边还有不属于"说明类"的文件 ⇒ 被算成"多分支" —— **这是压缩包吗，不是那你停什么**」。
        /// 现在单链档的判据只看**内层归档的数量**：1 个一律继续解，所以这个夹具会一路解到**层数上限**
        /// 才停（每一层都还是 1 个内层包 + 一个 `.mp4`）。</para>
        ///
        /// <para><b>新判据</b>：① 停因是「到层数上限」，不是「多分支不展开」；
        /// ② 结论里⛔ 不许出现"多分支"或"它旁边还有别的文件"那套旧说法；
        /// ③ 到上限时照旧点名没展开的是哪一个。</para>
        ///
        /// <para><b>红检</b>：把判据改回"同层除该包以外全是说明类文件"
        /// （`toProcess.Count == 1 &amp;&amp; HasOnlyInformationalSiblings(…)`）⇒ 本用例当场红
        /// （`Assert.Equal() Failure: Expected MaxDepthReached / Actual BranchNotExpanded`）。</para>
        /// </summary>
        [Fact]
        public async Task 更深的层只1个内层归档_旁边有别的文件也照旧往下解_到上限才停()
        {
            string archivePath = Path.Combine(_root, "single-branch.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "branch-single",
                new SingleInnerWithSiblingEngine(),
                Path.Combine(_root, "out_branch_single"));

            // ① 一路往下解，直到层数上限（不再因为"旁边有别的文件"停下）。
            Assert.Equal(RecursionStopReason.MaxDepthReached, result.StopReason);
            Assert.True(result.Layers.Count > 1, $"至少要真的解开一层，实际 {result.Layers.Count} 层：{result.Summary}");

            // ② ⛔ 旧判据那套说法一个字都不许再出现。
            Assert.DoesNotContain("多分支", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("它旁边还有别的文件", result.Summary, StringComparison.Ordinal);

            // ③ 到上限照样点名（用户要能拿名字去目录里对上号）。
            Assert.Contains("a.7z", result.Summary, StringComparison.Ordinal);
        }

        // ────────────────────────── 递归路的密码两条省时判据（2026-10-04 真机） ──────────────────────────

        /// <summary>
        /// **递归层里整包加密时不试空密码**（用户 2026-10-04 真机：真机日志第一行就是
        /// 「第 0 层：第6集.zip：开始解压，密码候选 1/10，尝试空密码」—— 一个字节都解不出来还白跑一整包）。
        ///
        /// <para>判据与单层路径**同一个出口**（<c>PasswordProbe.ShouldSkipEmptyPassword</c>）。</para>
        ///
        /// <para><b>红检</b>：把递归候选循环里那一段撤掉 ⇒ 本用例当场红
        /// （`Assert.DoesNotContain() Failure: 空密码被真的拿去解过整包`）。</para>
        /// </summary>
        [Fact]
        public async Task 递归层_整包加密时不试空密码()
        {
            string archivePath = Path.Combine(_root, "encrypted.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            var engine = new EncryptedPackageEngine();

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "encrypted",
                engine,
                Path.Combine(_root, "out_encrypted"),
                new[] { string.Empty, "占位密码" });

            // ① ⛔ 空密码那一次**根本没有解过整包**（白跑一整包正是要治的）。
            Assert.DoesNotContain(engine.ExtractCalls, call => string.IsNullOrEmpty(call));

            // ② 真的有别的候选被试过（不是"什么都没跑"）。
            Assert.Contains(engine.ExtractCalls, call => !string.IsNullOrEmpty(call));

            Assert.NotNull(result);
        }

        /// <summary>
        /// **只有一个空密码候选时不许跳**（与单层路径同一条边界）：跳了会让收场落到
        /// 一句更难懂的"未知解压失败"，比多跑一次更糟。
        /// </summary>
        [Fact]
        public async Task 递归层_只有空密码候选时照旧要试()
        {
            string archivePath = Path.Combine(_root, "encrypted-only-empty.7z");
            File.WriteAllBytes(archivePath, new byte[256]);

            var engine = new EncryptedPackageEngine();

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "encrypted-only-empty",
                engine,
                Path.Combine(_root, "out_encrypted_only_empty"),
                new[] { string.Empty });

            Assert.Contains(engine.ExtractCalls, call => string.IsNullOrEmpty(call));

            Assert.NotNull(result);
        }

        /// <summary>
        /// **递归层里先只解最小的那个条目（探针），探针说密码不对就不再解整包**（同一天真机）。
        /// 判据同样只有 <c>Extraction/PasswordProbe</c> 一份。
        ///
        /// <para><b>红检</b>：把递归候选循环里那次 <c>ProbeCandidateAsync</c> 撤掉 ⇒ 本用例当场红
        /// （探针之后仍然解了整包）。</para>
        /// </summary>
        [Fact]
        public async Task 递归层_探针不通过就不解整包()
        {
            // ⚠ 1 MiB 的"包"：清单自述 64 MiB（探针门槛）时展开比 ≈ 64 倍 —— 不触发压缩炸弹闸门（500 倍）。
            string archivePath = Path.Combine(_root, "probe.7z");
            File.WriteAllBytes(archivePath, new byte[1024 * 1024]);

            var engine = new ProbeRejectingEngine();
            var log = new List<(string Level, string Message)>();

            RecursionResult result = await RunRecursiveAsync(
                archivePath,
                "probe",
                engine,
                Path.Combine(_root, "out_probe"),
                new[] { "占位密码" },
                log);

            // ① 探针（只解最小条目）跑过。
            Assert.True(
                engine.ProbeEntryCalls.Count > 0,
                $"探针一次都没被调用：stop={result.StopReason} layers={result.Layers.Count} "
                + $"listCalls={engine.ListCalls} fullExtracts={engine.FullExtractCalls.Count} summary={result.Summary}");

            // ② ⛔ 探针说不通过之后**没有再去解整包**。
            Assert.Empty(engine.FullExtractCalls);

            // ③ 日志里如实写着"密码预检不通过"。
            Assert.Contains(
                log,
                entry => entry.Message.Contains("密码预检不通过", StringComparison.Ordinal));

            Assert.NotNull(result);
        }

        // ────────────────────────── 工具方法 ──────────────────────────

        private async Task<RecursionResult> RunRecursiveAsync(
            string archivePath,
            string tag,
            IArchiveEngine? engine = null,
            string? output = null,
            IReadOnlyList<string>? passwordCandidates = null,
            List<(string Level, string Message)>? log = null)
        {
            string? previousRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = Path.Combine(_root, "work");

                IArchiveEngine effectiveEngine = engine ?? new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));

                var extractor = new RecursiveExtractor(
                    effectiveEngine,
                    new MagicArchiveProber(),
                    _ => passwordCandidates ?? new[] { string.Empty },
                    log: log == null ? null : (level, message) => log.Add((level, message)));

                return await extractor.ExtractAsync(
                    new ArchiveTask(archivePath),
                    output ?? Path.Combine(_root, "out_" + tag),
                    RecursionMode.SingleChain,
                    null,
                    CancellationToken.None);
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousRoot;
            }
        }

        /// <summary>把 <paramref name="innerArchive"/> 放进一个外层 zip 里，返回外层包路径。</summary>
        private string BuildArchiveContaining(string entryName, byte[] innerBytes)
        {
            string source = Path.Combine(_root, "_outer");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, entryName), innerBytes);

            string outer = Path.Combine(_root, "outer_" + Guid.NewGuid().ToString("N") + ".zip");

            Run7z("a", "-tzip", outer, Path.Combine(source, entryName));

            return outer;
        }

        /// <summary>
        /// 造一个**条目名不安全**的 zip。这里刻意直接写 ZIP 结构而不是用 7z 造：
        /// 7z 会清洗 `..` 这类条目名，造不出"危险条目"这个真实攻击形态。
        /// </summary>
        private static byte[] BuildZipWithUnsafeEntryName(string entryName)
        {
            byte[] name = Encoding.UTF8.GetBytes(entryName);
            byte[] content = Encoding.UTF8.GetBytes("evil\n");

            using var memory = new MemoryStream();

            // Local file header
            memory.Write(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
            WriteUInt16(memory, 20);        // version needed
            WriteUInt16(memory, 0x0800);    // flags: UTF-8 名称
            WriteUInt16(memory, 0);         // method: stored
            WriteUInt16(memory, 0);         // time
            WriteUInt16(memory, 0);         // date
            WriteUInt32(memory, 0);         // crc（用 stored 且内容不校验，7z 只在测试时校验）
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt16(memory, (ushort)name.Length);
            WriteUInt16(memory, 0);
            memory.Write(name);
            memory.Write(content);

            int centralDirectoryOffset = (int)memory.Length;

            // Central directory header
            memory.Write(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
            WriteUInt16(memory, 20);
            WriteUInt16(memory, 20);
            WriteUInt16(memory, 0x0800);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt32(memory, 0);
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt32(memory, (uint)content.Length);
            WriteUInt16(memory, (ushort)name.Length);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt32(memory, 0);
            WriteUInt32(memory, 0);
            memory.Write(name);

            int centralDirectorySize = (int)memory.Length - centralDirectoryOffset;

            // End of central directory
            memory.Write(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 0);
            WriteUInt16(memory, 1);
            WriteUInt16(memory, 1);
            WriteUInt32(memory, (uint)centralDirectorySize);
            WriteUInt32(memory, (uint)centralDirectoryOffset);
            WriteUInt16(memory, 0);

            return memory.ToArray();
        }

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        private static UTF8Encoding Utf8NoBom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe），本用例无法运行。");
            }
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(
                        directory.FullName,
                        "src", "ArchiveFixer",
                        "tools",
                        "7zip",
                        "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        private void Run7z(params object[] args)
        {
            RequireSevenZip();

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
                psi.ArgumentList.Add(arg?.ToString() ?? string.Empty);
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

                throw new InvalidOperationException("7z 造样本超时：" + string.Join(' ', psi.ArgumentList));
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {process.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        /// <summary>
        /// 假引擎：在产物目录里造一个**指向目录外的联接点**，再从它里面写出文件。
        ///
        /// 为什么用这个形态：只检查"条目名"（第一道预检）看不出联接点有什么问题 ——
        /// 名字干干净净。而产物目录里的东西真实落点在目录之外，属于"解压后发现越界"这一类，
        /// 正是第二道落点校验存在的理由（不变量 4）。
        /// 用联接点（mklink /J）而不是符号链接：前者不需要管理员权限。
        /// </summary>
        private sealed class JunctionEngine : IArchiveEngine
        {
            public string Id => "junction";

            public string DisplayName => "联接点假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 8,
                    EngineId = Id,
                    EngineVersion = Version
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllText(Path.Combine(outputPath, "innocent.txt"), "正常产物\n", Utf8NoBom);

                    string outside = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(outputPath)!)!, "outside");
                    Directory.CreateDirectory(outside);

                    string link = Path.Combine(outputPath, "link-out");

                    if (!Directory.Exists(link))
                    {
                        CreateJunction(link, outside);
                    }

                    File.WriteAllText(Path.Combine(link, "escaped.txt"), "越界产物\n", Utf8NoBom);
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            /// <summary>用 mklink /J 造目录联接点（不需要管理员权限，也不改注册表）。</summary>
            private static void CreateJunction(string linkPath, string targetPath)
            {
                var psi = new ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(linkPath);
                psi.ArgumentList.Add(targetPath);

                using Process process = Process.Start(psi)
                    ?? throw new InvalidOperationException("无法启动 cmd.exe 建联接点");

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(30_000);
            }
        }

        /// <summary>
        /// 假引擎：第 0 层解出 1 个内层包（单链 → 自动展开），第 1 层解出 2 个内层包
        /// （更深层的多分支 → 必须停下并报告，而不是静默收尾）。
        /// </summary>
        private sealed class BranchingEngine : IArchiveEngine
        {
            public string Id => "branching";

            public string DisplayName => "多分支假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 16,
                    EngineId = Id,
                    EngineVersion = Version
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);

                    if (request.ArchivePath.EndsWith("branches.7z", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteFake7z(Path.Combine(outputPath, "level1.7z"));
                    }
                    else
                    {
                        WriteFake7z(Path.Combine(outputPath, "a.7z"));
                        WriteFake7z(Path.Combine(outputPath, "b.7z"));
                        File.WriteAllText(Path.Combine(outputPath, "note.txt"), "两个内层包\n", Utf8NoBom);
                    }
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            /// <summary>写一个只有 7z 魔数开头的文件：够探测层认出"这是内层归档"，不需要真能解开。</summary>
            private static void WriteFake7z(string path)
            {
                File.WriteAllBytes(path, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });
            }
        }

        /// <summary>
        /// 假引擎（2026-10-04 真机那一档）：第 1 层只解出 **1 个**内层归档 + 一个**非说明类**文件
        /// （`movie.mp4`）⇒ 按既有判据"不是单链"，多分支那一档停下，而没展开的**只有 1 个**。
        /// </summary>
        private sealed class SingleInnerWithSiblingEngine : IArchiveEngine
        {
            public string Id => "single-inner-with-sibling";

            public string DisplayName => "单内层包+旁文件假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = 16,
                    EngineId = Id,
                    EngineVersion = Version
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);

                    if (request.ArchivePath.EndsWith("single-branch.7z", StringComparison.OrdinalIgnoreCase))
                    {
                        // 第 0 层：一个内层包（单链，自动往下走）。
                        WriteFake7z(Path.Combine(outputPath, "level1.7z"));
                    }
                    else
                    {
                        // 第 1 层：**1 个**内层归档 + 一个非说明类文件 ⇒ 不是单链（但没展开的只有 1 个）。
                        WriteFake7z(Path.Combine(outputPath, "a.7z"));
                        File.WriteAllBytes(Path.Combine(outputPath, "movie.mp4"), new byte[64]);
                    }
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }

            private static void WriteFake7z(string path)
            {
                File.WriteAllBytes(path, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });
            }
        }

        /// <summary>
        /// 假引擎（B9）：清单说"整包加密"，解压只有**非空候选**才成功；
        /// 记下每次解压用的是哪个候选 —— 用例据此断言"空密码一次都没被拿去解整包"。
        /// </summary>
        private sealed class EncryptedPackageEngine : IArchiveEngine
        {
            public List<string> ExtractCalls { get; } = new();

            public string Id => "encrypted-package";

            public string DisplayName => "加密包假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    IsEncrypted = true,
                    FileCount = 1,
                    TotalUncompressedSize = 8,
                    EngineId = Id,
                    EngineVersion = Version
                });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                string password = request.Password ?? string.Empty;
                ExtractCalls.Add(password);

                if (string.IsNullOrEmpty(password))
                {
                    return Task.FromResult(new ArchiveOperationResult
                    {
                        Success = false,
                        Status = StatusText.WrongPassword,
                        Message = "Wrong password",
                        DetectedErrorType = "WrongPassword"
                    });
                }

                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllText(Path.Combine(outputPath, "payload.txt"), "内容\n", Utf8NoBom);
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }
        }

        /// <summary>
        /// 假引擎（B9 探针）：清单说加密、总量够大（&gt; 64 MiB）⇒ 值得做探针；
        /// **探针一律报密码不对**，整包解压一次都不该发生。记下两类调用供断言。
        /// </summary>
        private sealed class ProbeRejectingEngine : IArchiveEngine
        {
            public int ListCalls { get; private set; }

            public List<string> ProbeEntryCalls { get; } = new();

            public List<string> FullExtractCalls { get; } = new();

            public string Id => "probe-rejecting";

            public string DisplayName => "探针拒绝假引擎（仅测试用）";

            public string Version => "0.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                ListCalls++;

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    IsEncrypted = true,
                    FileCount = 2,
                    TotalUncompressedSize = 64L * 1024 * 1024,
                    Entries = new List<ArchiveEntry>
                    {
                        new() { Path = "small.txt", Size = 8 },
                        new() { Path = "big.bin", Size = 64L * 1024 * 1024 }
                    },
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<string>? include = options.IncludeEntries;

                if (include is { Count: > 0 })
                {
                    // 探针：只解点名的那一个条目 ⇒ 一律报"密码不对"。
                    ProbeEntryCalls.Add(include[0]);

                    return Task.FromResult(new ArchiveOperationResult
                    {
                        Success = false,
                        Status = StatusText.WrongPassword,
                        Message = "Wrong password",
                        DetectedErrorType = "WrongPassword"
                    });
                }

                // 整包解压：这一档**不该发生**（探针已经否掉了候选）。
                FullExtractCalls.Add(request.Password ?? string.Empty);

                string outputPath = request.OutputPath ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                    File.WriteAllText(Path.Combine(outputPath, "payload.bin"), "内容\n", Utf8NoBom);
                }

                return Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero));
            }
        }
    }
}
