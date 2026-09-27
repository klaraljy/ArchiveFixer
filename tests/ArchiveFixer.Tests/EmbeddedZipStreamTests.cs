using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 内嵌 ZIP **直读**（<see cref="EmbeddedZipStreamExtractor"/>）：从 <c>[Offset, ArchiveEnd)</c>
    /// 这个"虚拟 ZIP"里流式解出条目，**不产生那份等大的临时副本**。
    ///
    /// <para>来由（用户 2026-09-24）：真实场景是"视频前缀 + 尾部一个完整 ZIP"，ZIP 段 487 MB–2.4 GB，
    /// 而 7-Zip 只容忍 8 MiB 以内的前缀错位，所以原来必须把这一段抠成一份等大的副本；
    /// 直读把峰值空间从那 3 倍里省掉一份。</para>
    ///
    /// <para>这个文件里的每一条测试都对着一个具体判据：</para>
    /// <list type="bullet">
    /// <item><description>①② stored / deflate 两种方法的产物逐字节正确；</description></item>
    /// <item><description>③ 多条目 + 中文名 + 深目录；④⑤ 加密位与不支持的方法 → 返回"不支持"（回落信号）、不产文件；</description></item>
    /// <item><description>⑥ 越界条目名 → 判失败，**目标根之外一个文件都没有**；</description></item>
    /// <item><description>⑦ ZIP64 条目尺寸（extra 0x0001）能解出，缺 extra 则明确回落；</description></item>
    /// <item><description>⑧ 与真 7z 交叉验证：直读产物 == 先抠取再用 7z 解出来的产物（逐字节）；</description></item>
    /// <item><description>⑨ 取消不产任何东西；⑩ 进度上报与"总量为 0"的边界；⑪ 归档终点之后的尾巴不被当成条目数据；</description></item>
    /// <item><description>⑫ 端到端：真管线走直读，工作区里**没有**抠出来的 <c>.zip</c>。</description></item>
    /// </list>
    ///
    /// 样本**全部自己造**（临时目录 + 框架自带 <see cref="ZipArchive"/> + 项目内置 7z.exe），
    /// 绝不出现用户机器上的任何真实文件 / 路径 / 文件名（AGENTS.md §8）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class EmbeddedZipStreamTests : IDisposable
    {
        /// <summary>假视频头长度：与真实现场同量级。</summary>
        private const int FakeVideoPrefixLength = 32768;

        /// <summary>超过 7-Zip 容忍上限（实测 8 MiB）的前置数据长度：让"必须抠出来"这件事成立。</summary>
        private const int BeyondSevenZipTolerancePrefixLength = 9 * 1024 * 1024;

        /// <summary>EOCD 之后那段正常数据的长度：复现实测的 14,350–17,424 字节这个量级。</summary>
        private const int TailAfterEocdLength = 15000;

        private const string PayloadEntryName = "payload.txt";
        private const string PayloadText = "尾部 ZIP 里的最终数据\n";

        private readonly string _root;

        public EmbeddedZipStreamTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEmbeddedZipStream", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还被释放中）。
            }
        }

        // ================================================================ ① stored

        /// <summary>
        /// stored（方法 0）条目：产物与源逐字节一致，而且**工作区里没有抠出来的 .zip** ——
        /// 这正是"省掉了那份等大的临时副本"的直接证据（直读的落点只有内容物，没有任何过程物）。
        /// </summary>
        [Fact]
        public void Stored条目_产物逐字节一致且工作区里没有抠出来的副本()
        {
            byte[] payload = BuildRandomPayload(4096, seed: 11);
            byte[] zip = BuildZipBytes(("payload.bin", payload, CompressionLevel.NoCompression));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "stored");

            string workspace = Path.Combine(_root, "work", "task-1");
            string stage = Path.Combine(workspace, "stage");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                FakeVideoPrefixLength + zip.Length,
                stage);

            Assert.True(probe.Supported, probe.Reason);
            Assert.Equal(1, probe.List!.FileCount);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                FakeVideoPrefixLength + zip.Length,
                stage);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.FileCount);

            string produced = Path.Combine(stage, "payload.bin");
            Assert.True(File.Exists(produced), "直读应该把条目落进暂存目录");
            Assert.Equal(payload, File.ReadAllBytes(produced));

            // 工作区里（除内容物外）不许有任何 .zip —— 抠取路线会在这里留下一份等大的副本。
            Assert.Empty(Directory.GetFiles(workspace, "*.zip", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace, "*.afx-part", SearchOption.AllDirectories));
        }

        // ================================================================ ② deflate

        /// <summary>deflate（方法 8）条目：流式解压出来的内容必须与原始字节一致。</summary>
        [Fact]
        public void Deflate条目_内容正确()
        {
            byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("可压缩的一段文本 0123456789\n", 500)));
            byte[] zip = BuildZipBytes(("compressed.txt", payload, CompressionLevel.Optimal));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, 0, "deflate");
            string stage = Path.Combine(_root, "deflate-out");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(polyglot, FakeVideoPrefixLength, 0, stage);
            Assert.True(probe.Supported, probe.Reason);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                0,
                stage);

            Assert.True(result.Success, result.Message);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, "compressed.txt")));
        }

        // ================================================================ ③ 中文名 + 深目录

        /// <summary>
        /// 多条目 + 中文名 + 深目录：目录结构与名字都要对（名字解码走 UTF-8 位；
        /// 没有那一位的包才退到 GBK，见 DecodeName 的说明）。
        /// </summary>
        [Fact]
        public void 多条目中文名深目录_结构与名字正确()
        {
            byte[] a = Encoding.UTF8.GetBytes("第一层\n");
            byte[] b = Encoding.UTF8.GetBytes("第二层\n");
            byte[] c = Encoding.UTF8.GetBytes("根目录\n");

            byte[] zip = BuildZipBytes(
                ("中文目录/子目录/深层文件.txt", a, CompressionLevel.Optimal),
                ("中文目录/另一个 名字.bin", b, CompressionLevel.NoCompression),
                ("根文件.txt", c, CompressionLevel.Optimal));

            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "unicode");
            string stage = Path.Combine(_root, "unicode-out");

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                FakeVideoPrefixLength + zip.Length,
                stage);

            Assert.True(result.Success, result.Message);
            Assert.Equal(3, result.FileCount);

            Assert.Equal("第一层\n", File.ReadAllText(Path.Combine(stage, "中文目录", "子目录", "深层文件.txt")));
            Assert.Equal("第二层\n", File.ReadAllText(Path.Combine(stage, "中文目录", "另一个 名字.bin")));
            Assert.Equal("根目录\n", File.ReadAllText(Path.Combine(stage, "根文件.txt")));

            // 清单里的名字也必须是解码后的中文（乱码会让用户认不出产物是哪来的）。
            Assert.Contains(result.Entries, e => e.Name == "中文目录/子目录/深层文件.txt");
        }

        // ================================================================ ④ 加密位

        /// <summary>
        /// 通用位标志 bit0（加密）→ **返回"不支持"**（回落信号），而且一个文件都不产。
        /// 直读没有密码这一步，绝不假装能解。
        /// </summary>
        [Fact]
        public void 加密位条目_返回不支持且不产任何文件()
        {
            byte[] zip = BuildZipBytes((PayloadEntryName, Encoding.UTF8.GetBytes(PayloadText), CompressionLevel.NoCompression));

            // 把中央目录里那个条目的通用位标志 bit0 打开。
            byte[] encrypted = PatchUInt16Field(
                zip,
                PayloadEntryName,
                fieldOffset: 8,
                patch: value => (ushort)(value | 0x0001));

            string polyglot = BuildPolyglot(encrypted, FakeVideoPrefixLength, TailAfterEocdLength, "encrypted");
            string stage = Path.Combine(_root, "encrypted-out");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(polyglot, FakeVideoPrefixLength, 0, stage);
            Assert.False(probe.Supported);
            Assert.False(probe.PathRejected);
            Assert.Contains("加密", probe.Reason, StringComparison.Ordinal);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.False(result.Success);
            Assert.True(result.Unsupported, "加密条目必须是回落信号（交给抠取 + 7z），不是硬失败");
            Assert.False(Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Any());
        }

        // ================================================================ ⑤ 不支持的方法

        /// <summary>
        /// bzip2（方法 12）之类的压缩方法 → 同样返回"不支持"，不产文件。
        /// 直读只认 stored 与 deflate 两种（AGENTS.md §6 第 6 条：绝不半成品）。
        /// </summary>
        [Fact]
        public void 不支持的方法_返回不支持且不产任何文件()
        {
            byte[] zip = BuildZipBytes((PayloadEntryName, Encoding.UTF8.GetBytes(PayloadText), CompressionLevel.NoCompression));

            byte[] bzip2 = PatchUInt16Field(
                zip,
                PayloadEntryName,
                fieldOffset: 10,
                patch: _ => (ushort)12);

            string polyglot = BuildPolyglot(bzip2, FakeVideoPrefixLength, TailAfterEocdLength, "bzip2");
            string stage = Path.Combine(_root, "bzip2-out");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.False(probe.Supported);
            Assert.False(probe.PathRejected);
            Assert.Contains("压缩方法", probe.Reason, StringComparison.Ordinal);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.False(result.Success);
            Assert.True(result.Unsupported);
            Assert.False(Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Any());
        }

        // ================================================================ ⑥ 越界条目名

        /// <summary>
        /// 越界条目名（父目录穿越 / 盘符绝对路径 / UNC）→ 判失败，而且**目标根之外不得出现任何文件**。
        ///
        /// <para>用的是与正常解压**同一处实现**的 <see cref="ArchivePathGuard"/>，
        /// 所以这里既证明"直读拦得住"，也保证两条路的口径一致。</para>
        /// </summary>
        [Theory]
        [InlineData("aa/evil.txt", "../evil.txt")]
        [InlineData("CCCabs.txt", "C:\\abs.txt")]
        [InlineData("xxserver/share/x", "\\\\server\\share\\x")]
        public void 越界条目名_判失败且目标根之外没有文件(string placeholder, string malicious)
        {
            Assert.Equal(placeholder.Length, malicious.Length);

            byte[] zip = BuildZipBytes((placeholder, Encoding.UTF8.GetBytes("evil\n"), CompressionLevel.NoCompression));
            byte[] patched = PatchName(zip, placeholder, malicious);

            string polyglot = BuildPolyglot(patched, FakeVideoPrefixLength, TailAfterEocdLength, "unsafe-" + Guid.NewGuid().ToString("N"));
            string stage = Path.Combine(_root, "unsafe-out-" + Guid.NewGuid().ToString("N"));

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.False(probe.Supported);
            Assert.True(probe.PathRejected, "越界条目名是硬失败（抠出来交给 7z 也会被同一套预检拒掉）");

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.False(result.Success);
            Assert.False(result.Unsupported);

            /*
             * 重点断言：目标根之外一个文件都没有 —— 整个临时根下只允许存在样本自己（.mp4）。
             * 这比"某个具体路径不存在"强：父目录穿越、盘符、UNC 三种形态都由它一起盖住。
             */
            Assert.All(
                Directory.GetFiles(_root, "*", SearchOption.AllDirectories),
                path => Assert.EndsWith(".mp4", path, StringComparison.OrdinalIgnoreCase));

            Assert.False(Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Any());
        }

        // ================================================================ ⑦ ZIP64 条目尺寸

        /// <summary>
        /// ZIP64 条目尺寸（扩展字段 0x0001）：中央目录里的尺寸写成 <c>0xFFFFFFFF</c> 占位符、
        /// 真值只在 extra 里。**这条测的是"正确解出"**（不是回落）—— 真值解析得出来就该直读，
        /// 否则用户那 2.4 GB 的 ZIP64 包永远享受不到直读。
        /// </summary>
        [Fact]
        public void ZIP64条目尺寸_从扩展字段取真值并正确解出()
        {
            byte[] payload = BuildRandomPayload(2048, seed: 77);
            byte[] zip = BuildZip64SizeZip(PayloadEntryName, payload);
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "zip64-size");
            string stage = Path.Combine(_root, "zip64-out");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                polyglot,
                FakeVideoPrefixLength,
                FakeVideoPrefixLength + zip.Length,
                stage);

            Assert.True(probe.Supported, probe.Reason);
            EmbeddedZipEntry entry = Assert.Single(probe.Entries);
            Assert.Equal(payload.Length, entry.Size);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                FakeVideoPrefixLength + zip.Length,
                stage);

            Assert.True(result.Success, result.Message);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, PayloadEntryName)));
        }

        /// <summary>
        /// 反例：中央目录写了 ZIP64 占位符、却**没有** 0x0001 扩展字段（真值拿不到）→ 明确回落，
        /// 绝不拿 <c>0xFFFFFFFF</c> 当尺寸去读（那会读出 4 GB 的垃圾）。
        /// </summary>
        [Fact]
        public void ZIP64占位符但没有扩展字段_回落不支持()
        {
            byte[] zip = BuildZipBytes((PayloadEntryName, Encoding.UTF8.GetBytes(PayloadText), CompressionLevel.NoCompression));

            byte[] placeholder = PatchUInt32Field(
                zip,
                PayloadEntryName,
                fieldOffset: 24,
                patch: _ => uint.MaxValue);

            string polyglot = BuildPolyglot(placeholder, FakeVideoPrefixLength, TailAfterEocdLength, "zip64-missing");
            string stage = Path.Combine(_root, "zip64-missing-out");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.False(probe.Supported);
            Assert.False(probe.PathRejected);
            Assert.Contains("ZIP64", probe.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.Exists(stage) ? Directory.GetFiles(stage) : Array.Empty<string>());
        }

        // ================================================================ ⑧ 与真 7z 交叉验证

        /// <summary>
        /// 同一个合成双面包：直读产物 == **先抠取、再用真 7z 解**出来的产物（逐字节）。
        ///
        /// <para>前置数据 9 MiB 是故意的：它超过 7-Zip 的容忍上限（实测 8 MiB），
        /// 所以 7z 必须先拿到抠出来的副本 —— 两条路解的是同一个归档，结论必须一致。</para>
        /// </summary>
        [Fact]
        public async Task 与真七z交叉验证_两条路的产物逐字节一致()
        {
            byte[] a = BuildRandomPayload(3000, seed: 1);
            byte[] b = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("deflate 段 0123456789\n", 300)));
            byte[] c = Encoding.UTF8.GetBytes("零字节之外的最后一个条目\n");

            byte[] zip = BuildZipBytes(
                ("inner/data.bin", a, CompressionLevel.NoCompression),
                ("inner/compressed.txt", b, CompressionLevel.Optimal),
                ("inner/更深/中文.txt", c, CompressionLevel.Optimal));

            string polyglot = BuildPolyglot(zip, BeyondSevenZipTolerancePrefixLength, TailAfterEocdLength, "cross");
            long archiveEnd = new FileInfo(polyglot).Length - TailAfterEocdLength;

            // 直读那条路。
            string directOut = Path.Combine(_root, "cross-direct");
            EmbeddedZipExtractResult direct = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                BeyondSevenZipTolerancePrefixLength,
                archiveEnd,
                directOut);

            Assert.True(direct.Success, direct.Message);

            // 抠取 + 真 7z 那条路（今天的做法）。
            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(polyglot);
            Assert.True(info.Found, info.Message);

            string carvedPath = Path.Combine(_root, "cross-carved", "pack.zip");
            CarveResult carve = EmbeddedArchiveCarver.Carve(polyglot, info.Offset, carvedPath, info.ArchiveEnd);
            Assert.True(carve.Success, carve.Message);

            string sevenZipOut = Path.Combine(_root, "cross-sevenzip");
            Directory.CreateDirectory(sevenZipOut);

            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, "这条测试需要项目内置的 7z.exe");

            ArchiveOperationResult extracted = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = carve.OutputPath,
                    OutputPath = sevenZipOut
                },
                new ExtractOptions
                {
                    ExtractToOriginalDirectory = false,
                    CustomOutputDirectory = sevenZipOut,
                    KeepArchiveNameFolder = false,
                    OverwriteMode = "OverwriteAll"
                },
                CancellationToken.None);

            Assert.True(extracted.Success, extracted.Message);

            Dictionary<string, byte[]> directFiles = ReadTree(directOut);
            Dictionary<string, byte[]> sevenZipFiles = ReadTree(sevenZipOut);

            Assert.Equal(sevenZipFiles.Keys.OrderBy(x => x, StringComparer.Ordinal), directFiles.Keys.OrderBy(x => x, StringComparer.Ordinal));

            foreach ((string relative, byte[] bytes) in sevenZipFiles)
            {
                Assert.Equal(bytes, directFiles[relative]);
            }
        }

        // ================================================================ ⑨ 取消

        /// <summary>
        /// 中途取消：抛 <see cref="OperationCanceledException"/>（调用方落成"已取消"），
        /// **不留半成品、不留产物** —— 连已经落位的文件也要被回滚掉。
        ///
        /// <para>条目用"高度可压缩的大文件"（192 MiB 的零）：解压要跑几百毫秒，
        /// 于是进度回调一定能拿到一个中间百分比，取消就发生在**写盘进行中**。
        /// 预算放宽展开比：这里要测的是取消，不是压缩炸弹判定。</para>
        /// </summary>
        [Fact]
        public void 中途取消_抛取消且无半成品无产物()
        {
            byte[] zip = BuildZeroFilledDeflateZip("big.bin", 192 * 1024 * 1024);
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "cancel");
            string stage = Path.Combine(_root, "cancel-out");

            using var cts = new CancellationTokenSource();
            var progress = new CallbackProgress(percent =>
            {
                if (percent > 0)
                {
                    cts.Cancel();
                }
            });

            var options = new ResourceBudgetOptions { MaxExpansionRatio = 1_000_000d };

            Assert.ThrowsAny<OperationCanceledException>(() => EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                FakeVideoPrefixLength + zip.Length,
                stage,
                progress,
                cts.Token,
                options));

            Assert.False(Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Any(), "取消后不许留半个文件");
        }

        // ================================================================ ⑩ 进度

        /// <summary>
        /// 进度：至少上报一次百分比；**总量为 0**（只有空文件 / 目录条目）时不崩、也不谎报成功之外的结论。
        /// </summary>
        [Fact]
        public void 进度_至少上报一次且总量为零不崩()
        {
            byte[] payload = BuildRandomPayload(1024, seed: 5);
            byte[] zip = BuildZipBytes(("one.bin", payload, CompressionLevel.NoCompression));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, 0, "progress");
            string stage = Path.Combine(_root, "progress-out");

            var progress = new CallbackProgress(_ => { });

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(
                polyglot,
                FakeVideoPrefixLength,
                0,
                stage,
                progress);

            Assert.True(result.Success, result.Message);
            Assert.NotEmpty(progress.Percentages);
            Assert.Contains(100, progress.Percentages);

            // 总量为 0：只有空文件与目录条目 —— 不崩，进度也不越界。
            byte[] emptyZip = BuildZipBytes(
                ("空文件.txt", Array.Empty<byte>(), CompressionLevel.NoCompression),
                ("空目录/", Array.Empty<byte>(), CompressionLevel.NoCompression));

            string emptyPolyglot = BuildPolyglot(emptyZip, FakeVideoPrefixLength, 0, "progress-empty");
            string emptyStage = Path.Combine(_root, "progress-empty-out");
            var emptyProgress = new CallbackProgress(_ => { });

            EmbeddedZipExtractResult emptyResult = EmbeddedZipStreamExtractor.Extract(
                emptyPolyglot,
                FakeVideoPrefixLength,
                0,
                emptyStage,
                emptyProgress);

            Assert.True(emptyResult.Success, emptyResult.Message);
            Assert.NotEmpty(emptyProgress.Percentages);
            Assert.All(emptyProgress.Percentages, percent => Assert.InRange(percent, 0, 100));
            Assert.True(File.Exists(Path.Combine(emptyStage, "空文件.txt")));
            Assert.True(Directory.Exists(Path.Combine(emptyStage, "空目录")));
        }

        // ================================================================ ⑪ 尾巴不算条目数据

        /// <summary>
        /// <c>ArchiveEnd</c> 之后的尾巴字节**不得**被当成条目数据。
        ///
        /// <para>样本的尾巴里刻意埋了两样东西：一个假的 <c>PK\x03\x04</c>（看着像本地文件头）
        /// 和一个字段全是垃圾的假 <c>PK\x05\x06</c>（EOCD 候选）。结论必须仍然是
        /// "只有清单里那一个条目、内容逐字节正确" —— 假 EOCD 被跳过、假本地头不算条目。</para>
        ///
        /// <para>这里传 <c>archiveEnd = 0</c>（= 回落到文件末尾），是最苛刻的形态：
        /// 归档区间把尾巴也圈进来了，靠的全是"中央目录说了算"。</para>
        /// </summary>
        [Fact]
        public void 归档终点之后的尾巴_不被当成条目数据()
        {
            byte[] payload = Encoding.UTF8.GetBytes("真正的条目\n");
            byte[] zip = BuildZipBytes((PayloadEntryName, payload, CompressionLevel.NoCompression));
            string polyglot = BuildPolyglotWithDecoyTail(zip, FakeVideoPrefixLength);
            string stage = Path.Combine(_root, "tail-decoy-out");

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(polyglot, FakeVideoPrefixLength, 0, stage);
            Assert.True(probe.Supported, probe.Reason);
            Assert.Equal(1, probe.List!.FileCount);

            EmbeddedZipExtractResult result = EmbeddedZipStreamExtractor.Extract(polyglot, FakeVideoPrefixLength, 0, stage);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.FileCount);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(stage, PayloadEntryName)));
            Assert.Single(Directory.GetFiles(stage));
        }

        // ================================================================ ⑫ 空间核算

        /// <summary>
        /// 空间核算的口径（用户 2026-09-24 需求第 7 条）：**直读可用时不预留那笔抠取副本**，
        /// 不可用时照旧要算 —— 不许一边说"省了副本"、一边又把它预留出来。
        /// </summary>
        [Fact]
        public void 空间核算_直读可用时不预留抠取副本()
        {
            string sample = Path.Combine(_root, "estimate.mp4");
            File.WriteAllBytes(sample, new byte[8192]);

            var task = new ArchiveTask(sample)
            {
                EmbeddedArchiveOffset = 2048,
                EmbeddedArchiveEnd = 6144
            };

            TaskSpaceEstimate carveRoute = SpaceEstimator.FromSourceFiles(task, directReadAvailable: false);
            TaskSpaceEstimate directRoute = SpaceEstimator.FromSourceFiles(task, directReadAvailable: true);

            // 抠取那条路：过程物 = 区间长度（6144 − 2048），账面上有这一笔。
            Assert.Equal(4096, carveRoute.ProcessArtifactBytes);
            Assert.Contains("抠取副本", carveRoute.Basis, StringComparison.Ordinal);

            // 直读那条路：一笔都不记，而且账面上明说"不需要那份等大的临时副本"。
            Assert.Equal(0, directRoute.ProcessArtifactBytes);
            Assert.Equal(carveRoute.PeakBytes - 4096, directRoute.PeakBytes);
            Assert.Contains("不需要", directRoute.Basis, StringComparison.Ordinal);
        }

        // ================================================================ ⑬ 端到端

        /// <summary>
        /// 端到端：<c>[9 MiB 假 MP4 头][完整 ZIP][15 KB 尾巴]</c> 走完真管线
        /// （真 MainViewModel + 真各 Coordinator + 真 7z），内容物解出来，
        /// <b>而工作区里没有那份抠出来的等大 .zip</b> —— 这是"省掉副本"在真实流程里的证据。
        /// </summary>
        [Fact]
        public async Task 端到端_双面文件走真管线_直读解出内容且工作区里没有抠出来的zip()
        {
            byte[] zip = BuildZipBytes((PayloadEntryName, Encoding.UTF8.GetBytes(PayloadText), CompressionLevel.Optimal));
            string source = BuildPolyglot(zip, BeyondSevenZipTolerancePrefixLength, TailAfterEocdLength, "e2e");

            Harness harness = CreateHarness();

            await harness.AddPathsAsync(source);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.True(task.EmbeddedArchiveOffset > 0, "应该识别出尾部有内嵌归档");

            await harness.RunOneClickAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string[] payloads = Directory.GetFiles(harness.OutputRoot, PayloadEntryName, SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"应该只解出一份 {PayloadEntryName}，实际 {payloads.Length} 份");
            Assert.Equal(PayloadText, File.ReadAllText(payloads[0]));

            // 抠取路线的痕迹必须**不存在**：工作区里没有 .zip，日志里也没有"取出内嵌归档"。
            Assert.Empty(Directory.GetFiles(harness.WorkRoot, "*.zip", SearchOption.AllDirectories));
            Assert.Contains(harness.Log.Logs, x => x.Message.Contains("直读", StringComparison.Ordinal));
            Assert.DoesNotContain(harness.Log.Logs, x => x.Message.Contains("取出内嵌归档", StringComparison.Ordinal));
        }

        // ================================================================ 样本构造

        /// <summary>固定种子的随机字节（样本可复现，也不是全零 —— 全零会掩盖偏移写错）。</summary>
        private static byte[] BuildRandomPayload(int length, int seed)
        {
            byte[] payload = new byte[length];
            new Random(seed).NextBytes(payload);

            return payload;
        }

        /// <summary>用框架自带 <see cref="ZipArchive"/> 造一个普通 ZIP（在内存里）。</summary>
        private static byte[] BuildZipBytes(params (string Name, byte[] Data, CompressionLevel Level)[] entries)
        {
            using var memory = new MemoryStream();

            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach ((string name, byte[] data, CompressionLevel level) in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, level);

                    if (data.Length == 0)
                    {
                        // 空条目：连流都不写（目录条目也是这条路径）。
                        continue;
                    }

                    using Stream stream = entry.Open();
                    stream.Write(data, 0, data.Length);
                }
            }

            return memory.ToArray();
        }

        /// <summary>
        /// 造一个"deflate 大零块"的 ZIP：条目在内存里只有几百 KB，解出来却有 <paramref name="length"/> 字节 ——
        /// 取消测试要的就是"解压要跑一会儿、而磁盘上几乎不占地方"。
        /// </summary>
        private static byte[] BuildZeroFilledDeflateZip(string name, int length)
        {
            using var memory = new MemoryStream();

            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

                using Stream stream = entry.Open();
                byte[] chunk = new byte[1024 * 1024];

                int remaining = length;

                while (remaining > 0)
                {
                    int count = Math.Min(chunk.Length, remaining);

                    stream.Write(chunk, 0, count);
                    remaining -= count;
                }
            }

            return memory.ToArray();
        }

        /// <summary>
        /// 手搓一个"条目尺寸走 ZIP64 扩展字段"的 ZIP：
        /// 中央目录里的原始/压缩大小写成 <c>0xFFFFFFFF</c>，真值放在 0x0001 扩展字段里。
        ///
        /// <para>框架的 <see cref="ZipArchive"/> 不会产出这种形态（它只在真的超过 4 GiB 时才写 ZIP64），
        /// 所以这里按 APPNOTE 4.3.9 / 4.5.3 手工拼一遍 —— 真实现场里 2.4 GB 的那个包就是这种写法。</para>
        /// </summary>
        private static byte[] BuildZip64SizeZip(string name, byte[] payload)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int localLength = 30 + nameBytes.Length;
            int extraLength = 4 + 16; // 头 4 字节 + [原始大小 8][压缩后大小 8]

            using var memory = new MemoryStream();
            using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

            // 本地文件头：尺寸写 0（直读一律以中央目录为准，本地头里的值本来也不可信）。
            writer.Write(Encoding.ASCII.GetBytes("PK\x03\x04"));
            writer.Write((ushort)45);
            writer.Write((ushort)0);
            writer.Write((ushort)0);   // stored
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Write(payload);

            long cdOffset = memory.Position;

            // 中央目录条目：原始/压缩大小都是占位符，真值进 0x0001 扩展字段。
            writer.Write(Encoding.ASCII.GetBytes("PK\x01\x02"));
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)extraLength);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(nameBytes);
            writer.Write((ushort)0x0001);
            writer.Write((ushort)16);
            writer.Write((ulong)payload.Length);
            writer.Write((ulong)payload.Length);

            long cdSize = memory.Position - cdOffset;

            // EOCD：条目数 1、中央目录位置与大小都对得上。
            writer.Write(Encoding.ASCII.GetBytes("PK\x05\x06"));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((uint)cdSize);
            writer.Write((uint)cdOffset);
            writer.Write((ushort)0);

            writer.Flush();

            // 本地头长度得与 CD 里的偏移一致：这里本地头没有扩展字段，偏移就是 0。
            _ = localLength;

            return memory.ToArray();
        }

        /// <summary>
        /// 拼出"双面文件"：[假 MP4 头][ZIP 字节][EOCD 之后的尾巴]。
        /// <paramref name="zip"/> 直接给字节，因为有些样本是补丁改过的。
        /// </summary>
        private string BuildPolyglot(byte[] zip, int prefixLength, int tailAfterEocdLength, string tag)
        {
            string path = Path.Combine(_root, tag + ".mp4");

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            output.Write(BuildFakeVideoPrefix(prefixLength));
            output.Write(zip);

            if (tailAfterEocdLength > 0)
            {
                output.Write(BuildTailAfterEocd(tailAfterEocdLength));
            }

            return path;
        }

        /// <summary>同上的便捷重载（样本本身就是那个 ZIP 文件时）。</summary>
        private string BuildPolyglot(string zipPath, int prefixLength, int tailAfterEocdLength, string tag) =>
            BuildPolyglot(File.ReadAllBytes(zipPath), prefixLength, tailAfterEocdLength, tag);

        /// <summary>
        /// 尾巴里埋诱饵：一个假本地头（<c>PK\x03\x04</c>）+ 一个字段全是垃圾的假 EOCD（<c>PK\x05\x06</c>）。
        /// 尾巴的花纹是"逐字节递增"，本身永远不会碰巧出现 ZIP 签名。
        /// </summary>
        private string BuildPolyglotWithDecoyTail(byte[] zip, int prefixLength)
        {
            string path = Path.Combine(_root, "decoy-tail.mp4");

            byte[] tail = BuildTailAfterEocd(TailAfterEocdLength);

            // 假本地头：签名 + 26 字节数据，后面跟着一段"文件名"。
            Encoding.ASCII.GetBytes("PK\x03\x04").CopyTo(tail, 0);
            Encoding.ASCII.GetBytes("decoy.dat").CopyTo(tail, 30);

            // 假 EOCD：条目数 9、中央目录大小/偏移都是垃圾 → 自洽校验必须否掉它。
            int decoyEocd = 512;
            Encoding.ASCII.GetBytes("PK\x05\x06").CopyTo(tail, decoyEocd);
            BitConverter.GetBytes((ushort)9).CopyTo(tail, decoyEocd + 10);
            BitConverter.GetBytes((uint)123456u).CopyTo(tail, decoyEocd + 12);
            BitConverter.GetBytes((uint)654321u).CopyTo(tail, decoyEocd + 16);

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            output.Write(BuildFakeVideoPrefix(prefixLength));
            output.Write(zip);
            output.Write(tail);

            return path;
        }

        /// <summary>假 MP4 头：前 12 字节是真格式的 <c>ftyp</c> box，后面填随机字节（固定种子）。</summary>
        private static byte[] BuildFakeVideoPrefix(int length)
        {
            byte[] prefix = new byte[length];

            new Random(20260924).NextBytes(prefix);

            byte[] boxSize = { 0x00, 0x00, 0x00, 0x20 };
            byte[] ftyp = Encoding.ASCII.GetBytes("ftypisom");

            Array.Copy(boxSize, 0, prefix, 0, boxSize.Length);
            Array.Copy(ftyp, 0, prefix, 4, ftyp.Length);

            return prefix;
        }

        /// <summary>
        /// EOCD 之后那十几 KB 正常数据。刻意用"逐字节递增"的花纹：它**永远不会**碰巧出现
        /// <c>PK\x05\x06</c>（相邻字节只差 1），于是测的是"EOCD 后面有数据"，不是"后面又有一个假 EOCD"。
        /// </summary>
        private static byte[] BuildTailAfterEocd(int length)
        {
            byte[] tail = new byte[length];

            for (int i = 0; i < length; i++)
            {
                tail[i] = (byte)(i % 251);
            }

            return tail;
        }

        /// <summary>
        /// 把 ZIP 中央目录里某个条目的某个 32 位字段补丁掉
        /// （用来造"ZIP64 占位符"这类形态）。
        /// </summary>
        private static byte[] PatchUInt32Field(
            byte[] zip,
            string entryName,
            int fieldOffset,
            Func<uint, uint> patch)
        {
            byte[] result = (byte[])zip.Clone();
            int index = FindCentralDirectoryEntry(result, entryName);

            uint current = BitConverter.ToUInt32(result, index + fieldOffset);
            BitConverter.GetBytes(patch(current)).CopyTo(result, index + fieldOffset);

            return result;
        }

        /// <summary>同上，但按 16 位字段补丁（加密位 / 压缩方法都在这一档）。</summary>
        private static byte[] PatchUInt16Field(
            byte[] zip,
            string entryName,
            int fieldOffset,
            Func<ushort, ushort> patch)
        {
            byte[] result = (byte[])zip.Clone();
            int index = FindCentralDirectoryEntry(result, entryName);

            ushort current = BitConverter.ToUInt16(result, index + fieldOffset);
            BitConverter.GetBytes(patch(current)).CopyTo(result, index + fieldOffset);

            return result;
        }

        /// <summary>
        /// 把条目名整体换掉（本地头与中央目录两处都换）。要求等长 ——
        /// 这样偏移全都不用重算，样本里只有"名字"这一个变量。
        /// </summary>
        private static byte[] PatchName(byte[] zip, string placeholder, string malicious)
        {
            Assert.Equal(placeholder.Length, malicious.Length);

            byte[] find = Encoding.UTF8.GetBytes(placeholder);
            byte[] replace = Encoding.UTF8.GetBytes(malicious);
            byte[] result = (byte[])zip.Clone();

            int replaced = 0;

            for (int i = 0; i + find.Length <= result.Length; i++)
            {
                bool match = true;

                for (int j = 0; j < find.Length; j++)
                {
                    if (result[i + j] != find[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                Array.Copy(replace, 0, result, i, replace.Length);
                replaced++;
                i += find.Length - 1;
            }

            Assert.Equal(2, replaced); // 本地头 + 中央目录

            return result;
        }

        private static int FindCentralDirectoryEntry(byte[] zip, string entryName)
        {
            int eocd = FindEndOfCentralDirectory(zip);
            Assert.True(eocd > 0, "样本里应该能找到 EOCD");

            long cdSize = BitConverter.ToUInt32(zip, eocd + 12);
            long cdOffset = BitConverter.ToUInt32(zip, eocd + 16);
            int index = (int)cdOffset;

            while (index < cdOffset + cdSize)
            {
                int nameLength = BitConverter.ToUInt16(zip, index + 28);
                int extraLength = BitConverter.ToUInt16(zip, index + 30);
                int commentLength = BitConverter.ToUInt16(zip, index + 32);
                string name = Encoding.UTF8.GetString(zip, index + 46, nameLength);

                if (string.Equals(name, entryName, StringComparison.Ordinal))
                {
                    return index;
                }

                index += 46 + nameLength + extraLength + commentLength;
            }

            throw new InvalidOperationException("样本里找不到条目：" + entryName);
        }

        private static int FindEndOfCentralDirectory(byte[] bytes)
        {
            for (int i = bytes.Length - 22; i >= 0; i--)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>把一棵目录树读成"相对路径（小写、正斜杠）→ 字节"。</summary>
        private static Dictionary<string, byte[]> ReadTree(string root)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                files[relative] = File.ReadAllBytes(path);
            }

            return files;
        }

        /// <summary>同步回调的进度接收端（测试里要的是"回调里立刻能取消"，不是异步投递）。</summary>
        private sealed class CallbackProgress : IProgress<int>
        {
            private readonly Action<int> _onReport;

            public CallbackProgress(Action<int> onReport)
            {
                _onReport = onReport;
            }

            public List<int> Percentages { get; } = new();

            public void Report(int value)
            {
                lock (Percentages)
                {
                    Percentages.Add(value);
                }

                _onReport(value);
            }
        }

        // ================================================================ 端到端装配

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎。
        /// 缓存根 / 输出目录 / 密码本全部落在临时目录里，绝不碰用户的目录（AGENTS.md §8）。
        ///
        /// 与 <c>EmbeddedArchiveTailTests</c> 同一套装配方式：<see cref="MainViewModel"/> 的构造会写两个
        /// 进程级静态（7z 路径、递归工作区根目录），所以本类声明进 <c>ArchiveFixerGlobalState</c> 集合
        /// （不与其他集合并行），并在构造完立刻还原那两个静态。
        /// </summary>
        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            string bookPath = Path.Combine(_root, "password-book.txt");
            File.WriteAllText(bookPath, "# 合成密码本：本组测试不需要密码\n", new UTF8Encoding(false));

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;
            settings.CustomSevenZipExePath = string.Empty;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

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

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            extraction.KeepTaskDetailInLog = true;
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, outputRoot, pathService.WorkDirectory, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(MainViewModel vm, OneClickCoordinator oneClick, string outputRoot, string workRoot, LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                OutputRoot = outputRoot;
                WorkRoot = workRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public string OutputRoot { get; }

            /// <summary>工作区根（<c>&lt;数据根&gt;\work</c>）：抠出来的过程物会落在这里。</summary>
            public string WorkRoot { get; }

            public LogService Log { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }
    }
}
