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
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// "双面文件"的**第二种真实形态**（2026-09-24 用户报的缺陷）：
    /// 前面是真 MP4 头、尾部是完整 ZIP，但 **EOCD 之后还有十几 KB 正常数据**；
    /// 大的那个还带 **ZIP64 收尾**（EOCD 前面夹 56 字节 ZIP64 EOCD + 20 字节 locator）。
    ///
    /// <para>
    /// 旧判据是"EOCD 必须正好是文件最后 22 + 注释长度字节"，于是这批文件全被判成"格式未知"。
    /// 这个文件里的每一条测试都对着一个具体判据：
    /// </para>
    /// <list type="bullet">
    /// <item><description>① 真 MP4 头 + 尾部完整 ZIP → 命中，偏移与终点都对；</description></item>
    /// <item><description>② EOCD 之后还有 15 KB 尾巴 → 仍然命中，且终点**不含**尾巴；</description></item>
    /// <item><description>③ ZIP64 形态（EOCD 字段是占位符）→ 命中，偏移取自 ZIP64 记录；</description></item>
    /// <item><description>④–⑥ 三个反例：普通 MP4 / PE + 尾部数据 / 篡改过字段的假包 → 一律不命中；
    /// </description></item>
    /// <item><description>⑦ 按终点截断抠出来的字节 == 终点 − 起点，而且 7z 能列出里头的条目；</description></item>
    /// <item><description>⑧ 端到端：合成包走完真管线后解出 ZIP 里的内容，结论不再是"格式未知"。</description></item>
    /// </list>
    ///
    /// 样本**全部自己造**（临时目录 + 框架自带 <see cref="ZipArchive"/> + 项目内置 7z.exe），
    /// 绝不引用用户机器上的任何真实文件/路径/文件名（AGENTS.md §8）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class EmbeddedArchiveTailTests : IDisposable
    {
        /// <summary>假 MP4 头长度：与真实现场同量级，也保证 34 KB 的文件头读不到尾部的 ZIP。</summary>
        private const int FakeVideoPrefixLength = 32768;

        /// <summary>超过 7-Zip 容忍上限（实测 8 MiB）的前置数据长度：端到端那条用它证明"必须抠出来"。</summary>
        private const int BeyondSevenZipTolerancePrefixLength = 9 * 1024 * 1024;

        /// <summary>EOCD 之后那段正常数据的长度：复现实测的 14,350–17,424 字节这个量级。</summary>
        private const int TailAfterEocdLength = 15000;

        /// <summary>ZIP64 收尾长度：EOCD64(56) + locator(20)。</summary>
        private const int Zip64FooterLength = 76;

        private const string NestedEntryName = "data.7z.001";
        private const string PayloadEntryName = "payload.txt";
        private const string NestedEntryText = "尾部 ZIP 里的第一个条目\n";
        private const string PayloadText = "第二层才有的最终数据\n";

        private readonly string _root;
        private readonly string _sevenZip;

        public EmbeddedArchiveTailTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEmbeddedTail", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 真 MP4 头 + 尾部完整 ZIP

        /// <summary>
        /// 最基本的一条：真 MP4 头 + 尾部完整 ZIP（ZIP 正好到文件末尾）。
        /// 这条在旧判据下也是通的 —— 留着它是**回归基线**：放宽判据不许把原本能认的形态弄丢。
        /// </summary>
        [Fact]
        public void 真MP4头加尾部完整ZIP_命中且偏移与终点都在文件末尾()
        {
            string zip = BuildZip((NestedEntryName, NestedEntryText));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, tailAfterEocdLength: 0, "plain");
            long fileLength = new FileInfo(polyglot).Length;

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(polyglot);

            Assert.True(info.Found, info.Message);
            Assert.Equal("ZIP", info.Format);
            Assert.Equal(".zip", info.SuggestedExtension);
            Assert.Equal(FakeVideoPrefixLength, info.Offset);
            Assert.Equal(fileLength, info.ArchiveEnd);
            Assert.Equal(fileLength - FakeVideoPrefixLength, info.ArchiveLength);
            Assert.Equal(1, info.EntryCount);
        }

        // ================================================================ ② EOCD 之后还有 15 KB 尾巴

        /// <summary>
        /// 用户那 5 个文件的形态：<c>[MP4 头][完整 ZIP][15 KB 正常数据]</c>。
        ///
        /// 旧判据只认"EOCD 正好是文件最后 22 + 注释长度字节"，于是这种文件被判成"格式未知"。
        /// 现在必须命中，而且 <see cref="EmbeddedArchiveInfo.ArchiveEnd"/> 要停在 EOCD 之后 ——
        /// 那 15 KB 是别的东西，抠取时一个字节都不能带出去。
        /// </summary>
        [Fact]
        public void EOCD之后还有十五KB尾巴_仍然命中且终点不含尾巴()
        {
            string zip = BuildZip((NestedEntryName, NestedEntryText));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "with-tail");
            byte[] bytes = File.ReadAllBytes(polyglot);
            long fileLength = bytes.Length;

            int eocdIndex = FindLastEndOfCentralDirectory(bytes);
            Assert.True(eocdIndex > 0, "样本里应该能找到 EOCD");

            // 样本本身得真的复现现场：EOCD 不在文件末尾，后面还跟着 15 KB。
            Assert.Equal(TailAfterEocdLength, fileLength - eocdIndex - 22);

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(polyglot);

            Assert.True(info.Found, info.Message);
            Assert.Equal(FakeVideoPrefixLength, info.Offset);

            // 终点 = EOCD + 22 + 注释长度（注释 0），正好把那 15 KB 挡在外面。
            Assert.Equal(eocdIndex + 22, info.ArchiveEnd);
            Assert.Equal(fileLength - TailAfterEocdLength, info.ArchiveEnd);
            Assert.Equal(info.ArchiveEnd - info.Offset, info.ArchiveLength);
        }

        // ================================================================ ③ ZIP64 形态

        /// <summary>
        /// ZIP64 形态：EOCD 前面夹 56 字节 ZIP64 EOCD + 20 字节 locator，
        /// 而普通 EOCD 的三个字段全写成占位符（<c>0xFFFF</c> / <c>0xFFFFFFFF</c>）。
        ///
        /// 占位符比"直接写 64 位真值"更接近真实现场：能命中就说明代码真的去 ZIP64 记录里取值了，
        /// 不可能靠 32 位字段蒙对。样本也带 15 KB 尾巴（用户的 2.4 GB 那个就是 ZIP64 + 尾巴）。
        /// </summary>
        [Fact]
        public void ZIP64形态_占位符EOCD也命中且偏移与终点正确()
        {
            string zip = BuildZip((NestedEntryName, NestedEntryText));
            byte[] zip64 = BuildZip64Bytes(zip);
            string polyglot = BuildPolyglotBytes(zip64, FakeVideoPrefixLength, TailAfterEocdLength, "zip64");

            byte[] bytes = File.ReadAllBytes(polyglot);
            int eocdIndex = FindLastEndOfCentralDirectory(bytes);
            Assert.Equal(0xFFFF, BitConverter.ToUInt16(bytes, eocdIndex + 10));
            Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(bytes, eocdIndex + 12));
            Assert.Equal(0xFFFFFFFFu, BitConverter.ToUInt32(bytes, eocdIndex + 16));

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(polyglot);

            Assert.True(info.Found, info.Message);
            Assert.Equal(FakeVideoPrefixLength, info.Offset);
            Assert.Equal(FakeVideoPrefixLength + zip64.Length, info.ArchiveEnd);
            Assert.Equal(1, info.EntryCount);

            // 抠出来的字节必须与"ZIP64 化之后的那份 ZIP"逐字节一致 ——
            // 说明中央目录位置真是从 64 位字段算出来的，而不是碰巧。
            string carved = Path.Combine(_root, "carved-zip64", "pack.zip");
            CarveResult carve = EmbeddedArchiveCarver.Carve(polyglot, info.Offset, carved, info.ArchiveEnd);

            Assert.True(carve.Success, carve.Message);
            Assert.Equal(zip64.Length, carve.BytesWritten);
            Assert.Equal(zip64, File.ReadAllBytes(carve.OutputPath));
        }

        // ================================================================ ④ 反例：普通 MP4

        /// <summary>反例一：只有视频、尾部啥也没有 → 一个候选都过不了，必须不命中（不许误报）。</summary>
        [Fact]
        public async Task 反例_普通MP4只有视频_不命中且仍是格式未知()
        {
            string fake = Path.Combine(_root, "video-only.mp4");
            File.WriteAllBytes(fake, BuildFakeVideoPrefix(4 * 1024 * 1024));

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(fake);

            Assert.False(info.Found);
            Assert.Equal(0, info.Offset);
            Assert.Equal(0, info.ArchiveEnd);

            DetectResult result = await new ArchiveDetectService().DetectAsync(fake);

            Assert.Equal("Unknown", result.Format);
            Assert.False(result.IsArchive);
            Assert.Equal(0, result.EmbeddedArchiveOffset);
            Assert.Equal(0, result.EmbeddedArchiveEnd);
        }

        // ================================================================ ⑤ 反例：PE 头 + 尾部数据

        /// <summary>
        /// 反例二：自解压安装器那种形态 —— PE 头 + 尾部接数据。
        ///
        /// 其中"尾部接数据"这一版故意放了一个**孤零零的 EOCD**（位置与字段都自相矛盾）：
        /// 这正是放宽判据之后最需要防的误报路径 —— 光有 EOCD 签名不算数，
        /// 必须同时满足"中央目录签名在算出来的位置"和"起点是局部文件头签名"两条。
        /// </summary>
        [Fact]
        public void 反例_PE头加尾部数据_不命中()
        {
            string plain = Path.Combine(_root, "setup-plain.exe");
            File.WriteAllBytes(plain, BuildPeWithTrailingData(64 * 1024, orphanEocd: false));

            Assert.False(EmbeddedArchiveDetector.Detect(plain).Found);

            string withOrphanEocd = Path.Combine(_root, "setup-orphan-eocd.exe");
            File.WriteAllBytes(withOrphanEocd, BuildPeWithTrailingData(64 * 1024, orphanEocd: true));

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(withOrphanEocd);

            Assert.False(info.Found);
            Assert.Equal(0, info.Offset);
        }

        // ================================================================ ⑥ 反例：篡改一个字节

        /// <summary>
        /// 反例三：EOCD 后面跟着垃圾，而且中央目录位置对不上（cdSize / cdOffset 各篡改一字节）。
        ///
        /// 这两条是"放宽之后为什么仍然安全"的直接证据：位置是从文件里的数字算出来的，
        /// 数字被改动一个字节，算出来的地方就不再是签名 —— 结论立刻不成立。
        /// </summary>
        [Fact]
        public void 反例_尾巴加上被打歪的中央目录字段_不命中()
        {
            string zip = BuildZip((NestedEntryName, NestedEntryText));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "tamper");
            byte[] bytes = File.ReadAllBytes(polyglot);
            int eocdIndex = FindLastEndOfCentralDirectory(bytes);

            Assert.True(EmbeddedArchiveDetector.Detect(polyglot).Found, "原样本必须先是命中的，否则这条测试证明不了任何事");

            // ① 中央目录大小 +1：算出来的"中央目录实际起点"偏了一字节，那里不再有 PK 01 02。
            byte[] badSize = (byte[])bytes.Clone();
            BitConverter.GetBytes(BitConverter.ToUInt32(badSize, eocdIndex + 12) + 1).CopyTo(badSize, eocdIndex + 12);
            string badSizePath = Path.Combine(_root, "bad-size.mp4");
            File.WriteAllBytes(badSizePath, badSize);
            Assert.False(EmbeddedArchiveDetector.Detect(badSizePath).Found);

            // ② 中央目录偏移 +1：delta 因此偏了一字节，那里不再是 PK 03 04。
            byte[] badOffset = (byte[])bytes.Clone();
            BitConverter.GetBytes(BitConverter.ToUInt32(badOffset, eocdIndex + 16) + 1).CopyTo(badOffset, eocdIndex + 16);
            string badOffsetPath = Path.Combine(_root, "bad-offset.mp4");
            File.WriteAllBytes(badOffsetPath, badOffset);
            Assert.False(EmbeddedArchiveDetector.Detect(badOffsetPath).Found);
        }

        // ================================================================ ⑦ 抠取截断

        /// <summary>
        /// 抠取必须按终点截断：<c>抠出的字节数 == ArchiveEnd − Offset</c>，
        /// 而且抠出来的那份能被真 7z 列出条目（只抠对了位置、没抠对长度是列不出来的）。
        /// </summary>
        [Fact]
        public async Task 抠取按终点截断_字节数相符且七z能列出条目()
        {
            string zip = BuildZip((NestedEntryName, NestedEntryText));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "carve");
            long fileLength = new FileInfo(polyglot).Length;

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(polyglot);
            Assert.True(info.Found, info.Message);

            string target = Path.Combine(_root, "carved", "pack.zip");
            CarveResult carve = EmbeddedArchiveCarver.Carve(polyglot, info.Offset, target, info.ArchiveEnd);

            Assert.True(carve.Success, carve.Message);
            Assert.Equal(info.ArchiveEnd - info.Offset, carve.BytesWritten);
            Assert.Equal(carve.BytesWritten, new FileInfo(carve.OutputPath).Length);

            // 抠出来的那份必须比源文件短：短掉的是"前面那段视频头 + EOCD 之后那 15 KB 尾巴"。
            Assert.Equal(TailAfterEocdLength + info.Offset, fileLength - carve.BytesWritten);

            SevenZipEngine engine = CreateEngine();

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(carve.OutputPath), CancellationToken.None);

            Assert.True(list.Success, $"抠出来的包应能被 7z 列出：{list.Message}");
            Assert.Contains(list.Entries, e => e.Path.EndsWith(NestedEntryName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 终点缺省（0）时回落到文件末尾 —— 与这个参数出现之前的行为完全一致。
        /// 老调用点（以及"不知道归档到哪儿结束"的场合）走的就是这条，不许因为新参数而改变语义。
        /// </summary>
        [Fact]
        public void 抠取终点缺省时回落到文件末尾()
        {
            string zip = BuildZip((NestedEntryName, NestedEntryText));
            string polyglot = BuildPolyglot(zip, FakeVideoPrefixLength, TailAfterEocdLength, "fallback");
            long fileLength = new FileInfo(polyglot).Length;

            CarveResult carve = EmbeddedArchiveCarver.Carve(
                polyglot,
                FakeVideoPrefixLength,
                Path.Combine(_root, "carved-fallback", "pack.zip"));

            Assert.True(carve.Success, carve.Message);
            Assert.Equal(fileLength - FakeVideoPrefixLength, carve.BytesWritten);
        }

        // ================================================================ ⑧ 端到端

        /// <summary>
        /// 端到端：<c>[9 MiB 假 MP4 头][完整 ZIP][15 KB 尾巴]</c> 走完真管线
        /// （真 MainViewModel + 真各 Coordinator + 真 7z），最后解出 ZIP 里的内容。
        ///
        /// <para>前置数据 9 MiB 是**故意的**：它超过 7-Zip 的容忍上限（实测 8 MiB），
        /// 所以"原文件能打开"这条捷径不存在 —— 能解出来就说明管线真的按偏移抠了、而且按终点截断了。</para>
        ///
        /// <para>并断言结论**不再是旧结论**：任务状态与日志里都不许出现"格式未知"。</para>
        /// </summary>
        [Fact]
        public async Task 端到端_MP4头加尾部ZIP走完管线能解出内容且不报格式未知()
        {
            string zip = BuildZip((PayloadEntryName, PayloadText));
            string source = BuildPolyglot(zip, BeyondSevenZipTolerancePrefixLength, TailAfterEocdLength, "e2e");
            long sourceLength = new FileInfo(source).Length;

            // 前置数据超过 8 MiB 时 7z 必然拒绝原文件 —— 这是"必须抠出来"的硬证据。
            SevenZipEngine engine = CreateEngine();
            ArchiveListResult onOriginal = await engine.ListAsync(ArchiveRequest.For(source), CancellationToken.None);
            Assert.False(onOriginal.Success, "前置数据超过 8 MiB 时 7z 必须拒绝原文件");

            Harness harness = CreateHarness();
            await harness.AddPathsAsync(source);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);
            Assert.True(task.EmbeddedArchiveOffset > 0, "应该识别出尾部有内嵌归档");
            Assert.Equal(sourceLength - TailAfterEocdLength, task.EmbeddedArchiveEnd);

            await harness.RunOneClickAsync();

            // 内容物解出来了：ZIP 里那份 payload.txt 就在输出目录里，内容逐字节一致。
            string[] payloads = Directory.GetFiles(harness.OutputRoot, PayloadEntryName, SearchOption.AllDirectories);
            Assert.True(payloads.Length == 1, $"应该只解出一份 {PayloadEntryName}，实际 {payloads.Length} 份（{harness.OutputRoot}）");
            Assert.Equal(PayloadText, File.ReadAllText(payloads[0]));

            // 结论不是旧结论：任务不是"格式未知"，日志里也没有这一条。
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(StatusText.UnknownFormat, task.Status);
            Assert.DoesNotContain(
                harness.Log.Logs,
                x => x.Message.Contains(StatusText.UnknownFormat, StringComparison.Ordinal));
        }

        // ================================================================ 样本构造

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

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private SevenZipEngine CreateEngine()
        {
            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, $"测试需要内置 7z：{_sevenZip}");
            return engine;
        }

        /// <summary>假 MP4 头：前 12 字节是真格式的 <c>ftyp</c> box，后面填随机字节（固定种子，样本可复现）。</summary>
        private static byte[] BuildFakeVideoPrefix(int length = FakeVideoPrefixLength)
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
        /// EOCD 之后那 15 KB 正常数据。刻意用"逐字节递增"的花纹：它**永远不会**碰巧出现
        /// <c>PK\x05\x06</c>（要四个连续字节正好是 50 4B 05 06，而这里相邻字节只差 1），
        /// 于是测试验的是"EOCD 后面有数据"，不是"后面又有一个假 EOCD"。
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

        /// <summary>PE 头（自解压安装器形态）+ 尾部数据；<paramref name="orphanEocd"/> 会再挂一个"孤儿 EOCD"。</summary>
        private static byte[] BuildPeWithTrailingData(int length, bool orphanEocd)
        {
            byte[] bytes = new byte[length];

            new Random(20260925).NextBytes(bytes);

            bytes[0] = 0x4D; // MZ
            bytes[1] = 0x5A;

            // e_lfanew = 0x40，PE 签名在 0x40。
            BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
            Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, 0x40);

            if (!orphanEocd)
            {
                return bytes;
            }

            /*
             * 孤儿 EOCD：签名在、字段也在，但指向的中央目录根本不存在（自相矛盾）。
             * 位置刻意放在尾部数据里 —— 正是"从后往前逐个试候选"会碰到的那种假候选。
             */
            byte[] result = new byte[bytes.Length + 22];
            Array.Copy(bytes, result, bytes.Length);

            Encoding.ASCII.GetBytes("PK").CopyTo(result, bytes.Length);
            result[bytes.Length + 2] = 0x05;
            result[bytes.Length + 3] = 0x06;
            BitConverter.GetBytes((ushort)1).CopyTo(result, bytes.Length + 10);        // 条目数
            BitConverter.GetBytes((uint)1024).CopyTo(result, bytes.Length + 12);       // 中央目录大小
            BitConverter.GetBytes((uint)4096).CopyTo(result, bytes.Length + 16);       // 中央目录偏移

            return result;
        }

        /// <summary>造一个普通 ZIP（条目名可控，压缩方式默认），返回它的路径。</summary>
        private string BuildZip(params (string Name, string Text)[] entries)
        {
            string path = Path.Combine(_root, "tail_" + Guid.NewGuid().ToString("N") + ".zip");

            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);

            foreach ((string name, string text) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

                using Stream stream = entry.Open();
                byte[] payload = Encoding.UTF8.GetBytes(text);
                stream.Write(payload, 0, payload.Length);
            }

            return path;
        }

        /// <summary>
        /// 把普通 ZIP 改造成 ZIP64 形态：中央目录之后插入 56 字节 ZIP64 EOCD + 20 字节 locator，
        /// 普通 EOCD 的三个字段改成占位符（<c>0xFFFF</c> / <c>0xFFFFFFFF</c>）—— 真值只在 ZIP64 记录里。
        ///
        /// 中央目录本身不动，所以它的偏移/大小仍然有效；插入点在中央目录之后，位置关系与真实 ZIP64 一致。
        /// </summary>
        private static byte[] BuildZip64Bytes(string plainZipPath)
        {
            byte[] zip = File.ReadAllBytes(plainZipPath);
            int eocdIndex = FindLastEndOfCentralDirectory(zip);

            Assert.True(eocdIndex > 0, "样本里应该能找到 EOCD");
            Assert.Equal(0, BitConverter.ToUInt16(zip, eocdIndex + 20));

            long cdSize = BitConverter.ToUInt32(zip, eocdIndex + 12);
            long cdOffset = BitConverter.ToUInt32(zip, eocdIndex + 16);
            long entryCount = BitConverter.ToUInt16(zip, eocdIndex + 10);

            byte[] result = new byte[zip.Length + Zip64FooterLength];

            // [0, eocdIndex)：原来的中央目录（EOCD 之前的一切）。
            Array.Copy(zip, 0, result, 0, eocdIndex);

            // ZIP64 EOCD 记录：签名 4 + 记录大小 8 + 版本 2+2 + 盘号 4+4 + 条目数 8+8 + CD 大小 8 + CD 偏移 8。
            int record = eocdIndex;

            Encoding.ASCII.GetBytes("PK").CopyTo(result, record);
            result[record + 2] = 0x06;
            result[record + 3] = 0x06;
            BitConverter.GetBytes((ulong)44).CopyTo(result, record + 4);
            BitConverter.GetBytes((ushort)45).CopyTo(result, record + 12);
            BitConverter.GetBytes((ushort)45).CopyTo(result, record + 14);
            BitConverter.GetBytes(0u).CopyTo(result, record + 16);
            BitConverter.GetBytes(0u).CopyTo(result, record + 20);
            BitConverter.GetBytes((ulong)entryCount).CopyTo(result, record + 24);
            BitConverter.GetBytes((ulong)entryCount).CopyTo(result, record + 32);
            BitConverter.GetBytes((ulong)cdSize).CopyTo(result, record + 40);
            BitConverter.GetBytes((ulong)cdOffset).CopyTo(result, record + 48);

            // locator：签名 4 + ZIP64 EOCD 所在盘号 4 + ZIP64 EOCD 偏移 8 + 总盘数 4。
            int locator = record + 56;

            Encoding.ASCII.GetBytes("PK").CopyTo(result, locator);
            result[locator + 2] = 0x06;
            result[locator + 3] = 0x07;
            BitConverter.GetBytes(0u).CopyTo(result, locator + 4);
            BitConverter.GetBytes((ulong)record).CopyTo(result, locator + 8);
            BitConverter.GetBytes(1u).CopyTo(result, locator + 16);

            // 普通 EOCD：位置不变（只是被那 76 字节顶到后面），字段改成占位符。
            int newEocd = eocdIndex + Zip64FooterLength;
            Array.Copy(zip, eocdIndex, result, newEocd, 22);

            BitConverter.GetBytes(ushort.MaxValue).CopyTo(result, newEocd + 10);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(result, newEocd + 12);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(result, newEocd + 16);

            return result;
        }

        private string BuildPolyglot(string plainZipPath, int prefixLength, int tailAfterEocdLength, string tag) =>
            BuildPolyglotBytes(File.ReadAllBytes(plainZipPath), prefixLength, tailAfterEocdLength, tag);

        /// <summary>拼出"双面文件"：[假 MP4 头][ZIP 字节][EOCD 之后的尾巴]。</summary>
        private string BuildPolyglotBytes(byte[] zipBytes, int prefixLength, int tailAfterEocdLength, string tag)
        {
            string path = Path.Combine(_root, tag + ".mp4");

            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            output.Write(BuildFakeVideoPrefix(prefixLength));
            output.Write(zipBytes);

            if (tailAfterEocdLength > 0)
            {
                output.Write(BuildTailAfterEocd(tailAfterEocdLength));
            }

            return path;
        }

        private static int FindLastEndOfCentralDirectory(byte[] bytes)
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

        // ================================================================ 端到端装配

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎。
        /// 缓存根 / 输出目录 / 密码本全部落在临时目录里，绝不碰用户的目录（AGENTS.md §8）。
        ///
        /// 与 <c>InnerLayerContinuationTests</c> 同一套装配方式：<see cref="MainViewModel"/> 的构造会写两个
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
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.PasswordBookPath = bookPath;

            // 7z 路径留空 = 用 ToolLocator 解析出的内置路径（测试输出目录里也有一份 tools\7zip）。
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
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, outputRoot, logService);
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(MainViewModel vm, OneClickCoordinator oneClick, string outputRoot, LogService log)
            {
                Vm = vm;
                _oneClick = oneClick;
                OutputRoot = outputRoot;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public string OutputRoot { get; }

            /// <summary>
            /// 真 LogService：屏幕日志集合（<c>Logs</c>）在无 WPF 应用的测试进程里照样会被填充，
            /// 所以"有没有写这条日志"可以直接断言。
            /// </summary>
            public LogService Log { get; }

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>跑一键处理的**流程部分**（不弹任何对话框 —— 测试里没人在那儿点确定）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }
    }
}
