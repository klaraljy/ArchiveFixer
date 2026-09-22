using ArchiveFixer.Converters;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 加密文件名（RAR <c>-hp</c> / 7z <c>-mhe</c>）的判定与结论。
    ///
    /// 为什么要单独一类：这种包以前会落到"密码错误 / 文件损坏"里，用户拿到的结论是**错的** ——
    /// 包可能完全正常，只是需要正确密码才能列出内容。
    ///
    /// 判定只用一条证据：**列目录失败**。证据来自本机 7-Zip 26.01 的实测（samples/generated 里的加密样本）：
    /// · 数据加密（未加密文件名）的包：不给密码也能列出条目名，`l` 退出码 **0**；
    /// · 加密头（<c>-mhe</c>）的包：不给密码列目录，输出
    ///   <c>ERROR: x.7z : Cannot open encrypted archive. Wrong password?</c> + <c>ERRORS: Headers Error</c>，退出码 **2**；
    ///   给了正确密码则列目录成功（退出码 0）。
    /// 所以"列不出来"本身就说明名字读不出来 —— 而正常可解的加密包不会被判成这一类。
    ///
    /// ⚠ 这组测试里最要紧的是**反向**那两条：解压 / 测试路径上同一段输出必须仍然判"密码错误"。
    /// 那两条路径靠 <c>WrongPassword</c> 驱动密码候选循环，判成加密头会让循环在第一个候选就断掉，
    /// 我们自己的内层 <c>-mhe</c> 分卷会因此解不开。
    /// </summary>
    public class EncryptedHeadersTests
    {
        /// <summary>26.01 实测的原始输出（把包名换成占位符，其余一字未改）。</summary>
        private const string ListFailureOutput = @"7-Zip 26.01 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-04-27

Scanning the drive for archives:
1 file, 206 bytes (1 KiB)

Listing archive: hdr.7z


Errors: 1

ERROR: hdr.7z : Cannot open encrypted archive. Wrong password?

ERRORS:
Headers Error
";

        // ---------------------------------------------------------------- 判定

        [Fact]
        public void 加密头包列目录失败_判为文件名已加密()
        {
            string errorType = SevenZipOutputParser.DetectSevenZipErrorType(
                2,
                ListFailureOutput,
                string.Empty,
                "hdr.7z",
                EngineOperation.List);

            Assert.Equal(SevenZipOutputParser.EncryptedHeadersErrorType, errorType);

            string status = SevenZipOutputParser.ErrorTypeToTaskStatus(errorType);

            Assert.Equal(StatusText.EncryptedHeaders, status);
            Assert.NotEqual(StatusText.WrongPassword, status);
            Assert.NotEqual(StatusText.Corrupted, status);
            Assert.NotEqual(StatusText.ExtractSuccess, status);
        }

        [Fact]
        public void 提示文案要说清是哪个开关_以及下一步做什么()
        {
            string message = SevenZipOutputParser.ErrorTypeToMessage(
                SevenZipOutputParser.EncryptedHeadersErrorType,
                ListFailureOutput);

            Assert.Contains("加密了文件名", message);
            Assert.Contains("-mhe", message);
            Assert.Contains("-hp", message);
            Assert.Contains("需要正确密码", message);
        }

        [Fact]
        public void 解压与测试路径上不许判成加密头_否则会打断密码候选循环()
        {
            /*
             * 这是本组最重要的反向用例。
             *
             * 解压循环只对 DetectedErrorType == "WrongPassword" 继续试下一个候选
             * （ExtractionCoordinator：非 WrongPassword 就 break）。加密头的包在**第一个候选**
             * （常见是空密码）上就会报同一段输出；若在这里判成"文件名已加密"，
             * 循环立刻断掉，后面那个正确密码根本没机会被试到 ——
             * 我们自己的内层 -mhe 分卷就会全部解不开。
             */
            Assert.Equal(
                "WrongPassword",
                SevenZipOutputParser.DetectSevenZipErrorType(2, ListFailureOutput, string.Empty, "hdr.7z", EngineOperation.Extract));

            Assert.Equal(
                "WrongPassword",
                SevenZipOutputParser.DetectSevenZipErrorType(2, ListFailureOutput, string.Empty, "hdr.7z", EngineOperation.Test));
        }

        [Fact]
        public void 认不出命令时不下加密头结论_退回原来的密码错误()
        {
            // 老调用方（不传 operation）行为不变：宁可报"密码错误"，也不越权下一个新结论。
            Assert.Equal(
                "WrongPassword",
                SevenZipOutputParser.DetectSevenZipErrorType(2, ListFailureOutput, string.Empty));

            Assert.False(SevenZipOutputParser.LooksLikeEncryptedHeaders(2, ListFailureOutput, string.Empty, null));
        }

        [Fact]
        public void 列目录成功时不算加密头_正常可解的加密包不被误判()
        {
            // 给了正确密码：26.01 实测列目录退出码 0。不能因为"这是个加密包"就判成加密头。
            Assert.False(SevenZipOutputParser.LooksLikeEncryptedHeaders(
                0,
                "Path = hdr.7z\nPath = a.txt\n",
                string.Empty,
                EngineOperation.List));

            Assert.Equal("None", SevenZipOutputParser.DetectSevenZipErrorType(
                0,
                "Path = hdr.7z\nPath = a.txt\n",
                string.Empty,
                "hdr.7z",
                EngineOperation.List));
        }

        [Fact]
        public void 只是密码错误而没有头部错误字样_不算加密头()
        {
            // 非固实包内部文件用了不同密码这类情形：输出里没有加密头 / 头部错误字样，保持原判。
            const string wrongPasswordOnly = "ERROR: Data Error in encrypted file. Wrong password?";

            Assert.Equal(
                "WrongPassword",
                SevenZipOutputParser.DetectSevenZipErrorType(2, wrongPasswordOnly, string.Empty, "a.7z", EngineOperation.List));
        }

        [Fact]
        public void 头部错误加密码字样也算_覆盖RAR的另一种说法()
        {
            const string rarStyle = "ERROR: Headers Error\nWrong password?";

            Assert.True(SevenZipOutputParser.LooksLikeEncryptedHeaders(2, rarStyle, string.Empty, EngineOperation.List));
        }

        // ---------------------------------------------------------------- 三处同改（AGENTS.md §7）

        [Fact]
        public void 新增状态_文件名已加密_三处都已接通()
        {
            // ① 文案：不许散落手写中文字面量。
            Assert.Equal("文件名已加密", StatusText.EncryptedHeaders);

            // ② 配色：要人看一眼，但**不是**已证实的失败 —— 警告色（与"部分完成"同色），绝不是成功色。
            var converter = new StatusToBrushConverter();

            Assert.Same(
                converter.WarningBrush,
                converter.Convert(StatusText.EncryptedHeaders, typeof(object), null!, null!));
            Assert.NotSame(
                converter.SuccessBrush,
                converter.Convert(StatusText.EncryptedHeaders, typeof(object), null!, null!));
            Assert.NotSame(
                converter.ErrorBrush,
                converter.Convert(StatusText.EncryptedHeaders, typeof(object), null!, null!));

            // ③ 统计与失败清单：计失败侧、不算成功、也不算"密码错误"，并且必须进失败清单。
            var service = new TaskSummaryService();
            var task = new ArchiveTask(@"C:\tmp\hdr.7z")
            {
                Status = StatusText.EncryptedHeaders,
                IsSelected = true,
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal
            };

            TaskSummary summary = service.BuildSummary(new[] { task });

            Assert.Equal(1, summary.OtherFailedCount);
            Assert.Equal(0, summary.ExtractSuccessCount);
            Assert.Equal(0, summary.PasswordErrorCount);
            Assert.Equal(0, summary.CorruptedCount);
            Assert.Equal(1, summary.FailedTotalCount);

            Assert.True(service.IsFailedStatus(StatusText.EncryptedHeaders));
            Assert.True(service.IsFailedOrUnknownTask(task));
            Assert.Contains(StatusText.EncryptedHeaders, service.BuildFailedListText(new[] { task }));
        }

        [Fact]
        public void 加密头任务不会被分到成功桶里()
        {
            var service = new TaskSummaryService();

            Assert.Equal(
                SummaryBucket.OtherFailed,
                TaskSummaryService.ClassifyOutcome(new ArchiveTask(@"C:\t\hdr.7z") { Status = StatusText.EncryptedHeaders }));

            Assert.NotEqual(
                SummaryBucket.ExtractSuccess,
                TaskSummaryService.ClassifyOutcome(new ArchiveTask(@"C:\t\hdr.7z") { Status = StatusText.EncryptedHeaders }));
        }

        // ---------------------------------------------------------------- 真实 7z 端到端（造一个 -mhe 包自己验）

        /// <summary>测试用的合成密码（占位符，不是任何真实密码；不变量 5）。</summary>
        private const string SamplePassword = "Demo#Pass1";

        [SevenZipFact]
        public async Task 真实7z_加密头的包不给密码列不出来_给了正确密码列得出来()
        {
            /*
             * 这条是"别把正常加密包误判"的现场证据：
             * 同一个 -mhe 包，错误密码 ⇒ 判"文件名已加密"（内容无法判定）；
             * 正确密码 ⇒ 列目录成功，**不**判任何错误。
             * 我们自己的内层分卷就是用 -mhe 加的密，正常可解 —— 这个区别必须是可验证的。
             */
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            string directory = Path.Combine(Path.GetTempPath(), "af-mhe-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);

            try
            {
                string sourceFile = Path.Combine(directory, "a.txt");
                string archive = Path.Combine(directory, "hdr.7z");

                File.WriteAllText(sourceFile, "hello");

                RunSevenZip(sevenZip, new[]
                {
                    "a", "-t7z", "-mhe=on", "-p" + SamplePassword, archive, sourceFile
                });

                var engine = new SevenZipEngine();

                // ① 没给密码：7-Zip 连条目名都读不出来 → 明确的"文件名已加密"，不是"密码错误/文件损坏"。
                ArchiveListResult withoutPassword = await engine.ListAsync(ArchiveRequest.For(archive, string.Empty));

                Assert.False(withoutPassword.Success);
                Assert.Equal(SevenZipOutputParser.EncryptedHeadersErrorType, withoutPassword.ErrorType);
                Assert.Equal(StatusText.EncryptedHeaders, SevenZipOutputParser.ErrorTypeToTaskStatus(withoutPassword.ErrorType));
                Assert.Contains("加密了文件名", withoutPassword.Message);

                // ② 给了错密码：同样列不出来 —— 结论仍然是"内容无法判定"，用户该做的是补正确密码。
                ArchiveListResult wrongPassword = await engine.ListAsync(ArchiveRequest.For(archive, "wrong-password"));

                Assert.False(wrongPassword.Success);
                Assert.Equal(SevenZipOutputParser.EncryptedHeadersErrorType, wrongPassword.ErrorType);

                // ③ 给了正确密码：列目录必须成功（正常可解的加密包不许被误判）。
                ArchiveListResult correct = await engine.ListAsync(ArchiveRequest.For(archive, SamplePassword));

                Assert.True(correct.Success);
                Assert.Contains(correct.Entries, e => e.Path.EndsWith("a.txt", StringComparison.Ordinal));
            }
            finally
            {
                TryDeleteDirectory(directory);
            }
        }

        /// <summary>调真实 7z.exe。用 ArgumentList 安全传参，**不拼命令行字符串**（不变量 10）。</summary>
        private static void RunSevenZip(string sevenZipPath, string[] arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(60_000), "7z.exe 超时未退出");
            Assert.Equal(0, process.ExitCode);
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // 临时目录删不掉不影响测试结论。
            }
        }
    }
}
