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
    /// RAR **加密标志**的判读（用户 2026-09-29 任务 A）。
    ///
    /// <para><b>为什么要有这一组</b>：识别阶段过去只看开头那几个魔数字节，RAR 的块结构一个都不解析 ——
    /// <c>-p</c>（只加密数据、文件名可见）看不出加密，<c>-hp</c>（连文件名加密）更是什么都读不到。
    /// 后果写在 AGENTS.md §11：批首那句"本批有 N 个包没有可用密码"的预判对 RAR **整档漏报**。</para>
    ///
    /// <para><b>样本全部是现场造的真包</b>（本机 WinRAR 的 <c>Rar.exe</c>，⛔ 样本不入库、⛔ 密码用占位符）：
    /// 判据来自格式说明（RAR5 官方 technote / unrar 的 flags 常量，出处见
    /// <see cref="RarEncryptionReader"/> 的类注释），而"我对说明的理解对不对"只有真产品写出来的字节能回答。
    /// 本机没有 <c>Rar.exe</c> 时**跳过并说明原因**（⛔ 不假装跑过）。</para>
    ///
    /// <para><b>红检</b>：把 <see cref="ArchiveDetectService"/> 里那一行
    /// <c>headerResult = ApplyRarEncryptionVerdict(headerResult, filePath);</c> 摘掉 →
    /// 本组里所有"加密的认出来"的断言全红（<c>Assert.True(result.IsProbablyEncrypted)</c> 失败）；
    /// 只把 <c>RarEncryptionReader.Read</c> 改成恒返回 Unknown → 同样全红，而
    /// "没加密的不许误报"那几条照旧绿（它们本来就不该红 —— 这正是"宁可漏报不误报"的接线证明）。</para>
    /// </summary>
    public class RarEncryptionReaderTests : IDisposable
    {
        private readonly string _root;
        private readonly RarEncryptionSampleSet? _samples;

        public RarEncryptionReaderTests(ITestOutputHelper output)
        {
            _root = Path.Combine(Path.GetTempPath(), "af_rar_enc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _samples = RarEncryptionSampleSet.TryCreate(_root, output.WriteLine);

            if (_samples == null)
            {
                output.WriteLine("跳过原因：本机没有可用的 Rar.exe（WinRAR），造不出真 RAR 加密样本。");
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

        [Fact]
        public void RAR4_p_只加密数据_读出来是数据加密()
        {
            if (_samples == null)
            {
                return;
            }

            RarEncryptionReading reading = RarEncryptionReader.Read(_samples.Rar4DataEncrypted);

            Assert.Equal(RarEncryptionState.DataEncrypted, reading.State);
            Assert.True(reading.IsEncrypted);
        }

        [Fact]
        public void RAR4_hp_连头一起加密_读出来是头加密()
        {
            if (_samples == null)
            {
                return;
            }

            RarEncryptionReading reading = RarEncryptionReader.Read(_samples.Rar4HeadersEncrypted);

            Assert.Equal(RarEncryptionState.HeadersEncrypted, reading.State);
        }

        [Fact]
        public void RAR5_p_只加密数据_读出来是数据加密()
        {
            if (_samples == null)
            {
                return;
            }

            RarEncryptionReading reading = RarEncryptionReader.Read(_samples.Rar5DataEncrypted);

            Assert.Equal(RarEncryptionState.DataEncrypted, reading.State);
        }

        [Fact]
        public void RAR5_hp_连头一起加密_读出来是头加密()
        {
            if (_samples == null)
            {
                return;
            }

            RarEncryptionReading reading = RarEncryptionReader.Read(_samples.Rar5HeadersEncrypted);

            Assert.Equal(RarEncryptionState.HeadersEncrypted, reading.State);
        }

        [Theory]
        [InlineData("r4-plain")]
        [InlineData("r5-plain")]
        public void 不加密的对照组_不许误报(string which)
        {
            if (_samples == null)
            {
                return;
            }

            string path = which == "r4-plain" ? _samples.Rar4Plain : _samples.Rar5Plain;
            RarEncryptionReading reading = RarEncryptionReader.Read(path);

            Assert.Equal(RarEncryptionState.NotEncrypted, reading.State);
            Assert.False(reading.IsEncrypted);
        }

        /// <summary>
        /// 多卷 RAR：判据只看**第 1 卷**（主头与第一个文件头都在它里面），续卷读不出来是正常的。
        ///
        /// <para>这一条钉的正是用户点名的那个坑：<c>-hp</c> 的多卷组里，续卷从中间开始、连主头都没有，
        /// 拿它去判必然得不出结论 —— ⛔ 不许因为续卷读不出来就把整组判成"没加密"。</para>
        /// </summary>
        [Fact]
        public void 多卷RAR_第1卷就能定加密_续卷读不出来也不影响()
        {
            if (_samples == null)
            {
                return;
            }

            string? dataFirst = _samples.Rar4DataEncryptedFirstVolume;
            string? headersFirst = _samples.Rar4HeadersEncryptedFirstVolume;

            if (dataFirst == null || headersFirst == null)
            {
                return;
            }

            Assert.Equal(RarEncryptionState.DataEncrypted, RarEncryptionReader.Read(dataFirst).State);
            Assert.Equal(RarEncryptionState.HeadersEncrypted, RarEncryptionReader.Read(headersFirst).State);

            // 这是真的分卷组：第 1 卷之外还有 .part2.rar 起。
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(dataFirst)!, "r4-p-vol.part*.rar"));
        }

        [Fact]
        public async Task 多卷RAR_整组走识别_任务上的加密标志为真()
        {
            if (_samples == null)
            {
                return;
            }

            string? first = _samples.Rar4DataEncryptedFirstVolume;

            if (first == null)
            {
                return;
            }

            var task = new ArchiveTask(first);
            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal("RAR4", task.DetectedFormat);
            Assert.True(task.IsEncrypted, "多卷 RAR 的第 1 卷报了加密，整组就必须算加密（续卷读不出来不算否）");
        }

        [Fact]
        public async Task 识别阶段_四种加密样本全都认出来_两个对照组都不误报()
        {
            if (_samples == null)
            {
                return;
            }

            var service = new ArchiveDetectService();

            DetectResult r4P = await service.DetectAsync(_samples.Rar4DataEncrypted);
            DetectResult r4Hp = await service.DetectAsync(_samples.Rar4HeadersEncrypted);
            DetectResult r5P = await service.DetectAsync(_samples.Rar5DataEncrypted);
            DetectResult r5Hp = await service.DetectAsync(_samples.Rar5HeadersEncrypted);

            Assert.True(r4P.IsProbablyEncrypted, "RAR4 -p 必须被认出来");
            Assert.True(r4Hp.IsProbablyEncrypted, "RAR4 -hp 必须被认出来");
            Assert.True(r5P.IsProbablyEncrypted, "RAR5 -p 必须被认出来");
            Assert.True(r5Hp.IsProbablyEncrypted, "RAR5 -hp 必须被认出来");

            // 格式结论一个字都不许变（识别仍然"不信后缀"那一套）。
            Assert.Equal("RAR4", r4P.Format);
            Assert.Equal("RAR4", r4Hp.Format);
            Assert.Equal("RAR5", r5P.Format);
            Assert.Equal("RAR5", r5Hp.Format);

            DetectResult plain4 = await service.DetectAsync(_samples.Rar4Plain);
            DetectResult plain5 = await service.DetectAsync(_samples.Rar5Plain);

            Assert.False(plain4.IsProbablyEncrypted, "没加密的 RAR4 不许误报");
            Assert.False(plain5.IsProbablyEncrypted, "没加密的 RAR5 不许误报");
        }

        /// <summary>
        /// 截断 / 坏头 / 不是 RAR：一律"不知道"，⛔ 绝不当成"没加密"以外的任何结论，更不许猜成"加密"。
        /// </summary>
        [Fact]
        public void 截断与坏头_返回不知道()
        {
            if (_samples == null)
            {
                return;
            }

            // ①只有签名（头被截断）
            string truncated = Path.Combine(_root, "truncated.rar");
            byte[] whole = File.ReadAllBytes(_samples.Rar4HeadersEncrypted);
            File.WriteAllBytes(truncated, whole[..8]);
            Assert.Equal(RarEncryptionState.Unknown, RarEncryptionReader.Read(truncated).State);

            // ②签名 + 一个 CRC 对不上的主头（把 flags 那一字节改掉，块头 CRC 必然对不上）
            string broken = Path.Combine(_root, "broken.rar");
            byte[] brokenBytes = (byte[])whole.Clone();

            if (brokenBytes.Length > 12)
            {
                brokenBytes[11] ^= 0xFF;
            }

            File.WriteAllBytes(broken, brokenBytes);
            Assert.Equal(RarEncryptionState.Unknown, RarEncryptionReader.Read(broken).State);

            // ③根本不是 RAR
            string notRar = Path.Combine(_root, "not-rar.txt");
            File.WriteAllText(notRar, "这不是压缩包。");
            Assert.Equal(RarEncryptionState.Unknown, RarEncryptionReader.Read(notRar).State);

            // ④文件不存在
            Assert.Equal(
                RarEncryptionState.Unknown,
                RarEncryptionReader.Read(Path.Combine(_root, "根本没有这个文件.rar")).State);
        }

        /// <summary>
        /// 头部 CRC 真的在挡事：整组样本每一份的结论都不是靠"猜"得来的 ——
        /// 把主头 CRC 那两字节改坏之后，连"没加密"都得改口成"不知道"。
        /// </summary>
        [Fact]
        public void 头部CRC坏掉时_连没加密都要改口成不知道()
        {
            if (_samples == null)
            {
                return;
            }

            byte[] bytes = File.ReadAllBytes(_samples.Rar4Plain);
            bytes[8] ^= 0xFF;   // 主头 HEAD_CRC 的低字节

            string corrupted = Path.Combine(_root, "crc-broken.rar");
            File.WriteAllBytes(corrupted, bytes);

            Assert.Equal(RarEncryptionState.Unknown, RarEncryptionReader.Read(corrupted).State);
        }

        /// <summary>
        /// **读入量上限**（64 KiB）：判读只读头，包体一律 seek 跳过 ——
        /// 一个 200 MB 的 RAR 与一个 200 字节的 RAR，读进内存的量必须是同一个量级。
        /// </summary>
        [Fact]
        public void 读入量_不超过64KiB_包体多大都一样()
        {
            if (_samples == null)
            {
                return;
            }

            string big = Path.Combine(_root, "big-with-tail.rar");

            // 真包（-hp，主头 13 字节）+ 200 MB 垃圾尾巴：判读必须在第一个头就收工。
            using (FileStream stream = File.Create(big))
            {
                byte[] head = File.ReadAllBytes(_samples.Rar4HeadersEncrypted);
                stream.Write(head, 0, head.Length);
                stream.SetLength(200L * 1024 * 1024);
            }

            RarEncryptionReading reading = RarEncryptionReader.Read(big);

            Assert.Equal(RarEncryptionState.HeadersEncrypted, reading.State);
            Assert.True(
                reading.BytesRead <= RarEncryptionReader.MaxHeaderBytes,
                $"读入量 {reading.BytesRead} 字节超了上限 {RarEncryptionReader.MaxHeaderBytes}");
        }

        /// <summary>
        /// "读不出来"≠"没加密"：两档都不报加密（对外口径一致），但机器结论必须分得开 ——
        /// 以后排障要能回答"这一卷到底是没加密，还是我们没看明白"。
        /// </summary>
        [Fact]
        public void 不知道与没加密_都不报加密_但结论分得开()
        {
            if (_samples == null)
            {
                return;
            }

            RarEncryptionReading plain = RarEncryptionReader.Read(_samples.Rar5Plain);
            RarEncryptionReading unknown = RarEncryptionReader.Read(_samples.Rar5Plain + ".missing");

            Assert.False(plain.IsEncrypted);
            Assert.False(unknown.IsEncrypted);
            Assert.NotEqual(plain.State, unknown.State);
            Assert.False(string.IsNullOrWhiteSpace(plain.Basis));
            Assert.False(string.IsNullOrWhiteSpace(unknown.Basis));
        }
    }
}
