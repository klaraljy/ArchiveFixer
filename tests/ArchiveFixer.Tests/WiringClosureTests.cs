using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 收口接线回归：把"前四个代理互相移交、但没人接上"的几处接起来之后的看门测试。
    ///
    /// <list type="number">
    /// <item>落点（<c>OutputPlacement</c>）真的从设置 / 选项传到管线，两处算出的落点永远同一个；</item>
    /// <item>一致性铁律：同一个包在界面「输出目录」列与管线里算出的落点必须一致（手动档那个开关正是这么逮到的）；</item>
    /// <item>清理服务（过程物 / 空文件夹）有可被界面调用的"预览 + 执行"两步。</item>
    /// </list>
    ///
    /// 全部只动 <see cref="Path.GetTempPath"/> 下的临时目录，<see cref="Dispose"/> 里删干净；
    /// **绝不碰系统回收站**：删除档位一律通过注入的假执行器走
    /// （真实回收站在单测里不可接受 —— 跑一次测试就往用户回收站塞垃圾）。
    /// </summary>
    public class WiringClosureTests : IDisposable
    {
        private readonly string _root;

        public WiringClosureTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWiringTests", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        private string PathOf(string relativePath)
        {
            return Path.Combine(_root, relativePath);
        }

        private string WriteFile(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private string MakeDirectory(string relativePath)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 造一个"包有自己的输出目录"的任务（默认模式）：源包在别处，输出目录就是 <c>out</c>。
        ///
        /// 源包路径刻意放在 <c>src</c> 下 —— 源包不在输出目录里，判定就是"非共享目录"，
        /// 这也是最普通、最该只删自己那一份的情形。
        /// </summary>
        private ArchiveTask OutputOnlyTask(string outputRelativePath)
        {
            string sourceArchive = WriteFile(@"src\111.rar", "rar");

            return new ArchiveTask(sourceArchive) { OutputPath = PathOf(outputRelativePath) };
        }

        // ================================================================ ① 简洁档（终端落法已退役）

        /// <summary>
        /// 终端落法那一档**已退役**（用户 2026-09-27："这个可以删除掉，默认就是这样的情况"），
        /// 场景 B 塌缩也整条退役（"一律不塌缩"）；换上来的是**简洁档**开关，**默认关**（忠实档）。
        /// </summary>
        [Fact]
        public void 设置默认值_简洁档默认关_终端落法与塌缩字段都不在了()
        {
            AppSettings defaults = AppSettings.CreateDefault();

            Assert.False(defaults.OmitMiddleContinuationLayers, "简洁档默认关 = 忠实档（每层包名都留）");
        }

        /// <summary>旧配置里那两项（TerminalLayoutMode / CollapseRepeatedFolderLayer）读进来不报错、也不影响新行为。</summary>
        [Fact]
        public void 旧配置带退役字段_不报错且取到新默认值()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            Directory.CreateDirectory(_root);
            File.WriteAllText(
                pathService.SettingsFilePath,
                "{ \"RecursionMode\": \"SingleChain\", \"MaxRecursionDepth\": 4, " +
                "\"TerminalLayoutMode\": \"UseArchiveName\", \"CollapseRepeatedFolderLayer\": false }",
                new UTF8Encoding(false));

            AppSettings loaded = service.Load();

            Assert.Equal("SingleChain", loaded.RecursionMode);
            Assert.Equal(4, loaded.MaxRecursionDepth);
            Assert.False(loaded.OmitMiddleContinuationLayers);
        }

        /// <summary>
        /// 设置文件里**不再写**退役的两个键，新键**必须写**（落点模型 v2 的序列化收口）。
        ///
        /// <para>为什么值得单独钉：`AppSettings` 删属性时如果只删了界面绑定、忘了删落盘字段，
        /// 老键会一直躺在用户的 `appsettings.json` 里被后来的版本读到；反过来新键没接上，
        /// 用户勾了那一格、重启就没了（"改了没用的开关"）。判据用**真实的设置文件文本**。
        /// </para>
        /// </summary>
        [Fact]
        public void 设置文件不再写退役的两个键_新键必须写着()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            Directory.CreateDirectory(_root);

            AppSettings settings = AppSettings.CreateDefault();
            settings.OmitMiddleContinuationLayers = true;

            service.Save(settings);

            string json = File.ReadAllText(pathService.SettingsFilePath, Encoding.UTF8);

            Assert.Contains("omitMiddleContinuationLayers", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("true", json, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("terminalLayoutMode", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("collapseRepeatedFolderLayer", json, StringComparison.OrdinalIgnoreCase);

            // 读回来也必须是同一个值（值 + 落盘两条都钉住）。
            Assert.True(service.Load().OmitMiddleContinuationLayers);
        }
        /// <summary>
        /// 最大层数的一次性迁移（用户 2026-09-24 拍板"两个上限统一成 10"）。
        ///
        /// <para>旧设置文件里存着 3 的用户，那个 3 是**旧默认值**而不是他的选择 ——
        /// 迁移一次；但他之后自己把 3 改回来时，**不许再被顶回去**（否则就是"设置改不动"）。</para>
        /// </summary>
        [Fact]
        public void 旧的最大层数3_迁移成10只做一次()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            Directory.CreateDirectory(_root);

            // ① 旧设置文件：3 且没有迁移标记 → 读成 10
            File.WriteAllText(
                pathService.SettingsFilePath,
                "{ \"MaxRecursionDepth\": 3 }",
                new UTF8Encoding(false));

            AppSettings migrated = service.Load();

            Assert.Equal(10, migrated.MaxRecursionDepth);
            Assert.True(migrated.RecursionDefaultUnifiedToTen);

            // ② 用户明确改成 3 并存盘 → 再读一次仍然是 3（迁移只发生一次）
            migrated.MaxRecursionDepth = 3;
            service.Save(migrated);

            Assert.Equal(3, service.Load().MaxRecursionDepth);
        }

        // ================================================================ ② 落点（塌缩已退役）

        /// <summary>
        /// **塌缩退役之后**：包名与所在目录同名（<c>111\222\名字\名字.rar</c>）也**一律多一层**，
        /// 而且**与"那层里还有没有别的包"无关** —— 用户 2026-09-27："如果这层里还有很多别的东西呢，
        /// 不就混乱了吗"，所以判据里不再有"扫目录"这一环，落点对用户完全可预期。
        /// </summary>
        [Fact]
        public void 塌缩退役后_同名同目录也一律多一层_与目录里有没有别的包无关()
        {
            var pathService = new PathService();
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true
            };

            string onlyOne = WriteFile(@"111\222\名字\名字.rar", "rar");
            var task = new ArchiveTask(onlyOne);

            Assert.Equal(PathOf(@"111\222\名字\名字"), pathService.BuildOutputPath(task, options));

            // 同一个目录里再放一个包：落点**一个字都不变**（旧规则这里会从"塌缩"翻成"不塌缩"）。
            WriteFile(@"111\222\名字\另一个.rar", "other");

            Assert.Equal(PathOf(@"111\222\名字\名字"), pathService.BuildOutputPath(task, options));
        }

        /// <summary>
        /// 手动档「解压到当前文件夹」（2026-09-27 新加）：<c>111\222.rar</c> → 产物落 <c>111\</c>，
        /// 于是 <c>LandsInSourceDirectory</c> 这一条唯一还成立的例外就是它。
        /// </summary>
        [Fact]
        public void 手动档解压到当前文件夹_落点就是源包所在目录()
        {
            var pathService = new PathService();
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true,
                ExtractIntoSourceFolder = true
            };

            string archive = WriteFile(@"111\222.rar", "rar");
            var task = new ArchiveTask(archive);
            string output = pathService.BuildOutputPath(task, options);

            Assert.Equal(PathOf(@"111"), output);
            Assert.True(OutputPlacement.LandsInSourceDirectory(archive, output));

            /*
             * 自动档（默认）永远不是这一档：同一对设置下，落点是"同名子文件夹"。
             * ⛔ 一键处理/批量不许走这里 —— 只有用户点「解压到当前文件夹」才会置这个标志。
             */
            var autoOptions = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true
            };

            Assert.Equal(PathOf(@"111\222"), pathService.BuildOutputPath(task, autoOptions));
            Assert.False(OutputPlacement.LandsInSourceDirectory(archive, pathService.BuildOutputPath(task, autoOptions)));
        }

        /// <summary>
        /// 「解压到当前文件夹」的**接线**（2026-09-27）：那个开关只有 <see cref="ExtractOptions.ExtractIntoSourceFolder"/>
        /// 一个入口，界面那条路（<c>BuildOutputPath</c>，即「输出目录」列）与管线那条路（<c>ResolveOutputPlacement</c>）
        /// 必须给出同一个落点。
        ///
        /// <para>红在哪：修之前 <c>BuildOutputPath</c> 只认自己那个同名形参、不认选项对象上的这一格 ——
        /// 于是同一个任务在界面显示 <c>111\222</c>、真正解压却落到 <c>111\</c>（或者反过来）。
        /// 这条断言就是"两处必须同一个答案"。</para>
        /// </summary>
        [Fact]
        public void 手动档开关只有一个入口_界面列与管线算出同一个落点()
        {
            var pathService = new PathService();
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = true,
                KeepArchiveNameFolder = true,
                ExtractIntoSourceFolder = true
            };

            string archive = WriteFile(@"111\222.rar", "rar");
            var task = new ArchiveTask(archive);

            // ① 不带 volumeGroupBaseName 的那条（界面列走它）
            Assert.Equal(PathOf(@"111"), pathService.BuildOutputPath(task, options));

            // ② 管线那条（带整份结论）
            OutputPlacementResult placement = pathService.ResolveOutputPlacement(task, options);

            Assert.True(placement.Success);
            Assert.Equal(PathOf(@"111"), placement.DestinationDirectory);

            // 默认（一键处理/批量那条路）永远是"同名子文件夹"，摊平必须显式打开。
            Assert.False(new ExtractOptions().ExtractIntoSourceFolder);
        }

        // ================================================================ ③ 删除入口（其余物）

        /// <summary>
        /// 其余物目录名有两个合法来源：布局层给的规范名（<c>其余物</c>）
        /// 与老版本留下的历史名（<c>过程物</c>）。测试用例统一用规范名建目录，
        /// 于是**布局改名时这些用例跟着变**，不会各写一份字面量。
        /// </summary>
        private static string ArtifactName => ProcessArtifactLayout.ArtifactDirectoryName;

        /// <summary>老版本留下的目录名：显式测"老名字也认"时用它。</summary>
        private static string LegacyArtifactName => ProcessArtifactLayout.LegacyArtifactDirectoryName;

        [Fact]
        public void 其余物预览_报顶层项数与总大小且不删任何东西()
        {
            MakeDirectory($@"out\{ArtifactName}\sub");
            WriteFile($@"out\{ArtifactName}\inner.7z.001", "12345");        // 5 字节
            WriteFile($@"out\{ArtifactName}\sub\middle.zip", "1234567890"); // 10 字节

            MaintenanceCleanupService service = CreateCleanupService(out _);
            CleanupPreview preview = service.PreviewProcessArtifacts(OutputOnlyTask("out"));

            Assert.True(preview.HasTarget);
            Assert.Equal(PathOf($@"out\{ArtifactName}"), preview.ScopePath);
            Assert.Equal(2, preview.ItemCount);
            Assert.Equal(15L, preview.TotalBytes);
            Assert.True(preview.Determined);
            Assert.Contains("inner.7z.001", preview.Items);

            // 预览不删除：目录还在，文件也还在。
            Assert.True(Directory.Exists(PathOf($@"out\{ArtifactName}")));
            Assert.True(File.Exists(PathOf($@"out\{ArtifactName}\inner.7z.001")));
        }

        [Fact]
        public void 其余物预览_没有其余物目录时不进确认流程()
        {
            MakeDirectory(@"out");

            CleanupPreview preview = CreateCleanupService(out _).PreviewProcessArtifacts(OutputOnlyTask("out"));

            Assert.False(preview.HasTarget);

            // 文案只说"没有可删的"，不能让人以为路径算错了（路径就在最后）。
            Assert.Contains("没有可删除的其余物", preview.Message, StringComparison.Ordinal);
            Assert.Contains(ArtifactName, preview.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 其余物执行_默认档走回收站并留下路径理由条目数总大小()
        {
            MakeDirectory($@"out\{ArtifactName}\sub");
            WriteFile($@"out\{ArtifactName}\inner.7z.001", "12345");
            WriteFile($@"out\{ArtifactName}\sub\middle.zip", "1234567890");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            ArchiveTask task = OutputOnlyTask("out");
            CleanupPreview preview = service.PreviewProcessArtifacts(task);
            CleanupOutcome outcome = service.CleanProcessArtifacts(task, DeleteMode.RecycleBin, preview);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(0, outcome.FailureCount);
            Assert.Equal(15L, outcome.RecycledBytes);
            Assert.Equal(0L, outcome.FreedBytes);
            Assert.Empty(executor.PermanentCalls);
            Assert.Equal(PathOf($@"out\{ArtifactName}"), Assert.Single(executor.RecycleCalls));
            Assert.False(Directory.Exists(PathOf($@"out\{ArtifactName}")));

            // 日志四要素：路径 + 理由 + 条目数 + 总大小。
            string log = Assert.Single(outcome.LogLines);
            Assert.Contains(PathOf($@"out\{ArtifactName}"), log, StringComparison.Ordinal);
            Assert.Contains(MaintenanceCleanupService.ProcessArtifactReason, log, StringComparison.Ordinal);
            Assert.Contains("条目数=3", log, StringComparison.Ordinal);
            Assert.Contains("总大小=15 字节", log, StringComparison.Ordinal);
        }

        [Fact]
        public void 其余物执行_激进档彻底删除并报释放字节数()
        {
            MakeDirectory($@"out\{ArtifactName}");
            WriteFile($@"out\{ArtifactName}\inner.7z.001", "12345");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            ArchiveTask task = OutputOnlyTask("out");
            CleanupOutcome outcome = service.CleanProcessArtifacts(task, DeleteMode.Permanent);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(5L, outcome.FreedBytes);
            Assert.Equal(0L, outcome.RecycledBytes);
            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(PathOf($@"out\{ArtifactName}"), Assert.Single(executor.PermanentCalls));
        }

        [Fact]
        public void 其余物执行_回收站不可用时不降级为永久删除()
        {
            MakeDirectory($@"out\{ArtifactName}");
            WriteFile($@"out\{ArtifactName}\inner.7z.001", "12345");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            executor.RecycleResult = RecycleAttemptResult.Unavailable;

            CleanupOutcome outcome = service.CleanProcessArtifacts(OutputOnlyTask("out"), DeleteMode.RecycleBin);

            Assert.True(outcome.Attempted);
            Assert.Equal(0, outcome.SuccessCount);
            Assert.Equal(1, outcome.FailureCount);
            Assert.Empty(executor.PermanentCalls);
            Assert.True(Directory.Exists(PathOf($@"out\{ArtifactName}")), "拒绝删除时目标必须原样留在原处");
            Assert.Contains(outcome.FailureReasons, reason => reason.Contains("回收站", StringComparison.Ordinal));
        }

        // ================================================================ ③-b 作用域收窄（本次修复的核心）

        /// <summary>
        /// 本次修复的**核心回归判据**（用户 2026-09-21 报的真实缺陷）：
        /// 同一个共享输出目录下有 <c>其余物\222\</c> 与 <c>其余物\333\</c> 两份，
        /// 只对 222 执行删除 → <b>333 那一份一个条目都不能被删</b>。
        ///
        /// <para>
        /// 旧实现的作用域是 <c>&lt;任务输出目录&gt;\其余物</c>。模式 B（解压到当前目录）下所有任务的
        /// 输出目录都是同一个（源目录），于是"只勾选 222 去清理"会连带清掉 333、444 的其余物。
        /// </para>
        /// <para>
        /// 断言分三层，缺一不可：① 删除调用里只有 222 那一条路径；② 333 的目录与文件在磁盘上原样存在；
        /// ③ 内容逐字未变（"没被删"不等于"没被动过"）。
        /// </para>
        /// </summary>
        [Fact]
        public void 共享目录下只删本任务的其余物_333那一份一个条目都不动()
        {
            // 共享根 = 源目录（模式 B：输出目录就是源包所在目录）。
            string sourceArchive = WriteFile(@"111\222.rar", "rar");
            MakeDirectory($@"111\{ArtifactName}\222\volumes");
            WriteFile($@"111\{ArtifactName}\222\volumes\222.7z.001", "222222");
            WriteFile($@"111\{ArtifactName}\222\222.rar", "source-of-222");
            MakeDirectory($@"111\{ArtifactName}\333");
            WriteFile($@"111\{ArtifactName}\333\333.rar", "source-of-333");
            WriteFile($@"111\{ArtifactName}\333\outer.7z.001", "333333");

            // 只勾选 222：输出目录落在源目录里 → 共享模式。
            var task222 = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task222);

            Assert.True(preview.HasTarget);
            Assert.Equal(PathOf($@"111\{ArtifactName}\222"), preview.ScopePath);
            Assert.True(preview.ResolvedScope!.OutputDirectoryIsShared);
            Assert.False(preview.ResolvedScope.DeletesEverythingInDirectory);

            CleanupOutcome outcome = service.CleanProcessArtifacts(task222, DeleteMode.RecycleBin, preview);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(0, outcome.FailureCount);

            // ① 删除调用里只有 222 那一条路径：333 的目录名一次都没出现在删除请求里。
            string recycled = Assert.Single(executor.RecycleCalls);
            Assert.Equal(PathOf($@"111\{ArtifactName}\222"), recycled);
            Assert.DoesNotContain(@"\333", recycled, StringComparison.Ordinal);
            Assert.Empty(executor.PermanentCalls);

            // ② 333 那一份原样存在。
            Assert.False(Directory.Exists(PathOf($@"111\{ArtifactName}\222")));
            Assert.True(Directory.Exists(PathOf($@"111\{ArtifactName}\333")));
            Assert.True(File.Exists(PathOf($@"111\{ArtifactName}\333\333.rar")));
            Assert.True(File.Exists(PathOf($@"111\{ArtifactName}\333\outer.7z.001")));

            // ③ 内容逐字未变。
            Assert.Equal("source-of-333", File.ReadAllText(PathOf($@"111\{ArtifactName}\333\333.rar")));
            Assert.Equal("333333", File.ReadAllText(PathOf($@"111\{ArtifactName}\333\outer.7z.001")));

            // 共享根自己（源目录）与它里面的别的包也不能被动。
            Assert.True(Directory.Exists(PathOf("111")));
            Assert.True(File.Exists(PathOf(@"111\222.rar")));
        }

        /// <summary>
        /// 反面对着照：共享目录下**没有**本任务那一份时，绝不退到上一层去删整个其余物。
        /// （这正是旧缺陷的形态："找不到窄的就去删宽的"。）
        /// </summary>
        [Fact]
        public void 共享目录下没有本任务那一份时_拒绝删除而不是退到上一层()
        {
            string sourceArchive = WriteFile(@"111\222.rar", "rar");
            MakeDirectory($@"111\{ArtifactName}\333");
            WriteFile($@"111\{ArtifactName}\333\333.rar", "source-of-333");

            var task222 = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task222);

            Assert.False(preview.HasTarget);
            Assert.Contains("已拒绝删除其余物", preview.Message, StringComparison.Ordinal);
            Assert.Contains("只允许删本任务那一份", preview.Message, StringComparison.Ordinal);
            Assert.Contains("不会退到上一层", preview.Message, StringComparison.Ordinal);

            CleanupOutcome outcome = service.CleanProcessArtifacts(task222, DeleteMode.RecycleBin, preview);

            Assert.False(outcome.Attempted);
            Assert.Empty(executor.RecycleCalls);
            Assert.Empty(executor.PermanentCalls);
            Assert.True(Directory.Exists(PathOf($@"111\{ArtifactName}\333")));
        }

        /// <summary>
        /// 老版本留下的 <c>过程物</c> 目录必须也能被删掉（不能变成清不掉的历史垃圾）；
        /// 新版 <c>其余物</c> 与老版 <c>过程物</c> 同时存在时，两份都属于本任务，一起删。
        /// </summary>
        [Fact]
        public void 老名字过程物也要能被删_与新名字同时存在时一起删()
        {
            string sourceArchive = WriteFile(@"111\222.rar", "rar");
            MakeDirectory($@"111\{LegacyArtifactName}\222");
            WriteFile($@"111\{LegacyArtifactName}\222\legacy.7z.001", "legacy");
            MakeDirectory($@"111\{ArtifactName}\222");
            WriteFile($@"111\{ArtifactName}\222\current.7z.001", "current");

            var task222 = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task222);

            Assert.True(preview.HasTarget);
            Assert.Equal(2, preview.ResolvedScope!.ArtifactDirectories.Count);

            CleanupOutcome outcome = service.CleanProcessArtifacts(task222, DeleteMode.RecycleBin, preview);

            Assert.True(outcome.Attempted);
            Assert.Equal(2, outcome.SuccessCount);
            Assert.False(Directory.Exists(PathOf($@"111\{LegacyArtifactName}\222")));
            Assert.False(Directory.Exists(PathOf($@"111\{ArtifactName}\222")));
            Assert.Equal(2, executor.RecycleCalls.Count);
        }

        /// <summary>只有老名字存在时（升级上来的用户目录）照样能删干净。</summary>
        [Fact]
        public void 只有老名字过程物时也能删掉()
        {
            string sourceArchive = WriteFile(@"111\222.rar", "rar");
            MakeDirectory($@"111\{LegacyArtifactName}\222");
            WriteFile($@"111\{LegacyArtifactName}\222\legacy.7z.001", "legacy");

            var task222 = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task222);

            Assert.True(preview.HasTarget);
            Assert.Equal(PathOf($@"111\{LegacyArtifactName}\222"), preview.ScopePath);

            CleanupOutcome outcome = service.CleanProcessArtifacts(task222, DeleteMode.RecycleBin, preview);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.False(Directory.Exists(PathOf($@"111\{LegacyArtifactName}\222")));
        }

        /// <summary>
        /// 另一个包（333）自己那条删除路径必须只删 333：对称地证明收窄不是"碰巧只删了一个"。
        /// </summary>
        [Fact]
        public void 共享目录下勾选333时只删333那一份()
        {
            string sourceArchive = WriteFile(@"111\333.rar", "rar");
            MakeDirectory($@"111\{ArtifactName}\222");
            WriteFile($@"111\{ArtifactName}\222\222.rar", "source-of-222");
            MakeDirectory($@"111\{ArtifactName}\333");
            WriteFile($@"111\{ArtifactName}\333\333.rar", "source-of-333");

            var task333 = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task333);
            CleanupOutcome outcome = service.CleanProcessArtifacts(task333, DeleteMode.RecycleBin, preview);

            Assert.True(outcome.Attempted);
            Assert.Equal(PathOf($@"111\{ArtifactName}\333"), Assert.Single(executor.RecycleCalls));
            Assert.True(File.Exists(PathOf($@"111\{ArtifactName}\222\222.rar")), "222 那一份一个条目都不能被删");
        }

        /// <summary>
        /// 「删除本目录全部其余物」是**显式**入口：它才会覆盖整个共享目录（文案里写明影响范围）。
        /// </summary>
        [Fact]
        public void 删除本目录全部其余物_才覆盖整个共享目录()
        {
            string sourceArchive = WriteFile(@"111\222.rar", "rar");
            MakeDirectory($@"111\{ArtifactName}\222");
            MakeDirectory($@"111\{ArtifactName}\333");

            var task222 = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task222, ArtifactDeleteScope.EverythingInDirectory);

            Assert.True(preview.HasTarget);
            Assert.True(preview.ResolvedScope!.DeletesEverythingInDirectory);
            Assert.Equal(PathOf($@"111\{ArtifactName}"), preview.ScopePath);

            CleanupOutcome outcome = service.CleanProcessArtifacts(
                task222,
                DeleteMode.RecycleBin,
                preview,
                ArtifactDeleteScope.EverythingInDirectory);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(PathOf($@"111\{ArtifactName}"), Assert.Single(executor.RecycleCalls));
            Assert.Contains(MaintenanceCleanupService.AllArtifactsReason, Assert.Single(outcome.LogLines), StringComparison.Ordinal);
        }

        /// <summary>包有自己的输出目录（默认模式）时，其余物就在它自己的目录里，边界就是它。</summary>
        [Fact]
        public void 默认模式下只删任务自己目录里的其余物()
        {
            string sourceArchive = WriteFile(@"111\222.rar", "rar");
            MakeDirectory($@"111\222\{ArtifactName}");
            WriteFile($@"111\222\{ArtifactName}\inner.7z", "inner");
            MakeDirectory($@"111\333\{ArtifactName}");
            WriteFile($@"111\333\{ArtifactName}\other.7z", "other");

            var task = new ArchiveTask(sourceArchive) { OutputPath = PathOf(@"111\222") };

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupPreview preview = service.PreviewProcessArtifacts(task);

            Assert.True(preview.HasTarget);
            Assert.False(preview.ResolvedScope!.OutputDirectoryIsShared);
            Assert.Equal(PathOf($@"111\222\{ArtifactName}"), preview.ScopePath);

            service.CleanProcessArtifacts(task, DeleteMode.RecycleBin, preview);

            Assert.Equal(PathOf($@"111\222\{ArtifactName}"), Assert.Single(executor.RecycleCalls));
            Assert.True(File.Exists(PathOf($@"111\333\{ArtifactName}\other.7z")), "别的包的其余物一个条目都不能被删");
        }

        /// <summary>
        /// 预览里含源包（压缩包本身）时必须明确标出个数 —— 删掉它意味着要重新下载。
        ///
        /// <para>
        /// 判据是"任务自己登记过的源文件"：内层归档与分卷（由源包生成、删了还能再解出来）
        /// 不能被算成源包，否则警告天天误报。
        /// </para>
        /// </summary>
        [Fact]
        public void 预览里含源包时明确标出个数()
        {
            string sourceArchive = WriteFile(@"111\222.rar", "source");
            MakeDirectory($@"111\{ArtifactName}\222\volumes");
            WriteFile($@"111\{ArtifactName}\222\inner.7z.001", "12345");

            // 源包被移到其余物里（新布局），任务的 CurrentPath 仍然指着它原来那个位置。
            File.Move(sourceArchive, PathOf($@"111\{ArtifactName}\222\222.rar"));

            var task = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            CleanupPreview preview = CreateCleanupService(out _).PreviewProcessArtifacts(task);

            Assert.True(preview.HasTarget);

            // 只有任务登记过的 222.rar 算源包；同为顶层文件的内层 222.7z.001 不算（它由源包生成）。
            Assert.Equal(1, preview.SourcePackageCount);
            Assert.Contains("222.rar", preview.SourcePackageNames);
            Assert.Contains("1 个源包文件", preview.Message, StringComparison.Ordinal);

            string confirmText = MainViewModel.BuildCleanupConfirmText("删除其余物", preview, DeleteMode.RecycleBin);

            Assert.Contains("含 1 个源包文件", confirmText, StringComparison.Ordinal);
            Assert.Contains("需要重新下载", confirmText, StringComparison.Ordinal);
        }

        /// <summary>
        /// 任务没有任何源文件登记时退到扩展名兜底（例如任务来自更早的会话、清单已经拿不到）。
        /// 兜底只在这种情况下生效：登记清单非空时**只认清单**，不再按扩展名猜 ——
        /// 否则其余物里的内层归档天天被误报成"源包"。
        /// </summary>
        [Fact]
        public void 没有登记源文件时按扩展名兜底认源包()
        {
            MakeDirectory($@"out\{ArtifactName}");
            WriteFile($@"out\{ArtifactName}\222.rar", "source");
            WriteFile($@"out\{ArtifactName}\readme.txt", "not-archive");

            // 空任务：没有任何源文件登记（CurrentPath 为空）。
            var task = new ArchiveTask { OutputPath = PathOf("out") };

            CleanupPreview preview = CreateCleanupService(out _).PreviewProcessArtifacts(task);

            Assert.True(preview.HasTarget);
            Assert.Equal(1, preview.SourcePackageCount);
            Assert.Contains("222.rar", preview.SourcePackageNames);
        }

        /// <summary>内层归档与分卷不是"源包"：它们由源包生成，删掉还能再解出来，不该被算进警告。</summary>
        [Fact]
        public void 预览里只有内层归档与分卷时不误报源包()
        {
            string sourceArchive = WriteFile(@"111\222.7z.001", "volume-1");
            MakeDirectory($@"111\{ArtifactName}\222\volumes");
            WriteFile($@"111\{ArtifactName}\222\inner.7z.001", "inner");

            // 内层归档（由源包生成，删了还能再解出来）也要留在其余物里。
            WriteFile($@"111\{ArtifactName}\222\volumes\carved.zip", "carved");

            var task = new ArchiveTask(sourceArchive) { OutputPath = PathOf("111") };

            CleanupPreview preview = CreateCleanupService(out _).PreviewProcessArtifacts(task);

            Assert.True(preview.HasTarget);
            Assert.Equal(0, preview.SourcePackageCount);
            Assert.DoesNotContain("源包", preview.Message, StringComparison.Ordinal);
        }

        /// <summary>作用域解析拿不到可信边界时一律拒绝（删除不可逆：宁可不删，不可错删）。</summary>
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        public void 作用域解析_拿不到输出目录时拒绝(string? outputPath, string? sourcePath)
        {
            ArtifactCleanupScope scope = MaintenanceCleanupService.ResolveArtifactScope(outputPath, sourcePath);

            Assert.False(scope.IsResolved);
            Assert.Equal(string.Empty, scope.ArtifactDirectory);
            Assert.False(string.IsNullOrWhiteSpace(scope.BlockReason));
        }

        [Fact]
        public void 作用域解析_没有任务时拒绝()
        {
            ArtifactCleanupScope scope = MaintenanceCleanupService.ResolveArtifactScope((ArchiveTask?)null);

            Assert.False(scope.IsResolved);
            Assert.Contains("没有选中的任务", scope.BlockReason, StringComparison.Ordinal);
        }

        [Fact]
        public void 空文件夹预览_只列任意层级都没有文件的子目录()
        {
            MakeDirectory(@"out\empty-tree\deeper");
            WriteFile(@"out\has-file\x.mp4", "content");

            CleanupPreview preview = CreateCleanupService(out _).PreviewEmptyFolders(PathOf("out"));

            Assert.True(preview.HasTarget);
            Assert.Equal(1, preview.ItemCount);
            Assert.Equal(PathOf(@"out\empty-tree"), Assert.Single(preview.Items));
            Assert.True(Directory.Exists(PathOf(@"out\empty-tree")));
        }

        [Fact]
        public void 空文件夹执行_删掉空目录树且保留有文件的目录()
        {
            MakeDirectory(@"out\empty-tree\deeper");
            WriteFile(@"out\has-file\x.mp4", "content");

            MaintenanceCleanupService service = CreateCleanupService(out FakeDeleteExecutor executor);
            CleanupOutcome outcome = service.CleanEmptyFolders(PathOf("out"), DeleteMode.RecycleBin);

            Assert.True(outcome.Attempted);
            Assert.Equal(1, outcome.SuccessCount);
            Assert.Equal(PathOf(@"out\empty-tree"), Assert.Single(executor.RecycleCalls));
            Assert.False(Directory.Exists(PathOf(@"out\empty-tree")));
            Assert.True(File.Exists(PathOf(@"out\has-file\x.mp4")));
            Assert.Contains(outcome.LogLines, line => line.Contains(MaintenanceCleanupService.EmptyFolderReason, StringComparison.Ordinal));
        }

        [Fact]
        public void 清理作用域_其余物目录由ProcessArtifactLayout唯一来源给出()
        {
            MakeDirectory($@"out\{ArtifactName}");

            var task = OutputOnlyTask("out");

            Assert.Equal(
                PathOf($@"out\{ArtifactName}"),
                MaintenanceCleanupService.ResolveProcessArtifactScope(task));

            // 布局层给的规范名 + 历史名，两个都要认（老目录不能清不掉）。
            Assert.Equal("其余物", ProcessArtifactLayout.ArtifactDirectoryName);
            Assert.Equal("过程物", ProcessArtifactLayout.LegacyArtifactDirectoryName);
            Assert.Equal("其余物", MaintenanceCleanupService.ArtifactDirectoryName);

            Assert.Equal(string.Empty, MaintenanceCleanupService.ResolveProcessArtifactScope(null));
        }

        [Fact]
        public void 作用域提醒_共享模式下说清只删本任务那一份()
        {
            /*
             * 输出目录 = 源包所在目录。⚠ 2026-09-27 起这只可能是**手动档「解压到当前文件夹」**
             * 之后的样子 —— 旧的场景 B 塌缩与"多个包共用一层"都已退役，措辞也按新事实改了
             * （不再说"与其它包共用 / 按包基名分开的子目录"，那是已经不存在的情形）。
             */
            var task = new ArchiveTask(PathOf(@"111\222\333.rar")) { OutputPath = PathOf(@"111\222") };

            string artifactNote = MainViewModel.BuildSharedScopeNote(CleanupScope.Artifacts, task);

            Assert.Contains("只删本任务那一份", artifactNote, StringComparison.Ordinal);
            Assert.Contains("不会碰别的包", artifactNote, StringComparison.Ordinal);
            Assert.Contains("源包所在目录", MainViewModel.BuildSharedScopeNote(CleanupScope.EmptyFolders, task));

            // 普通模式（输出目录是包子目录）：不提这句。
            task.OutputPath = PathOf(@"111\222\333");

            Assert.Equal(string.Empty, MainViewModel.BuildSharedScopeNote(CleanupScope.Artifacts, task));
            Assert.Equal(string.Empty, MainViewModel.BuildSharedScopeNote(CleanupScope.EmptyFolders, task));
        }

        [Fact]
        public void 确认框正文_含作用范围条目数总大小与档位说明()
        {
            var preview = new CleanupPreview
            {
                Scope = CleanupScope.Artifacts,
                ScopePath = @"D:\out\其余物",
                HasTarget = true,
                ItemCount = 3,
                EntryCount = 9,
                TotalBytes = 1024,
                Determined = true,
                Items = new[] { "a.7z", "b.7z" }
            };

            string recycleText = MainViewModel.BuildCleanupConfirmText("删除其余物", preview, DeleteMode.RecycleBin);

            Assert.Contains(@"D:\out\其余物", recycleText, StringComparison.Ordinal);
            Assert.Contains("顶层 3 项", recycleText, StringComparison.Ordinal);
            Assert.Contains("1024 字节", recycleText, StringComparison.Ordinal);
            Assert.Contains("回收站", recycleText, StringComparison.Ordinal);
            Assert.DoesNotContain("无法恢复", recycleText, StringComparison.Ordinal);

            string permanentText = MainViewModel.BuildCleanupConfirmText(
                "删除其余物", preview, DeleteMode.Permanent, "作用域共用提醒");

            Assert.Contains("无法恢复", permanentText, StringComparison.Ordinal);
            Assert.Contains("作用域共用提醒", permanentText, StringComparison.Ordinal);
        }

        /// <summary>任务详情文本里补上"其余物目录"，便于用户手动去看（与删除入口同一份解析）。</summary>
        [Fact]
        public void 任务详情文本_带上其余物路径()
        {
            MakeDirectory($@"out\{ArtifactName}");

            string text = MainViewModel.BuildTaskInfoText(OutputOnlyTask("out"));

            Assert.Contains("其余物目录：" + PathOf($@"out\{ArtifactName}"), text, StringComparison.Ordinal);
        }

        // ================================================================ ⑤ 设置项真正生效

        /*
         * ⛔ 2026-09-30：这里原来有三条「缓存根目录」用例（C 盘 / 相对路径一律拒绝、填 C 盘保存被拦下、
         * 合法值保存通过），随 `AppSettings.CacheRootDirectory` 设置项一起删除 ——
         * 工作区位置不再由用户指定，那一格与它的校验都不存在了。
         * "旧配置里的键会被安静忽略"由 `SettingsCacheRootRemovalTests` 接手钉住；
         * 而"保存被拦下并给出原因"这道闸门本身还在（工具路径那一类），改由下面这条钉着。
         */

        /// <summary>
        /// 显式保存那道闸门：**工具路径指向一个不存在的文件 → 整份设置先不存，并说清原因与改法**。
        ///
        /// <para>为什么必须拦：<c>Normalize</c> 会把失效的工具路径**静默清空**，
        /// 于是"填错路径 → 保存 → 看到成功"是一条静默丢弃用户输入的路（见 <c>ValidateToolExePath</c> 的注释）。</para>
        /// </summary>
        [Fact]
        public void 工具路径不存在_保存被拦下并给出原因()
        {
            var viewModel = new SettingsViewModel(new AppSettings(), new SettingsService());

            /*
             * ⚠ 必须在**构造之后**才填这个路径：构造里那次 CloneSettings 会顺手 Normalize，
             * 而 Normalize 会把"文件不存在"的工具路径直接清空 —— 装配前塞进去的非法值根本留不到保存那一刻。
             * 真机上的顺序正是这样：用户在设置页上敲进一个坏路径，然后（或定时器）才轮到保存。
             */
            viewModel.Settings.CustomRarExePath = @"C:\af-not-exists\Rar.exe";

            viewModel.SaveCommand.Execute(null);

            Assert.Null(viewModel.DialogResult);
            Assert.Contains("Rar.exe", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("设置未保存", viewModel.Message, StringComparison.Ordinal);

            // 内存里那份也还在（没有在"校验没过"的路上被静默清掉 —— 用户看得到自己填的是什么）。
            Assert.Equal(@"C:\af-not-exists\Rar.exe", viewModel.Settings.CustomRarExePath);
        }

        [Fact]
        public void 设置窗口的简洁档开关_读写设置项()
        {
            var viewModel = new SettingsViewModel(new AppSettings(), new SettingsService());

            Assert.False(viewModel.OmitMiddleContinuationLayers, "默认关 = 忠实档");

            viewModel.OmitMiddleContinuationLayers = true;
            Assert.True(viewModel.Settings.OmitMiddleContinuationLayers);

            viewModel.OmitMiddleContinuationLayers = false;
            Assert.False(viewModel.Settings.OmitMiddleContinuationLayers);
        }

        // ================================================================ ⑥ 异常文本脱敏（无 UI 宿主）

        [Fact]
        public void 异常提示文本脱敏_无UI宿主也不抛异常()
        {
            var service = new DialogService();

            // 异常消息里夹着 7z 命令行片段：这正是最可能带出明文密码的形态。
            service.ShowException(
                new InvalidOperationException("7z.exe x out.7z -pSecretPass123 password=SecretPass123"),
                "程序发生未处理的界面异常（已记录日志）");

            Assert.DoesNotContain(
                DialogService.FallbackLog,
                entry => entry.Contains("SecretPass123", StringComparison.Ordinal));
        }

        // ================================================================ 装配

        private static MaintenanceCleanupService CreateCleanupService(out FakeDeleteExecutor executor)
        {
            executor = new FakeDeleteExecutor();
            return new MaintenanceCleanupService(executor);
        }

        /// <summary>
        /// 记录调用、可脚本化结果的假执行器：全程不碰系统回收站。
        /// 与 RecycleBinServiceTests 里的同名假件同一口径（"报成功就必须真的把目标移走"）。
        /// </summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public RecycleAttemptResult RecycleResult { get; set; } = RecycleAttemptResult.Recycled;

            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                if (RecycleResult == RecycleAttemptResult.Recycled)
                {
                    if (isDirectory)
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                return RecycleResult;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);

                if (isDirectory)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
        }
    }
}
