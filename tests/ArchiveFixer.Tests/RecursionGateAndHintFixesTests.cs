using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static ArchiveFixer.Tests.RecursionFixHarness;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-10-05 只读审计：**递归那条路（出厂默认档 = 展开所有分支）缺的闸门与提示**。
    ///
    /// <para>这一组钉三件事（每一条都有对应的红检：撤掉修复 ⇒ 用例变红）：</para>
    /// <list type="number">
    /// <item>单文件大小上限：递归上限要映设置里那一条，且拿本层清单真判、点名是哪个条目（拿不到清单 ⇒ 不拦）；</item>
    /// <item>可疑条目提示：递归层拿到清单后回传结论，由协调器写进与单层路径同一个字段（关掉设置就一个字都不写）；</item>
    /// <item>清工作区前的**第二道**容器内校验：目录里只许有我们自己造的子目录（越界只写 WARN、一个字节都不删）。</item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RecursionGateAndHintFixesTests : IDisposable
    {
        private readonly string _root;

        public RecursionGateAndHintFixesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerGateFix", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        // ================================================================ ⑥ 单文件上限

        /// <summary>
        /// 递归上限里的**单文件**那一条必须跟着⑥设置走（四条一条都不许漏映）。
        ///
        /// <para><b>红检</b>：撤掉 <c>BuildRecursionLimits</c> 里的
        /// <c>MaxSingleFileSize = BudgetLimits.MaxSingleFileSize</c> ⇒ 本用例变红
        /// （拿到的是默认 64 GiB，而设置里是 3 GiB）。</para>
        /// </summary>
        [Fact]
        public void 递归上限里的单文件上限跟着设置走()
        {
            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                Array.Empty<string>(),
                attemptLimit: 10);

            harness.Vm.Settings.MaxSingleExtractedFileGiB = 3;

            Assert.Equal(3L * 1024 * 1024 * 1024, harness.Coordinator.BuildRecursionLimits().MaxSingleFileSize);

            harness.Vm.Settings.MaxSingleExtractedFileGiB = 7;

            Assert.Equal(7L * 1024 * 1024 * 1024, harness.Coordinator.BuildRecursionLimits().MaxSingleFileSize);
        }

        /// <summary>
        /// 本层清单里最大的条目超过单文件上限 ⇒ **停下**，而且拒绝文案要与解压前预算同口径：
        /// 点名是哪个条目、多大、上限多少，并带上 <see cref="StatusText.SecurityCapHint"/>。
        ///
        /// <para><b>红检</b>：撤掉候选循环里那段单文件上限判据 ⇒ 引擎照旧被调去解压、
        /// 停因变成 <see cref="RecursionStopReason.Completed"/>，本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 本层清单里有超大条目_递归层按单文件上限停下并点名()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("movie.bin", 4096L), ("readme.txt", 10L)),
                OnExtract = request => ExtractSucceeded(request.OutputPath)
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                new RecursionLimits { MaxSingleFileSize = 1024 },
                out List<string> logs);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "single-file-cap.7z");

            Assert.Equal(RecursionStopReason.MaxSingleFileSizeReached, result.StopReason);

            // 一个字节都没解（拦在解压之前）。
            Assert.Empty(engine.Extracted);

            RecursionLayerReport layer = Assert.Single(result.Layers);

            Assert.Contains("movie.bin", layer.Message, StringComparison.Ordinal);
            Assert.Contains("4096", layer.Message, StringComparison.Ordinal);
            Assert.Contains("1024", layer.Message, StringComparison.Ordinal);
            Assert.Contains(StatusText.SecurityCapHint, layer.Message, StringComparison.Ordinal);

            // 停因那句话与"累计总大小上限"同档（都是"撞上程序的安全上限"）。
            Assert.Contains(
                StatusText.RecursionSingleFileSizeReachedReason,
                result.Summary,
                StringComparison.Ordinal);

            Assert.Contains(logs, line => line.Contains("movie.bin", StringComparison.Ordinal));
        }

        /// <summary>
        /// 最大条目**正好等于**上限时不许拦（判据是"超过"，与 <c>ResourceBudget</c> 同一个比较）。
        /// </summary>
        [Fact]
        public async Task 最大条目正好等于上限时不拦()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("exact.bin", 1024L)),
                OnExtract = request => ExtractSucceeded(request.OutputPath)
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                new RecursionLimits { MaxSingleFileSize = 1024 },
                out _);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "single-file-exact.7z");

            Assert.Equal(RecursionStopReason.Completed, result.StopReason);
            Assert.Single(engine.Extracted);
        }

        /// <summary>
        /// ⛔ **拿不到清单 ⇒ 不拦**（与展开比那一条的兜底同口径）：加密头包 / 列目录失败时
        /// 盘上那份清单根本不存在，凭"不知道"去拦会把正常包误杀。
        /// </summary>
        [Fact]
        public async Task 拿不到清单时不按单文件上限拦()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => ListingFailure(EngineErrorTypes.UnknownError),
                OnExtract = request => ExtractSucceeded(request.OutputPath)
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                new RecursionLimits { MaxSingleFileSize = 1024 },
                out _);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "single-file-no-list.7z");

            Assert.Equal(RecursionStopReason.Completed, result.StopReason);
            Assert.Single(engine.Extracted);
        }

        // ================================================================ ⑦a 可疑条目提示

        /// <summary>
        /// 递归层拿到本层清单之后要把"可疑条目"的结论回传给调用方 ——
        /// 判据与文案仍是单层路径那一份（<c>ExtractionCoordinator.AnalyzeDangerousEntries</c>）。
        ///
        /// <para><b>红检</b>：撤掉候选循环里那次
        /// <c>DangerousEntriesReported?.Invoke(...)</c> ⇒ 回调一次都不发生，本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 递归层把可疑条目提示回传给调用方()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("evil.exe", 10L), ("readme.txt", 5L)),
                OnExtract = request => ExtractSucceeded(request.OutputPath)
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                limits: null,
                out _);

            extractor.ReportDangerousEntries = true;

            string? reported = null;
            extractor.DangerousEntriesReported = hint => reported = hint;

            await RunRecursionAsync(extractor, _root, "dangerous-entries.7z");

            Assert.False(string.IsNullOrEmpty(reported), "递归层必须把可疑条目提示回传出来");

            Assert.Contains("evil.exe", reported!, StringComparison.Ordinal);
            Assert.DoesNotContain("readme.txt", reported!, StringComparison.Ordinal);
            Assert.StartsWith(ArchiveFixer.ViewModels.ExtractionCoordinator.DangerousEntriesHintPrefix, reported!, StringComparison.Ordinal);
        }

        /// <summary>⑥设置里关掉那一格 ⇒ 判据返回空串，调用方不写字段、也不打日志（与加这条之前逐字相同）。</summary>
        [Fact]
        public async Task 关掉可疑条目提示时递归层一个字都不回传()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("evil.exe", 10L)),
                OnExtract = request => ExtractSucceeded(request.OutputPath)
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                limits: null,
                out _);

            extractor.ReportDangerousEntries = false;

            string? reported = null;
            extractor.DangerousEntriesReported = hint => reported = hint;

            await RunRecursionAsync(extractor, _root, "dangerous-entries-off.7z");

            Assert.True(string.IsNullOrEmpty(reported));
        }

        /// <summary>
        /// 端到端（出厂默认档 = 展开所有分支）：**内层包**里的 <c>.exe</c> 也要写进任务的
        /// <c>DangerousEntriesWarning</c> —— 源包本身没有可疑条目，所以这个字段只可能来自递归那一层。
        ///
        /// <para><b>红检</b>：撤掉 <c>RunRecursiveAsync</c> 里那句
        /// <c>recursiveExtractor.DangerousEntriesReported = …</c> ⇒ 字段为空，本用例变红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 默认档下内层包里的可疑条目也会写进任务字段()
        {
            RecursionFixHarness.RequireSevenZip();

            string outer = BuildOuterWithSuspiciousInner();

            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                Array.Empty<string>(),
                attemptLimit: 10,
                recursionMode: "AllBranches");

            ArchiveTask task = await harness.AddTaskAsync(outer);

            await harness.Coordinator.StartExtractAsync();

            /*
             * 判据 = **任务字段**（与单层路径写的是同一个字段：失败清单 / 详情窗都读它）。
             * ⚠ 不断言那一行日志：这条提示按 INFO 级别写，而任务的 INFO 行走**任务级缓冲**
             * （成功时随缓冲一起丢、只有"详细日志"或失败时才吐出来）—— 单层路径那一处 INFO 完全同一条口径，
             * 所以"默认档成功时日志里看不到"是既定的，不是这条修复该管的事。
             */
            Assert.Contains("evil.exe", task.DangerousEntriesWarning, StringComparison.Ordinal);
            Assert.Contains(
                ArchiveFixer.ViewModels.ExtractionCoordinator.DangerousEntriesHintPrefix,
                task.DangerousEntriesWarning,
                StringComparison.Ordinal);
        }

        // ================================================================ ⑦b 工作区清理第二道校验

        /// <summary>
        /// 白名单**只能是递归自己的形状**：<c>layer-NNN</c> / <c>carved</c> / <c>_密码预检</c>。
        ///
        /// <para>⛔ 单层路径那一套（<c>stage</c> / <c>volumes</c>）在这里必须判否 ——
        /// 套用它等于把递归工作区的每一次清理都拦下（那正是"关掉清理"）。</para>
        /// </summary>
        [Fact]
        public void 工作区白名单只认递归自己的形状()
        {
            Assert.True(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("layer-000"));
            Assert.True(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("layer-7"));
            Assert.True(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("carved"));
            Assert.True(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory(RecursiveExtractor.ProbeDirectoryName));

            // "看着像我们的"也不行（层号必须是纯数字）。
            Assert.False(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("layer-backup"));
            Assert.False(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("layer-"));

            // 别人的白名单不许拿来套（这正是"套错 = 等于关掉清理"的那一档）。
            Assert.False(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("stage"));
            Assert.False(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("volumes"));

            Assert.False(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory("别人的目录"));
            Assert.False(WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory(null));

            // 找"外来的那一个"：返回完整路径（日志要报位置）；全是自己的 ⇒ null。
            Assert.Null(WorkspaceCleanupGuard.FindForeignSubdirectory(
                new[] { @"C:\ws\t\layer-000", @"C:\ws\t\carved" },
                WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory));

            Assert.Equal(
                @"C:\ws\t\别人的",
                WorkspaceCleanupGuard.FindForeignSubdirectory(
                    new[] { @"C:\ws\t\layer-000", @"C:\ws\t\别人的" },
                    WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory));

            Assert.Null(WorkspaceCleanupGuard.FindForeignSubdirectory(
                null,
                WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory));
        }

        /// <summary>
        /// 工作区里混进了**不是我们造的**子目录 ⇒ 失败收尾那次清理**一个字节都不删**，
        /// 只写一条 WARN（与单层路径同一条红线）。
        ///
        /// <para><b>红检</b>：撤掉 <c>TryDiscardCurrentWorkspaceOnFailure</c> 里那道
        /// <c>HasOnlyOwnedWorkspaceSubdirectories</c> ⇒ 目录被整份删掉，本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 工作区里有外来子目录时_失败清理一个字节都不删()
        {
            (RecursiveExtractor extractor, List<string> logs) = await CreateFailingExtractorAsync();

            string taskDirectory = extractor.CurrentWorkspace!.TaskDirectory;

            Assert.True(Directory.Exists(taskDirectory));

            string foreign = Path.Combine(taskDirectory, "别人的东西");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "keep.txt"), "一个字节都不许删");

            Assert.False(extractor.TryDiscardCurrentWorkspaceOnFailure("测试任务"));

            Assert.True(Directory.Exists(taskDirectory), "越界时工作区目录不许被删掉");
            Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")), "外来目录里的文件一个字节都不许动");
            Assert.Contains(logs, line => line.Contains("非本任务造的子目录", StringComparison.Ordinal));
        }

        /// <summary>
        /// 形状正常（只有 <c>layer-NNN</c>）时失败收尾**照旧删掉** ——
        /// 新加的那道白名单不许把正常清理也拦下（那等于把清理整块关掉）。
        /// </summary>
        [Fact]
        public async Task 工作区形状正常时_失败清理照旧删掉()
        {
            (RecursiveExtractor extractor, _) = await CreateFailingExtractorAsync();

            string taskDirectory = extractor.CurrentWorkspace!.TaskDirectory;

            // 前提：目录里真的有一层的目录（否则这条用例什么都没验到）。
            Assert.NotEmpty(Directory.GetDirectories(taskDirectory));

            Assert.True(extractor.TryDiscardCurrentWorkspaceOnFailure("测试任务"));
            Assert.False(Directory.Exists(taskDirectory));
        }

        /// <summary>
        /// 成功那一支走的是**另一个**删除点（<c>CleanupWorkspace</c>）：那道也要过新白名单，
        /// 而正常的递归工作区（只有 <c>layer-NNN</c>）照旧被清掉。
        /// </summary>
        [Fact]
        public async Task 递归成功后_工作区照旧被清掉()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("data.bin", 100L)),
                OnExtract = request => ExtractSucceeded(request.OutputPath)
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                limits: null,
                out _);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "cleanup-success.7z");

            Assert.True(result.Completed, result.Summary);

            string taskDirectory = extractor.CurrentWorkspace!.TaskDirectory;

            Assert.False(
                Directory.Exists(taskDirectory),
                "成功之后工作区必须照旧被清掉（新加的容器内校验不许把它拦下）");
        }

        // ================================================================ 样本与夹具

        /// <summary>跑一次"必定失败"的递归（密码不对），返回手上还有工作区的那个递归核心。</summary>
        private async Task<(RecursiveExtractor Extractor, List<string> Logs)> CreateFailingExtractorAsync()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => ListingFailure(EngineErrorTypes.UnknownError),
                OnExtract = _ => WrongPassword()
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { "错的" },
                limits: null,
                out List<string> logs);

            RecursionResult result = await RunRecursionAsync(
                extractor,
                _root,
                "workspace-guard-" + Guid.NewGuid().ToString("N") + ".7z");

            Assert.False(result.Completed);

            return (extractor, logs);
        }

        /// <summary>
        /// 造 `outer.7z` → 里面有 `inner.zip` → 里面有 `evil.exe` 与 `readme.txt`。
        /// 源包本身**没有**可疑条目，所以提示只可能来自递归那一层。
        /// </summary>
        private string BuildOuterWithSuspiciousInner()
        {
            string innerStage = Path.Combine(_root, "inner-stage-" + Guid.NewGuid().ToString("N"));
            string outerStage = Path.Combine(_root, "outer-stage-" + Guid.NewGuid().ToString("N"));
            string packages = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(innerStage);
            Directory.CreateDirectory(outerStage);
            Directory.CreateDirectory(packages);

            File.WriteAllText(Path.Combine(innerStage, "evil.exe"), "MZ-fake-payload");
            File.WriteAllText(Path.Combine(innerStage, "readme.txt"), "内层包的说明文件");

            string innerZip = Path.Combine(outerStage, "inner.zip");

            RecursionFixHarness.Run7z(innerStage, "a", "-tzip", innerZip, "evil.exe", "readme.txt");

            string outer = Path.Combine(packages, "outer.7z");

            RecursionFixHarness.Run7z(outerStage, "a", "-t7z", "-mx0", outer, "inner.zip");

            Assert.True(File.Exists(outer), "外层包没造出来：" + outer);

            return outer;
        }
    }
}
