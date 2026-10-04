using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 只读体检报告（<c>_tmp\ArchiveFixer\audit-code.md</c>）里"最该先修"那几项的回归守卫。
    ///
    /// <para>
    /// 每一项都钉**两件**事，缺一不可：
    /// ① <b>行为</b> —— 同一组输入（格式 / 文件名 / 退出码）在收敛后的那条路上必须得到正确结论；
    /// ② <b>结构</b> —— 该收敛的规则只剩一处实现、该删的旧副本不许复活。
    /// 只钉行为不够：这个项目踩过的坑正是"算得对，但真正干活的是另一份代码"
    /// （旧冲突判定表、旧引擎能力判断、旧分卷剥离副本都是这个形态）。
    /// </para>
    ///
    /// <para>
    /// 与被测代码共享进程级静态（<c>ToolLocator.Default</c> / <c>EngineRuntimeSettings</c>），
    /// 所以和同类用例一起**串行**跑，每个用例结束都还原（<c>InnerLayerContinuationTests</c> 顶部的 CollectionDefinition）。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class AuditFixVerificationTests : IDisposable
    {
        private readonly string _root;
        private readonly string _originalCustomSevenZipPath;

        public AuditFixVerificationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerAuditFix", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _originalCustomSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;
        }

        public void Dispose()
        {
            // 工具路径与引擎优先级是**进程级**静态：用例之间必须还原，免得漏到下一个。
            ToolLocator.Default.CustomSevenZipExePath = _originalCustomSevenZipPath;
            ToolLocator.Default.Invalidate();
            EngineRuntimeSettings.ResetToDefaults();

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

        // ================================================================ ③ 分卷标记剥离：只有一份实现

        /// <summary>
        /// 同一组文件名，两条路（归档基名 / 包基名）必须给出**完全相同**的基名。
        ///
        /// 为什么这条重要：归档基名服务于"改名"，包基名服务于"落点目录"。
        /// 两边剥分卷标记的算法一旦不一致，同一个包会被算成两个名字，落点就指到两个不同的目录 ——
        /// 这正是体检报告 §2 第 2 条（两份逐字相同的 40 行）要防的事。
        /// </summary>
        [Theory]
        [InlineData("x.7z.001", "x.7z", "x")]
        [InlineData("x.zip.001", "x.zip", "x")]
        [InlineData("x.part1.rar", "x", "x")]
        [InlineData("x.r00", "x", "x")]
        [InlineData("x.z01", "x", "x")]
        [InlineData("x.tar.gz", "x.tar.gz", "x")]
        [InlineData("222.7z.001", "222.7z", "222")]
        [InlineData("222.part01.rar", "222", "222")]
        public void 分卷标记剥离_归档基名与包基名给出完全相同的基名(
            string fileName,
            string expectedAfterStrip,
            string expectedBaseName)
        {
            // ① 剥分卷标记这一段只剩 FileNameHelper 一处实现。
            Assert.Equal(expectedAfterStrip, FileNameHelper.StripVolumeMarkers(fileName));

            string path = Path.Combine(@"C:\111", fileName);

            // ② 归档基名（FileNameHelper）与包基名（OutputPlacement 的落点公式）必须同解。
            Assert.Equal(expectedBaseName, FileNameHelper.GetArchiveBaseName(path));
            Assert.Equal(expectedBaseName, OutputPlacement.ResolveArchiveBaseName(path));

            // ③ 落点目录名也必须用同一个基名（不是"某一条路另算一遍"）。
            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                path,
                OutputPlacementMode.PerArchiveSubfolder);

            Assert.True(placement.Success);
            Assert.Equal(expectedBaseName, placement.ArchiveBaseName);
            Assert.Equal(Path.Combine(@"C:\111", expectedBaseName), placement.DestinationDirectory);
        }

        /// <summary>
        /// "<c>222.rar</c> 里的 <c>222</c> 不是分卷段" —— 剥法里那条守卫（<c>previousDot &gt; 0</c>）
        /// 的唯一判据测试：没有它，整个包名会被当成三位数字分卷段吃光，落点变成上一级目录。
        /// </summary>
        [Fact]
        public void 分卷标记剥离_不许把普通包名的数字段当分卷吃光()
        {
            Assert.Equal("222.rar", FileNameHelper.StripVolumeMarkers("222.rar"));
            Assert.Equal("222.7z", FileNameHelper.StripVolumeMarkers("222.7z"));
            Assert.Equal("222", FileNameHelper.StripVolumeMarkers("222"));
            Assert.Equal("movie.2024", FileNameHelper.StripVolumeMarkers("movie.2024"));

            // 真的分卷段照旧剥掉（守卫不能把该剥的也拦下来）。
            Assert.Equal("222", FileNameHelper.StripVolumeMarkers("222.part1.rar"));
            Assert.Equal("222", FileNameHelper.StripVolumeMarkers("222.001.rar"));
        }

        /// <summary>
        /// 两条路**故意**不同的地方也要钉住：非归档后缀（<c>movie.2024</c>）只有包基名保留它 ——
        /// 归档基名按"普通文件"剥一层，包基名只剥**已知归档后缀**。
        /// 这一条不是缺陷，而是 <c>OutputPlacement</c> 那条更严格规则的存在理由；
        /// 写进测试免得后人"顺手统一"把落点规则改坏。
        /// </summary>
        [Fact]
        public void 分卷之外的差异是既定设计_包基名只剥已知归档后缀()
        {
            Assert.Equal("movie", FileNameHelper.GetArchiveBaseName(@"C:\111\movie.2024"));
            Assert.Equal("movie.2024", OutputPlacement.ResolveArchiveBaseName(@"C:\111\movie.2024"));
        }

        /// <summary>分卷剥离的**定义**在生产代码里只允许一处 —— 副本复活即失败（体检报告 §2 第 2 条）。</summary>
        [Fact]
        public void 分卷标记剥离_生产代码里只允许一处实现()
        {
            string helper = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Helpers", "FileNameHelper.cs"));
            string placement = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Extraction", "OutputPlacement.cs"));

            Assert.Contains("public static string StripVolumeMarkers(string fileName)", helper, StringComparison.Ordinal);

            // 旧副本的形态就是"同文件里再写一个同名私有方法"。
            Assert.DoesNotContain("string StripVolumeMarkers(string fileName)", placement, StringComparison.Ordinal);

            /*
             * 2026-10-03 阶段 A 收口：包基名不再"先转调剥法出口、再自己剥一层归档后缀"，
             * 而是整条交给**唯一基名出口**的 `PackageName` 档 —— 判据仍只有一份，而且归档基名 /
             * 包基名 / 归组键三处现在共用同一个出口。
             */
            Assert.Contains("FileNameHelper.TryResolveVolumeBaseName(", placement, StringComparison.Ordinal);
            Assert.Contains("VolumeBaseNameLevel.PackageName", placement, StringComparison.Ordinal);

            int definitions = ProductionSourceFiles()
                .Count(p => File.ReadAllText(p).Contains(
                    "string StripVolumeMarkers(string fileName)",
                    StringComparison.Ordinal));

            Assert.Equal(1, definitions);

            // 剥法的**档体**（那个循环）也只有一处：⛔ 不许在别的文件里再抄一遍。
            int cores = ProductionSourceFiles()
                .Count(p => File.ReadAllText(p).Contains(
                    "StripVolumeMarkersCore(",
                    StringComparison.Ordinal));

            Assert.Equal(1, cores);
        }

        // ================================================================ ② 同名冲突判定表：只有一份

        /// <summary>
        /// <c>RenameService</c> 那份死的第二份判定表必须彻底不在。
        ///
        /// 旧副本留着的不只是 40 行冗余：它还带着项目声称已修掉的缺陷（"Ask" 档被当成 AutoRename，
        /// 于是"用户选了询问"变成"静默改名"）。活的判定表在 <c>PathService.ResolveConflict</c>，
        /// <c>RenameService</c> 只调它。
        /// </summary>
        [Fact]
        public void 冲突判定表只有一处_RenameService不许再带第二份()
        {
            Assert.Null(typeof(ArchiveFixer.Services.RenameService).GetMethod("ResolveConflict"));

            string renameService = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Services", "RenameService.cs"));

            Assert.DoesNotContain("public string ResolveConflict", renameService, StringComparison.Ordinal);

            // :273 那句注释（"判定表只有一处实现"）现在与代码一致：文件里确实只剩对 PathService 的调用。
            Assert.Contains("pathService.ResolveConflict(", renameService, StringComparison.Ordinal);
        }

        // ================================================================ ④ 引擎能力筛选：规则只有一份

        /// <summary>
        /// 白名单引擎"没登记 = 干不了"、通用引擎"没登记就退回总体能力位" —— 三种操作（list/test/extract）
        /// 全部同解，且具名问法与通用问法走的是同一份实现。
        /// </summary>
        [Fact]
        public void 能力筛选_白名单引擎没登记就是不支持_三种操作同解()
        {
            EngineCapabilities rarOnly = RarOnlyCapabilities();
            EngineCapabilities general = GeneralCapabilities();

            foreach (EngineOperation operation in new[]
                     {
                         EngineOperation.List,
                         EngineOperation.Test,
                         EngineOperation.Extract
                     })
            {
                // 专用引擎（白名单）：未登记的 zip / 7z / tar 一律"干不了"。
                // 不这样判，它们会退回总体能力位，于是全被路由到只认 RAR 的 UnRAR 上。
                Assert.False(rarOnly.CanFormat("ZIP", operation));
                Assert.False(rarOnly.CanFormat("7Z", operation));
                Assert.False(rarOnly.CanFormat("TAR", operation));
                Assert.False(rarOnly.CanFormat(null, operation));

                Assert.True(rarOnly.CanFormat("RAR5", operation));

                // 通用引擎：未登记的格式仍让它去试一把（宁可试错，也不因为"没登记"就拒绝）。
                Assert.True(general.CanFormat("ZIP", operation));
                Assert.True(general.CanFormat("没登记过的格式", operation));
            }

            // 三个具名问法都必须落在同一份实现上（选择器的 Extract/List/Test 档分别调它们）。
            Assert.False(rarOnly.CanExtractFormat("ZIP"));
            Assert.False(rarOnly.CanListFormat("ZIP"));
            Assert.False(rarOnly.CanTestFormat("ZIP"));
            Assert.True(rarOnly.CanExtractFormat("rar5"));
        }

        /// <summary>登记过的格式按它**自己那几位**判：能列目录不等于能解压。</summary>
        [Fact]
        public void 能力筛选_登记过的格式按它自己那几位判()
        {
            var capabilities = new EngineCapabilities
            {
                CanList = true,
                CanTest = true,
                CanExtract = true,
                FormatsAreWhitelist = false,
                Formats = new List<EngineFormatCapability>
                {
                    new() { Format = "ZIP", CanList = true, CanTest = true, CanExtract = false },
                    new() { Format = "RAR5", CanExtract = true }
                }
            };

            Assert.False(capabilities.CanExtractFormat("ZIP"));
            Assert.True(capabilities.CanListFormat("ZIP"));
            Assert.True(capabilities.CanTestFormat("ZIP"));

            // 格式名不区分大小写（与 DetectResult.Format 同一套口径）。
            Assert.True(capabilities.CanExtractFormat("rar5"));
        }

        /// <summary>
        /// 选择器端到端：zip / 7z 不许落到只认 RAR 的引擎上，RAR 才走它 ——
        /// 也就是"本来能打开的包反而打不开"这个实测缺陷不许回来。
        /// </summary>
        [Fact]
        public void 选择器_不把zip与7z路由到只认RAR的引擎()
        {
            var rarEngine = new StubEngine(EngineIds.WinRar, RarOnlyCapabilities());
            var generalEngine = new StubEngine(EngineIds.SevenZip, GeneralCapabilities());

            var registry = new EngineRegistry();
            registry.Register(rarEngine);
            registry.Register(generalEngine);

            // 优先级刻意让 RAR 引擎排第一 —— 能力筛选是硬门槛，优先级只在"都能干"时起作用。
            var selector = new EngineSelector(registry, EngineIds.DefaultPriority);

            Assert.Same(generalEngine, selector.SelectFor("ZIP", EngineOperation.Extract));
            Assert.Same(generalEngine, selector.SelectFor("7Z", EngineOperation.Extract));
            Assert.Same(generalEngine, selector.SelectFor("TAR", EngineOperation.List));
            Assert.Same(rarEngine, selector.SelectFor("RAR5", EngineOperation.Extract));

            // 白名单引擎不许出现在 zip 的候选里（连"试一把"都不行）。
            EngineSelection selection = selector.Explain("ZIP", EngineOperation.Extract);

            Assert.DoesNotContain(rarEngine, selection.Candidates);
            Assert.Same(generalEngine, selection.Selected);
        }

        /// <summary>
        /// 能力判定只有一处实现：选择器里不许再内联那份判断，
        /// 而**实测教训注释**必须留在带名字的规范版（<c>EngineCapabilities</c>）那一处。
        /// </summary>
        [Fact]
        public void 引擎筛选规则只有一处实现_选择器不许再内联判断()
        {
            string selector = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Engines", "EngineSelector.cs"));
            string capabilities = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Engines", "EngineCapabilities.cs"));

            Assert.DoesNotContain("FormatsAreWhitelist", selector, StringComparison.Ordinal);
            Assert.Contains("CanExtractFormat(detectedFormat)", selector, StringComparison.Ordinal);

            // 教训必须留在白名单开关旁边：改错就会把 zip / 7z / tar 全路由到 UnRAR。
            Assert.Contains("FormatsAreWhitelist", capabilities, StringComparison.Ordinal);
            Assert.Contains("UnRAR", capabilities, StringComparison.Ordinal);
        }

        // ================================================================ ① 引擎不可用提示：由 ToolLocator 现算

        /// <summary>
        /// 提示里的两条期望路径必须**跟着真实解析结果走**。
        ///
        /// 判据是"把自定义 7z 路径指到一个真的存在的文件，提示里的期望路径跟着变"——
        /// 写死的文案不可能跟着走。这正是体检报告 §4 第 1 条的判据。
        /// </summary>
        [Fact]
        public void 引擎不可用提示_两条期望路径都由ToolLocator现算()
        {
            ToolLocator locator = ToolLocator.Default;

            string custom = Path.Combine(_root, "custom-7z", "7z.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
            File.WriteAllText(custom, "stub");

            locator.CustomSevenZipExePath = custom;

            try
            {
                string message = locator.DescribeNoEngineAvailable();

                Assert.Contains(custom, message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(locator.UnRarExePath, message, StringComparison.OrdinalIgnoreCase);

                // 两个引擎都要点名，并说清"任装其一即可"。
                Assert.Contains("7-Zip", message, StringComparison.Ordinal);
                Assert.Contains("UnRAR", message, StringComparison.Ordinal);
                Assert.Contains("任装其一即可", message, StringComparison.Ordinal);

                // 优先级顺序也必须说清（默认 WinRAR 系在前）。
                Assert.Contains(ToolLocator.DescribePriority(), message, StringComparison.Ordinal);
            }
            finally
            {
                locator.CustomSevenZipExePath = _originalCustomSevenZipPath;
                locator.Invalidate();
            }
        }

        /// <summary>优先级的人读顺序跟着运行时设置走（不是代码里排好的）。</summary>
        [Fact]
        public void 引擎不可用提示_优先级顺序跟着设置走()
        {
            try
            {
                EngineRuntimeSettings.ResetToDefaults();
                Assert.Equal("UnRAR（RAR 系） → 7-Zip", ToolLocator.DescribePriority());

                EngineRuntimeSettings.SetPriority(new[] { EngineIds.SevenZip, EngineIds.WinRar });
                Assert.Equal("7-Zip → UnRAR（RAR 系）", ToolLocator.DescribePriority());

                Assert.Contains(
                    "7-Zip → UnRAR（RAR 系）",
                    ToolLocator.Default.DescribeNoEngineAvailable(),
                    StringComparison.Ordinal);
            }
            finally
            {
                EngineRuntimeSettings.ResetToDefaults();
            }
        }

        /// <summary>
        /// 两个调用方（解压前的报错、引擎解析器的错误文案）都必须走 ToolLocator，
        /// 生产代码里不许再出现写死的 <c>tools\7zip</c> 提示。
        /// </summary>
        [Fact]
        public void 引擎不可用提示_调用方不许再写死7z路径()
        {
            string coordinator = File.ReadAllText(RepoPath("src", "ArchiveFixer", "ViewModels", "ExtractionCoordinator.cs"));
            string parser = File.ReadAllText(RepoPath("src", "ArchiveFixer", "Engines", "SevenZip", "SevenZipOutputParser.cs"));

            Assert.Contains("ToolLocator.Default.DescribeNoEngineAvailable()", coordinator, StringComparison.Ordinal);
            Assert.Contains("ToolLocator.Default.DescribeNoEngineAvailable()", parser, StringComparison.Ordinal);

            Assert.DoesNotContain("未找到 tools", coordinator, StringComparison.Ordinal);
            Assert.DoesNotContain("未找到 tools", parser, StringComparison.Ordinal);
            Assert.DoesNotContain("tools\\\\7zip", coordinator, StringComparison.Ordinal);
            Assert.DoesNotContain("tools\\\\7zip", parser, StringComparison.Ordinal);

            // 解析器报"引擎全不可用"时，用户看到的也必须是那两条期望路径。
            string message = SevenZipOutputParser.ErrorTypeToMessage("SevenZipMissing");

            Assert.Contains(ToolLocator.Default.SevenZipExePath, message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ToolLocator.Default.UnRarExePath, message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("任装其一即可", message, StringComparison.Ordinal);
        }

        // ================================================================ ⑥ 超时：结构化判定，不拿中文文案当接口

        /// <summary>
        /// 超时靠**哨兵退出码**（<c>-3</c>，运行器强制结束进程时写的）判定，
        /// 不靠匹配我们自己产出的中文提示；而"取消"是另一个哨兵值（<c>-2</c>），不许串档。
        ///
        /// ⚠ 这条测试的价值不在"超时能判出来"（那一直都能），而在**判据是结构化的**：
        /// 文案改了、换了措辞、加了空格，分类都不受影响。
        /// </summary>
        [Fact]
        public void 超时按结构化哨兵退出码归类()
        {
            var runner = new SevenZipProcessRunner();

            ArchiveOperationResult timedOut = runner.AnalyzeResult(
                SevenZipOutputParser.TimedOutExitCode,
                string.Empty,
                string.Empty);

            Assert.False(timedOut.Success);
            Assert.Equal(EngineErrorTypes.TimedOut, timedOut.DetectedErrorType);

            // 不变量 6：超时不得显示成功。
            Assert.NotEqual(StatusText.ProgressCompleted, timedOut.Status);

            // 取消是另一个哨兵值。
            ArchiveOperationResult cancelled = runner.AnalyzeResult(
                SevenZipOutputParser.CancelledExitCode,
                string.Empty,
                string.Empty);

            Assert.Equal(EngineErrorTypes.Cancelled, cancelled.DetectedErrorType);

            // 结果工厂与解析器认的是同一个哨兵值（两边写岔了就会静默变成"未知错误"）。
            ArchiveOperationResult factory = ArchiveOperationResult.CreateTimedOut(TimeSpan.FromSeconds(1));

            Assert.Equal(SevenZipOutputParser.TimedOutExitCode, factory.ExitCode);
            Assert.Equal(EngineErrorTypes.TimedOut, factory.DetectedErrorType);
        }

        /// <summary>
        /// 旧判据必须彻底失效：**只有**我们自己的中文超时提示、退出码却是普通值（2 = 致命错误）时，
        /// 结论只能是致命错误，不能是超时 —— 那正是"改中文文案就静默失效"的反面。
        /// </summary>
        [Fact]
        public void 超时不再靠中文文案判定()
        {
            Assert.NotEqual(
                EngineErrorTypes.TimedOut,
                SevenZipOutputParser.DetectSevenZipErrorType(
                    2,
                    "7-Zip 执行超时，已强制结束进程",
                    string.Empty));

            // 外部程序自己打的英文超时字样仍然认（那不是我们的文案）。
            Assert.Equal(
                EngineErrorTypes.TimedOut,
                SevenZipOutputParser.DetectSevenZipErrorType(2, "ERROR: operation timed out", string.Empty));

            // 给用户看的那句文案还在（分类换了判据，用户该看到的结论不能跟着消失）。
            Assert.Contains(
                "超时",
                SevenZipOutputParser.ErrorTypeToMessage(EngineErrorTypes.TimedOut),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// 源码守卫：<c>SevenZipOutputParser.cs</c> 里那句中文只允许出现在**给用户看的文案**
        /// （<c>ErrorTypeToMessage</c> 的 TimedOut 分支），决不允许再出现在任何判定里。
        /// </summary>
        [Fact]
        public void 超时的中文文案只允许出现在用户可见文案里()
        {
            string path = RepoPath("src", "ArchiveFixer", "Engines", "SevenZip", "SevenZipOutputParser.cs");

            string[] hits = File.ReadAllLines(path)
                .Select((line, index) => new { Line = line.Trim(), Number = index + 1 })
                .Where(x => x.Line.Contains("执行超时", StringComparison.Ordinal))
                .Select(x => $"第 {x.Number} 行：{x.Line}")
                .ToArray();

            Assert.NotEmpty(hits);

            Assert.All(
                hits,
                hit => Assert.Contains("\"TimedOut\" =>", hit, StringComparison.Ordinal));
        }

        // ================================================================ ⑤ 死类：DelegateDeleteLogSink

        /// <summary>
        /// 删除日志的委托适配器**整类**已删：接口还在（界面日志、文件日志、测试自己的收集器都实现它），
        /// 但那个零引用的适配器不许复活 —— 留着它只会让人以为"接日志"要走它。
        /// </summary>
        [Fact]
        public void 删除日志的委托适配器整类已删且无残留引用()
        {
            Type? removed = typeof(RecycleBinService).Assembly.GetType("ArchiveFixer.Storage.DelegateDeleteLogSink");

            Assert.Null(removed);

            // 接口本身必须还在（本服务对外"写日志"的唯一形态）。
            Assert.NotNull(typeof(RecycleBinService).Assembly.GetType("ArchiveFixer.Storage.IDeleteLogSink"));

            string[] hits = ProductionSourceFiles()
                .Where(p => File.ReadAllText(p).Contains("DelegateDeleteLogSink", StringComparison.Ordinal))
                .ToArray();

            Assert.Empty(hits);
        }

        // ================================================================ 共用替身 / 探针 / 工具

        /// <summary>RAR 专用引擎的能力位（白名单：只认 RAR 系列 —— 与 <c>UnRarEngine</c> 一致）。</summary>
        private static EngineCapabilities RarOnlyCapabilities() => new()
        {
            CanList = true,
            CanTest = true,
            CanExtract = true,
            SupportsPassword = true,
            SupportsMultiVolume = true,
            FormatsAreWhitelist = true,
            Formats = new List<EngineFormatCapability>
            {
                new() { Format = "RAR5", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "RAR4", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "RAR", CanList = true, CanTest = true, CanExtract = true }
            }
        };

        /// <summary>通用引擎的能力位（不设白名单：没登记的格式也让它去试一把 —— 与 7-Zip 一致）。</summary>
        private static EngineCapabilities GeneralCapabilities() => new()
        {
            CanList = true,
            CanTest = true,
            CanExtract = true,
            SupportsPassword = true,
            FormatsAreWhitelist = false,
            Formats = new List<EngineFormatCapability>
            {
                new() { Format = "ZIP", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "7Z", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "TAR", CanList = true, CanTest = true, CanExtract = true },
                new() { Format = "RAR5", CanList = true, CanTest = true, CanExtract = true }
            }
        };

        /// <summary>只回答"我是谁、我能干什么"的替身引擎：本组用例只问选择器，不让它真干活。</summary>
        private sealed class StubEngine : IArchiveEngine
        {
            public StubEngine(string id, EngineCapabilities capabilities)
            {
                Id = id;
                Capabilities = capabilities;
            }

            public string Id { get; }

            public string DisplayName => Id + "（替身）";

            public string Version => "9.9-test";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; }

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "Unknown" });

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult { Success = true, EngineId = Id, EngineVersion = Version });

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero)
                    .StampEngine(Id, DisplayName, Version));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateSuccess(0, "OK", string.Empty, TimeSpan.Zero)
                    .StampEngine(Id, DisplayName, Version));
        }

        /// <summary>生产代码的源码文件（排除 <c>obj\</c> / <c>bin\</c> 里的生成物）。</summary>
        private static IEnumerable<string> ProductionSourceFiles()
        {
            string separator = Path.DirectorySeparatorChar.ToString();

            return Directory
                .EnumerateFiles(RepoPath("src", "ArchiveFixer"), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase)
                         && !p.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>仓库根下的文件路径（从测试输出目录往上找 ArchiveFixer.slnx）。</summary>
        private static string RepoPath(params string[] parts)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("找不到仓库根（ArchiveFixer.slnx）。");
        }
    }
}
