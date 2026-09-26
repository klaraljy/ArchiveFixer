using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Engines.WinRar;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 第二引擎（RARLAB UnRAR）的**纯单元**用例：能力位、参数、输出分类、列表解析。
    ///
    /// 这一组不起外部进程（真样本对照在 <see cref="UnRarRealSampleTests"/>），
    /// 喂进去的文本都来自本机 <c>UNRAR 7.23 x64 freeware</c> 的实测输出
    /// （原始日志见交付报告 `fix-unrar.md` 与 `_tmp\ArchiveFixer\unrar-samples\`）。
    /// </summary>
    public class UnRarEngineTests
    {
        // ────────────────────────── 能力位必须如实 ──────────────────────────

        [Fact]
        public void 能力位只认RAR_不许声称能处理zip或7z()
        {
            var engine = new UnRarEngine(new UnRarProcessRunner(new ToolLocator()));

            Assert.True(engine.Capabilities.CanList);
            Assert.True(engine.Capabilities.CanTest);
            Assert.True(engine.Capabilities.CanExtract);
            Assert.True(engine.Capabilities.SupportsPassword);
            Assert.True(engine.Capabilities.SupportsMultiVolume);
            Assert.True(engine.Capabilities.PreservesUnicodeNames);

            // 探测：UnRAR 没有廉价的文件头探测能力（-hp 的包连目录都要密码），如实报 false。
            Assert.False(engine.Capabilities.CanProbe);

            foreach (string format in new[] { "RAR", "RAR4", "RAR5" })
            {
                EngineFormatCapability? capability = engine.Capabilities.ForFormat(format);

                Assert.NotNull(capability);
                Assert.True(capability!.CanList);
                Assert.True(capability.CanTest);
                Assert.True(capability.CanExtract);
            }

            // ⛔ 一个非 RAR 格式都不许登记：登记了就会把 zip 包路由到解不开的引擎上。
            foreach (string format in new[] { "ZIP", "ZIP_EMPTY", "ZIP_SPANNED", "7Z", "TAR", "GZIP", "BZIP2", "XZ", "ISO", "CAB", "ARJ", "LZH" })
            {
                Assert.Null(engine.Capabilities.ForFormat(format));
            }

            Assert.Equal("winrar", engine.Id);
            Assert.Contains("UnRAR", engine.DisplayName, StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────── 参数 ──────────────────────────

        [Fact]
        public void 列表与测试参数必须带_scf_否则中文名乱码()
        {
            var runner = new UnRarProcessRunner(new ToolLocator());

            var list = runner.BuildListArguments(@"C:\t\中文包.rar", "pw");
            var test = runner.BuildTestArguments(@"C:\t\中文包.rar", "pw");

            // -scf = 输出用 UTF-8（实测：不加就是 OEM/GBK，中文条目名全乱）。
            Assert.Contains("-scf", list);
            Assert.Contains("-scf", test);

            // lt = 技术列表（稳定键值），不是给人看的对齐表格。
            Assert.Equal("lt", list[0]);
            Assert.Equal("t", test[0]);

            // 密码只作为单个参数出现（ArgumentList 传参，不拼 cmd 字符串）。
            Assert.Contains("-ppw", list);
            Assert.Contains(@"C:\t\中文包.rar", list);
        }

        [Fact]
        public void 空密码传_p_减号_绝不传裸的_p_否则会去问密码()
        {
            // 裸的 -p 会让 UnRAR 交互式问密码；标准输入是关着的 → 实测报"Read error in the file stdin"。
            Assert.Equal("-p-", UnRarProcessRunner.BuildPasswordArgument(null));
            Assert.Equal("-p-", UnRarProcessRunner.BuildPasswordArgument(string.Empty));
            Assert.Equal("-ppw", UnRarProcessRunner.BuildPasswordArgument("pw"));

            var runner = new UnRarProcessRunner(new ToolLocator());

            Assert.DoesNotContain("-p", runner.BuildListArguments("a.rar", null));
            Assert.Contains("-p-", runner.BuildListArguments("a.rar", null));
        }

        [Fact]
        public void 解压参数_保留受损文件时才加_kb_且用_op_指定输出目录()
        {
            var runner = new UnRarProcessRunner(new ToolLocator());

            var normal = runner.BuildExtractArguments(
                @"C:\t\a.rar",
                @"C:\t\out",
                null,
                new ExtractOptions(),
                keepBrokenFiles: false);

            var keep = runner.BuildExtractArguments(
                @"C:\t\a.rar",
                @"C:\t\out",
                null,
                new ExtractOptions(),
                keepBrokenFiles: true);

            // x = 保留包内路径（永不用 e：摊平必然撞名）。
            Assert.Equal("x", normal[0]);
            Assert.DoesNotContain("-kb", normal);
            Assert.Contains("-kb", keep);

            Assert.Contains(@"-opC:\t\out", normal);

            // 默认不覆盖（不变量 3）：SkipExisting → -o-。
            Assert.Contains("-o-", normal);

            // 绝不传 -y：它是"所有询问都回答是"，会连带把覆盖策略变成"覆盖全部"。
            Assert.DoesNotContain("-y", normal);
            Assert.DoesNotContain("-y", keep);
        }

        [Theory]
        [InlineData("SkipExisting", "-o-")]
        [InlineData("OverwriteAll", "-o+")]
        [InlineData("AutoRenameExtracted", "-or")]
        [InlineData("AutoRenameExisting", "-o-")] // UnRAR 没有对应开关，保守落到"不覆盖"
        [InlineData("whatever", "-o-")]
        public void 覆盖模式映射(string mode, string expected)
        {
            var runner = new UnRarProcessRunner(new ToolLocator());

            Assert.Equal(expected, runner.GetOverwriteArgument(mode));
        }

        [Fact]
        public void 参数里能认出命令与归档路径()
        {
            var runner = new UnRarProcessRunner(new ToolLocator());

            var list = runner.BuildListArguments(@"C:\t\a.rar", null);

            Assert.Equal(EngineOperation.List, UnRarProcessRunner.ResolveOperation(list));
            Assert.Equal(@"C:\t\a.rar", UnRarProcessRunner.FindArchiveArgument(list));

            Assert.Equal(EngineOperation.Test, UnRarProcessRunner.ResolveOperation(new[] { "t", "-scf", "a.rar" }));
            Assert.Equal(EngineOperation.Extract, UnRarProcessRunner.ResolveOperation(new[] { "x", "-scf", "a.rar" }));

            // 认不出来 = 这层不下结论（与 7-Zip 侧同一口径）。
            Assert.Null(UnRarProcessRunner.ResolveOperation(new[] { "-scf", "a.rar" }));
            Assert.Null(UnRarProcessRunner.ResolveOperation(Array.Empty<string>()));
        }

        // ────────────────────────── 输出分类（最关键的一组） ──────────────────────────

        [Fact]
        public void 加密文件名_列目录失败才算_解压与测试路径必须报密码错误()
        {
            /*
             * 实测（UNRAR 7.23）：
             * · l  -hp 包无密码 / 错密码 → 退出码 11，输出含 "Details: RAR 5, encrypted headers"
             *   + "Incorrect password for <包>"；
             * · t / x 同样缺密码 → 退出码 11，**只有** "Incorrect password for …"。
             *
             * ⚠ 最要紧的一条：解压 / 测试路径上的 WrongPassword 是**密码候选循环的驱动信号**
             * （ExtractionCoordinator 见到非 WrongPassword 就 break）。在这里改判成"文件名已加密"，
             * 等于第一个候选（常是空密码）就把循环打断 —— 加密包会全部解不开。
             */
            const string listOutput = "Archive: C:\\t\\hp.rar\nDetails: RAR 5, encrypted headers\n  0 files\n\nIncorrect password for C:\\t\\hp.rar\n";
            const string testOutput = "Total errors: 1\nIncorrect password for C:\\t\\hp.rar\n";

            // 列目录：加密头。
            Assert.Equal(
                UnRarOutputParser.EncryptedHeadersErrorType,
                UnRarOutputParser.DetectErrorType(11, listOutput, string.Empty, EngineOperation.List));

            Assert.Equal(
                StatusText.EncryptedHeaders,
                UnRarOutputParser.ErrorTypeToTaskStatus(UnRarOutputParser.EncryptedHeadersErrorType));

            // 测试 / 解压：必须是 WrongPassword（驱动下一个候选）。
            Assert.Equal(
                EngineErrorTypes.WrongPassword,
                UnRarOutputParser.DetectErrorType(11, testOutput, string.Empty, EngineOperation.Test));

            Assert.Equal(
                EngineErrorTypes.WrongPassword,
                UnRarOutputParser.DetectErrorType(11, testOutput, string.Empty, EngineOperation.Extract));

            // 数据加密（未加密文件名）的包不给密码也能列目录 → 不该被判成加密头。
            const string dataEncryptedList = "Archive: C:\\t\\enc.rar\nDetails: RAR 5\n\n*   ..A....   103893  2026-09-22 19:37  payload.bin\n";
            Assert.NotEqual(
                UnRarOutputParser.EncryptedHeadersErrorType,
                UnRarOutputParser.DetectErrorType(0, dataEncryptedList, string.Empty, EngineOperation.List));
        }

        [Fact]
        public void 缺卷_必须报分卷缺失并点名缺的是哪一个()
        {
            // 实测：退出码是 6（不是 3），输出里明确写了缺的卷名。
            const string output =
                "Testing archive C:\\t\\vol.part1.rar\n" +
                "Testing     payload.bin                                                 49%\n" +
                "Testing archive C:\\t\\vol.part2.rar\n" +
                "Cannot find volume C:\\t\\vol.part3.rar\n" +
                "Total errors: 2\n" +
                "payload.bin          - checksum error\n";

            string errorType = UnRarOutputParser.DetectErrorType(6, output, string.Empty, EngineOperation.Extract);

            Assert.Equal(EngineErrorTypes.VolumeMissing, errorType);
            Assert.Equal(StatusText.VolumeMissing, UnRarOutputParser.ErrorTypeToTaskStatus(errorType));

            var runner = new UnRarProcessRunner(new ToolLocator());

            ArchiveOperationResult result = runner.AnalyzeResult(
                6,
                output,
                string.Empty,
                null,
                TimeSpan.Zero,
                @"C:\t\vol.part1.rar",
                EngineOperation.Extract);

            Assert.False(result.Success);
            Assert.Equal(EngineErrorTypes.VolumeMissing, result.DetectedErrorType);

            // 不变量 7：必须报"缺哪几个"，而且要是用户手上的那个名字。
            Assert.Contains("vol.part3.rar", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 损坏归档_退出码3与校验和字样都归到文件损坏()
        {
            const string output =
                "Testing archive C:\\t\\broken.rar\n\n" +
                "payload.bin          - checksum error\n" +
                "Testing     payload.bin                                                 100%\n" +
                "Total errors: 2\n" +
                "Unexpected end of archive\n";

            Assert.Equal(
                EngineErrorTypes.CorruptedArchive,
                UnRarOutputParser.DetectErrorType(3, output, string.Empty, EngineOperation.Test));

            // 没有关键字时按退出码兜底：3 = 无效校验和。
            Assert.Equal(
                EngineErrorTypes.CorruptedArchive,
                UnRarOutputParser.DetectErrorType(3, "whatever", string.Empty, EngineOperation.Extract));
        }

        [Fact]
        public void 退出码1是非致命错误_绝不算成功()
        {
            // RAR 手册：1 = 发生非致命错误。部分成功不得显示为成功（不变量 6）。
            Assert.False(UnRarOutputParser.LooksLikeSuccess(1));
            Assert.True(UnRarOutputParser.LooksLikeSuccess(0));

            string errorType = UnRarOutputParser.DetectErrorType(1, "Some warning", string.Empty, EngineOperation.Extract);

            Assert.Equal(UnRarOutputParser.NonFatalErrorType, errorType);
            Assert.Equal(StatusText.PartiallyCompleted, UnRarOutputParser.ErrorTypeToTaskStatus(errorType));

            var runner = new UnRarProcessRunner(new ToolLocator());

            ArchiveOperationResult result = runner.AnalyzeResult(1, "All OK", string.Empty, null, TimeSpan.Zero, "a.rar", EngineOperation.Extract);

            Assert.False(result.Success);
            Assert.Equal(StatusText.PartiallyCompleted, result.Status);
        }

        [Fact]
        public void 其余退出码的落点()
        {
            Assert.Equal(EngineErrorTypes.CommandLineError, UnRarOutputParser.DetectErrorType(7, string.Empty, string.Empty, null));
            Assert.Equal(EngineErrorTypes.OutOfMemory, UnRarOutputParser.DetectErrorType(8, string.Empty, string.Empty, null));
            Assert.Equal(EngineErrorTypes.WrongPassword, UnRarOutputParser.DetectErrorType(11, string.Empty, string.Empty, EngineOperation.Extract));
            Assert.Equal(EngineErrorTypes.Cancelled, UnRarOutputParser.DetectErrorType(255, string.Empty, string.Empty, null));
            Assert.Equal(EngineErrorTypes.UnknownError, UnRarOutputParser.DetectErrorType(42, string.Empty, string.Empty, null));

            // 12 = "需要密码却问不到"（实测：stdin 被关）→ 报密码问题，别把用户引到"文件损坏"上。
            Assert.Equal(EngineErrorTypes.WrongPassword, UnRarOutputParser.DetectErrorType(12, "Enter password", string.Empty, EngineOperation.Test));

            // 退出码 0 才是成功。
            Assert.Equal("None", UnRarOutputParser.DetectErrorType(0, "All OK", string.Empty, EngineOperation.Extract));
        }

        [Fact]
        public async Task 引擎缺失时不冒用7z的文案_也不抛异常()
        {
            // 三档 UnRAR 来源全关 = 这台机器上没有 UnRAR。
            var tools = new ToolLocator { UseWinRarInstallation = false, UseBundledUnRar = false };
            var runner = new UnRarProcessRunner(tools);

            Assert.False(runner.CheckUnRarExists());

            ArchiveOperationResult result = await runner.RunAsync(new[] { "lt", "-scf", "-p-", "a.rar" }, null, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(EngineErrorTypes.EngineUnavailable, result.DetectedErrorType);

            // 消息里必须说"UnRAR 没找到"：报一句"7z不存在"而其实是 UnRAR 缺失，只会把用户带偏。
            Assert.Contains("UnRAR", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(StatusText.SevenZipMissing, result.Message);
        }

        // ────────────────────────── 列表解析 ──────────────────────────

        [Fact]
        public void 技术列表解析_条目大小目录与加密标志()
        {
            // 实测输出片段（unrar lt -scf tree.rar），条目之间没有空行。
            const string output =
                "\nUNRAR 7.23 x64 freeware      Copyright (c) 1993-2026 Alexander Roshal\n\n" +
                "Archive: C:\\t\\tree.rar\n" +
                "Details: RAR 5\n\n" +
                "        Name: src\\readme.txt\n" +
                "        Type: File\n" +
                "        Size: 27\n" +
                " Packed size: 27\n" +
                "       Ratio: 100%\n" +
                "    Modified: 2026-09-22 19:37:15,429666800\n" +
                "  Attributes: ..A....\n" +
                "       CRC32: D4FE8E60\n" +
                "     Host OS: Windows\n" +
                " Compression: RAR 5.0(v50) -m0 -md=128k\n\n" +
                "        Name: src\\中文名.txt\n" +
                "        Type: File\n" +
                "        Size: 20\n" +
                "  Attributes: ..A....\n" +
                "       Flags: encrypted \n\n" +
                "        Name: src\\sub\n" +
                "        Type: Directory\n" +
                "    Modified: 2026-09-22 19:37:15,432914700\n" +
                "  Attributes: ...D...\n";

            var engine = new UnRarEngine(new UnRarProcessRunner(new ToolLocator()));

            // 直达解析层：UnRarListParser 是 internal，测试通过引擎的公开行为覆盖它 ——
            // 这里用反射之外的办法：把同一段文本喂给 UnRarListParser.Parse。
            UnRarListParser.ParsedListing parsed = UnRarListParser.Parse(output, @"C:\t\tree.rar");

            Assert.Equal(3, parsed.Entries.Count);
            Assert.Equal(2, parsed.FileCount);
            Assert.Equal(1, parsed.DirectoryCount);
            Assert.Equal(47, parsed.TotalUncompressedSize);

            Assert.Contains(parsed.Entries, e => e.Path == "src\\readme.txt" && e.Size == 27 && !e.IsDirectory);
            Assert.Contains(parsed.Entries, e => e.Path == "src\\中文名.txt" && e.IsEncrypted);
            Assert.Contains(parsed.Entries, e => e.Path == "src\\sub" && e.IsDirectory);

            Assert.True(parsed.AnyEntryEncrypted);
            Assert.False(parsed.HeaderEncrypted);
            Assert.False(parsed.IsMultiVolume);
            Assert.Equal("RAR 5", parsed.Details);
        }

        [Fact]
        public void 技术列表解析_加密头与卷号()
        {
            const string output =
                "Archive: C:\\t\\hp.rar\n" +
                "Details: RAR 5, encrypted headers\n" +
                "        Name: payload.bin\n" +
                "        Type: File\n" +
                "        Size: 103893\n" +
                "       Flags: encrypted \n";

            UnRarListParser.ParsedListing parsed = UnRarListParser.Parse(output, @"C:\t\hp.rar");

            Assert.True(parsed.HeaderEncrypted);
            Assert.True(parsed.AnyEntryEncrypted);
            Assert.Equal(1, parsed.FileCount);

            UnRarListParser.ParsedListing volume = UnRarListParser.Parse(
                "Archive: C:\\t\\vol.part2.rar\nDetails: RAR 5, volume 2\n",
                @"C:\t\vol.part2.rar");

            Assert.True(volume.IsMultiVolume);
            Assert.Equal(2, volume.VolumeIndex);
            Assert.Empty(volume.Entries);
        }

        [Fact]
        public void 从后续卷启动时_解析层能给出卷号()
        {
            /*
             * 实测：unrar l vol.part2.rar 会"成功"地列出 0 个条目（退出码 0）。
             * 那不是空包，而是起点不对 —— 引擎据此报 MissingFirstVolume
             * （端到端行为在 UnRarRealSampleTests 里用真样本覆盖）。
             */
            UnRarListParser.ParsedListing parsed = UnRarListParser.Parse(
                "Archive: C:\\t\\vol.part3.rar\nDetails: RAR 5, volume 3\n",
                @"C:\t\vol.part3.rar");

            Assert.Equal(3, parsed.VolumeIndex);
            Assert.Empty(parsed.Entries);

            // 第一卷不该被判成"不是第一卷"。
            UnRarListParser.ParsedListing first = UnRarListParser.Parse(
                "Archive: C:\\t\\vol.part1.rar\nDetails: RAR 5, volume 1\n        Name: a.txt\n        Type: File\n        Size: 1\n",
                @"C:\t\vol.part1.rar");

            Assert.Equal(1, first.VolumeIndex);
            Assert.Single(first.Entries);
        }

        // ────────────────────────── 结果可追溯 ──────────────────────────

        [Fact]
        public void 结果对象带上引擎身份_报告可追溯到具体引擎与版本()
        {
            var runner = new UnRarProcessRunner(new ToolLocator());

            ArchiveOperationResult result = runner
                .AnalyzeResult(0, "All OK", string.Empty, null, TimeSpan.Zero, "a.rar", EngineOperation.Extract)
                .StampEngine("winrar", "UnRAR 命令行（RARLAB 解压工具）", "7.23.0");

            Assert.Equal("winrar", result.EngineId);
            Assert.Equal("7.23.0", result.EngineVersion);

            EngineIdentity identity = result.ToEngineIdentity();

            Assert.Equal("winrar", identity.EngineId);
            Assert.Contains("7.23.0", identity.Describe());

            // 没盖戳的结果不能编造引擎名。
            ArchiveOperationResult unstamped = runner.AnalyzeResult(0, "All OK", string.Empty, null, TimeSpan.Zero, "a.rar", EngineOperation.Extract);

            Assert.Equal(EngineIdentity.UnknownEngineText, unstamped.ToEngineIdentity().Describe());
        }

        // ────────────────────────── 引擎选择（能力优先 + 优先级） ──────────────────────────

        [Fact]
        public void 优先级默认是WinRar在前_RAR交给UnRAR()
        {
            var registry = EngineRegistry.CreateDefault(new ToolLocator());
            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            IArchiveEngine? forRar = selector.SelectFor("RAR5", EngineOperation.Extract);

            Assert.NotNull(forRar);

            // 内置 UnRAR 就在 tools\unrar 里（随程序分发），所以这一条在 CI 上也成立。
            Assert.Equal(EngineIds.WinRar, forRar!.Id);
        }

        [Fact]
        public void 通用引擎仍是7zip_免得把RAR专用引擎写进zip任务的报告()
        {
            var registry = EngineRegistry.CreateDefault(new ToolLocator());

            IArchiveEngine? general = registry.Default;

            Assert.NotNull(general);
            Assert.Equal(EngineIds.SevenZip, general!.Id);
            Assert.Equal(EngineIds.SevenZip, EngineIdentityResolver.ResolveDefault().EngineId);
        }

        [Fact]
        public void 内置unrar真的随程序分发_且版本取自文件资源()
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "unrar", "UnRAR.exe");

            Assert.True(File.Exists(bundled), "内置 UnRAR.exe 没有被复制到输出目录：" + bundled);

            // 关掉"已装 WinRAR 目录"那一档，确认解析到的是内置那一份（避免被本机 WinRAR 抢走）。
            var tools = new ToolLocator { UseWinRarInstallation = false };
            var engine = new UnRarEngine(new UnRarProcessRunner(tools));

            Assert.True(engine.IsAvailable);
            Assert.False(tools.IsUsingCustomUnRarPath);
            Assert.False(tools.IsUsingWinRarInstallation);
            Assert.Equal(bundled, tools.UnRarExePath, ignoreCase: true);

            // 版本取自文件版本资源（不起进程）：内置那份是 7.x 稳定版。
            Assert.False(string.IsNullOrWhiteSpace(engine.Version));
            Assert.NotEqual("unknown", engine.Version);
            Assert.StartsWith("7.", engine.Version, StringComparison.Ordinal);

            // 许可文本也必须随包走（分发义务，见 tools\unrar\README.md §3）。
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "unrar", "license.txt")));
        }
    }
}
