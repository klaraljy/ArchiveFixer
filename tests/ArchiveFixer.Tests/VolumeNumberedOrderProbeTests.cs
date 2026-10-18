using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **通用分片的顺序验证必须真的解一遍**：列目录只看末片的中央目录，顺序错了照样列得出来；
    /// 只有 <c>TestAsync</c>（逐条目 CRC）才能回答"中间片这个顺序对不对"。
    /// </summary>
    public sealed class VolumeNumberedOrderProbeTests : IDisposable
    {
        private readonly string _root;

        public VolumeNumberedOrderProbeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerNumberedOrderProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [SevenZipFact]
        public async Task 通用分片_正确顺序确认_中间片对调不成立()
        {
            (string first, string second, string tail) = CreateSplitZip();

            string probeRoot = Path.Combine(_root, "probe");
            Directory.CreateDirectory(probeRoot);

            var verifier = new VolumeProbeVerifier(new SevenZipEngine());

            VolumeProbeOutcome correct = await verifier.VerifyNumberedZipOrderAsync(
                new List<IReadOnlyList<string>> { new[] { first, second, tail } },
                probeRoot);

            Assert.True(correct.Confirmed, "正确顺序必须被确认：" + correct.Reason);

            VolumeProbeOutcome swapped = await verifier.VerifyNumberedZipOrderAsync(
                new List<IReadOnlyList<string>> { new[] { second, first, tail } },
                probeRoot);

            Assert.False(swapped.Confirmed, "中间片对调必须不被确认（否则顺序验证等于没做）");

            // ⛔ 试开只在试开目录里建链接：源片名字与字节都没变。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.True(File.Exists(tail));
            Assert.Empty(Directory.GetFiles(_root, "*.zip.001", SearchOption.AllDirectories));
        }

        /// <summary>造一个真实的 ZIP，再按字节切成"前两片等大 + 末片较小"的通用分片。</summary>
        private (string First, string Second, string Tail) CreateSplitZip()
        {
            string stage = Path.Combine(_root, "stage");
            Directory.CreateDirectory(stage);

            var random = new Random(20261018);

            for (int file = 0; file < 3; file++)
            {
                var bytes = new byte[1_000_000];
                random.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(stage, "payload-" + file + ".bin"), bytes);
            }

            string zipPath = Path.Combine(_root, "whole.zip");
            ZipFile.CreateFromDirectory(stage, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

            byte[] zip = File.ReadAllBytes(zipPath);
            int firstLength = zip.Length / 3;
            int secondLength = zip.Length / 3;

            string first = Path.Combine(_root, "piece-a.bin");
            string second = Path.Combine(_root, "piece-b.bin");
            string tail = Path.Combine(_root, "piece-c.bin");

            File.WriteAllBytes(first, zip[..firstLength]);
            File.WriteAllBytes(second, zip[firstLength..(firstLength + secondLength)]);
            File.WriteAllBytes(tail, zip[(firstLength + secondLength)..]);

            return (first, second, tail);
        }
    }
}
