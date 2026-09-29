using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 内容级分卷推断的**纯逻辑**部分（用户 2026-09-28 三层方案的第 1、2 层）：
    /// 内容认第一卷、同目录尺寸排候选与假设顺序、由第一卷推改名后的基名。
    ///
    /// <para>⛔ 这些用例**不碰引擎、不建目录、不改名字** —— 它们钉的是"顺序怎么排"，
    /// "顺序对不对"由真 7z 试开用例钉（<c>DisguisedVolumeContentProbeTests</c>）。</para>
    /// </summary>
    public class VolumeContentInferenceTests : IDisposable
    {
        private readonly string _root;

        public VolumeContentInferenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeInference", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点，不影响结论。
            }
        }

        // ── 内容识别 ──

        [Fact]
        public void 文件头认得出七z_rar_zip_三种格式()
        {
            string sevenZip = Write("a.7z", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });
            string rar = Write("b.rar", new byte[] { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00 });
            string zip = Write("c.zip", new byte[] { (byte)'P', (byte)'K', 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 });

            Assert.Equal(VolumeContentFormat.SevenZip, VolumeContentInference.SniffFormat(sevenZip));
            Assert.Equal(VolumeContentFormat.Rar, VolumeContentInference.SniffFormat(rar));
            Assert.Equal(VolumeContentFormat.Zip, VolumeContentInference.SniffFormat(zip));
        }

        [Fact]
        public void 后续卷是裸字节流_内容上认不出是七z()
        {
            /*
             * 这条是**物理事实**的守门用例：7z 的 -v 分卷里没有卷号标记，第二卷开头就是数据。
             * 一旦哪天有人写出"从内容读出这是第几卷"的代码，这条会提醒他 7z 上没有这个信息。
             */
            string payload = Write("v2.7z.002", new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });

            Assert.Equal(VolumeContentFormat.Unknown, VolumeContentInference.SniffFormat(payload));
        }

        [Fact]
        public void 读不出来的文件一律当认不出_不抛异常()
        {
            Assert.Equal(VolumeContentFormat.Unknown, VolumeContentInference.SniffFormat(Path.Combine(_root, "不存在.bin")));
            Assert.Equal(VolumeContentFormat.Unknown, VolumeContentInference.SniffFormat(null));
            Assert.Equal(VolumeContentFormat.Unknown, VolumeContentInference.SniffFormat(string.Empty));
        }

        // ── 候选与尺寸规律 ──

        [Fact]
        public void 候选_只留不大于第一卷的_按大小降序_同大小按名字()
        {
            string first = WriteBig("first.01", 100);
            WriteBig("big.02", 200);      // 比第一卷大 → 不可能是同组后续卷
            WriteBig("same-b.03", 100);
            WriteBig("same-a.04", 100);
            WriteBig("small.05", 40);

            IReadOnlyList<VolumeCandidate> candidates =
                VolumeContentInference.BuildCandidates(first, Enumerate());

            Assert.Equal(
                new[] { "same-a.04", "same-b.03", "small.05" },
                candidates.Select(c => Path.GetFileName(c.Path)));
        }

        [Fact]
        public void 尺寸规律_要有与第一卷等长的文件才算有证据()
        {
            string first = WriteBig("a.01", 100);
            WriteBig("b.02", 60);

            // 只有更短的文件 → 没有"等长"这条证据，不敢试
            Assert.False(VolumeContentInference.HasVolumeSizePattern(
                first, VolumeContentInference.BuildCandidates(first, Enumerate())));

            WriteBig("c.03", 100);
            Assert.True(VolumeContentInference.HasVolumeSizePattern(
                first, VolumeContentInference.BuildCandidates(first, Enumerate())));
        }

        // ── 用户 2026-09-29：两卷形状 + 名字里的短数字尾巴 ──

        /// <summary>
        /// 短数字尾巴只认"最后一段是 1~3 位纯数字"：<c>.01</c> → 1、<c>.2</c> → 2、
        /// <c>.003</c> → 3；4 位以上（<c>0012</c>）与点段（<c>txt</c>）一律不认 ——
        /// 与 <c>ExtensionHelper</c> 的老口径一致（纯数字尾巴更像另一套位宽，不猜）。
        /// </summary>
        [Theory]
        [InlineData(@"C:\t\amb909.7.01", 1)]
        [InlineData(@"C:\t\amb909.z.2", 2)]
        [InlineData(@"C:\t\amb909..3", 3)]
        [InlineData(@"C:\t\amb909.7z.003", 3)]
        [InlineData(@"C:\t\amb909.12", 12)]
        [InlineData(@"C:\t\x.0012", null)]      // 4 位 → 不猜
        [InlineData(@"C:\t\x.movie.2024", null)]
        [InlineData(@"C:\t\readme.txt", null)]
        [InlineData(@"C:\t\noextension", null)]
        [InlineData(@"C:\t\x.0", null)]         // 卷号从 1 起
        public void 短数字尾巴_只认一到三位的纯数字段(string path, int? expected)
        {
            Assert.Equal(expected, VolumeContentInference.TryReadShortNumberTail(path));
        }

        /// <summary>
        /// **两卷形状这张门票**（用户 2026-09-29 现场：<c>amb909.7.01</c> 正好 2 GiB +
        /// <c>amb909.z.2</c> 1.89 GB）：一个等长的候选都没有 —— 尺寸规律永远给不出证据，
        /// 但它是真实存在的切法，必须放行去试开一次。
        /// </summary>
        [Fact]
        public void 两卷形状_名字尾巴接得上或第一卷整MiB就放行()
        {
            /*
             * ⚠ 每个形状放自己的子目录：候选是按"整个目录"算的，挤在一个目录里会互相干扰
             * （例如旁边那个 1 MiB 的文件会冒充"等长满片"）。
             */

            // ① 名字接得上：`two.01`(1) + `two.2`(2) —— 用户原话"可以靠后缀数字 2 的情况猜一猜是第二卷"
            string byName = NewDirectory("by-name");
            string first = WriteIn(byName, "two.01", 1024 * 1024);
            WriteIn(byName, "two.2", 700_000);

            IReadOnlyList<VolumeCandidate> named = VolumeContentInference
                .BuildCandidates(first, EnumerateAt(byName));

            Assert.False(VolumeContentInference.HasVolumeSizePattern(first, named));   // 没有一个等长的
            Assert.True(VolumeContentInference.HasTwoVolumeShapeEvidence(first, named));

            // ② 名字里连数字都没有 → 靠体积规律（7z 的切分上限永远是整数 MiB，所以第一卷像个满片）
            string plainDirectory = NewDirectory("plain");
            string plain = WriteIn(plainDirectory, "plain.aa", 1024 * 1024);
            WriteIn(plainDirectory, "plain.bb", 700_000);

            Assert.True(VolumeContentInference.HasTwoVolumeShapeEvidence(
                plain, VolumeContentInference.BuildCandidates(plain, EnumerateAt(plainDirectory))));

            // ③ 反例：第一卷不是整数 MiB、名字也对不上 → 连试都不试（宁可什么都不做）
            string oddDirectory = NewDirectory("odd");
            string odd = WriteIn(oddDirectory, "odd.01", 1_048_577);
            WriteIn(oddDirectory, "odd.zz", 700_000);

            Assert.False(VolumeContentInference.HasTwoVolumeShapeEvidence(
                odd, VolumeContentInference.BuildCandidates(odd, EnumerateAt(oddDirectory))));

            // ④ 反例：一个更短的候选都没有（缺的卷真不在这个目录里）→ 不放行
            string lonelyDirectory = NewDirectory("lonely");
            string lonely = WriteIn(lonelyDirectory, "lonely.aa", 1024 * 1024);

            Assert.False(VolumeContentInference.HasTwoVolumeShapeEvidence(
                lonely, VolumeContentInference.BuildCandidates(lonely, EnumerateAt(lonelyDirectory))));
        }

        /// <summary>
        /// 候选里**不收"认得出的归档"**（用户 2026-09-29 原话："候选从同目录里、不是已识别的归档……
        /// 的文件里取"）：7z 的后续卷是裸字节流，所以一个有归档魔数的文件必然是**另一个独立的包**，
        /// 留在候选里只会拿它去白试一次。认不出格式的那些恰恰是真正的续卷。
        /// </summary>
        [Fact]
        public void 候选_认得出的归档不算续卷_认不出格式的才算()
        {
            string first = WriteBig("c1.01", 4096);

            string rawContinuation = WriteBig("c1.2", 2048);

            // 另一个独立的 zip / RAR：有魔数 → 不许当续卷
            Write("other.zip", new byte[] { (byte)'P', (byte)'K', 0x03, 0x04, 0x14, 0x00, 0x00, 0x00 });
            Write("other.rar", new byte[] { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00 });

            IReadOnlyList<string> names = VolumeContentInference
                .BuildCandidates(first, Enumerate())
                .Select(c => Path.GetFileName(c.Path))
                .ToList();

            Assert.Equal(new[] { "c1.2" }, names);
            Assert.Contains(Path.GetFileName(rawContinuation), names);
        }

        [Fact]
        public void 假设顺序_等长的那批按名字排列_更短的那个必然排最后()
        {
            string first = WriteBig("x.01", 100);
            WriteBig("x.03", 100);
            WriteBig("x.02", 100);
            WriteBig("x.04", 30);
            WriteBig("other.bin", 5); // 无关小文件：不参与假设顺序

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings = VolumeContentInference.BuildOrderings(
                first, VolumeContentInference.BuildCandidates(first, Enumerate()));

            Assert.NotEmpty(orderings);

            IReadOnlyList<string> head = orderings[0].Select(c => Path.GetFileName(c.Path)).ToList();

            Assert.Equal(new[] { "x.02", "x.03", "x.04" }, head);
            Assert.DoesNotContain("other.bin", head);
        }

        /// <summary>
        /// **短数字尾巴优先**（用户 2026-09-29："数字对得上就直接按号排"）：满卷之间大小完全一样，
        /// 名字是唯一的线索 —— 而按号排往往与按名字排**不一致**（`x.10` 按名字排在 `x.2` 前面），
        /// 所以第一个尝试的顺序必须按号来（试开一次就成立，不必再烧后面那些排列）。
        /// </summary>
        [Fact]
        public void 假设顺序_短数字尾巴接得上时先按号排()
        {
            string first = WriteBig("y.01", 100);
            WriteBig("y.10", 100);
            WriteBig("y.2", 100);
            WriteBig("y.3", 40);

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings = VolumeContentInference.BuildOrderings(
                first, VolumeContentInference.BuildCandidates(first, Enumerate()));

            Assert.NotEmpty(orderings);

            // 按号：2 → 10（末卷必然最后）
            Assert.Equal(
                new[] { "y.2", "y.10", "y.3" },
                orderings[0].Select(c => Path.GetFileName(c.Path)));

            // 老口径（按名字升序打头）照旧在后面兜底，一个字没丢。
            Assert.Contains(
                orderings,
                ordering => ordering.Select(c => Path.GetFileName(c.Path)).SequenceEqual(new[] { "y.10", "y.2", "y.3" }));
        }

        /// <summary>
        /// 一个等长的候选都没有时也给出顺序（两卷形状）—— 老口径在这里返回空，
        /// 于是"第一卷满片 + 末卷是余量"这一组连试都不试（用户 2026-09-29 的现场）。
        /// </summary>
        [Fact]
        public void 假设顺序_没有等长卷时按两卷形状给出唯一顺序()
        {
            string first = WriteBig("two.01", 100);
            WriteBig("two.2", 40);

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings = VolumeContentInference.BuildOrderings(
                first, VolumeContentInference.BuildCandidates(first, Enumerate()));

            Assert.Single(orderings);
            Assert.Equal(new[] { "two.2" }, orderings[0].Select(c => Path.GetFileName(c.Path)));
        }

        [Fact]
        public void 假设顺序_有上限_不会因为候选多就试上千次()
        {
            string first = WriteBig("y.01", 100);

            for (int i = 0; i < 8; i++)
            {
                WriteBig($"y.{i + 2:D2}", 100);
            }

            IReadOnlyList<IReadOnlyList<VolumeCandidate>> orderings = VolumeContentInference.BuildOrderings(
                first, VolumeContentInference.BuildCandidates(first, Enumerate()));

            Assert.True(orderings.Count <= VolumeContentInference.MaxOrderings, $"排列数 {orderings.Count} 超上限");
        }

        // ── 改名后的基名 ──

        [Theory]
        // 用户 2026-09-28 现场：后缀被吃掉一个字符、卷号被改烂 → 末段"差一个字符"补回来
        [InlineData("amb909.7.01", VolumeContentFormat.SevenZip, "amb909")]
        // 认不出来的末段：原样留着，绝不硬剪
        [InlineData("mystery.bin", VolumeContentFormat.SevenZip, "mystery.bin")]
        [InlineData("pack", VolumeContentFormat.SevenZip, "pack")]
        // 已经对了就别乱动
        [InlineData("good.7z.001", VolumeContentFormat.SevenZip, "good")]
        [InlineData("movie.rar.001", VolumeContentFormat.Rar, "movie")]
        public void 基名_保守地去掉卷号段与后缀段(string fileName, VolumeContentFormat format, string expected)
        {
            Assert.True(VolumeNumberFromContent.TryDeriveStem(
                Path.Combine(_root, fileName), format, out string stem));

            Assert.Equal(expected, stem);
        }

        [Fact]
        public void 标准名_与数字族口径一致_三位补零()
        {
            Assert.Equal(
                new[] { "amb909.7z.001", "amb909.7z.002", "amb909.7z.003" },
                VolumeNumberFromContent.BuildStandardNames("amb909", VolumeNamingFamily.SevenZipNumbered, 3));

            // 与 VolumeGroupDetector 认的"数字族"对得上：改完名字必须能被它认成分卷
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("amb909.7z.001"));
            Assert.Equal(3, VolumeGroupDetector.TryGetVolumeIndex("amb909.7z.003"));
        }

        [Fact]
        public void 试开目录_建在第一卷所在卷根下_同卷才做得了硬链接()
        {
            string first = WriteBig("probe.01", 10);
            string probeRoot = VolumeContentInference.BuildProbeRoot(first);

            Assert.NotEqual(string.Empty, probeRoot);
            Assert.Equal(Path.GetPathRoot(first), Path.GetPathRoot(probeRoot));
            Assert.Contains(VolumeContentInference.WorkDirectoryName, probeRoot, StringComparison.Ordinal);
            Assert.False(Directory.Exists(probeRoot), "只是算路径，不该真的建目录");
        }

        [Fact]
        public void 试开名_七z只认基名加三位卷号()
        {
            Assert.Equal("volprobe.7z.001", VolumeContentInference.ProbeFileName(1));
            Assert.Equal("volprobe.7z.012", VolumeContentInference.ProbeFileName(12));
        }

        private IEnumerable<VolumeCandidate> Enumerate() => EnumerateAt(_root);

        /// <summary>某个目录里的"路径 + 大小"（候选是按**一个目录**算的，所以用例要能指定目录）。</summary>
        private static IEnumerable<VolumeCandidate> EnumerateAt(string directory) =>
            Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new VolumeCandidate { Path = path, Size = new FileInfo(path).Length })
                .ToList();

        private string NewDirectory(string name)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);

            return directory;
        }

        private static string WriteIn(string directory, string name, int bytes)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[bytes]);

            return path;
        }

        private string Write(string name, byte[] content)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, content);

            return path;
        }

        private string WriteBig(string name, int bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[bytes]);

            return path;
        }
    }
}
