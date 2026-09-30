using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **引擎原话**：结论里带几行、日志里带几行、带的是哪几行（用户 2026-09-27）。
    ///
    /// <para>他点名的三件事，这里逐条钉住：</para>
    /// <list type="number">
    /// <item><description><b>结论不能只带一行</b>：7-Zip 打出来的第一行常常只是
    /// <c>ERROR: &lt;路径&gt;</c>，真正的原因句在后面的行里（真机日志实测）；
    /// 旧实现"命中一行就 return"，于是原因句被丢掉。</description></item>
    /// <item><description><b>日志里要有引擎原话</b>：7-Zip / UnRAR 的输出过去只被"解析"、
    /// 从不落日志 —— 真机那次 13 分钟白跑，日志里连一句原话都没有。</description></item>
    /// <item><description><b>⛔ 不许把整段 stdout 倾泻进日志</b>：只挑带 ERROR / Cannot / Wrong /
    /// CRC / Data Error / Missing 等关键字的行，最多 3 行、每行截断 300 字符，并且**必须脱敏**。</description></item>
    /// </list>
    /// </summary>
    public class EngineOutputLoggingTests
    {
        /// <summary>
        /// **红检现场**（改回"命中一行就 return"这一条立刻变红）：
        /// 第一行是 <c>ERROR: &lt;路径&gt;</c>、第二行才是原因句 —— 结论里必须**两句都在**。
        /// </summary>
        [Fact]
        public void 结论_第一行只是ERROR路径时_原因句必须在结果里()
        {
            const string output =
                "ERROR: C:\\t\\giu.7z.001\n" +
                "Cannot open encrypted archive. Wrong password?\n" +
                "ERRORS: Headers Error\n";

            string message = SevenZipOutputParser.ExtractImportantMessage(output);

            // ① 第一行（只有路径）在
            Assert.Contains("ERROR: C:\\t\\giu.7z.001", message, StringComparison.Ordinal);

            // ② **原因句也在** —— 这一条就是红检：旧实现 return line 时它直接不在结果里
            Assert.Contains("Cannot open encrypted archive", message, StringComparison.Ordinal);

            // ③ 多行之间用同一个分隔符连起来（不是拼成一坨）
            Assert.Contains(SevenZipOutputParser.ImportantMessageSeparator, message, StringComparison.Ordinal);

            // ④ 上限 3 行：这里刚好 3 行，多的不许进来
            Assert.Equal(3, message.Split(SevenZipOutputParser.ImportantMessageSeparator).Length);
        }

        [Fact]
        public void 结论_最多三行_按重要度排序_且去重()
        {
            const string output =
                "Scanning the drive for archives\n" +
                "ERRORS: Headers Error\n" +
                "Cannot open encrypted archive. Wrong password?\n" +
                "ERROR: C:\\t\\a.7z.001\n" +
                "WARNING: No more files\n" +
                "Operation completed with errors\n";

            string message = SevenZipOutputParser.ExtractImportantMessage(output);
            string[] lines = message.Split(SevenZipOutputParser.ImportantMessageSeparator);

            // 三行封顶
            Assert.Equal(SevenZipOutputParser.ImportantMessageMaxLines, lines.Length);

            // ERROR 行排在最前面（三档里的第一档）
            Assert.StartsWith("ERRORS: Headers Error", message, StringComparison.Ordinal);

            // 一行都不重复
            Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>
        /// **原因句保底**：`ERROR` 行多到占满三格时，原因句仍然必须在结果里 ——
        /// 这正是"结论里必须看得见原因"这条要求的边界（多文件包解压失败时 7-Zip
        /// 会为**每一个**失败文件各打一条 `ERROR:`，三条是很容易到的）。
        ///
        /// <para><b>红检</b>：把 <c>EngineOutputKeywords.PickImportantLines</c> 末尾那段
        /// "原因句保底"撤掉（改回"按三档取满就停"）→ 本用例当场红，原因是关键词句整句不在结果里。</para>
        /// </summary>
        [Fact]
        public void 结论_ERROR行占满三格时_原因句仍然必须在()
        {
            const string output =
                "ERROR: C:\\t\\a.7z.001\n" +
                "ERROR: C:\\t\\b.bin\n" +
                "ERROR: C:\\t\\c.bin\n" +
                "Cannot open encrypted archive. Wrong password?\n";

            string message = SevenZipOutputParser.ExtractImportantMessage(output);

            // 三行封顶不变（⛔ 不许因为保底就把上限撑破）
            Assert.Equal(
                SevenZipOutputParser.ImportantMessageMaxLines,
                message.Split(SevenZipOutputParser.ImportantMessageSeparator).Length);

            // ERROR 行仍然排在最前
            Assert.StartsWith("ERROR: C:\\t\\a.7z.001", message, StringComparison.Ordinal);

            // 原因句必须看得见 —— 这一条就是红检
            Assert.Contains("Cannot open encrypted archive", message, StringComparison.Ordinal);
        }

        [Fact]
        public void 结论_一行关键字都不命中时_退回现有行为()
        {
            // 7-Zip 侧的新兜底是"第一行"（比旧代码的"最后一行"更贴结论，见方法注释）。
            Assert.Equal(
                "first line",
                SevenZipOutputParser.ExtractImportantMessage("first line\nsecond line"));

            // UnRAR 侧的结论常在最末尾 —— 老口径，别动。
            Assert.Equal(
                "second line",
                UnRarOutputParser.ExtractImportantMessage("first line\nsecond line"));

            Assert.Equal(string.Empty, SevenZipOutputParser.ExtractImportantMessage(string.Empty));
            Assert.Equal(string.Empty, UnRarOutputParser.ExtractImportantMessage(null));
        }

        /// <summary>结论里的原话**必须脱敏**（不变量 5：密码永远不许进日志 / 报告 / 消息）。</summary>
        [Fact]
        public void 结论_原话里的明文密码会被脱敏()
        {
            string message = SevenZipOutputParser.ExtractImportantMessage(
                "ERROR: Cannot open encrypted archive -pSuperSecret123\n" +
                "Wrong password\n");

            Assert.DoesNotContain("SuperSecret123", message, StringComparison.Ordinal);
            Assert.Contains("-p******", message, StringComparison.Ordinal);
        }

        /// <summary>UnRAR 侧与 7-Zip 侧同一份规则：多行、排序、上限。</summary>
        [Fact]
        public void UnRAR结论_同样多行且原因句在结果里()
        {
            const string output =
                "ERROR: C:\\t\\vol.part1.rar\n" +
                "Incorrect password for C:\\t\\vol.part1.rar\n" +
                "Total errors: 1\n";

            string message = UnRarOutputParser.ExtractImportantMessage(output);

            Assert.Contains("ERROR: C:\\t\\vol.part1.rar", message, StringComparison.Ordinal);
            Assert.Contains("Incorrect password", message, StringComparison.Ordinal);
            Assert.Contains(UnRarOutputParser.ImportantMessageSeparator, message, StringComparison.Ordinal);
        }

        // ================================================================ 日志出口

        /// <summary>
        /// **日志里那几行的唯一出口**：带 ERROR 关键字的行被挑出来、加上引擎原话前缀与级别。
        ///
        /// <para>红检：把 <see cref="EngineOutputLog.LogFailure"/> 的调用撤掉（或让它 return 空）
        /// → 本用例立刻变红。</para>
        /// </summary>
        [Fact]
        public void 失败的引擎调用_把原话写进日志_级别ERROR()
        {
            var log = new List<(string Level, string Message)>();

            ArchiveOperationResult result = new()
            {
                Success = false,
                ExitCode = 2,
                StandardOutput = "ERROR: C:\\t\\a.7z\nCannot open encrypted archive. Wrong password?\n",
                Status = StatusText.WrongPassword,
                DetectedErrorType = "WrongPassword",
                EngineId = EngineIds.SevenZip
            };

            EngineOutputLog.LogFailure((level, message) => log.Add((level, message)), "a.7z", result);

            (string level, string message) = Assert.Single(log);

            Assert.Equal("ERROR", level);
            Assert.Contains("7-Zip 原话", message, StringComparison.Ordinal);
            Assert.Contains("Cannot open encrypted archive", message, StringComparison.Ordinal);
        }

        /// <summary>"部分完成"是 WARN（有东西解出来了，不是全盘失败）—— 级别按机器终态判。</summary>
        [Fact]
        public void 部分完成的引擎调用_级别是WARN()
        {
            var log = new List<(string Level, string Message)>();

            ArchiveOperationResult result = new()
            {
                Success = false,
                ExitCode = 1,
                StandardOutput = "ERROR: CRC Failed in encrypted file\n",
                Status = StatusText.PartiallyCompleted,
                DetectedErrorType = "NonFatalError",
                EngineId = EngineIds.SevenZip
            };

            EngineOutputLog.LogFailure((level, message) => log.Add((level, message)), "a.7z", result);

            Assert.Equal("WARN", Assert.Single(log).Level);
        }

        /// <summary>UnRAR 那一侧的标签跟着引擎走（同一件事不许有两种说法）。</summary>
        [Fact]
        public void 引擎原话的标签按引擎走()
        {
            ArchiveOperationResult unrar = new() { EngineId = EngineIds.WinRar };
            ArchiveOperationResult sevenZip = new() { EngineId = EngineIds.SevenZip };
            ArchiveOperationResult unknown = new();

            Assert.Equal("UnRAR 原话", unrar.EngineOutputLabel);
            Assert.Equal("7-Zip 原话", sevenZip.EngineOutputLabel);
            Assert.Equal("引擎原话", unknown.EngineOutputLabel);
        }

        /// <summary>
        /// **⛔ 整段 stdout 不许进日志**：一份"93% 进度 + 名字列表"的长输出里，
        /// 只留带关键字的行（这份输出里只有 2 行带关键字），且**每行 ≤300 字符**。
        /// </summary>
        [Fact]
        public void 只挑关键行_不把整段输出倒进日志()
        {
            var lines = new List<string>
            {
                "Scanning the drive for archives",
                "Extracting archive: C:\\t\\big.7z"
            };

            for (int i = 0; i < 200; i++)
            {
                lines.Add($"  {i}% {i}\\200 {new string('x', 40)}.bin");
            }

            lines.Add("ERROR: " + new string('y', 500));
            lines.Add("Cannot open encrypted archive. Wrong password?");

            IReadOnlyList<string> picked = ArchiveOperationResult.BuildKeyOutputLines(
                string.Join(Environment.NewLine, lines),
                EngineIds.SevenZip);

            // 只有那两行带关键字 —— 200 行进度与文件名列表一行都不许进来
            Assert.Equal(2, picked.Count);

            foreach (string line in picked)
            {
                Assert.True(
                    line.Length <= ArchiveOperationResult.KeyOutputMaxLineLength + 1,
                    $"原话那一行太长（{line.Length} 字符）：{line}");
            }

            Assert.DoesNotContain(picked, line => line.Contains("Scanning the drive", StringComparison.Ordinal));
            Assert.DoesNotContain(picked, line => line.Contains("Extracting archive", StringComparison.Ordinal));
            Assert.Contains(picked, line => line.Contains("Cannot open encrypted archive", StringComparison.Ordinal));
        }

        // ================================================================ 参数摘要

        /// <summary>
        /// 详细日志里的"引擎调用"摘要：**只写文件名、密码恒为 <c>-p******</c>**、输出目录不写进去。
        ///
        /// <para>隐私红线（§8）：完整路径不进日志；不变量 5：明文密码绝不进日志。</para>
        /// </summary>
        [Fact]
        public void 参数摘要_脱敏且只写文件名()
        {
            string summary = SevenZipProcessRunner.BuildCommandSummary(new[]
            {
                "x",
                "-bsp1",
                "-sccUTF-8",
                @"C:\Users\someone\Desktop\giu.7z.001",
                @"-oC:\Users\someone\Desktop\out",
                "-y",
                "-aos",
                "-pSuperSecret123"
            });

            Assert.StartsWith("7z x ", summary, StringComparison.Ordinal);
            Assert.Contains("-sccUTF-8", summary, StringComparison.Ordinal);
            Assert.Contains("giu.7z.001", summary, StringComparison.Ordinal);

            // ⛔ 明文密码 / 用户名 / 输出目录一个都不许出现
            Assert.DoesNotContain("SuperSecret123", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("someone", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("Desktop", summary, StringComparison.Ordinal);

            Assert.Contains("-p******", summary, StringComparison.Ordinal);
        }

        /// <summary>UnRAR 的参数摘要同一口径（<c>-hp</c> 也要脱敏）。</summary>
        [Fact]
        public void UnRAR参数摘要_同一口径()
        {
            string summary = UnRarProcessRunner.BuildCommandSummary(new[]
            {
                "x",
                "-y",
                @"C:\t\vol.part1.rar",
                @"C:\t\out\",
                "-hpAnotherSecret"
            });

            Assert.StartsWith("unrar x ", summary, StringComparison.Ordinal);
            Assert.Contains("vol.part1.rar", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("AnotherSecret", summary, StringComparison.Ordinal);

            // ⛔ 输出目录那条绝对路径不许进摘要（§8），脱敏后只留 `-hp******`
            Assert.DoesNotContain(@"C:\t\out", summary, StringComparison.Ordinal);
            Assert.Contains("-hp******", summary, StringComparison.Ordinal);
        }

        /// <summary>详细档写"参数摘要 + 原话"两行（成功也写）；空结果一个字都不写。</summary>
        [Fact]
        public void 详细档_参数摘要与原话都写()
        {
            var log = new List<(string Level, string Message)>();

            ArchiveOperationResult result = new()
            {
                Success = true,
                ExitCode = 0,
                StandardOutput = "Everything is Ok\n",
                Status = StatusText.ExtractSuccess,
                DetectedErrorType = "None",
                EngineId = EngineIds.SevenZip,
                CommandSummary = "7z x -sccUTF-8 a.7z -p******"
            };

            EngineOutputLog.LogVerbose((level, message) => log.Add((level, message)), "a.7z", result);

            // 参数摘要那一行一定在
            Assert.Contains(log, entry => entry.Message.Contains(StatusText.EngineCommandSummaryPrefix, StringComparison.Ordinal));

            // 成功时一行原话都挑不出来（Everything is Ok 不含关键字）→ 只剩参数摘要那一行
            Assert.Single(log);
            Assert.Equal("INFO", log[0].Level);

            // 没有结果对象时什么都不写（日志出口自己也不抛）
            var empty = new List<(string Level, string Message)>();
            EngineOutputLog.LogFailure((level, message) => empty.Add((level, message)), "a.7z", null);
            EngineOutputLog.LogVerbose((level, message) => empty.Add((level, message)), "a.7z", null);

            Assert.Empty(empty);
        }

        /// <summary>原话里夹着密码时，日志那一行也必须是脱敏后的（第二道防线）。</summary>
        [Fact]
        public void 日志那一行_密码同样脱敏()
        {
            var log = new List<(string Level, string Message)>();

            ArchiveOperationResult result = new()
            {
                Success = false,
                StandardOutput = "ERROR: Cannot open\npassword=TopSecret999\n",
                Status = StatusText.WrongPassword,
                DetectedErrorType = "WrongPassword",
                EngineId = EngineIds.SevenZip
            };

            EngineOutputLog.LogFailure((level, message) => log.Add((level, message)), "a.7z", result);

            string text = string.Join(" | ", log.Select(entry => entry.Message));

            Assert.DoesNotContain("TopSecret999", text, StringComparison.Ordinal);
        }

        /// <summary>脱敏器本身是唯一出口（这里只是把它钉在"引擎原话"这条路上）。</summary>
        [Fact]
        public void 引擎原话脱敏走的是统一脱敏器()
        {
            Assert.DoesNotContain(
                "hunter2",
                PasswordMasker.Sanitize("ERROR: Wrong password -phunter2"),
                StringComparison.Ordinal);
        }
    }
}
