using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 7z **加密标志**的判读（用户 2026-09-30）。
    ///
    /// <para><b>为什么要有这一组</b>：识别阶段过去只看开头 6 个魔数字节，而 7z 的加密信号写在
    /// **coder 链**里（AES-256 的 method ID = <c>06 F1 07 01</c>）：要么在明文主头里（<c>-p</c>），
    /// 要么在"编码头"的解码链里（<c>-mhe</c>）。后果是批首那句"本批有 N 个包没有可用密码"对 7z 整档漏报；
    /// 而 <c>-mhe</c> 更狠 —— 引擎侧连条目名都列不出来（<c>7z l -slt</c> 一条都不报），
    /// 是"程序现在完全不知道它加密"的那一档。</para>
    ///
    /// <para><b>样本全部是现场造的真包</b>（项目内置 <c>7z.exe</c>，⛔ 样本不入库、⛔ 密码用占位符）。
    /// 拿不到内置 7z 时**跳过并说明原因**（⛔ 不假装跑过）。</para>
    ///
    /// <para><b>红检</b>：把 <see cref="SevenZipEncryptionReader"/> 里"<c>kEncodedHeader</c> 的解码链里找 AES"
    /// 那一段去掉（恒返回"不知道"），<see cref="SevenZip_mhe_连头一起加密_读出来是头加密"/> 与
    /// <see cref="SevenZip_多卷_给整组卷才读得出头加密"/> **立刻变红**；
    /// 把"明文头 MainStreamsInfo 里找 AES"去掉，<see cref="SevenZip_p_只加密数据_读出来是数据加密"/> 变红。</para>
    /// </summary>
    public class SevenZipEncryptionReaderTests : IDisposable
    {
        private readonly string _root;
        private readonly SevenZipEncryptionSampleSet? _samples;

        public SevenZipEncryptionReaderTests(ITestOutputHelper output)
        {
            _root = Path.Combine(Path.GetTempPath(), "af_7z_enc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _samples = SevenZipEncryptionSampleSet.TryCreate(_root, output.WriteLine);

            if (_samples == null)
            {
                output.WriteLine("跳过原因：测试机上没有可用的内置 7z.exe，造不出真 7z 加密样本。");
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
        public void SevenZip_p_只加密数据_读出来是数据加密()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = SevenZipEncryptionReader.Read(_samples.DataEncrypted);

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, reading.State);
            Assert.True(reading.IsEncrypted);
        }

        [Fact]
        public void SevenZip_mhe_连头一起加密_读出来是头加密()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = SevenZipEncryptionReader.Read(_samples.HeadersEncrypted);

            Assert.Equal(ArchiveEncryptionState.HeadersEncrypted, reading.State);
        }

        [Fact]
        public void SevenZip_普通包_不许误报()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = SevenZipEncryptionReader.Read(_samples.Plain);

            Assert.Equal(ArchiveEncryptionState.NotEncrypted, reading.State);
            Assert.False(reading.IsEncrypted);
        }

        /// <summary>
        /// **本组最重要的一条**：<c>-p</c> + 80 个文件时头被**压缩**，编码链里只有 LZMA、看不见 AES ——
        /// 可这个包是**真加密**的（<c>7z t -pWrongPassword</c> 报 "Wrong password?"）。
        ///
        /// <para>所以这一档只能如实说"不知道"，⛔ **绝不许报"不加密"**。
        /// 而普通包（同样 80 个文件、同样被压缩的头）在这 64 KiB 里与它**逐字节同构** ——
        /// 两者都无法区分，说"没加密"就是误报。</para>
        /// </summary>
        [Fact]
        public void SevenZip_p_80个文件_头被压缩_如实返回不知道()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = SevenZipEncryptionReader.Read(_samples.ManyDataEncrypted);

            Assert.Equal(ArchiveEncryptionState.Unknown, reading.State);
            Assert.False(reading.IsEncrypted);

            // 对照组：同样 80 个文件、不加密的包，读出来也必须是"不知道"（同样看不见 coder 链）。
            Assert.Equal(
                ArchiveEncryptionState.Unknown,
                SevenZipEncryptionReader.Read(_samples.ManyPlain).State);
        }

        [Fact]
        public void SevenZip_80个文件_mhe_读出来是头加密()
        {
            if (_samples == null)
            {
                return;
            }

            Assert.Equal(
                ArchiveEncryptionState.HeadersEncrypted,
                SevenZipEncryptionReader.Read(_samples.ManyHeadersEncrypted).State);
        }

        /// <summary>
        /// 截断 / 布局不符 / 不是 7z：一律"不知道"，⛔ 绝不当成"没加密"以外的任何结论，更不许猜成"加密"。
        /// </summary>
        [Fact]
        public void SevenZip_截断与布局不符_返回不知道()
        {
            if (_samples == null)
            {
                return;
            }

            // ①只剩签名（起始头都不到 32 字节）
            string truncated = Path.Combine(_root, "truncated.7z");
            File.WriteAllBytes(truncated, File.ReadAllBytes(_samples.HeadersEncrypted)[..16]);
            Assert.Equal(ArchiveEncryptionState.Unknown, SevenZipEncryptionReader.Read(truncated).State);

            // ②起始头完整、但 NextHeaderOffset 指向文件外面（32 + off + size > 文件长度）
            string badLayout = Path.Combine(_root, "bad-layout.7z");
            byte[] whole = File.ReadAllBytes(_samples.Plain);
            whole[12] = 0xFF;
            whole[13] = 0xFF;
            File.WriteAllBytes(badLayout, whole);
            Assert.Equal(ArchiveEncryptionState.Unknown, SevenZipEncryptionReader.Read(badLayout).State);

            // ③根本不是 7z
            string notSevenZip = Path.Combine(_root, "not-7z.txt");
            File.WriteAllText(notSevenZip, "这不是压缩包。");
            Assert.Equal(ArchiveEncryptionState.Unknown, SevenZipEncryptionReader.Read(notSevenZip).State);

            // ④文件不存在
            Assert.Equal(
                ArchiveEncryptionState.Unknown,
                SevenZipEncryptionReader.Read(Path.Combine(_root, "根本没有这个文件.7z")).State);
        }

        /// <summary>
        /// **读入量上限**（64 KiB）：判读只读起始头 + next header，包体一律 seek 跳过 ——
        /// 一个 256 KiB 的 7z 与一个 256 MiB 的 7z，读进内存的量必须是同一个量级。
        /// </summary>
        [Fact]
        public void SevenZip_读入量_不超过64KiB()
        {
            if (_samples == null)
            {
                return;
            }

            // 真包（-mhe，next header 只有几十字节）+ 256 MiB 尾巴：
            // 追加垃圾让文件变大不会破坏布局闸门（它只要求"落在文件内"），判读照旧只看头。
            string big = Path.Combine(_root, "big-with-tail.7z");

            using (FileStream stream = File.Create(big))
            {
                byte[] head = File.ReadAllBytes(_samples.HeadersEncrypted);
                stream.Write(head, 0, head.Length);
                stream.SetLength(256L * 1024 * 1024);
            }

            ArchiveEncryptionReading reading = SevenZipEncryptionReader.Read(big);

            Assert.Equal(ArchiveEncryptionState.HeadersEncrypted, reading.State);
            Assert.True(
                reading.BytesRead <= SevenZipEncryptionReader.MaxHeaderBytes,
                $"读入量 {reading.BytesRead} 字节超了上限 {SevenZipEncryptionReader.MaxHeaderBytes}");
        }

        /// <summary>
        /// **多卷 7z**：元数据在**最后一卷**，"只看第 1 卷"必然读不出来（如实"不知道"）；
        /// 给了整组卷才读得出加密。这一条同时钉住"单文件那一档不许猜"。
        /// </summary>
        [Fact]
        public void SevenZip_多卷_只看第1卷读不出来_给整组卷才读得出()
        {
            if (_samples == null)
            {
                return;
            }

            string? plainFirst = _samples.VolumePlainFirst;
            string? dataFirst = _samples.VolumeDataEncryptedFirst;
            string? headersFirst = _samples.VolumeHeadersEncryptedFirst;

            if (plainFirst == null || dataFirst == null || headersFirst == null)
            {
                return;
            }

            // ① 只看第 1 卷：一律"不知道"（连不加密的那个也是 —— 它确实读不出来）。
            Assert.Equal(ArchiveEncryptionState.Unknown, SevenZipEncryptionReader.Read(dataFirst).State);
            Assert.Equal(ArchiveEncryptionState.Unknown, SevenZipEncryptionReader.Read(headersFirst).State);
            Assert.Equal(ArchiveEncryptionState.Unknown, SevenZipEncryptionReader.Read(plainFirst).State);

            // ② 给整组卷：判据落在拼接坐标里，读得出正确结论。
            Assert.Equal(
                ArchiveEncryptionState.DataEncrypted,
                SevenZipEncryptionReader.Read(dataFirst, 0, _samples.VolumesOf(dataFirst)).State);
            Assert.Equal(
                ArchiveEncryptionState.HeadersEncrypted,
                SevenZipEncryptionReader.Read(headersFirst, 0, _samples.VolumesOf(headersFirst)).State);
            Assert.Equal(
                ArchiveEncryptionState.NotEncrypted,
                SevenZipEncryptionReader.Read(plainFirst, 0, _samples.VolumesOf(plainFirst)).State);

            // 这是真的分卷组：第 1 卷之外还有 .002 起。
            Assert.True(_samples.VolumesOf(dataFirst).Count > 1, "样本必须是真分卷");
        }

        /// <summary>
        /// **多卷 7z 的端到端接线**：<c>ApplyDetectResultAsync</c> 那一刻卷组已经填好，
        /// 所以任务上的加密标志必须为真（判据仍然只走那一个出口）。
        /// </summary>
        [Fact]
        public async Task SevenZip_多卷_端到端_任务上的加密标志为真()
        {
            if (_samples == null)
            {
                return;
            }

            string? dataFirst = _samples.VolumeDataEncryptedFirst;

            if (dataFirst == null)
            {
                return;
            }

            IReadOnlyList<string> volumes = _samples.VolumesOf(dataFirst);

            var task = new ArchiveTask(dataFirst);

            // 卷组信息在真实流程里由 FileScanService.ScanPathsAsync 归组时填好（识别之前），
            // 这里显式摆成那一刻的样子。
            foreach (string volume in volumes)
            {
                task.VolumePaths.Add(volume);
            }

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal("7Z", task.DetectedFormat);
            Assert.True(task.IsEncrypted, "多卷 7z 的元数据在最后一卷，拿到卷组就必须读得出加密");
        }

        /// <summary>
        /// **端到端**：<see cref="ArchiveDetectService.DetectAsync"/> 必须把标志落到
        /// <see cref="DetectResult.IsProbablyEncrypted"/>，并在消息里带上对应注脚。
        /// </summary>
        [Fact]
        public async Task SevenZip_端到端_识别把标志与注脚落下来()
        {
            if (_samples == null)
            {
                return;
            }

            var service = new ArchiveDetectService();

            DetectResult data = await service.DetectAsync(_samples.DataEncrypted);
            DetectResult headers = await service.DetectAsync(_samples.HeadersEncrypted);
            DetectResult manyHeaders = await service.DetectAsync(_samples.ManyHeadersEncrypted);

            Assert.True(data.IsProbablyEncrypted, "7z -p 必须被认出来");
            Assert.True(headers.IsProbablyEncrypted, "7z -mhe 必须被认出来");
            Assert.True(manyHeaders.IsProbablyEncrypted, "7z -mhe（头被编码）必须被认出来");

            Assert.Contains(StatusText.DetectSevenZipDataEncryptedNote, data.Message, StringComparison.Ordinal);
            Assert.Contains(StatusText.DetectSevenZipHeadersEncryptedNote, headers.Message, StringComparison.Ordinal);

            // 格式结论一个字都不许变。
            Assert.Equal("7Z", data.Format);
            Assert.Equal("7Z", headers.Format);

            /*
             * ⛔ 读不出来的两档**不许**报加密：
             * 普通包（明文头、没有 AES）与 many-p.7z（头被压缩、看不见 coder 链，但真加密）。
             */
            DetectResult plain = await service.DetectAsync(_samples.Plain);
            DetectResult manyP = await service.DetectAsync(_samples.ManyDataEncrypted);

            Assert.False(plain.IsProbablyEncrypted, "没加密的 7z 不许误报");
            Assert.False(manyP.IsProbablyEncrypted, "读不出来时一律不报加密（宁可漏报）—— ⛔ 但也绝不许说它没加密");
        }

        /// <summary>
        /// "读不出来"≠"没加密"：两档都不报加密（对外口径一致），但机器结论必须分得开 ——
        /// 以后排障要能回答"这一包到底是没加密，还是我们没看明白"。
        /// </summary>
        [Fact]
        public void SevenZip_不知道与没加密_都不报加密_但结论分得开()
        {
            if (_samples == null)
            {
                return;
            }

            ArchiveEncryptionReading plain = SevenZipEncryptionReader.Read(_samples.Plain);
            ArchiveEncryptionReading unknown = SevenZipEncryptionReader.Read(_samples.ManyPlain);

            Assert.False(plain.IsEncrypted);
            Assert.False(unknown.IsEncrypted);
            Assert.NotEqual(plain.State, unknown.State);
            Assert.False(string.IsNullOrWhiteSpace(plain.Basis));
            Assert.False(string.IsNullOrWhiteSpace(unknown.Basis));
        }
    }
}
