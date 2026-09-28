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
        [InlineData("amb909.7.01", VolumeContentFormat.SevenZip, "amb909.7z")]
        // 认不出来的末段：把后缀接在后面，绝不硬猜
        [InlineData("mystery.bin", VolumeContentFormat.SevenZip, "mystery.bin.7z")]
        [InlineData("pack", VolumeContentFormat.SevenZip, "pack.7z")]
        // 已经对了就别乱动
        [InlineData("good.7z.001", VolumeContentFormat.SevenZip, "good.7z")]
        [InlineData("movie.rar.001", VolumeContentFormat.Rar, "movie.rar")]
        public void 基名_保守地往内容后缀上靠(string fileName, VolumeContentFormat format, string expected)
        {
            Assert.True(VolumeContentInference.TryDeriveBaseName(
                Path.Combine(_root, fileName), format, out string baseName));

            Assert.Equal(expected, baseName);
        }

        [Fact]
        public void 标准名_与数字族口径一致_三位补零()
        {
            Assert.Equal(
                new[] { "amb909.7z.001", "amb909.7z.002", "amb909.7z.003" },
                VolumeContentInference.BuildStandardFileNames("amb909.7z", 3));

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

        private IEnumerable<VolumeCandidate> Enumerate() =>
            Directory.GetFiles(_root, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new VolumeCandidate { Path = path, Size = new FileInfo(path).Length })
                .ToList();

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
