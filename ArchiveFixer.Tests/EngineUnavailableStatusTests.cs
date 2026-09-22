using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「没有可用的解压引擎」这个状态的三处接线（AGENTS.md §7：新增状态必须同时更新
    /// <c>StatusText</c> + <c>StatusToBrushConverter</c> + <c>TaskSummaryService</c>）。
    ///
    /// <para><b>为什么要新增它</b>：以前一个引擎都找不到时用的是 <c>StatusText.SevenZipMissing</c>
    /// （文案「7z不存在」），而判据其实是"<b>任一</b>引擎可用"（<c>EngineRouter.IsAvailable</c>），
    /// 默认优先级又是 UnRAR → 7-Zip。于是"只缺 UnRAR"或"把内置件关掉了"的用户会看到
    /// 「7z不存在」—— 被引去修一个本来没问题的 7z 目录。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class EngineUnavailableStatusTests : IDisposable
    {
        private readonly string _root;

        public EngineUnavailableStatusTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerNoEngine", Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void 新状态与7z不存在分开_文案不再指错方向()
        {
            Assert.NotEqual(StatusText.SevenZipMissing, StatusText.NoEngineAvailable);

            Assert.Contains("引擎", StatusText.NoEngineAvailable, StringComparison.Ordinal);

            // 「7z」这三个字一旦出现在这里，就又变成"指着一个具体工具"了。
            Assert.DoesNotContain("7z", StatusText.NoEngineAvailable, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 三处同改_配色按失败_统计进解压失败桶()
        {
            // ① 配色：失败类状态用错误色（与"7z不存在"同色，才不会有"一个红一个黑"的错觉）。
            var converter = new Converters.StatusToBrushConverter();
            var expectedError = (Brush)converter.Convert(StatusText.SevenZipMissing, typeof(Brush), null!, CultureInfo.InvariantCulture);
            var actual = (Brush)converter.Convert(StatusText.NoEngineAvailable, typeof(Brush), null!, CultureInfo.InvariantCulture);

            Assert.Same(expectedError, actual);

            // ② 统计分桶：归"解压失败"，不是"待处理"（否则汇总里会凭空少一个任务）。
            var task = new ArchiveTask(@"C:\t\222.7z", 1) { Status = StatusText.NoEngineAvailable };

            Assert.Equal(SummaryBucket.ExtractFailed, TaskSummaryService.ClassifyOutcome(task));

            var service = new TaskSummaryService();
            TaskSummary summary = service.BuildSummary(new[] { task });

            Assert.Equal(1, summary.TotalCount);
            Assert.Equal(1, summary.ExtractFailedCount);
            Assert.Equal(0, summary.OtherFailedCount);

            // ③ 失败清单：必须收得进（这是用户唯一的行动线索）。
            Assert.True(service.IsFailedStatus(StatusText.NoEngineAvailable));
            Assert.Single(service.GetFailedTasks(new[] { task }));
        }

        [Fact]
        public async Task 一个引擎都不可用时_报的是新状态而不是7z不存在()
        {
            string archivePath = Path.Combine(_root, "222.7z");

            // 真 7z 魔数（识别得出来格式，但没有任何可用引擎能接手）。
            File.WriteAllBytes(archivePath, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 });

            /*
             * 用**假引擎**而不是"把路径指到不存在的地方"：ToolLocator 有两级回落
             * （用户自选 → 程序内置），而测试输出目录里真的带着内置的 7z 与 UnRAR ——
             * 指一个不存在的路径只会回落到内置件，两个引擎照样是"可用"的（实测就是这么被打脸的）。
             * 这里要验的是"一个可用引擎都没有"时**报什么状态**，与真实工具在不在无关。
             */
            var registry = new EngineRegistry();
            registry.Register(new UnavailableEngine(EngineIds.WinRar));
            registry.Register(new UnavailableEngine(EngineIds.SevenZip));

            Assert.All(registry.Engines, engine => Assert.False(engine.IsAvailable));

            var router = new EngineRouter(registry);

            Assert.False(router.IsAvailable);

            ArchiveOperationResult result = await router.ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = archivePath,
                    Password = string.Empty,
                    OutputPath = Path.Combine(_root, "out")
                },
                new ExtractOptions { CustomOutputDirectory = Path.Combine(_root, "out") });

            Assert.False(result.Success);
            Assert.Equal(StatusText.NoEngineAvailable, result.Status);

            /*
             * 提示也必须把**两个**引擎都点到名、并给出下一步（"去检查 tools 目录或设置里的路径"）——
             * 只说"7z 不存在"会把只缺 UnRAR 的用户引到错的地方。
             */
            Assert.Contains("7-Zip", result.Message, StringComparison.Ordinal);
            Assert.Contains("UnRAR", result.Message, StringComparison.Ordinal);
            Assert.Contains("检查", result.Message, StringComparison.Ordinal);
        }

        /// <summary>永远"没检测到"的假引擎（只用来构造"一个可用引擎都没有"这一种局面）。</summary>
        private sealed class UnavailableEngine : IArchiveEngine
        {
            public UnavailableEngine(string id)
            {
                Id = id;

                Capabilities = new EngineCapabilities
                {
                    CanList = true,
                    CanTest = true,
                    CanExtract = true,
                    FormatsAreWhitelist = true,
                    Formats = new List<EngineFormatCapability>
                    {
                        new() { Format = "7Z", CanList = true, CanTest = true, CanExtract = true },
                        new() { Format = "RAR5", CanList = true, CanTest = true, CanExtract = true }
                    }
                };
            }

            public string Id { get; }

            public string DisplayName => Id;

            public string Version => "0.0";

            public bool IsAvailable => false;

            public EngineCapabilities Capabilities { get; }

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = false, Format = "Unknown" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveListResult.Failure(EngineErrorTypes.EngineUnavailable, "不可用", Id, Version));

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateFailure(
                    -1, string.Empty, string.Empty, StatusText.NoEngineAvailable, "引擎不可用", EngineErrorTypes.EngineUnavailable, TimeSpan.Zero));

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(ArchiveOperationResult.CreateFailure(
                    -1, string.Empty, string.Empty, StatusText.NoEngineAvailable, "引擎不可用", EngineErrorTypes.EngineUnavailable, TimeSpan.Zero));
        }

        [Fact]
        public void 启动日志里的引擎缺失提示不再写死7z路径()
        {
            // 静态钉住（MainViewModel 的启动日志）：判据是"任一引擎可用"，
            // 所以那句提示必须由 ToolLocator 现算 —— 不许再出现写死的 tools\7zip\7z.exe 文案。
            string source = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer", "ViewModels", "MainViewModel.cs"));

            Assert.Contains("DescribeNoEngineAvailable", source, StringComparison.Ordinal);

            // 旧文案（写死路径 + "解压时会失败"）必须消失。
            Assert.DoesNotContain(
                "未找到 tools\\\\7zip\\\\7z.exe，软件可启动",
                source,
                StringComparison.Ordinal);
        }
    }
}
