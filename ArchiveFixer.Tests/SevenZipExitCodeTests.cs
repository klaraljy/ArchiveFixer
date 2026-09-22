using System;
using System.Collections.Generic;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 7-Zip 退出码映射（`docs/WinRAR功能参考.md` §1.15 的返回码表 / 7-Zip 自己的 <c>NExitCode</c>）。
    ///
    /// 这一组测试锁的是**最容易把"部分成功"显示成"成功"的那条路径**：
    /// 退出码 1 = "发生非致命错误"，7z 在"一部分文件解出来、一部分失败"时正是给 1
    /// （AGENTS.md §6 第 6 条：部分成功不得显示为成功）。
    ///
    /// ⚠ 诚实交代一处证据边界：本机 26.01 上**没能**用合成样本跑出退出码 1 ——
    /// 能造出来的警告（`There are data after the end of archive`、跳过已存在、保留名 / 结尾点）
    /// 实测全都返回 0。所以下面的"退出码 1"用例是按**文档契约**钉死的（表里写的就是非致命），
    /// 不是照抄某次实测输出。真正被测到的两条实测事实是：
    /// 加密头（-mhe）列目录失败返回 2、数据加密的包不给密码列目录返回 0。
    /// </summary>
    public class SevenZipExitCodeTests
    {
        // ---------------------------------------------------------------- 退出码表

        [Theory]
        [InlineData(0, SevenZipExitKind.Success)]
        [InlineData(1, SevenZipExitKind.NonFatalWarning)]
        [InlineData(2, SevenZipExitKind.FatalError)]
        [InlineData(3, SevenZipExitKind.DataError)]
        [InlineData(4, SevenZipExitKind.LockedArchive)]
        [InlineData(5, SevenZipExitKind.WriteError)]
        [InlineData(6, SevenZipExitKind.OpenError)]
        [InlineData(7, SevenZipExitKind.CommandLineError)]
        [InlineData(8, SevenZipExitKind.OutOfMemory)]
        [InlineData(9, SevenZipExitKind.CreateError)]
        [InlineData(10, SevenZipExitKind.NoMatchingItems)]
        [InlineData(11, SevenZipExitKind.WrongPassword)]
        [InlineData(12, SevenZipExitKind.ReadError)]
        [InlineData(255, SevenZipExitKind.UserBreak)]
        [InlineData(99, SevenZipExitKind.Unknown)]
        [InlineData(-1, SevenZipExitKind.Unknown)]
        public void 退出码表与参考文档逐条一致(int exitCode, SevenZipExitKind expected)
        {
            Assert.Equal(expected, SevenZipExitCodes.Classify(exitCode));
        }

        [Fact]
        public void 表里有的码都要有中文说明_未知码不编造含义()
        {
            foreach (int code in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 255 })
            {
                Assert.False(string.IsNullOrWhiteSpace(SevenZipExitCodes.Describe(code)), $"退出码 {code} 没有说明");
            }

            // 表里没有的码：不猜。
            Assert.Equal(string.Empty, SevenZipExitCodes.Describe(99));
        }

        // ---------------------------------------------------------------- 退出码 1（最重要的一条）

        [Fact]
        public void 退出码1_即使输出写着EverythingIsOk也不算成功()
        {
            /*
             * 旧实现里有一个"退出码 1 + 输出含 Everything is Ok 就算成功"的口子。
             * 它正是"部分文件解出来、部分失败"被判成成功的路径 —— 这条测试钉死它不再存在。
             * "Everything is Ok" 只描述 7-Zip 自己写完的那部分，不保证条目齐全。
             */
            Assert.False(SevenZipOutputParser.LooksLikeSuccess(1, "Everything is Ok", string.Empty));
            Assert.False(SevenZipOutputParser.LooksLikeSuccess(1, "Everything is Ok", "Sub items Errors: 2"));
        }

        [Fact]
        public void 退出码1_映射为部分完成状态_不是成功()
        {
            string errorType = SevenZipOutputParser.DetectSevenZipErrorType(1, "Everything is Ok", string.Empty);

            Assert.Equal(SevenZipOutputParser.NonFatalErrorType, errorType);

            string status = SevenZipOutputParser.ErrorTypeToTaskStatus(errorType);

            Assert.Equal(StatusText.PartiallyCompleted, status);
            Assert.NotEqual(StatusText.ExtractSuccess, status);
            Assert.NotEqual(StatusText.Success, status);
            Assert.NotEqual(StatusText.TestPassed, status);
        }

        [Fact]
        public void 退出码1_的消息里要说清是部分内容()
        {
            string message = SevenZipOutputParser.ErrorTypeToMessage(
                SevenZipOutputParser.NonFatalErrorType,
                "Sub items Errors: 3");

            Assert.Contains("非致命", message);
            Assert.Contains("部分", message);
            Assert.Contains("Sub items Errors: 3", message);
        }

        // ---------------------------------------------------------------- 其余硬映射

        [Fact]
        public void 退出码0_才是成功()
        {
            Assert.True(SevenZipOutputParser.LooksLikeSuccess(0, "Everything is Ok", string.Empty));
            Assert.Equal("None", SevenZipOutputParser.DetectSevenZipErrorType(0, "Everything is Ok", string.Empty));
        }

        [Fact]
        public void 退出码2_无更具体关键字时映射为致命错误_仍是失败()
        {
            string errorType = SevenZipOutputParser.DetectSevenZipErrorType(
                2,
                "ERROR: something went wrong",
                string.Empty);

            Assert.Equal(SevenZipOutputParser.FatalErrorType, errorType);
            Assert.Equal(StatusText.ExtractFailed, SevenZipOutputParser.ErrorTypeToTaskStatus(errorType));
            Assert.Contains("致命错误", SevenZipOutputParser.ErrorTypeToMessage(errorType, "ERROR: something went wrong"));
        }

        [Fact]
        public void 退出码7_映射为参数错误_而不是未知错误()
        {
            Assert.Equal("CommandLineError", SevenZipOutputParser.DetectSevenZipErrorType(7, string.Empty, string.Empty));
        }

        [Fact]
        public void 退出码8_映射为内存不足()
        {
            string errorType = SevenZipOutputParser.DetectSevenZipErrorType(8, string.Empty, string.Empty);

            Assert.Equal(SevenZipOutputParser.OutOfMemoryErrorType, errorType);
            Assert.Equal(StatusText.ExtractFailed, SevenZipOutputParser.ErrorTypeToTaskStatus(errorType));
            Assert.Contains("内存不足", SevenZipOutputParser.ErrorTypeToMessage(errorType, string.Empty));
        }

        [Fact]
        public void 退出码255_映射为用户中断()
        {
            // 255 的官方含义就是"用户中断"，没有关键字时也按它判。
            Assert.Equal("Cancelled", SevenZipOutputParser.DetectSevenZipErrorType(255, string.Empty, string.Empty));
            Assert.Equal(StatusText.Cancelled, SevenZipOutputParser.ErrorTypeToTaskStatus("Cancelled"));
        }

        // ---------------------------------------------------------------- 关键字优先于退出码

        [Fact]
        public void 关键字比退出码更具体_所以关键字优先()
        {
            // 退出码 2 只说"致命错误"，具体原因要靠输出：密码错 / 缺卷 / 路径过长 各自有更准的结论。
            Assert.Equal(
                "WrongPassword",
                SevenZipOutputParser.DetectSevenZipErrorType(2, "ERROR: Wrong password", string.Empty));

            Assert.Equal(
                "PathTooLong",
                SevenZipOutputParser.DetectSevenZipErrorType(2, "The filename or extension is too long", string.Empty));

            Assert.Equal(
                "VolumeMissing",
                SevenZipOutputParser.DetectSevenZipErrorType(2, "Cannot find archive part", string.Empty));
        }

        [Fact]
        public void 退出码3与其余码按现状仍由关键字判定_没有结论时才是未知错误()
        {
            // "其余按现状"：表里有定义，但不参与"退出码优先"的硬映射（关键字能给更具体的结论）。
            Assert.Null(SevenZipExitCodes.ToErrorType(3));
            Assert.Equal("CorruptedArchive", SevenZipOutputParser.DetectSevenZipErrorType(3, "CRC Failed", string.Empty));
            Assert.Equal("UnknownError", SevenZipOutputParser.DetectSevenZipErrorType(3, "whatever", string.Empty));
        }

        // ---------------------------------------------------------------- 命令识别

        [Fact]
        public void 能从参数表认出跑的是哪个命令()
        {
            Assert.Equal(EngineOperation.List, SevenZipProcessRunner.ResolveOperation(new[] { "l", "-slt", "a.7z" }));
            Assert.Equal(EngineOperation.Test, SevenZipProcessRunner.ResolveOperation(new[] { "t", "a.7z" }));
            Assert.Equal(EngineOperation.Extract, SevenZipProcessRunner.ResolveOperation(new[] { "x", "a.7z" }));
            Assert.Equal(EngineOperation.Extract, SevenZipProcessRunner.ResolveOperation(new[] { "e", "a.7z" }));

            // 认不出来就是 null = "这层不下结论"，与旧行为一致。
            Assert.Null(SevenZipProcessRunner.ResolveOperation(Array.Empty<string>()));
            Assert.Null(SevenZipProcessRunner.ResolveOperation(new[] { "-slt", "a.7z" }));
            Assert.Null(SevenZipProcessRunner.ResolveOperation(null));
        }

        [Fact]
        public void 运行器分析结果时按退出码1落成部分完成()
        {
            // 走一遍真正的 AnalyzeResult（运行器唯一的结果出口），确认状态链路是通的。
            var runner = new SevenZipProcessRunner();

            ArchiveOperationResult result = runner.AnalyzeResult(
                1,
                "Everything is Ok",
                "Sub items Errors: 1",
                string.Empty,
                TimeSpan.Zero);

            Assert.False(result.Success);
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(SevenZipOutputParser.NonFatalErrorType, result.DetectedErrorType);
            Assert.Equal(StatusText.PartiallyCompleted, result.Status);
            Assert.Contains("部分", result.Message);
        }

        [Fact]
        public void 部分完成的结果对象自带可判定的标志()
        {
            var runner = new SevenZipProcessRunner();

            ArchiveOperationResult partial = runner.AnalyzeResult(1, "Sub items Errors: 1", string.Empty);
            ArchiveOperationResult ok = runner.AnalyzeResult(0, "Everything is Ok", string.Empty);

            Assert.True(partial.IsPartiallyCompleted);
            Assert.False(partial.Success);

            Assert.False(ok.IsPartiallyCompleted);
            Assert.True(ok.Success);
        }

        [Fact]
        public void 结果类型里没有把退出码1当成功的旧写法()
        {
            // 旧代码在 CreateSuccess 里不看退出码，调用方又用 LooksLikeSuccess 兜 —— 现在两端都不认 1。
            List<int> successCodes = new();

            if (SevenZipOutputParser.LooksLikeSuccess(1, "Everything is Ok", string.Empty))
            {
                successCodes.Add(1);
            }

            Assert.Empty(successCodes);
        }
    }
}
