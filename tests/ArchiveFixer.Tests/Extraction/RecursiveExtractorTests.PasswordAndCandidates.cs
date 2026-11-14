using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    public partial class RecursiveExtractorTests
    {
        // ────────────────────────────── 密码 ──────────────────────────────

        [SevenZipFact]
        public async Task 密码_先给错再给对能解开且报告里没有明文密码()
        {
            RequireSevenZip();

            string outer = BuildEncryptedInnerPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass", TestPass123 });

            string output = Path.Combine(_root, "out", "pass-ok");
            RecursionResult result = await extractor.ExtractAsync(
                task,
                output,
                null,
                CancellationToken.None);

            Assert.True(result.Completed, result.Summary);

            // 内层包 `secret.7z` 在原位置留下 `secret\`（就地替换），内容物放进去。
            Assert.True(File.Exists(Path.Combine(output, "secret", "secret.txt")), result.Summary);

            // 内层那一层的密码标记只能是脱敏值，且任何报告字段都不能出现明文（规则 9）。
            RecursionLayerReport innerLayer = result.Layers.Single(layer => layer.Depth == 1);
            Assert.Equal("******", innerLayer.UsedPasswordMasked);

            string everything = string.Join(
                "|",
                result.Layers.Select(layer => $"{layer.Status}|{layer.Message}|{layer.UsedPasswordMasked}"))
                + "|" + result.Summary;

            Assert.DoesNotContain(TestPass123, everything, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-pass", everything, StringComparison.Ordinal);

            // 外层是没加密的 zip：第一层走的是空密码。
            Assert.Equal("空密码", result.Layers.Single(layer => layer.Depth == 0).UsedPasswordMasked);
        }

        [SevenZipFact]
        public async Task 密码_候选全试完仍失败报WrongPassword()
        {
            RequireSevenZip();

            /*
             * ⚠ 内层包刻意**不用** `-mhe`（头不加密 ⇒ 清单列得出来）：`-mhe` 的包连清单都读不出来，
             * 2026-10-05 起收尾落的是「文件名已加密」那一档（与单层路径对齐，见
             * `RecursionGateAndHintFixesTests` 那三条），轮不到这里要验的"密码错误 / 达到上限"。
             *
             * 本用例要钉的是：**候选全试完了都不对 ⇒ 密码错误**（不是「达到尝试上限」）。
             */
            string outer = BuildEncryptedInnerPackage(encryptFileNames: false);

            // 两个候选都错 + 上限 8 → 候选被试完，属于"密码错误"，不是"达到尝试上限"。
            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass" },
                new RecursionLimits { MaxPasswordAttemptsPerLayer = 8 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "pass-wrong"),
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
            Assert.True(result.PartiallyCompleted, result.Summary);
            Assert.False(result.Completed);
        }

        /// <summary>
        /// **报错取"走得最远的那一次"**（用户 2026-09-30 真机）：7z `-mhe` 四卷包里，正确密码那一次
        /// 真解出 17.7 GiB 才失败，紧跟其后的错密码候选 50 毫秒就被顶回来 —— 老口径收尾用
        /// `lastFailure`，报出来的就是**最后那个错候选**的原话，真原因（数据校验不过）被吃掉。
        ///
        /// <para>判据只看事实：这一次尝试在产物目录里留下过多少字节。</para>
        ///
        /// <para><b>红检</b>：把 `informativeFailure` 那两行撤掉（收尾改回 `lastFailure`）→
        /// 本用例立刻变红（Message 变成 <c>Cannot open encrypted archive. Wrong password?</c>）。</para>
        /// </summary>
        [Fact]
        public async Task 候选循环_报错取走得最远的那一次_不是最后一次()
        {
            string source = Path.Combine(_root, "widest-failure-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();

            engine.OnExtractAsync = (request, options) =>
            {
                if (string.Equals(request.Password, "first", StringComparison.Ordinal))
                {
                    // 这一次"走得很远"：真的留下了产物，然后才失败（数据校验不过）。
                    // 输出路径由本测试自己发请求时给全，`!` 只为消掉 CS8604（`ExtractRequest.OutputPath` 是可空字符串），不改语义。
                    Directory.CreateDirectory(request.OutputPath!);
                    File.WriteAllBytes(Path.Combine(request.OutputPath!, "partial.bin"), new byte[4096]);

                    return Task.FromResult(new ArchiveOperationResult
                    {
                        Success = false,
                        Status = StatusText.ExtractFailed,
                        Message = "CRC Failed in encrypted file. Wrong password? : partial.bin",
                        DetectedErrorType = "WrongPassword",
                        EngineId = engine.Id
                    });
                }

                // 后面几个候选连门都没进去，一个字节都没留下。
                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.WrongPassword,
                    Message = "Cannot open encrypted archive. Wrong password?",
                    DetectedErrorType = "WrongPassword",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "first", "second", "third" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-widest-failure"),
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
            Assert.Contains("CRC Failed", result.Layers[0].Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Cannot open encrypted archive", result.Layers[0].Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// **递归那条路也要认「密码已被这一趟解压证实」**（用户 2026-10-01 真机 `giu910` 重跑一次又白等 13 分钟）。
        ///
        /// <para>现场：那一档修复先只接在**单层**候选循环上（`ExtractionCoordinator`），
        /// 而真机 `giu910` 走的是**递归**那条（`SingleChain`）—— 于是候选 3 把 17.7 GiB 解到 98%、
        /// 只死在一个 mp4 的 CRC 上，程序照样把它当成"这个密码候选不对"，回去把候选 4..10 又试一遍，
        /// 13 分钟的产物全扔、结论写「密码错误」。用户原话：
        /// 「**为什么试了密码之后再去试一次，我说过要试密码的话要在最开始的时候**」。</para>
        ///
        /// <para><b>红检</b>：把 <c>RecursiveExtractor</c> 里那一档（<see cref="ProducedContentGate"/> 那个
        /// <c>if</c>）撤掉 → 本用例立刻变红（只试了 1 个候选变成 3 个候选全试完、结论从"压缩包损坏"
        /// 变回"密码错误"）。</para>
        /// </summary>
        [Fact]
        public async Task 递归候选循环_解出了大半个包之后失败_不再试下一个候选()
        {
            string source = Path.Combine(_root, "crc-proven-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();
            int extractCalls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                extractCalls++;

                // 第一趟（正确密码）：真解出两个非空文件才在一个文件上 CRC 失败 —— 真机那一单的形状。
                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "good-1.mp4"), new byte[8192]);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "good-2.mp4"), new byte[4096]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.ExtractFailed,
                    Message = "Sub items Errors: 1 | ERROR: CRC Failed in encrypted file. Wrong password? : bad.mp4",
                    DetectedErrorType = "WrongPassword",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "right-password", "wrong-1", "wrong-2" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-crc-proven"),
                null,
                CancellationToken.None);

            // ⛔ 一个候选就够：密码已经被这一趟解压本身证明了，再试下去纯粹是浪费用户的时间。
            Assert.Equal(1, extractCalls);

            // 结论必须落在"数据坏了"，⛔ 不是「密码错误」（否则用户会拿着密码本白核对半天）。
            Assert.Equal(RecursionStopReason.Corrupted, result.StopReason);
            Assert.Contains("密码已经证实是对的", result.Layers[0].Message, StringComparison.Ordinal);
            Assert.Contains("CRC Failed", result.Layers[0].Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 对照：**只解出一个文件**时照老口径继续试下一个候选（保守边界，⛔ 不许放宽）。
        ///
        /// <para>「错密码」与「密码对、文件坏」在这一档上分不开 —— 真机那 10 个错候选里就有留下一个文件的。
        /// 所以门槛是"至少两个非空文件"，一个文件必须继续试。</para>
        ///
        /// <para>⚠ 2026-10-21 订正：**「结果相同 ⇒ 提前停」只对"引擎已经接受密码"那一支成立**
        /// （引擎说成功、产物没通过校验）。错密码这一支**不许**用它停：加密包的错密码也会在产物目录里
        /// 留下一模一样的半截数据，而正确密码排在后面时产物完全不同 —— 实测
        /// `Item45LogAndPasswordTests.真7z_先试错两个候选再成功_成功档照样只留一行` 因此在全量里变红
        /// （候选 3 才是对的，循环在候选 2 就停了）。⇒ 本条恢复"三个候选都试"。</para>
        /// </summary>
        [Fact]
        public async Task 递归候选循环_只解出一个文件时_仍按老口径继续试下一个候选()
        {
            string source = Path.Combine(_root, "one-file-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();
            int extractCalls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                extractCalls++;

                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "only-one.bin"), new byte[4096]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.WrongPassword,
                    Message = "Cannot open encrypted archive. Wrong password?",
                    DetectedErrorType = "WrongPassword",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "c1", "c2", "c3" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-one-file"),
                null,
                CancellationToken.None);

            // 三个候选全试完（错密码这一支不许因为"结果一样"提前停）。
            Assert.Equal(3, extractCalls);
            Assert.Equal(RecursionStopReason.WrongPassword, result.StopReason);
        }

        /// <summary>
        /// 两义那一档（引擎说"密码可能不对、也可能数据坏了"）在**真解出了内容**之后当场收敛成数据损坏 ——
        /// 密码已经不需要再猜了（用户 2026-10-01 真机 `giu910` 走的就是 7-Zip 那句
        /// `CRC Failed in encrypted file. Wrong password?`）。
        /// </summary>
        [Fact]
        public async Task 递归候选循环_两义那一档解出了内容之后_当场收敛成数据损坏()
        {
            string source = Path.Combine(_root, "ambiguous-proven-source.rar");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            var engine = new FakeEngine();
            int extractCalls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                extractCalls++;

                Directory.CreateDirectory(request.OutputPath!);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "a.bin"), new byte[1024]);
                File.WriteAllBytes(Path.Combine(request.OutputPath!, "b.bin"), new byte[2048]);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.PasswordOrCorrupted,
                    Message = "在加密文件 X 里校验和错误。文件已损坏或密码错误。",
                    DetectedErrorType = "PasswordOrCorrupted",
                    EngineId = engine.Id
                });
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { "p1", "p2", "p3" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-ambiguous-proven"),
                null,
                CancellationToken.None);

            Assert.Equal(1, extractCalls);
            Assert.Equal(RecursionStopReason.Corrupted, result.StopReason);
            Assert.Contains("密码已经证实是对的", result.Layers[0].Message, StringComparison.Ordinal);
        }

        [SevenZipFact]
        public async Task 密码_候选还有剩余时报PasswordAttemptsExceeded()
        {
            RequireSevenZip();

            /*
             * ⚠ 这个包**保持** `-mhe`（连文件名一起加密）：正因为它连清单都读不出来，
             * 第 1 层才会"一个候选都没成功"就撞上"每层只试 1 个"的上限 —— 收尾必须是
             * 「达到密码尝试上限」（候选还有剩、再试就能开），⛔ **不许**被「文件名已加密」那一档盖掉
             * （判据只覆盖"密码错误"那一档，见 RecursiveExtractor 里那段说明）。
             */
            string outer = BuildEncryptedInnerPackage();

            /*
             * 三个候选（空、错、对），但每层只允许试 1 个。
             * 第 0 层用空密码就成功了，所以"试 1 个"的限制落在第 1 层：它先是跳过空密码
             * （`-mhe` 那一档：列目录报加密头 ⇒ 空密码必然白跑），再试了第一个非空候选就停手，
             * 而候选还有剩 —— 这正是"达到密码尝试上限"，与"密码错误"必须区分开（AGENTS.md §9.2）。
             */
            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass", TestPass123 },
                new RecursionLimits { MaxPasswordAttemptsPerLayer = 1 });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "pass-limit"),
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.PasswordAttemptsExceeded, result.StopReason);
            Assert.True(result.PartiallyCompleted);
            Assert.Contains("尝试次数上限", result.Summary, StringComparison.Ordinal);
        }

        // ────────────────────────────── 取消 ──────────────────────────────

        [SevenZipFact]
        public async Task 已取消的token_返回UserCancelled且不抛异常()
        {
            RequireSevenZip();

            string source = BuildSourceDir(("readme.txt", "取消用例\n"));
            string archive = Path.Combine(_root, "cancel.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "*"));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = new ArchiveTask(archive);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty });

            // 这里刻意**不包 try/catch**：一旦实现改成"取消就抛"，本用例会直接红掉。
            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "cancelled"),
                null,
                cts.Token);

            Assert.Equal(RecursionStopReason.UserCancelled, result.StopReason);
            Assert.False(result.Completed);
            Assert.False(result.PartiallyCompleted);

            // 一层都没解出来，所以不算"部分完成"；但工作区仍然保留着，方便排查与重试（规则 8）。
            Assert.True(Directory.Exists(result.FinalOutputPath), result.Summary);
            Assert.Contains("工作区", result.Summary, StringComparison.Ordinal);
        }
    }
}
