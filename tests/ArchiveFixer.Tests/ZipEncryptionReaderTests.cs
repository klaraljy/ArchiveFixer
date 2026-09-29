using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// ZIP **加密标志**的判读（用户 2026-09-30）。
    ///
    /// <para><b>为什么要有这一组</b>：旧实现（<c>ArchiveDetectService.IsZipProbablyEncrypted</c>）
    /// 只读**第一个本地头** offset 6-7 的通用位标志 bit0 —— 而"第一个条目不是加密的"根本不等于"整包没加密"。
    /// 真样本 <c>dirfirst.zip</c>：第一个本地头是目录条目 <c>sub/</c>（flags = <c>0x0000</c>），
    /// 第二个条目 <c>sub/inner.txt</c> 才是加密的（中央目录两条记录 flags = <c>0x0000 0x0001</c>）
    /// ⇒ 旧实现**漏报**。本组第一条就是这个缺陷的红检。</para>
    ///
    /// <para><b>样本全部是现场造的真包</b>（项目内置 <c>7z.exe</c>，⛔ 样本不入库、⛔ 密码用占位符）：
    /// 判据是"中央目录记录 +8 的 flags 的 bit0"这类**字段偏移**，只有真产品写出来的字节能回答。
    /// 拿不到内置 7z 时**跳过并说明原因**（⛔ 不假装跑过）。</para>
    ///
    /// <para><b>红检</b>：把 <see cref="ZipEncryptionReader"/> 里"扫中央目录"那一段换成"直接走兜底"，
    /// <see cref="ZIP_第一个条目是目录_第二个条目加密_必须报加密"/> 与
    /// <see cref="ZIP_混合_有加密条目就必须报加密"/> **立刻变红**（都判成"不知道"）；
    /// 恢复后全绿。现场记录见 <c>docs/真机事故复盘.md</c>。</para>
    /// </summary>
    public class ZipEncryptionReaderTests : IDisposable
    {
        private readonly string _root;
        private readonly ZipEncryptionSampleSet? _samples;

        public ZipEncryptionReaderTests(ITestOutputHelper output)
        {
            _root = Path.Combine(Path.GetTempPath(), "af_zip_enc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _samples = ZipEncryptionSampleSet.TryCreate(_root, output.WriteLine);

            if (_samples == null)
            {
                output.WriteLine("跳过原因：测试机上没有可用的内置 7z.exe，造不出真 ZIP 加密样本。");
            }
            else
            {
                output.WriteLine("样本来源：" + _samples.Source);
            }
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
                // 临时目录删不掉不影响任何结论。
            }
        }

        /// <summary>
        /// **本次缺陷的红检用例**：第一个本地头是目录（flags = 0x0000）、第二个条目才加密。
        /// 只读第一个本地头的实现必然把它判成"没加密"；必须扫中央目录才看得出来。
        /// </summary>
        [Fact]
        public void ZIP_第一个条目是目录_第二个条目加密_必须报加密()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = ZipEncryptionReader.Read(_samples.DirectoryFirstEncrypted);

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, reading.State);
            Assert.True(reading.IsEncrypted, "第一个条目是目录不等于整包没加密 —— 中央目录里第二条是加密的");
        }

        /// <summary>混合包：不加密的 <c>a.txt</c> + 加密的 <c>sub/inner.txt</c> ⇒ 必须报加密。</summary>
        [Fact]
        public void ZIP_混合_有加密条目就必须报加密()
        {
            if (_samples == null)
            {
                return;
            }

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, ZipEncryptionReader.Read(_samples.Mixed).State);
        }

        [Fact]
        public void ZIP_ZipCrypto_报数据加密()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = ZipEncryptionReader.Read(_samples.ZipCrypto);

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, reading.State);
            Assert.True(reading.IsEncrypted);
        }

        [Fact]
        public void ZIP_AES256_报数据加密()
        {
            if (_samples == null)
            {
                return;
            }

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, ZipEncryptionReader.Read(_samples.Aes).State);
        }

        [Fact]
        public void ZIP_不加密的对照组_不许误报()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = ZipEncryptionReader.Read(_samples.Plain);

            Assert.Equal(ArchiveEncryptionState.NotEncrypted, reading.State);
            Assert.False(reading.IsEncrypted);
        }

        /// <summary>
        /// 截断 / 不是 ZIP / 文件不存在：一律"不知道"，⛔ 绝不当成"没加密"以外的任何结论，更不许猜成"加密"。
        ///
        /// <para>⚠ 这里刻意**不**拿"截掉尾部的加密包"当例子：那种文件的第一个本地头照样读得到
        /// （flags = <c>0x0001</c>），兜底档如实报"加密"是**对的**（有正证据）。
        /// 这一条要钉的是"读不出来 ⇒ 不知道"。</para>
        /// </summary>
        [Fact]
        public void ZIP_截断与坏样本_返回不知道()
        {
            if (_samples == null)
            {
                return;
            }

            // ①只剩 4 个字节：连一个本地头都读不全（兜底档需要 8 字节）
            string truncated = Path.Combine(_root, "truncated.zip");
            byte[] whole = File.ReadAllBytes(_samples.ZipCrypto);
            File.WriteAllBytes(truncated, whole[..4]);
            Assert.Equal(ArchiveEncryptionState.Unknown, ZipEncryptionReader.Read(truncated).State);

            // ②EOCD 被砍掉、而第一个本地头是"没加密"的 ⇒ 也说不出结论（兜底档绝不说"没加密"）
            string headOnly = Path.Combine(_root, "head-only.zip");
            File.WriteAllBytes(headOnly, File.ReadAllBytes(_samples.Plain)[..30]);
            Assert.Equal(ArchiveEncryptionState.Unknown, ZipEncryptionReader.Read(headOnly).State);

            // ③根本不是 ZIP
            string notZip = Path.Combine(_root, "not-zip.txt");
            File.WriteAllText(notZip, "这不是压缩包。");
            Assert.Equal(ArchiveEncryptionState.Unknown, ZipEncryptionReader.Read(notZip).State);

            // ④文件不存在
            Assert.Equal(
                ArchiveEncryptionState.Unknown,
                ZipEncryptionReader.Read(Path.Combine(_root, "根本没有这个文件.zip")).State);
        }

        /// <summary>
        /// 中央目录定位不了（EOCD 被改坏）时走**兜底**那一档：读第一个本地头的 flags。
        ///
        /// <para>这一档**只可能给出"加密"或"不知道"**：一个本地头说不出别的条目加没加密。
        /// 所以 <c>dirfirst.zip</c>（第一个本地头 flags = 0）在兜底档里必须是 <b>Unknown</b>，
        /// ⛔ **不是 NotEncrypted** —— 放宽成"没加密"就是把这个缺陷换个地方再挖一遍。</para>
        /// </summary>
        [Fact]
        public void ZIP_中央目录读不出时_走兜底档且绝不说没加密()
        {
            if (_samples == null)
            {
                return;
            }

            // 把 EOCD 签名改坏（PK\x05\x06 → PK\x05\x07）：中央目录定位不了，只剩第一个本地头可看。
            string brokenDirFirst = Path.Combine(_root, "broken-eocd-dirfirst.zip");
            File.WriteAllBytes(brokenDirFirst, CorruptEndOfCentralDirectory(_samples.DirectoryFirstEncrypted));

            ArchiveEncryptionReading dirFirstReading = ZipEncryptionReader.Read(brokenDirFirst);

            Assert.Equal(ArchiveEncryptionState.Unknown, dirFirstReading.State);
            Assert.False(dirFirstReading.IsEncrypted);

            // 而"第一个本地头就加密"的包在兜底档里照样报得出来（这是兜底档唯一的价值）。
            string brokenZipCrypto = Path.Combine(_root, "broken-eocd-zc.zip");
            File.WriteAllBytes(brokenZipCrypto, CorruptEndOfCentralDirectory(_samples.ZipCrypto));

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, ZipEncryptionReader.Read(brokenZipCrypto).State);
        }

        /// <summary>
        /// **跨盘 zip 第 1 卷**：文件以 <c>PK\x07\x08</c>（跨盘标记）开头，本地头紧跟在 <b>+4</b> 处 ——
        /// 标记后面**没有**盘号字段。
        ///
        /// <para>判据出处是**真样本**：真 PKZIP 跨盘 zip 的第 1 片前 8 个字节就是
        /// <c>50 4b 07 08 50 4b 03 04</c>（跨盘标记 + 本地头签名）。
        /// 旧实现在这个形态下读的是 offset 6-7 —— 那两字节落在**本地头签名的后半**里，
        /// 与"第一个条目加没加密"毫无关系。</para>
        ///
        /// <para>⚠ 本用例用的是**构造的最小形态**（12 + 30 字节），不是真样本：
        /// 那一片真样本不在仓库里（AGENTS.md §8），而 7-Zip 造不出跨盘 zip。
        /// 真切片的形状由 <c>RealVolumeSampleTests</c> 那一组（环境变量门控）覆盖。</para>
        /// </summary>
        [Fact]
        public void ZIP_跨盘第1卷_本地头在加4处()
        {
            string spanned = Path.Combine(_root, "spanned-first-volume.zip");

            var bytes = new byte[12 + 30];
            bytes[0] = 0x50;
            bytes[1] = 0x4B;
            bytes[2] = 0x07;
            bytes[3] = 0x08;

            // +4 处就是本地头：签名 + 版本(2) + flags(2)。
            bytes[4] = 0x50;
            bytes[5] = 0x4B;
            bytes[6] = 0x03;
            bytes[7] = 0x04;

            File.WriteAllBytes(spanned, bytes);

            ArchiveEncryptionReading plain = ZipEncryptionReader.Read(spanned);

            Assert.Equal(ArchiveEncryptionState.Unknown, plain.State);
            Assert.False(plain.IsEncrypted);

            // +4 处的 flags = 0x0001 ⇒ 必须报加密（走的正是"本地头在 +4"这一条）。
            var encrypted = (byte[])bytes.Clone();
            encrypted[10] = 0x01;
            File.WriteAllBytes(spanned, encrypted);

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, ZipEncryptionReader.Read(spanned).State);
        }

        /// <summary>
        /// **读入量**：中央目录是分块扫的，读进内存的字节数不许随包体大小增长
        /// （EOCD 检索窗口 ≤ 64 KiB + EOCD 本身，再加中央目录那几十字节）。
        /// </summary>
        [Fact]
        public void ZIP_读入量_不随包体增长()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading small = ZipEncryptionReader.Read(_samples.Plain);

            Assert.True(
                small.BytesRead <= ZipEncryptionReader.EocdSearchWindow + 4096,
                $"读入量 {small.BytesRead} 字节应当只有 EOCD 窗口那么多");
        }

        /// <summary>
        /// **端到端**：<see cref="ArchiveDetectService.DetectAsync"/> 必须把标志落到
        /// <see cref="DetectResult.IsProbablyEncrypted"/>，并在消息里带上注脚。
        /// </summary>
        [Fact]
        public async Task ZIP_端到端_识别把标志与注脚落下来()
        {
            if (_samples == null)
            {
                return;
            }

            var service = new ArchiveDetectService();

            DetectResult dirFirst = await service.DetectAsync(_samples.DirectoryFirstEncrypted);
            DetectResult aes = await service.DetectAsync(_samples.Aes);
            DetectResult zc = await service.DetectAsync(_samples.ZipCrypto);

            Assert.True(dirFirst.IsProbablyEncrypted, "第一个条目是目录 + 第二个加密 ⇒ 必须报加密");
            Assert.True(aes.IsProbablyEncrypted, "AES-256 必须被认出来");
            Assert.True(zc.IsProbablyEncrypted, "ZipCrypto 必须被认出来");

            Assert.Contains(StatusText.DetectZipEncryptedNote, dirFirst.Message, StringComparison.Ordinal);

            // 格式结论一个字都不许变（识别仍然"不信后缀"那一套）。
            Assert.Equal("ZIP", dirFirst.Format);
            Assert.Equal("ZIP", aes.Format);
            Assert.Equal("ZIP", zc.Format);

            DetectResult plain = await service.DetectAsync(_samples.Plain);
            DetectResult mixed = await service.DetectAsync(_samples.Mixed);

            Assert.False(plain.IsProbablyEncrypted, "没加密的 ZIP 不许误报");
            Assert.True(mixed.IsProbablyEncrypted, "混合包里有一条加密的就必须报");
        }

        /// <summary>
        /// **端到端且走缓存**：同一份内容的第二个拷贝命中识别缓存之后，
        /// 加密标志必须照旧现算出来（缓存里不含加密标志）。
        /// </summary>
        [Fact]
        public async Task ZIP_端到端_缓存命中后加密标志照旧现算()
        {
            if (_samples == null)
            {
                return;
            }

            string copy = Path.Combine(_root, "copy-of-dirfirst.zip");
            File.Copy(_samples.DirectoryFirstEncrypted, copy, overwrite: true);

            var service = new ArchiveDetectService();

            DetectResult first = await service.DetectAsync(_samples.DirectoryFirstEncrypted);
            DetectResult second = await service.DetectAsync(copy);

            Assert.True(first.IsProbablyEncrypted);
            Assert.True(second.IsProbablyEncrypted, "缓存命中那一档也必须带上加密结论");
            Assert.Contains(StatusText.DetectZipEncryptedNote, second.Message, StringComparison.Ordinal);
        }

        /// <summary>把最后那个 EOCD 的签名改坏（<c>PK\x05\x06</c> → <c>PK\x05\x07</c>）。</summary>
        private static byte[] CorruptEndOfCentralDirectory(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            for (int index = bytes.Length - 22; index >= 0; index--)
            {
                if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 0x05 && bytes[index + 3] == 0x06)
                {
                    bytes[index + 3] = 0x07;

                    break;
                }
            }

            return bytes;
        }
    }
}
