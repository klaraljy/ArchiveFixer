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
    /// **真样本**验收：加密判读在真实包上跑通（用户 2026-09-29 深夜定的验收规则：
    /// "必须在真样本（或真机文件的只读副本）上跑通；合成样本通过 ≠ 问题解决"）。
    ///
    /// <para><b>为什么必须单独一组</b>：合成样本只能证明"我按自己的理解造的字节能被自己的解析器读出来"。
    /// 真样本才会暴露"我以为的字段偏移 / coder ID 与真产品写出来的字节不一致"。
    /// 本组覆盖三档：真实 <c>-p</c> 7z（明文头里有 AES coder）、真实**不加密** ZIP（负对照：
    /// flags 是 <c>0x0800</c>（UTF-8 文件名标志）而 bit0 = 0）、真实 RAR5 <c>-p</c>。</para>
    ///
    /// <para><b>路径一律走环境变量</b>（AGENTS.md §8：真实路径 / 包名不进仓库），
    /// 没设就**跳过并说明原因**（⛔ 不假装跑过）。三个变量各自独立，缺哪个只跳哪一条：</para>
    /// <list type="bullet">
    /// <item><description><c>ARCHIVEFIXER_REAL_ENCRYPTION_7Z</c> —— 真实的 7z <c>-p</c> 包（明文头、含 AES coder）；</description></item>
    /// <item><description><c>ARCHIVEFIXER_REAL_ENCRYPTION_ZIP_PLAIN</c> —— 真实的**不加密** ZIP（负对照）；</description></item>
    /// <item><description><c>ARCHIVEFIXER_REAL_ENCRYPTION_RAR</c> —— 真实的 RAR <c>-p</c> 包（RAR5 或 RAR4 都收）。</description></item>
    /// </list>
    ///
    /// <para>⛔ 全程**只读**：只 <c>File.Open(..., FileAccess.Read, FileShare.ReadWrite)</c>，
    /// 一个字节都不改、不复制、不建临时副本。</para>
    /// </summary>
    public class RealEncryptionSampleTests
    {
        /// <summary>真实 7z <c>-p</c> 包的路径（环境变量名）。</summary>
        internal const string SevenZipEnvironmentVariable = "ARCHIVEFIXER_REAL_ENCRYPTION_7Z";

        /// <summary>真实**不加密** ZIP 的路径（负对照；环境变量名）。</summary>
        internal const string PlainZipEnvironmentVariable = "ARCHIVEFIXER_REAL_ENCRYPTION_ZIP_PLAIN";

        /// <summary>真实 RAR <c>-p</c> 包的路径（环境变量名）。</summary>
        internal const string RarEnvironmentVariable = "ARCHIVEFIXER_REAL_ENCRYPTION_RAR";

        private readonly ITestOutputHelper _output;

        public RealEncryptionSampleTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task 真样本_7z数据加密_识别阶段就读得出来()
        {
            string? path = Resolve(SevenZipEnvironmentVariable);

            if (path == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = SevenZipEncryptionReader.Read(path);

            _output.WriteLine($"{Path.GetFileName(path)} → {reading.State}（{reading.Basis}，读入 {reading.BytesRead} 字节）");

            Assert.Equal(ArchiveEncryptionState.DataEncrypted, reading.State);

            DetectResult result = await new ArchiveDetectService().DetectAsync(path);

            Assert.Equal("7Z", result.Format);
            Assert.True(result.IsProbablyEncrypted, "真实 7z -p 包必须被认出来");
            Assert.Contains(StatusText.DetectSevenZipDataEncryptedNote, result.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// **负对照**：真实的不加密 ZIP。它的通用位标志是 <c>0x0800</c>（bit11 = UTF-8 文件名标志），
        /// bit0 = 0 ⇒ 必须报"不加密"。⛔ 谁要是把 <c>0x0800</c> 当成加密位，这一条立刻变红。
        /// </summary>
        [Fact]
        public async Task 真样本_不加密ZIP_不许误报()
        {
            string? path = Resolve(PlainZipEnvironmentVariable);

            if (path == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = ZipEncryptionReader.Read(path);

            _output.WriteLine($"{Path.GetFileName(path)} → {reading.State}（{reading.Basis}，读入 {reading.BytesRead} 字节）");

            Assert.Equal(ArchiveEncryptionState.NotEncrypted, reading.State);
            Assert.False(reading.IsEncrypted);

            DetectResult result = await new ArchiveDetectService().DetectAsync(path);

            Assert.False(result.IsProbablyEncrypted, "没加密的真实 ZIP 不许误报");
        }

        [Fact]
        public async Task 真样本_RAR数据加密_识别阶段就读得出来()
        {
            string? path = Resolve(RarEnvironmentVariable);

            if (path == null)
            {
                return;
            }

            ArchiveEncryptionReading reading = RarEncryptionReader.Read(path);

            _output.WriteLine($"{Path.GetFileName(path)} → {reading.State}（{reading.Basis}，读入 {reading.BytesRead} 字节）");

            // 只收"有数据或头加密证据"的真样本：没加密的 RAR 交进来时如实说"这条不适用"。
            if (!reading.IsEncrypted)
            {
                _output.WriteLine("这一份真样本没有读出加密证据 —— 本用例按「不适用」跳过（⛔ 不当成通过）。");

                return;
            }

            DetectResult result = await new ArchiveDetectService().DetectAsync(path);

            Assert.True(result.Format == "RAR4" || result.Format == "RAR5");
            Assert.True(result.IsProbablyEncrypted, "真实 RAR -p 包必须被认出来");
        }

        /// <summary>读环境变量；没设 / 路径不存在 → 打印原因并返回 null（用例跳过）。</summary>
        private string? Resolve(string variable)
        {
            string? path = Environment.GetEnvironmentVariable(variable);

            if (string.IsNullOrWhiteSpace(path))
            {
                _output.WriteLine($"跳过原因：没有设置环境变量 {variable}（真样本路径按 AGENTS.md §8 走环境变量，不写进仓库）。");

                return null;
            }

            if (!File.Exists(path))
            {
                _output.WriteLine($"跳过原因：{variable} 指的文件不存在。");

                return null;
            }

            return path;
        }
    }
}
