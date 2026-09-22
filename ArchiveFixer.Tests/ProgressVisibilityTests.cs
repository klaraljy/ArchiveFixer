using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
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
    /// 进度可见（WinRAR 参考 §3 第 1 条 / 用户"卡死"抱怨）的回归测试。
    ///
    /// <para>
    /// 覆盖四件事，每一件都对应一条明确的判据：
    /// </para>
    /// <list type="number">
    /// <item><description><b>解析</b>：用**真跑命令抓下来的原始字节**（7-Zip 26.03 / UnRAR 7.23）
    /// 写用例，涵盖百分比行、条目名含空格与中文、空行、无进度行；</description></item>
    /// <item><description><b>节流</b>：高频进度序列的上报次数必须**远小于**输入条数；</description></item>
    /// <item><description><b>线程与生命周期</b>：无 UI 宿主不抛异常；取消 / 进程退出后不再有回调；</description></item>
    /// <item><description><b>卡住检测</b>：长时间无输出时给出 WARN 与提示，且**绝不杀进程**。</description></item>
    /// </list>
    ///
    /// <para>
    /// 走真 <see cref="MainViewModel"/> + 真解压管线的用例与 ExtractionPipelineFixTests 同一组：
    /// MainViewModel 的构造会写进程级静态，必须串行并在装配后还原。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ProgressVisibilityTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public ProgressVisibilityTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerProgress", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ================================================================ 1 解析：真实 7-Zip 输出

        /// <summary>
        /// 7-Zip 26.03 x64 的进度行（2026-09-22 用 250MB 的包跑
        /// <c>7z x -bsp1 -sccUTF-8 sample.7z</c> 抓下来的原始字节，只有路径换成了占位）。
        ///
        /// 格式对得上 7-Zip 自己的 <c>CPercentPrinter::Print</c>：
        /// 百分比（宽度 4，<c>%</c> 或 <c>M</c>）→ 条目序号（可选）→ 命令（可选）→ 条目名（可选）。
        /// </summary>
        [Theory]
        [InlineData("  0%", 0, "")]
        [InlineData(" 67% 8 - payload 08 数据.bin", 67, "payload 08 数据.bin")]
        [InlineData("  2% + payload 01 数据.bin", 2, "payload 01 数据.bin")]
        [InlineData("100% 15 - 说明 中文 条目.txt", 100, "说明 中文 条目.txt")]
        [InlineData("  5% 1 + readme 空格.txt", 5, "readme 空格.txt")]
        [InlineData(" 63% 8 - payload 08 数据.bin                              ", 63, "payload 08 数据.bin")]
        public void 七Zip进度行_能解析出百分比与条目名(string line, int expectedPercent, string expectedEntry)
        {
            ArchiveProgress? progress = SevenZipProgressParser.TryParse(line);

            Assert.NotNull(progress);
            Assert.Equal(expectedPercent, progress!.Percent);
            Assert.Equal(expectedEntry, progress.CurrentEntry);
            Assert.False(progress.IsIndeterminate);
        }

        [Fact]
        public void 七Zip扫描行_只报在处理_不编造百分比()
        {
            // 实测原样："  0M Scan E:\...\src\" —— 总大小未知时 7-Zip 用兆字节口径的扫描行。
            ArchiveProgress? progress = SevenZipProgressParser.TryParse(@"  0M Scan C:\samples\src\");

            Assert.NotNull(progress);
            Assert.True(progress!.IsIndeterminate);
            Assert.Equal(ArchiveProgress.UnknownPercent, progress.Percent);
            Assert.Equal(@"Scan C:\samples\src\", progress.CurrentEntry);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("                                                               ")]
        [InlineData("Everything is Ok")]
        [InlineData("Path = C:\\samples\\sample.7z")]
        [InlineData("Type = 7z")]
        [InlineData("1 file, 251674592 bytes (241 MiB)")]
        [InlineData("----------")]
        [InlineData("Files: 15")]
        [InlineData("7-Zip 26.03 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-09-03")]
        [InlineData("ERROR: Wrong password : payload 01.bin")]
        public void 七Zip非进度行_一律不认(string line)
        {
            Assert.Null(SevenZipProgressParser.TryParse(line));
        }

        // ================================================================ 2 解析：真实 UnRAR 输出

        /// <summary>
        /// UnRAR 7.23 x64 的进度片段（2026-09-22 跑 <c>unrar x</c> 抓下来的原始字节）。
        /// 原始形态是**同一行内用退格回写**：<c>…payload.bin     \b\b\b\b  1%\b\b\b\b  3%…</c>，
        /// 所以这里按"被 <c>\b</c> 切开之后"的片段喂进来。
        /// </summary>
        [Theory]
        [InlineData("  1%", 1)]
        [InlineData(" 11%", 11)]
        [InlineData("100%", 100)]
        public void UnRAR百分比片段_能解析(string fragment, int expected)
        {
            ArchiveProgress? progress = UnRarProgressParser.TryParse(fragment);

            Assert.NotNull(progress);
            Assert.Equal(expected, progress!.Percent);
        }

        [Theory]
        [InlineData(@"Extracting  C:\samples\out\payload 01 数据.bin     ", "payload 01 数据.bin")]
        [InlineData(@"Testing     payload 02 数据.bin                                            ", "payload 02 数据.bin")]
        [InlineData(@"Extracting  C:\samples\out\sub dir\inner file.txt", "inner file.txt")]
        public void UnRAR条目片段_能解析出条目名(string fragment, string expectedEntry)
        {
            ArchiveProgress? progress = UnRarProgressParser.TryParse(fragment);

            Assert.NotNull(progress);
            Assert.Equal(expectedEntry, progress!.CurrentEntry);
            Assert.True(progress.IsIndeterminate); // 这一条只说"在处理谁"，没说百分比
        }

        [Theory]
        [InlineData("  OK ")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("UNRAR 7.23 x64 freeware      Copyright (c) 1993-2026 Alexander Roshal")]
        [InlineData(@"Extracting from C:\samples\sample.rar")]
        [InlineData(@"Cannot find volume C:\samples\vol.part2.rar")]
        [InlineData("All OK")]
        public void UnRAR非进度片段_一律不认(string fragment)
        {
            Assert.Null(UnRarProgressParser.TryParse(fragment));
        }

        // ================================================================ 3 输出泵：\r / \b 切分

        [Fact]
        public async Task 输出泵_把回车分隔的进度切成片段_把CRLF切成整行()
        {
            // 7-Zip 的真实形态：进度是 \r 分隔的，诊断信息是 CRLF 的。
            const string raw =
                "7-Zip 26.03\r\n\r\n  0%\r    \r 67% 8 - payload 08 数据.bin\r                              \rEverything is Ok\r\n";

            List<ProcessOutputSegment> segments = await PumpAsync(raw);

            Assert.Equal(
                new[]
                {
                    "7-Zip 26.03",
                    "",
                    "  0%",
                    "    ",
                    " 67% 8 - payload 08 数据.bin",
                    "                              ",
                    "Everything is Ok"
                },
                segments.Select(x => x.Text));

            // 进度是"片段"，整行是"行" —— 类型不能混（错一行就会污染 -slt 的空行分隔语义）。
            Assert.Equal(ProcessOutputSegmentKind.Line, segments[0].Kind);
            Assert.Equal(ProcessOutputSegmentKind.Line, segments[1].Kind);
            Assert.Equal(ProcessOutputSegmentKind.Fragment, segments[2].Kind);
            Assert.Equal(ProcessOutputSegmentKind.Fragment, segments[4].Kind);
            Assert.Equal(ProcessOutputSegmentKind.Line, segments[6].Kind);
        }

        [Fact]
        public async Task 输出泵_UnRAR的退格回写被切成独立片段()
        {
            // 实测原样（UnRAR 7.23）：同一行里用 \b 回退重写百分比。
            const string raw =
                "Extracting  C:\\samples\\out\\payload 01 数据.bin     \b\b\b\b  1%\b\b\b\b  3%\b\b\b\b\b  OK \r\n";

            List<ProcessOutputSegment> segments = await PumpAsync(raw);

            Assert.Equal(
                new[]
                {
                    @"Extracting  C:\samples\out\payload 01 数据.bin     ",
                    "",
                    "",
                    "",
                    "  1%",
                    "",
                    "",
                    "",
                    "  3%",
                    "",
                    "",
                    "",
                    "",
                    "  OK "
                },
                segments.Select(x => x.Text));

            Assert.Equal(ProcessOutputSegmentKind.Fragment, segments[0].Kind);
            Assert.Equal(ProcessOutputSegmentKind.Fragment, segments[4].Kind);
            Assert.Equal(ProcessOutputSegmentKind.Line, segments[^1].Kind);
        }

        // ================================================================ 4 端到端：原始字节 → 上报次数

        [Fact]
        public async Task 真实7z原始输出_进度被解析出来且进度行不进日志缓冲区()
        {
            // 真实抓下来的 stdout 形态：296 个进度片段挤在两次刷新之间。
            string raw = BuildSevenZipRawOutput(progressLineCount: 296);

            var reports = new List<ArchiveProgress>();
            var reporter = new ArchiveProgressReporter(
                new RecordingProgress(reports),
                ArchiveProgressReporter.MinInterval,
                ArchiveProgressReporter.MinPercentInterval,
                ArchiveProgressReporter.MinPercentDelta,
                new SimulatedClock(TimeSpan.FromMilliseconds(5)).Next);

            var buffer = new StringBuilder();
            var collector = new EngineOutputCollector(
                buffer,
                new object(),
                SevenZipProgressParser.TryParse,
                reporter,
                monitor: null);

            await ProcessOutputPump.PumpAsync(new StringReader(raw), collector.Handle);

            Assert.True(reports.Count >= 1, "真实 7-Zip 输出里应当至少解析出一条进度");
            Assert.All(reports, r => Assert.InRange(r.Percent, 0, 100));

            // 节流：输入 296 条进度片段，上报次数必须**远小于**它。
            Assert.True(
                reports.Count < 30,
                $"节流失效：296 条进度片段上报了 {reports.Count} 次");
            Assert.Equal(296, collector.ProgressSegmentCount);

            // 百分比单调不降，而且确实报到了后段（不是只报了一条 0%）。
            int previous = -1;

            foreach (ArchiveProgress report in reports)
            {
                Assert.True(report.Percent >= previous, $"百分比回退：{previous} → {report.Percent}");
                previous = report.Percent;
            }

            Assert.True(previous >= 80, $"进度没有往后走：最后只到 {previous}%");

            // 进度行不得进日志缓冲区（否则日志与错误分类都会被百分比刷屏）。
            string logged = buffer.ToString();
            Assert.DoesNotContain("%", logged);
            Assert.Contains("Everything is Ok", logged);
            Assert.Contains("Path = C:\\samples\\sample.7z", logged);
        }

        [Fact]
        public async Task 真实UnRAR原始输出_进度被解析出来且不淹没缓冲区()
        {
            string raw = BuildUnRarRawOutput(fileCount: 40);

            var reports = new List<ArchiveProgress>();
            var reporter = new ArchiveProgressReporter(
                new RecordingProgress(reports),
                ArchiveProgressReporter.MinInterval,
                ArchiveProgressReporter.MinPercentInterval,
                ArchiveProgressReporter.MinPercentDelta,
                new SimulatedClock(TimeSpan.FromMilliseconds(5)).Next);

            var buffer = new StringBuilder();
            var collector = new EngineOutputCollector(
                buffer,
                new object(),
                UnRarProgressParser.TryParse,
                reporter,
                monitor: null);

            await ProcessOutputPump.PumpAsync(new StringReader(raw), collector.Handle);

            Assert.True(reports.Count >= 1);

            // 百分比必须单调不降（错位解析会立刻暴露）。
            int previous = -1;
            int maxPercent = -1;

            foreach (ArchiveProgress report in reports.Where(r => r.Percent >= 0))
            {
                Assert.True(report.Percent >= previous, $"百分比回退：{previous} → {report.Percent}");
                previous = report.Percent;
                maxPercent = report.Percent;
            }

            Assert.True(maxPercent >= 50, $"进度没有往后走：最后只到 {maxPercent}%");
            Assert.True(reports.Count < 240 / 5, $"节流失效：{reports.Count} 次上报");

            // 条目名从进度里出得来（界面的"当前条目"就是靠它），而不是留在日志缓冲区里。
            Assert.Contains(reports, r => r.CurrentEntry == "payload 01 数据.bin");

            // 缓冲区里剩下的才是"真输出"：版本 banner 与收尾结论。
            string logged = buffer.ToString();
            Assert.DoesNotContain("%", logged);
            Assert.Contains("UNRAR 7.23 x64 freeware", logged);
            Assert.Contains("All OK", logged);
        }

        // ================================================================ 5 节流

        [Fact]
        public void 节流_同百分比的五千条只上报一次()
        {
            var reports = new List<ArchiveProgress>();
            long ticks = 0;

            var reporter = new ArchiveProgressReporter(
                new RecordingProgress(reports),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(100),
                1,
                () => ticks);

            for (int i = 0; i < 5000; i++)
            {
                ticks += Stopwatch.Frequency / 10_000; // 每条间隔 0.1ms：远小于 250ms 的闸门
                reporter.Report(new ArchiveProgress { Percent = 42, CurrentEntry = "payload.bin" });
            }

            Assert.Equal(1, reporter.ReportedCount);
            Assert.Single(reports);
        }

        [Fact]
        public void 节流_百分比每点一跳也要被时间闸压住()
        {
            var reports = new List<ArchiveProgress>();
            long ticks = 0;

            var reporter = new ArchiveProgressReporter(
                new RecordingProgress(reports),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(100),
                1,
                () => ticks);

            // 10 秒的模拟时间里喂 10000 条（每条 1ms，百分比 0→100 来回跳）
            for (int i = 0; i < 10_000; i++)
            {
                ticks += Stopwatch.Frequency / 1000;

                reporter.Report(new ArchiveProgress
                {
                    Percent = i % 101,
                    CurrentEntry = $"payload {i:D5}.bin"
                });
            }

            /*
             * 两道闸：慢闸 250ms（≈4 次/秒），快闸 100ms（"百分比真的涨了"才走）。
             * 10 秒里最多 100 次，即 **10 次/秒** —— 这就是这段节流的硬上限；
             * 10000 条输入被压到这个量级，UI 线程不可能被刷爆。
             */
            Assert.True(
                reporter.ReportedCount <= 105,
                $"节流上限失效：10000 条上报了 {reporter.ReportedCount} 次");
            Assert.True(reporter.ReportedCount >= 10, "节流把进度压得太平，界面上会看不到动");
        }

        [Fact]
        public void 节流_百分比不动只有条目名在换_走慢闸()
        {
            var reports = new List<ArchiveProgress>();
            long ticks = 0;

            var reporter = new ArchiveProgressReporter(
                new RecordingProgress(reports),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(100),
                1,
                () => ticks);

            for (int i = 0; i < 10_000; i++)
            {
                ticks += Stopwatch.Frequency / 1000;

                reporter.Report(new ArchiveProgress
                {
                    Percent = 42,
                    CurrentEntry = $"payload {i:D5}.bin"
                });
            }

            // 250ms 一道闸 → 10 秒最多 40 次。
            Assert.True(
                reporter.ReportedCount <= 45,
                $"慢闸失效：10000 条上报了 {reporter.ReportedCount} 次");
        }

        [Fact]
        public void 节流_接收端抛异常不影响上报方()
        {
            long ticks = 0;

            var reporter = new ArchiveProgressReporter(
                new ThrowingProgress(),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(100),
                1,
                () => ticks);

            Assert.True(reporter.Report(new ArchiveProgress { Percent = 10 }));

            ticks += Stopwatch.Frequency / 2; // 过半秒：第二道闸已经开了

            Assert.True(reporter.Report(new ArchiveProgress { Percent = 20 }));
            Assert.Equal(2, reporter.ReportedCount);
        }

        [Fact]
        public void 节流_收口之后一律丢弃()
        {
            var reports = new List<ArchiveProgress>();
            var reporter = new ArchiveProgressReporter(new RecordingProgress(reports));

            Assert.True(reporter.Report(new ArchiveProgress { Percent = 5 }));
            reporter.Complete();

            for (int i = 6; i <= 100; i++)
            {
                Assert.False(reporter.Report(new ArchiveProgress { Percent = i }));
            }

            Assert.Single(reports);
            Assert.True(reporter.IsCompleted);
        }

        // ================================================================ 6 卡住检测（假输出流）

        [Fact]
        public async Task 卡住检测_长时间无输出的假流_会报一次提示且不杀掉输出源()
        {
            var notices = new List<ArchiveStallNotice>();
            var monitor = new EngineOutputActivityMonitor(
                TimeSpan.FromMilliseconds(60),
                notice => notices.Add(notice));

            var reporter = new ArchiveProgressReporter(null);
            var collector = new EngineOutputCollector(
                new StringBuilder(),
                new object(),
                SevenZipProgressParser.TryParse,
                reporter,
                monitor);

            // 假进程：流一直开着、但一个字节都不吐（"还在跑但没动静"）。
            var reader = new SilentTextReader();

            Task pump = ProcessOutputPump.PumpAsync(reader, collector.Handle);

            using var watchdogCts = new CancellationTokenSource();
            Task watchdog = EngineOutputActivityMonitor.WatchAsync(
                monitor,
                TimeSpan.FromMilliseconds(10),
                watchdogCts.Token);

            await WaitUntilAsync(() => notices.Count > 0, TimeSpan.FromSeconds(10));

            Assert.True(monitor.StallCount >= 1);
            Assert.True(notices[0].Idle >= TimeSpan.FromMilliseconds(60));
            Assert.True(notices[0].Threshold > TimeSpan.Zero);
            Assert.True(notices[0].ProcessStillRunning);

            // 同一段沉默只提示一次（不会每 5 秒刷一条）。
            int afterFirst = notices.Count;
            await Task.Delay(250);
            Assert.Equal(afterFirst, notices.Count);

            /*
             * "不会误杀进程"的判据：输出源**还活着**、泵**还挂着**。
             * 提示链路手上根本没有 Process 对象（见 EngineOutputActivityMonitor 的类注释），
             * 所以这是设计上的保证，这里用可观察的事实把它钉住。
             */
            Assert.False(reader.WasDisposed);
            Assert.False(pump.IsCompleted, "卡住提示不该结束输出泵（那等于把进程掐了）");

            // 收尾：停看门狗 → 掐断回调出口 → 放掉假流。
            watchdogCts.Cancel();
            await watchdog;
            monitor.Complete();

            int beforeRelease = notices.Count;
            reader.Release();
            await pump;

            await Task.Delay(100);
            Assert.Equal(beforeRelease, notices.Count);
        }

        [Fact]
        public void 卡住检测_假时钟下的阈值与去抖()
        {
            var notices = new List<ArchiveStallNotice>();
            DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var monitor = new EngineOutputActivityMonitor(
                TimeSpan.FromSeconds(90),
                notice => notices.Add(notice),
                () => now);

            now = now.AddSeconds(89);
            Assert.False(monitor.CheckForStall());

            now = now.AddSeconds(2); // 累计 91 秒
            Assert.True(monitor.CheckForStall());
            Assert.Single(notices);
            Assert.Equal(91, (int)Math.Round(notices[0].Idle.TotalSeconds));

            // 同一段沉默不再重复报。
            now = now.AddSeconds(300);
            Assert.False(monitor.CheckForStall());
            Assert.Single(notices);

            // 又有输出 → 下一段沉默可以再报（"又卡了一次"必须还能看见）。
            monitor.MarkActivity();
            now = now.AddSeconds(91);
            Assert.True(monitor.CheckForStall());
            Assert.Equal(2, notices.Count);

            // 收口之后不再报（进程已经结束了）。
            monitor.Complete();
            monitor.MarkActivity();
            now = now.AddSeconds(600);
            Assert.False(monitor.CheckForStall());
            Assert.Equal(2, notices.Count);
        }

        // ================================================================ 7 真进程：卡住提示 + 不误杀 + 无迟到回调

        [Fact]
        public async Task 真7z进程_卡住只提示不杀进程_且返回后不再有回调()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                _output.WriteLine("测试机上没有内置 7z.exe，跳过。");
                return;
            }

            string archive = BuildSevenZipArchive(sevenZip, fileCount: 12, bytesPerFile: 64 * 1024);
            string output = Path.Combine(_root, "out_stall");
            string extractionOutput = Path.Combine(_root, "out_progress");

            // ---- 卡住提示：阈值 1ms（引擎必然"沉默"超过它），而且必须**不杀进程**。
            var notices = new List<ArchiveStallNotice>();
            var runner = new SevenZipProcessRunner(new ToolLocator());
            var reporter = new ArchiveProgressReporter(null);

            ArchiveOperationResult result = await RunSevenZipWithContextAsync(
                runner,
                new[] { "x", "-bsp0", "-bd", "-sccUTF-8", archive, "-o" + output, "-y", "-p" },
                string.Empty,
                new EngineProgressContext
                {
                    Stalled = notice => notices.Add(notice),
                    StallThreshold = TimeSpan.FromMilliseconds(1)
                });

            Assert.True(result.Success, $"真 7z 解压应当成功（消息：{result.Message}）");
            Assert.True(notices.Count >= 1, "阈值 1ms 下应当报出至少一次'长时间无输出'");

            // 不误杀：进程正常退出并把产物写完。
            Assert.True(
                Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any(),
                "进程被卡住提示误杀了：产物一个都没有");

            // ---- 迟到回调：返回之后不许再有任何上报。
            var lateCheck = new List<ArchiveProgress>();

            ArchiveOperationResult progressResult = await RunSevenZipWithContextAsync(
                runner,
                new[] { "x", "-bsp1", "-bd", "-sccUTF-8", archive, "-o" + extractionOutput, "-y", "-p" },
                string.Empty,
                new EngineProgressContext { Progress = new RecordingProgress(lateCheck) });

            Assert.True(progressResult.Success, progressResult.Message);

            int atReturn = lateCheck.Count;
            await Task.Delay(400);
            Assert.Equal(atReturn, lateCheck.Count);

            /*
             * 进度行不得留在 StandardOutput 里（它会淹没日志、干扰错误关键字分类）。
             * 这一条同时证明"运行器确实按 -bsp1 解析了这次输出"。
             */
            Assert.DoesNotContain("%", progressResult.StandardOutput);
        }

        [Fact]
        public async Task 取消之后的引擎调用_没有任何进度回调()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                _output.WriteLine("测试机上没有内置 7z.exe，跳过。");
                return;
            }

            string archive = BuildSevenZipArchive(sevenZip, fileCount: 4, bytesPerFile: 4096);

            var reports = new List<ArchiveProgress>();
            var runner = new SevenZipProcessRunner(new ToolLocator());

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // 一上来就是取消态：运行器必须立刻收口

            ArchiveOperationResult result = await RunSevenZipWithContextAsync(
                runner,
                new[] { "x", "-bsp1", "-bd", "-sccUTF-8", archive, "-o" + Path.Combine(_root, "out_cancel"), "-y", "-p" },
                string.Empty,
                new EngineProgressContext { Progress = new RecordingProgress(reports) },
                cts.Token);

            Assert.False(result.Success);

            int atReturn = reports.Count;
            await Task.Delay(400);
            Assert.Equal(atReturn, reports.Count);
        }

        // ================================================================ 8 管线：进度落到任务上

        [Fact]
        public async Task 解压进度_落到任务上并且只在跨10个百分点时写日志()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("progress.7z"));

            int[] percents = { 0, 5, 9, 10, 25, 47, 88, 100 };
            var statusSeenWhileRunning = new List<string>();

            harness.Engine.OnExtractAsync = async request =>
            {
                foreach (int percent in percents)
                {
                    request.Progress!.Report(new ArchiveProgress
                    {
                        Percent = percent,
                        CurrentEntry = $"payload {percent:D3} 数据.bin"
                    });

                    // 引擎层的慢闸是 250ms：睡够它，每一条才真的送得出来。
                    // 这一步模拟的是"真引擎每 250ms 刷一次进度"，不是被测试放宽的等待。
                    await Task.Delay(320);
                }

                // 进度是同步落地的（无界面宿主时就地执行），所以这里能直接观察运行中的显示。
                statusSeenWhileRunning.Add(task.StatusDisplayText);

                WriteSinglePayload(request.OutputPath!);
                return Succeeded();
            };

            harness.Engine.OnListAsync = _ => Task.FromResult(SinglePayloadListing());

            await harness.Coordinator.StartExtractAsync();

            // 运行中：状态列必须显示"状态 + 百分比"（不新增列）。
            Assert.Contains("100%", statusSeenWhileRunning[0]);
            Assert.StartsWith(StatusText.Extracting, statusSeenWhileRunning[0], StringComparison.Ordinal);

            // 收尾后：任务成功，且**不得**再挂着进度（不变量 6 的反面同样成立）。
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(ArchiveTask.NoProgress, task.ProgressPercent);
            Assert.Equal(StatusText.ExtractSuccess, task.StatusDisplayText);
            Assert.DoesNotContain("%", task.StatusDisplayText);

            /*
             * 进度日志：只跨 10% 档位才写一行。
             * 0/5/9 都在第 0 档 → 只写 0% 那一行；10→10%、25→20%档、47→40%档、88→80%档、100→100%档。
             */
            string[] progressLogs = harness.Log.Logs
                .Select(x => x.Message)
                .Where(x => x.Contains("：进度 ", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(6, progressLogs.Length);
            Assert.Contains(progressLogs, x => x.Contains("进度 0%", StringComparison.Ordinal));
            Assert.Contains(progressLogs, x => x.Contains("进度 10%", StringComparison.Ordinal));
            Assert.Contains(progressLogs, x => x.Contains("进度 25%", StringComparison.Ordinal));
            Assert.Contains(progressLogs, x => x.Contains("进度 47%", StringComparison.Ordinal));
            Assert.Contains(progressLogs, x => x.Contains("进度 88%", StringComparison.Ordinal));
            Assert.Contains(progressLogs, x => x.Contains("进度 100%", StringComparison.Ordinal));

            // 同一个 10% 档位里的 5 / 9 不许各写一行（这正是"淹没日志"的来源）。
            Assert.DoesNotContain(progressLogs, x => x.Contains("进度 5%", StringComparison.Ordinal));
            Assert.DoesNotContain(progressLogs, x => x.Contains("进度 9%", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 卡住提示_写WARN日志并在任务上挂提示_但不影响任务结论也不取消令牌()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("stall.7z"));

            string hintAtStall = string.Empty;
            bool tokenCancelledAtStall = true;

            harness.Engine.OnExtractAsync = request =>
            {
                Assert.NotNull(request.Stalled);
                Assert.Equal(EngineOutputActivityMonitor.DefaultStallThreshold, request.StallThreshold);

                request.Stalled!(new ArchiveStallNotice
                {
                    Idle = TimeSpan.FromSeconds(95),
                    Threshold = request.StallThreshold
                });

                hintAtStall = task.ResponsivenessHint;
                tokenCancelledAtStall = false; // 假引擎没有令牌，这里只表示"没有被要求停下"

                WriteSinglePayload(request.OutputPath!);
                return Task.FromResult(Succeeded());
            };

            harness.Engine.OnListAsync = _ => Task.FromResult(SinglePayloadListing());

            await harness.Coordinator.StartExtractAsync();

            Assert.Contains(ArchiveTask.NoResponseHintText, hintAtStall, StringComparison.Ordinal);
            Assert.False(tokenCancelledAtStall);

            // WARN 一条，且说清了"不会自动结束它"。
            string[] warnings = harness.Log.Logs
                .Where(x => x.Level == "WARN")
                .Select(x => x.Message)
                .Where(x => x.Contains(ArchiveTask.NoResponseHintText, StringComparison.Ordinal))
                .ToArray();

            Assert.Single(warnings);
            Assert.Contains("取消当前", warnings[0], StringComparison.Ordinal);

            // 只提示不处理：任务照常按引擎的结论收尾，提示随收尾一起清掉。
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.False(task.HasResponsivenessHint);
            Assert.Equal(StatusText.ExtractSuccess, task.StatusDisplayText);
        }

        [Fact]
        public async Task 不变量6_进度到一百但引擎失败_状态不得是成功也不得显示进度()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("broken.7z"));

            harness.Engine.OnExtractAsync = request =>
            {
                request.Progress!.Report(new ArchiveProgress { Percent = 100, CurrentEntry = "payload.bin" });

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    ExitCode = 2,
                    Status = StatusText.ExtractFailed,
                    Message = "文件损坏",
                    DetectedErrorType = "CorruptedArchive"
                });
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(StatusText.ExtractFailed, task.Status);
            Assert.Equal(ArchiveTask.NoProgress, task.ProgressPercent);

            // 界面上不许留下任何进度痕迹（"解压失败 100%" 是骗人的）。
            Assert.DoesNotContain("%", task.StatusDisplayText);
            Assert.DoesNotContain("%", task.ProgressDetail);
        }

        [Fact]
        public void 任务收尾之后_界面不再显示百分比()
        {
            var task = new ArchiveTask(@"C:\samples\a.7z") { Status = StatusText.Extracting };

            task.ApplyProgress(45, "payload 08 数据.bin");

            Assert.True(task.HasLiveProgress);
            Assert.Equal($"{StatusText.Extracting} 45%", task.StatusDisplayText);
            Assert.Equal("45% · payload 08 数据.bin", task.ProgressDetail);

            task.EndTime = DateTime.Now;

            Assert.False(task.HasLiveProgress);
            task.ProgressText = StatusText.ProgressCompleted;
            Assert.Equal(StatusText.Extracting, task.StatusDisplayText);
            Assert.Equal(StatusText.ProgressCompleted, task.ProgressDetail);
        }

        [Fact]
        public void 无界面宿主下_未收尾的任务上挂提示也不抛()
        {
            // Application.Current 在这里是 null（xUnit 没有 WPF 应用）：
            // 提示与进度必须"就地落地"，既不抛异常也不死等。
            Assert.Null(System.Windows.Application.Current);

            var task = new ArchiveTask(@"C:\samples\a.7z") { Status = StatusText.Extracting };
            task.ApplyProgress(12, "x.bin");
            task.ResponsivenessHint = ArchiveTask.NoResponseHintText;

            Assert.Equal($"{StatusText.Extracting} 12% · {ArchiveTask.NoResponseHintText}", task.StatusDisplayText);
        }

        // ================================================================ 装配

        private static async Task<List<ProcessOutputSegment>> PumpAsync(string raw)
        {
            var segments = new List<ProcessOutputSegment>();

            await ProcessOutputPump.PumpAsync(
                new StringReader(raw),
                segment => segments.Add(segment));

            return segments;
        }

        /// <summary>100% 单调递增的进度序列（7-Zip 的真实分布：固定间隔刷新）。</summary>
        private static string BuildSevenZipRawOutput(int progressLineCount)
        {
            var builder = new StringBuilder();

            builder.Append("\r\n7-Zip 26.03 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-09-03\r\n\r\n");
            builder.Append("Scanning the drive for archives:\r\n");
            builder.Append("1 file, 251674592 bytes (241 MiB)\r\n\r\n");
            builder.Append("Extracting archive: C:\\samples\\sample.7z\r\n--\r\n");
            builder.Append("Path = C:\\samples\\sample.7z\r\nType = 7z\r\nPhysical Size = 251674592\r\n\r\n");

            for (int i = 0; i < progressLineCount; i++)
            {
                int percent = i * 100 / progressLineCount;

                // 真实形态：值 + 序号 + 命令 + 条目名，四条之间用 \r 分隔，并夹着"擦掉上一行"的空格。
                builder.Append($" {percent,3}% {i % 12 + 1} - payload {i % 12 + 1:D2} 数据.bin\r");
                builder.Append("                              \r");
            }

            builder.Append("Everything is Ok\r\n\r\nFolders: 1\r\nFiles: 15\r\n");

            return builder.ToString();
        }

        /// <summary>UnRAR 的真实分布：每个文件 1%→100% 累计，同一行内用退格回写。</summary>
        private static string BuildUnRarRawOutput(int fileCount)
        {
            var builder = new StringBuilder();

            builder.Append("UNRAR 7.23 x64 freeware      Copyright (c) 1993-2026 Alexander Roshal\r\n\r\n");
            builder.Append("Extracting from C:\\samples\\sample.rar\r\n\r\n");

            for (int i = 0; i < fileCount; i++)
            {
                builder.Append($"Extracting  C:\\samples\\out\\payload {i + 1:D2} 数据.bin     ");

                for (int step = 0; step < 5; step++)
                {
                    int percent = ((i * 5) + step + 1) * 100 / (fileCount * 5);
                    builder.Append("\b\b\b\b");
                    builder.Append($" {Math.Min(percent, 100),3}%");
                }

                builder.Append("\b\b\b\b\b  OK \r\n");
            }

            builder.Append("\r\nAll OK\r\n");

            return builder.ToString();
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();

            while (watch.Elapsed < timeout)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"等待条件超时（{timeout.TotalSeconds:F0} 秒）");
        }

        /// <summary>记录型进度接收端（可能来自读管道的线程，所以必须加锁）。</summary>
        private sealed class RecordingProgress : IProgress<ArchiveProgress>
        {
            private readonly List<ArchiveProgress> _sink;
            private readonly object _gate = new();

            public RecordingProgress(List<ArchiveProgress> sink)
            {
                _sink = sink;
            }

            public void Report(ArchiveProgress? value)
            {
                if (value == null)
                {
                    return;
                }

                lock (_gate)
                {
                    _sink.Add(value);
                }
            }
        }

        /// <summary>
        /// 假时钟：每被问一次就往前走一小步。
        ///
        /// 为什么要它：节流是**按时间**判定的，用真时钟跑"真实输出样本"的用例会变成
        /// "同一次管道读取里几千条进度全被压成一条"，断言就只能写得含糊。
        /// 用一个确定的步长把时间摊开，既保住了节流语义，又让"上报次数远小于输入条数"可确定地断言。
        /// </summary>
        private sealed class SimulatedClock
        {
            private readonly long _stepTicks;
            private long _ticks;

            public SimulatedClock(TimeSpan step)
            {
                _stepTicks = (long)(step.TotalSeconds * Stopwatch.Frequency);
            }

            public long Next()
            {
                _ticks += _stepTicks;

                return _ticks;
            }
        }

        private sealed class ThrowingProgress : IProgress<ArchiveProgress>
        {
            public void Report(ArchiveProgress? value)
            {
                throw new InvalidOperationException("接收端炸了");
            }
        }

        /// <summary>
        /// 假"输出流"：一直开着，但在 <see cref="Release"/> 之前一个字节都不吐
        /// （= 进程还在跑、但没有任何输出）。
        /// </summary>
        private sealed class SilentTextReader : TextReader
        {
            private readonly TaskCompletionSource _release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool WasDisposed { get; private set; }

            public void Release() => _release.TrySetResult();

            public override Task<int> ReadAsync(char[] buffer, int index, int count)
            {
                return ReadCoreAsync();
            }

            public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            {
                return new ValueTask<int>(ReadCoreAsync());
            }

            protected override void Dispose(bool disposing)
            {
                WasDisposed = true;
                base.Dispose(disposing);
            }

            private async Task<int> ReadCoreAsync()
            {
                await _release.Task.ConfigureAwait(false);

                return 0; // 放掉之后就是 EOF
            }
        }

        // ---------------------------------------------------------------- 真引擎调用（绕过 IArchiveEngine，直接接运行器）

        private static async Task<ArchiveOperationResult> RunSevenZipWithContextAsync(
            SevenZipProcessRunner runner,
            string[] arguments,
            string password,
            EngineProgressContext? context,
            CancellationToken cancellationToken = default)
        {
            return await runner.RunSevenZipAsync(arguments, password, cancellationToken, context);
        }

        // ---------------------------------------------------------------- 造样本

        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        /// <summary>造一个真 7z 包（条目名含空格与中文，与实测现场一致）。</summary>
        private string BuildSevenZipArchive(string sevenZip, int fileCount, int bytesPerFile)
        {
            string source = Path.Combine(_root, "src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);

            var payload = new byte[bytesPerFile];
            new Random(20260922).NextBytes(payload);

            for (int i = 0; i < fileCount; i++)
            {
                File.WriteAllBytes(Path.Combine(source, $"payload {i + 1:D2} 数据.bin"), payload);
            }

            File.WriteAllText(Path.Combine(source, "说明 中文 条目.txt"), "unicode entry", new UTF8Encoding(false));

            string archive = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".7z");

            RunSevenZip(sevenZip, "a", "-t7z", "-mx1", "-sccUTF-8", archive, Path.Combine(source, "*"));

            return archive;
        }

        private static void RunSevenZip(string sevenZip, params string[] args)
        {
            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException("造样本超时");
            }

            Assert.Equal(0, process.ExitCode);
        }

        // ---------------------------------------------------------------- 管线装配

        private sealed class Harness
        {
            public Harness(MainViewModel vm, FakeEngine engine, ExtractionCoordinator coordinator, LogService log)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }
        }

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会写两个进程级静态：先存后还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            return new Harness(vm, engine, coordinator, logService);
        }

        private ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "fake archive", new UTF8Encoding(false));

            return path;
        }

        private static void WriteSinglePayload(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, "payload-00000.bin"), new byte[] { 1 });
        }

        private static ArchiveListResult SinglePayloadListing()
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 1,
                Entries = new List<ArchiveEntry>
                {
                    new() { Path = "payload-00000.bin", Size = 1 }
                },
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        private static ArchiveOperationResult Succeeded()
        {
            return new ArchiveOperationResult
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }

        private sealed class FakeEngine : IArchiveEngine
        {
            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(SinglePayloadListing());
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(Succeeded());
            }
        }
    }
}
