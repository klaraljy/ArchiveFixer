using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **引擎点名的坏条目**要读成结构化数据（「部分完成也把已解出的内容放进目标目录」那块功能的地基）。
    ///
    /// <para>为什么非要它：引擎解到一半失败时，**退出码只说"有错"，说清"哪一个条目坏了"的只有它打的那几行字**；
    /// 而"哪些文件敢发布"必须逐条回答。解析只能发生在 <c>Engines/</c> 里面（AGENTS §3 禁止项②）。</para>
    ///
    /// <para>本文件的文本全部是**本机真样本实测**的原文（2026-10-02 现造现打，见每个用例的注释）：
    /// 打坏一个条目 → 跑真引擎 → 抄原话。⛔ 不许凭记忆编。</para>
    ///
    /// <para>⚠ 一条要点：**中文版 UnRAR 这些词也是中文的**（用户机器上就是中文 6.11）⇒ 那边可能点不出名；
    /// 所以判据是"名字 + 引擎自报的计数"两件一起，调用方还要拿"清单 vs 盘上实际"对账（见
    /// <c>ArchiveOperationResult.FailedEntryNames</c> 的说明）。</para>
    /// </summary>
    public class EngineFailedEntryListTests : IDisposable
    {
        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public EngineFailedEntryListTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerFailedEntries", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ────────────────────────── ① 7-Zip：真样本原文 ──────────────────────────

        /// <summary>7-Zip 26.03 打坏一个**固实** 7z 的原文（<c>ERROR: Data Error : good.bin</c>）。</summary>
        private const string SevenZipSolidDataError = """
7-Zip 26.03 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-09-03

Scanning the drive for archives:
1 file, 737 bytes (1 KiB)

Extracting archive: C:\samples\two-broken.7z
--
Path = C:\samples\two-broken.7z
Type = 7z
Physical Size = 737
Headers Size = 170
Method = LZMA2:19
Solid = +
Blocks = 1

ERROR: Data Error : good.bin

Sub items Errors: 1

Archives with Errors: 1

Sub items Errors: 1

""";

        /// <summary>加密头（<c>-mhe</c>）包给对密码、数据坏那一档的原文（giu910 现场的同一形状）。</summary>
        private const string SevenZipEncryptedDataError = """
Extracting archive: C:\samples\enc-broken.7z
--
Path = C:\samples\enc-broken.7z
Type = 7z
Method = LZMA2:19 7zAES:19
Solid = +
Blocks = 1

ERROR: Data Error in encrypted file. Wrong password? : good.bin

Sub items Errors: 1

""";

        /// <summary>
        /// **归档级那一档不许被当成条目名**：7-Zip 打的是 <c>ERROR: &lt;归档路径&gt; : &lt;原因&gt;</c>
        /// —— **路径在左边**，与条目级的 <c>ERROR: &lt;原因&gt; : &lt;条目名&gt;</c> 方向相反。
        /// （这条原文来自用户 2026-10-02 真机日志：`51658213.7z.001` 那一单。）
        /// </summary>
        private const string SevenZipArchiveLevelError = """
ERROR: C:\samples\51658213.7z.001 : Cannot open encrypted archive. Wrong password?

Sub items Errors: 1
""";

        [Fact]
        public void 七Zip_条目级错误要读出条目名与自报计数()
        {
            IReadOnlyList<string> names = SevenZipOutputParser.ExtractFailedEntryNames(SevenZipSolidDataError);

            _output.WriteLine(string.Join(" | ", names));

            Assert.Equal(new[] { "good.bin" }, names);
            Assert.Equal(1, SevenZipOutputParser.ExtractSubItemErrorCount(SevenZipSolidDataError));
        }

        [Fact]
        public void 七Zip_加密文件那一档也读得出条目名()
        {
            Assert.Equal(
                new[] { "good.bin" },
                SevenZipOutputParser.ExtractFailedEntryNames(SevenZipEncryptedDataError));
        }

        [Fact]
        public void 七Zip_归档级错误不许被当成坏条目()
        {
            IReadOnlyList<string> names = SevenZipOutputParser.ExtractFailedEntryNames(SevenZipArchiveLevelError);

            _output.WriteLine(string.Join(" | ", names));

            // ⛔ 关键：路径在左边那一档，右边那句 "Cannot open encrypted archive..." 不是条目名。
            Assert.Empty(names);

            // 但"自报有错"这件事照样要读出来 —— 调用方靠它当闸门（点不出名 ⇒ 不发布）。
            Assert.Equal(1, SevenZipOutputParser.ExtractSubItemErrorCount(SevenZipArchiveLevelError));
        }

        [Fact]
        public void 七Zip_成功输出里没有任何坏条目()
        {
            const string success = """
Everything is Ok

Folders: 1
Files: 2
Size:       400000
Compressed: 737
""";

            Assert.Empty(SevenZipOutputParser.ExtractFailedEntryNames(success));
            Assert.Equal(0, SevenZipOutputParser.ExtractSubItemErrorCount(success));
        }

        // ────────────────────────── ② UnRAR：真样本原文 ──────────────────────────

        /// <summary>UnRAR 7.23 打坏一个条目后的原文（<c>bad.bin              - checksum error</c>）。</summary>
        private const string UnRarChecksumError = """

UNRAR 7.23 x64 freeware      Copyright (c) 1993-2026 Alexander Roshal

Extracting from C:\samples\two-broken.rar

Creating    C:\samples\outrar  OK
Extracting  C:\samples\outrar\good.bin      50%  OK 

Extracting  C:\samples\outrar\bad.bin      100%
Total errors: 1
bad.bin              - checksum error

""";

        [Fact]
        public void UnRAR_校验和错误要读出条目名与自报计数()
        {
            IReadOnlyList<string> names = UnRarOutputParser.ExtractFailedEntryNames(UnRarChecksumError);

            _output.WriteLine(string.Join(" | ", names));

            Assert.Equal(new[] { "bad.bin" }, names);
            Assert.Equal(1, UnRarOutputParser.ExtractTotalErrorCount(UnRarChecksumError));

            // ⛔ 别把 "Extracting … 50% OK" 那种行当成坏条目。
            Assert.DoesNotContain(names, name => name.Contains("Extracting", StringComparison.Ordinal));
        }

        [Fact]
        public void UnRAR_中文界面的输出点不出名但也不许编名字()
        {
            /*
             * 用户机器上那份中文 UnRAR 打的是中文（现场见 UnRarOutputParser.LooksLikePasswordOrCorrupted 的注释）。
             * 这一档的正确行为是：**一个名字都读不出来**（⛔ 不许把整行当成条目名），
             * 计数也读不出来 —— 于是调用方只能靠"清单 vs 盘上实际"对账 + 退出码，判不出就不发布。
             */
            const string chinese = """
正在从 C:\samples\two-broken.rar 中解压

解压      C:\samples\outrar\good.bin      50%  确定
错误      在加密文件 bad.bin 里校验和错误。文件已损坏或密码错误。
共 1 个错误
""";

            Assert.Empty(UnRarOutputParser.ExtractFailedEntryNames(chinese));
            Assert.Equal(0, UnRarOutputParser.ExtractTotalErrorCount(chinese));
        }

        // ────────────────────────── ③ 真引擎端到端（7-Zip 现造现打坏） ──────────────────────────

        /// <summary>
        /// 真 7-Zip：造一个两文件的固实 7z → 翻掉数据区一个字节 → 用**真引擎**解一遍 →
        /// 断言 <c>FailedEntryNames</c> 与 <c>ReportedSubItemErrors</c> 真的被填上了。
        ///
        /// <para>⚠ 这条是**防"解析器只对得上手抄文本"**的：7-Zip 换版本改了措辞，它会当场红。</para>
        /// </summary>
        [Fact]
        public async Task 真七Zip_解坏包时结构化名单里要有点名的条目()
        {
            string sevenZip = LocateSevenZip();

            if (sevenZip.Length == 0)
            {
                _output.WriteLine("找不到内置 7z.exe ⇒ 跳过（不许假装验过）");
                return;
            }

            string stage = Path.Combine(_root, "build");
            Directory.CreateDirectory(stage);

            File.WriteAllBytes(Path.Combine(stage, "good.bin"), Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray());
            File.WriteAllBytes(Path.Combine(stage, "bad.bin"), Enumerable.Range(0, 200_000).Select(i => (byte)((i * 7) % 253)).ToArray());

            string archive = Path.Combine(_root, "two.7z");

            Run7z(sevenZip, stage, "a", "-t7z", archive, "good.bin", "bad.bin");

            byte[] bytes = File.ReadAllBytes(archive);
            int offset = (int)(bytes.Length * 0.75);
            bytes[offset] ^= 0xFF;
            File.WriteAllBytes(archive, bytes);

            var engine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator
            {
                CustomSevenZipExePath = sevenZip
            }));

            ArchiveOperationResult result = await engine.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = archive,
                    OutputPath = Path.Combine(_root, "out")
                },
                new ExtractOptions(),
                CancellationToken.None);

            _output.WriteLine(
                $"Success={result.Success} 退出码={result.ExitCode} 点名={string.Join('、', result.FailedEntryNames)} "
                + $"自报坏条目数={result.ReportedSubItemErrors}");

            Assert.False(result.Success, "打坏的包不许报成功");

            Assert.True(
                result.ReportedSubItemErrors >= 1,
                "引擎自报的坏条目数必须被读出来 —— 它是「点不出名就不发布」那道闸门的输入");

            Assert.Contains(
                result.FailedEntryNames,
                name => name.Contains("good.bin", StringComparison.OrdinalIgnoreCase));
        }

        // ────────────────────────── 装配 ──────────────────────────

        private static string LocateSevenZip()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);

            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(current.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                current = current.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        private static void Run7z(string sevenZip, string workingDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
                StandardOutputEncoding = Encoding.UTF8
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"7z 失败（exit {process.ExitCode}）：{stdout}\n{stderr}");
            }
        }
    }
}
